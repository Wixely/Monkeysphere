using System.Globalization;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

public sealed class SqliteMonkeysphereStore(MonkeysphereConnectionFactory connections) : IMonkeysphereStore
{
    public async Task<IReadOnlyList<RecordType>> ListRecordTypesAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<RecordTypeRow> rows = await connection.QueryAsync<RecordTypeRow>(
            new CommandDefinition(
                "SELECT Id, Name, CreatedAtUtc, UpdatedAtUtc FROM RecordTypes ORDER BY Name COLLATE NOCASE, Id;",
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(MapRecordType).ToArray();
    }

    public async Task<RecordTypeDetails?> GetRecordTypeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        RecordTypeRow? type = await connection.QuerySingleOrDefaultAsync<RecordTypeRow>(
            new CommandDefinition(
                "SELECT Id, Name, CreatedAtUtc, UpdatedAtUtc FROM RecordTypes WHERE Id = @Id;",
                new { Id = Key(id) },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (type is null)
        {
            return null;
        }

        IReadOnlyList<RecordTypeField> fields = await QueryFieldsAsync(connection, id, cancellationToken).ConfigureAwait(false);
        return new RecordTypeDetails(MapRecordType(type), fields);
    }

    public async Task<IReadOnlyList<FieldDefinition>> ListFieldDefinitionsAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<FieldRow> rows = await connection.QueryAsync<FieldRow>(new CommandDefinition("""
            SELECT Id, Name, TypeId, ConfigurationJson, Lifecycle, CreatedAtUtc, UpdatedAtUtc,
                   0 AS SortOrder, 0 AS IsRequired
            FROM FieldDefinitions
            ORDER BY Lifecycle, Name COLLATE NOCASE, Id;
            """, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(MapField).ToArray();
    }

    public async Task<RecordType> CreateRecordTypeAsync(
        Guid id,
        string name,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        string timestamp = Timestamp(now);
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO RecordTypes (Id, Name, CreatedAtUtc, UpdatedAtUtc) VALUES (@Id, @Name, @Now, @Now);",
                new { Id = Key(id), Name = name, Now = timestamp },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (IsUniqueConstraint(exception))
        {
            throw new DomainValidationException("A record type with that name already exists.", exception);
        }

        return new RecordType(id, name, now, now);
    }

    public async Task RenameRecordTypeAsync(Guid id, string name, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        int changed;
        try
        {
            changed = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE RecordTypes SET Name = @Name, UpdatedAtUtc = @Now WHERE Id = @Id;",
                new { Id = Key(id), Name = name, Now = Timestamp(now) },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (IsUniqueConstraint(exception))
        {
            throw new DomainValidationException("A record type with that name already exists.", exception);
        }

        RequireChanged(changed, "Record type was not found.");
    }

    public async Task<FieldDefinition> CreateAndAttachFieldAsync(
        Guid recordTypeId,
        Guid fieldDefinitionId,
        string name,
        string typeId,
        string configurationJson,
        bool isRequired,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        string timestamp = Timestamp(now);
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        int typeExists = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM RecordTypes WHERE Id = @Id;",
            new { Id = Key(recordTypeId) },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (typeExists == 0)
        {
            throw new DomainValidationException("Record type was not found.");
        }

        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO FieldDefinitions
                (Id, Name, TypeId, ConfigurationJson, Lifecycle, CreatedAtUtc, UpdatedAtUtc)
            VALUES
                (@Id, @Name, @TypeId, @ConfigurationJson, 0, @Now, @Now);
            """,
            new
            {
                Id = Key(fieldDefinitionId),
                Name = name,
                TypeId = typeId,
                ConfigurationJson = configurationJson,
                Now = timestamp,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO RecordTypeFields (RecordTypeId, FieldDefinitionId, SortOrder, IsRequired)
            SELECT @RecordTypeId, @FieldDefinitionId, COALESCE(MAX(SortOrder) + 1, 0), @IsRequired
            FROM RecordTypeFields
            WHERE RecordTypeId = @RecordTypeId;
            """,
            new
            {
                RecordTypeId = Key(recordTypeId),
                FieldDefinitionId = Key(fieldDefinitionId),
                IsRequired = isRequired ? 1 : 0,
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new FieldDefinition(fieldDefinitionId, name, typeId, configurationJson, FieldLifecycle.Active, now, now);
    }

    public async Task AttachFieldAsync(
        Guid recordTypeId,
        Guid fieldDefinitionId,
        bool isRequired,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        int validPair = await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*)
            FROM RecordTypes rt
            CROSS JOIN FieldDefinitions fd
            WHERE rt.Id = @RecordTypeId AND fd.Id = @FieldDefinitionId AND fd.Lifecycle = 0;
            """,
            new { RecordTypeId = Key(recordTypeId), FieldDefinitionId = Key(fieldDefinitionId) },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (validPair == 0)
        {
            throw new DomainValidationException("The record type or active field definition was not found.");
        }

        int attached = await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM RecordTypeFields
            WHERE RecordTypeId = @RecordTypeId AND FieldDefinitionId = @FieldDefinitionId;
            """,
            new { RecordTypeId = Key(recordTypeId), FieldDefinitionId = Key(fieldDefinitionId) },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (attached != 0)
        {
            throw new DomainValidationException("That field definition is already attached to this record type.");
        }

        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO RecordTypeFields (RecordTypeId, FieldDefinitionId, SortOrder, IsRequired)
            SELECT @RecordTypeId, @FieldDefinitionId, COALESCE(MAX(SortOrder) + 1, 0), @IsRequired
            FROM RecordTypeFields
            WHERE RecordTypeId = @RecordTypeId;
            UPDATE RecordTypes SET UpdatedAtUtc = @Now WHERE Id = @RecordTypeId;
            """,
            new
            {
                RecordTypeId = Key(recordTypeId),
                FieldDefinitionId = Key(fieldDefinitionId),
                IsRequired = isRequired ? 1 : 0,
                Now = Timestamp(now),
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RenameFieldAsync(Guid id, string name, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        int changed = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE FieldDefinitions SET Name = @Name, UpdatedAtUtc = @Now WHERE Id = @Id;",
            new { Id = Key(id), Name = name, Now = Timestamp(now) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        RequireChanged(changed, "Field definition was not found.");
    }

    public async Task RetireFieldAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        int changed = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE FieldDefinitions SET Lifecycle = 1, UpdatedAtUtc = @Now WHERE Id = @Id AND Lifecycle = 0;",
            new { Id = Key(id), Now = Timestamp(now) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        RequireChanged(changed, "Active field definition was not found.");
    }

    public async Task<RecordDetails> CreateRecordAsync(
        Guid id,
        Guid recordTypeId,
        string displayName,
        IReadOnlyList<NormalizedFieldValue> values,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        string timestamp = Timestamp(now);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO Records (Id, RecordTypeId, DisplayName, CreatedAtUtc, UpdatedAtUtc)
            VALUES (@Id, @RecordTypeId, @DisplayName, @Now, @Now);
            """,
            new { Id = Key(id), RecordTypeId = Key(recordTypeId), DisplayName = displayName, Now = timestamp },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        await InsertValuesAsync(connection, transaction, id, values, timestamp, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetRecordAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Created record could not be read back.");
    }

    public async Task<RecordDetails?> GetRecordAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        RecordSummaryRow? row = await connection.QuerySingleOrDefaultAsync<RecordSummaryRow>(new CommandDefinition("""
            SELECT r.Id, r.RecordTypeId, rt.Name AS RecordTypeName, r.DisplayName, r.UpdatedAtUtc
            FROM Records r
            JOIN RecordTypes rt ON rt.Id = r.RecordTypeId
            WHERE r.Id = @Id;
            """,
            new { Id = Key(id) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        Guid recordTypeId = ParseGuid(row.RecordTypeId);
        IReadOnlyList<RecordTypeField> fields = await QueryFieldsAsync(connection, recordTypeId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<RecordValue> values = await QueryValuesAsync(connection, id, cancellationToken).ConfigureAwait(false);
        return new RecordDetails(MapSummary(row), values, fields);
    }

    public async Task<RecordDetails> UpdateRecordAsync(
        Guid id,
        string displayName,
        IReadOnlyList<NormalizedFieldValue> values,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        string timestamp = Timestamp(now);
        int changed = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE Records SET DisplayName = @DisplayName, UpdatedAtUtc = @Now WHERE Id = @Id;",
            new { Id = Key(id), DisplayName = displayName, Now = timestamp },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        RequireChanged(changed, "Record was not found.");
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM FieldValues WHERE RecordId = @RecordId;",
            new { RecordId = Key(id) },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        await InsertValuesAsync(connection, transaction, id, values, timestamp, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetRecordAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Updated record could not be read back.");
    }

    public async Task<bool> DeleteRecordAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        int changed = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM Records WHERE Id = @Id;",
            new { Id = Key(id) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return changed == 1;
    }

    public async Task<PagedResult<RecordSummary>> SearchRecordsAsync(RecordSearch search, CancellationToken cancellationToken = default)
    {
        StringBuilder where = new(" WHERE 1 = 1");
        DynamicParameters parameters = new();
        if (search.RecordTypeId is Guid typeId)
        {
            where.Append(" AND r.RecordTypeId = @RecordTypeId");
            parameters.Add("RecordTypeId", Key(typeId));
        }

        if (!string.IsNullOrWhiteSpace(search.Query))
        {
            where.Append("""
                 AND (
                    r.DisplayName LIKE @Query ESCAPE '\' COLLATE NOCASE
                    OR EXISTS (
                        SELECT 1 FROM FieldValues qv
                        WHERE qv.RecordId = r.Id
                          AND (qv.TextValue LIKE @Query ESCAPE '\' COLLATE NOCASE
                               OR qv.NumberValue LIKE @Query ESCAPE '\' COLLATE NOCASE
                               OR qv.DateValue LIKE @Query ESCAPE '\' COLLATE NOCASE
                               OR qv.TemporalValue LIKE @Query ESCAPE '\' COLLATE NOCASE
                               OR qv.ApproximationNote LIKE @Query ESCAPE '\' COLLATE NOCASE)
                    )
                    OR EXISTS (
                        SELECT 1 FROM FieldValues qv
                        JOIN FieldValueTags qt ON qt.FieldValueId = qv.Id
                        WHERE qv.RecordId = r.Id
                          AND qt.Value LIKE @Query ESCAPE '\' COLLATE NOCASE
                    )
                )
                """);
            parameters.Add("Query", $"%{EscapeLike(search.Query)}%");
        }

        AppendTypedFilter(where, parameters, search);

        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        int total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM Records r" + where + ";",
            parameters,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        parameters.Add("Limit", search.PageSize);
        parameters.Add("Offset", (search.Page - 1) * search.PageSize);
        IEnumerable<RecordSummaryRow> rows = await connection.QueryAsync<RecordSummaryRow>(new CommandDefinition("""
            SELECT r.Id, r.RecordTypeId, rt.Name AS RecordTypeName, r.DisplayName, r.UpdatedAtUtc
            FROM Records r
            JOIN RecordTypes rt ON rt.Id = r.RecordTypeId
            """ + where + " ORDER BY r.UpdatedAtUtc DESC, r.DisplayName COLLATE NOCASE, r.Id LIMIT @Limit OFFSET @Offset;",
            parameters,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return new PagedResult<RecordSummary>(rows.Select(MapSummary).ToArray(), search.Page, search.PageSize, total);
    }

    private static void AppendTypedFilter(StringBuilder where, DynamicParameters parameters, RecordSearch search)
    {
        if (search.FieldDefinitionId is not Guid fieldId || search.Operator is not FieldFilterOperator operation)
        {
            return;
        }

        parameters.Add("FilterFieldId", Key(fieldId));
        string filter = search.FilterValue!;
        string predicate = operation switch
        {
            FieldFilterOperator.Equals =>
                "(fv.TextValue = @Filter COLLATE NOCASE OR fv.NumberValue = @Filter OR fv.DateValue = @Filter OR fv.TemporalValue = @Filter OR EXISTS (SELECT 1 FROM FieldValueTags ft WHERE ft.FieldValueId = fv.Id AND ft.Value = @Filter COLLATE NOCASE))",
            FieldFilterOperator.Contains =>
                "(fv.TextValue LIKE @FilterPattern ESCAPE '\\' COLLATE NOCASE OR EXISTS (SELECT 1 FROM FieldValueTags ft WHERE ft.FieldValueId = fv.Id AND ft.Value LIKE @FilterPattern ESCAPE '\\' COLLATE NOCASE))",
            FieldFilterOperator.GreaterThan => "fv.NumberSortValue > @FilterNumber",
            FieldFilterOperator.LessThan => "fv.NumberSortValue < @FilterNumber",
            FieldFilterOperator.Before => "COALESCE(fv.TemporalSortKey, fv.DateValue) < @FilterDate",
            FieldFilterOperator.After => "COALESCE(fv.TemporalSortKey, fv.DateValue) > @FilterDate",
            _ => throw new DomainValidationException("Unsupported field filter operator."),
        };

        parameters.Add("Filter", filter);
        parameters.Add("FilterPattern", $"%{EscapeLike(filter)}%");
        if (operation is FieldFilterOperator.GreaterThan or FieldFilterOperator.LessThan)
        {
            if (!double.TryParse(filter, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            {
                throw new DomainValidationException("Numeric filters require an invariant number.");
            }

            parameters.Add("FilterNumber", number);
        }

        if (operation is FieldFilterOperator.Before or FieldFilterOperator.After)
        {
            parameters.Add("FilterDate", TemporalValues.NormalizeFilterSortKey(filter));
        }

        where.Append(" AND EXISTS (SELECT 1 FROM FieldValues fv WHERE fv.RecordId = r.Id AND fv.FieldDefinitionId = @FilterFieldId AND ");
        where.Append(predicate);
        where.Append(')');
    }

    private static async Task InsertValuesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid recordId,
        IReadOnlyList<NormalizedFieldValue> values,
        string timestamp,
        CancellationToken cancellationToken)
    {
        foreach (NormalizedFieldValue value in values)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO FieldValues
                    (Id, RecordId, FieldDefinitionId, Ordinal, TextValue, NumberValue, NumberSortValue, DateValue,
                     TemporalValue, TemporalPrecision, TemporalSortKey, IsApproximate, ApproximationNote, CreatedAtUtc, UpdatedAtUtc)
                VALUES
                    (@Id, @RecordId, @FieldDefinitionId, @Ordinal, @TextValue, @NumberValue, @NumberSortValue, @DateValue,
                     @TemporalValue, @TemporalPrecision, @TemporalSortKey, @IsApproximate, @ApproximationNote, @Now, @Now);
                """,
                new
                {
                    Id = Key(value.Id),
                    RecordId = Key(recordId),
                    FieldDefinitionId = Key(value.FieldDefinitionId),
                    value.Ordinal,
                    value.TextValue,
                    value.NumberValue,
                    value.NumberSortValue,
                    value.DateValue,
                    TemporalValue = value.Temporal?.Value,
                    TemporalPrecision = value.Temporal is null ? null : (int?)value.Temporal.Precision,
                    TemporalSortKey = value.Temporal?.SortKey,
                    IsApproximate = value.Temporal?.IsApproximate == true ? 1 : 0,
                    ApproximationNote = value.Temporal?.ApproximationNote,
                    Now = timestamp,
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            for (int ordinal = 0; ordinal < value.Tags.Count; ordinal++)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO FieldValueTags (FieldValueId, Ordinal, Value) VALUES (@FieldValueId, @Ordinal, @Value);",
                    new { FieldValueId = Key(value.Id), Ordinal = ordinal, Value = value.Tags[ordinal] },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
        }
    }

    private static async Task<IReadOnlyList<RecordTypeField>> QueryFieldsAsync(
        SqliteConnection connection,
        Guid recordTypeId,
        CancellationToken cancellationToken)
    {
        IEnumerable<FieldRow> rows = await connection.QueryAsync<FieldRow>(new CommandDefinition("""
            SELECT fd.Id, fd.Name, fd.TypeId, fd.ConfigurationJson, fd.Lifecycle,
                   fd.CreatedAtUtc, fd.UpdatedAtUtc, rtf.SortOrder, rtf.IsRequired
            FROM RecordTypeFields rtf
            JOIN FieldDefinitions fd ON fd.Id = rtf.FieldDefinitionId
            WHERE rtf.RecordTypeId = @RecordTypeId
            ORDER BY rtf.SortOrder, fd.Id;
            """,
            new { RecordTypeId = Key(recordTypeId) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(row => new RecordTypeField(MapField(row), row.SortOrder, row.IsRequired != 0)).ToArray();
    }

    private static async Task<IReadOnlyList<RecordValue>> QueryValuesAsync(
        SqliteConnection connection,
        Guid recordId,
        CancellationToken cancellationToken)
    {
        FieldValueRow[] rows = (await connection.QueryAsync<FieldValueRow>(new CommandDefinition("""
            SELECT fv.Id, fv.FieldDefinitionId, fd.Name AS FieldName, fd.TypeId, fv.Ordinal,
                   fv.TextValue, fv.NumberValue, fv.NumberSortValue, fv.DateValue,
                   fv.TemporalValue, fv.TemporalPrecision, fv.TemporalSortKey,
                   fv.IsApproximate, fv.ApproximationNote
            FROM FieldValues fv
            JOIN FieldDefinitions fd ON fd.Id = fv.FieldDefinitionId
            WHERE fv.RecordId = @RecordId
            ORDER BY fd.Name COLLATE NOCASE, fv.Ordinal, fv.Id;
            """,
            new { RecordId = Key(recordId) },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToArray();

        if (rows.Length == 0)
        {
            return [];
        }

        IEnumerable<TagRow> tags = await connection.QueryAsync<TagRow>(new CommandDefinition("""
            SELECT t.FieldValueId, t.Ordinal, t.Value
            FROM FieldValueTags t
            JOIN FieldValues fv ON fv.Id = t.FieldValueId
            WHERE fv.RecordId = @RecordId
            ORDER BY t.FieldValueId, t.Ordinal;
            """,
            new { RecordId = Key(recordId) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        Dictionary<string, string[]> tagLookup = tags
            .GroupBy(item => item.FieldValueId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Value).ToArray(), StringComparer.OrdinalIgnoreCase);

        return rows.Select(row => new RecordValue(
            ParseGuid(row.Id),
            ParseGuid(row.FieldDefinitionId),
            row.FieldName,
            row.TypeId,
            row.Ordinal,
            row.TextValue,
            row.NumberValue,
            row.NumberSortValue,
            row.DateValue,
            tagLookup.GetValueOrDefault(row.Id, []),
            row.TemporalValue,
            row.TemporalPrecision is null ? null : (TemporalPrecision?)row.TemporalPrecision,
            row.TemporalSortKey,
            row.IsApproximate != 0,
            row.ApproximationNote)).ToArray();
    }

    private static RecordType MapRecordType(RecordTypeRow row) =>
        new(ParseGuid(row.Id), row.Name, ParseTimestamp(row.CreatedAtUtc), ParseTimestamp(row.UpdatedAtUtc));

    private static FieldDefinition MapField(FieldRow row) =>
        new(
            ParseGuid(row.Id),
            row.Name,
            row.TypeId,
            row.ConfigurationJson,
            (FieldLifecycle)row.Lifecycle,
            ParseTimestamp(row.CreatedAtUtc),
            ParseTimestamp(row.UpdatedAtUtc));

    private static RecordSummary MapSummary(RecordSummaryRow row) =>
        new(ParseGuid(row.Id), ParseGuid(row.RecordTypeId), row.RecordTypeName, row.DisplayName, ParseTimestamp(row.UpdatedAtUtc));

    private static string Key(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    private static Guid ParseGuid(string value) => Guid.ParseExact(value, "D");

    private static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static void RequireChanged(int changed, string message)
    {
        if (changed != 1)
        {
            throw new DomainValidationException(message);
        }
    }

    private static bool IsUniqueConstraint(SqliteException exception) =>
        exception.SqliteErrorCode == 19 && exception.SqliteExtendedErrorCode == 2067;

    private sealed class RecordTypeRow
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string CreatedAtUtc { get; init; }
        public required string UpdatedAtUtc { get; init; }
    }

    private sealed class FieldRow
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string TypeId { get; init; }
        public required string ConfigurationJson { get; init; }
        public int Lifecycle { get; init; }
        public required string CreatedAtUtc { get; init; }
        public required string UpdatedAtUtc { get; init; }
        public int SortOrder { get; init; }
        public int IsRequired { get; init; }
    }

    private sealed class RecordSummaryRow
    {
        public required string Id { get; init; }
        public required string RecordTypeId { get; init; }
        public required string RecordTypeName { get; init; }
        public required string DisplayName { get; init; }
        public required string UpdatedAtUtc { get; init; }
    }

    private sealed class FieldValueRow
    {
        public required string Id { get; init; }
        public required string FieldDefinitionId { get; init; }
        public required string FieldName { get; init; }
        public required string TypeId { get; init; }
        public int Ordinal { get; init; }
        public string? TextValue { get; init; }
        public string? NumberValue { get; init; }
        public double? NumberSortValue { get; init; }
        public string? DateValue { get; init; }
        public string? TemporalValue { get; init; }
        public int? TemporalPrecision { get; init; }
        public string? TemporalSortKey { get; init; }
        public int IsApproximate { get; init; }
        public string? ApproximationNote { get; init; }
    }

    private sealed class TagRow
    {
        public required string FieldValueId { get; init; }
        public int Ordinal { get; init; }
        public required string Value { get; init; }
    }
}
