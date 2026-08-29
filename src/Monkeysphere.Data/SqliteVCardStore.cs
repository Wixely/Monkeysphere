using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

public sealed class SqliteVCardStore(
    MonkeysphereConnectionFactory connections,
    IMonkeysphereStore records) : IVCardStore
{
    private const string PersonPresetKey = "monkeysphere.person";

    public async Task<IReadOnlyList<VCardExistingContact>> ListExistingAsync(
        Guid recordTypeId,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        ExistingRow[] contacts = (await connection.QueryAsync<ExistingRow>(new CommandDefinition("""
            SELECT Id, DisplayName FROM Records WHERE RecordTypeId = @RecordTypeId ORDER BY Id;
            """, new { RecordTypeId = Key(recordTypeId) }, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToArray();
        if (contacts.Length == 0)
        {
            return [];
        }

        string[] ids = contacts.Select(contact => contact.Id).ToArray();
        AliasRow[] aliases = (await connection.QueryAsync<AliasRow>(new CommandDefinition("""
            SELECT RecordId, Value FROM RecordAliases WHERE RecordId IN @Ids ORDER BY RecordId, Ordinal;
            """, new { Ids = ids }, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToArray();
        ExistingValueRow[] values = (await connection.QueryAsync<ExistingValueRow>(new CommandDefinition("""
            SELECT fv.RecordId, fd.CanonicalKey,
                   COALESCE(fv.TextValue, fv.DateValue, fv.TemporalValue) AS Value
            FROM FieldValues fv
            INNER JOIN FieldDefinitions fd ON fd.Id = fv.FieldDefinitionId
            WHERE fv.RecordId IN @Ids AND fd.CanonicalKey IS NOT NULL
            ORDER BY fv.RecordId, fd.CanonicalKey, fv.Ordinal;
            """, new { Ids = ids }, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToArray();
        ImportRow[] fingerprints = (await connection.QueryAsync<ImportRow>(new CommandDefinition("""
            SELECT RecordId, Fingerprint FROM VCardImports WHERE RecordId IN @Ids ORDER BY RecordId, Fingerprint;
            """, new { Ids = ids }, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToArray();

        return contacts.Select(contact => new VCardExistingContact(
            Parse(contact.Id),
            contact.DisplayName,
            aliases.Where(alias => alias.RecordId == contact.Id).Select(alias => alias.Value).ToArray(),
            values.Where(value => value.RecordId == contact.Id)
                .GroupBy(value => value.CanonicalKey, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<string>)group.Select(value => value.Value).ToArray(),
                    StringComparer.Ordinal),
            fingerprints.Where(item => item.RecordId == contact.Id)
                .Select(item => item.Fingerprint)
                .ToHashSet(StringComparer.Ordinal))).ToArray();
    }

    public async Task<VCardImportResult> ApplyAsync(
        IReadOnlyList<VCardPreparedImport> contacts,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        string timestamp = Timestamp(now);
        int created = 0;
        int merged = 0;
        int replaced = 0;
        int skipped = 0;
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (VCardPreparedImport contact in contacts)
        {
            if (contact.Action == VCardImportAction.Skip)
            {
                skipped++;
                continue;
            }

            Guid recordId;
            HashSet<Guid> effectiveMappedFields = [];
            switch (contact.Action)
            {
                case VCardImportAction.CreateSeparately:
                    recordId = Guid.CreateVersion7();
                    await InsertRecordAsync(connection, transaction, recordId, contact.Record, timestamp, cancellationToken).ConfigureAwait(false);
                    effectiveMappedFields.UnionWith(contact.Record.Values.Select(value => value.FieldDefinitionId));
                    created++;
                    break;
                case VCardImportAction.MergeNonConflicting:
                    recordId = RequireTarget(contact);
                    await RequirePersonRecordAsync(connection, transaction, recordId, contact.Record.RecordTypeId, cancellationToken).ConfigureAwait(false);
                    await MergeAliasesAsync(connection, transaction, recordId, contact.Record, cancellationToken).ConfigureAwait(false);
                    foreach (NormalizedFieldValue value in contact.Record.Values)
                    {
                        StoredScalar? current = await ReadScalarAsync(connection, transaction, recordId, value.FieldDefinitionId, cancellationToken).ConfigureAwait(false);
                        if (current is null)
                        {
                            await InsertValueAsync(connection, transaction, recordId, value, timestamp, cancellationToken).ConfigureAwait(false);
                            effectiveMappedFields.Add(value.FieldDefinitionId);
                        }
                        else if (string.Equals(current.Value, Scalar(value), StringComparison.OrdinalIgnoreCase))
                        {
                            effectiveMappedFields.Add(value.FieldDefinitionId);
                        }
                    }

                    await TouchAsync(connection, transaction, recordId, timestamp, cancellationToken).ConfigureAwait(false);
                    merged++;
                    break;
                case VCardImportAction.ReplaceMappedValues:
                    recordId = RequireTarget(contact);
                    await RequirePersonRecordAsync(connection, transaction, recordId, contact.Record.RecordTypeId, cancellationToken).ConfigureAwait(false);
                    await connection.ExecuteAsync(new CommandDefinition("""
                        UPDATE Records SET DisplayName = @DisplayName, UpdatedAtUtc = @Now WHERE Id = @RecordId;
                        DELETE FROM RecordAliases WHERE RecordId = @RecordId;
                        DELETE FROM VCardProperties WHERE RecordId = @RecordId AND MappingKind <> 0;
                        """, new
                    {
                        RecordId = Key(recordId),
                        contact.Record.DisplayName,
                        Now = timestamp,
                    }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
                    await InsertAliasesAsync(connection, transaction, recordId, contact.Record.Aliases, cancellationToken).ConfigureAwait(false);
                    foreach (NormalizedFieldValue value in contact.Record.Values)
                    {
                        await connection.ExecuteAsync(new CommandDefinition("""
                            DELETE FROM FieldValues WHERE RecordId = @RecordId AND FieldDefinitionId = @FieldDefinitionId;
                            """, new { RecordId = Key(recordId), FieldDefinitionId = Key(value.FieldDefinitionId) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
                        await InsertValueAsync(connection, transaction, recordId, value, timestamp, cancellationToken).ConfigureAwait(false);
                        effectiveMappedFields.Add(value.FieldDefinitionId);
                    }

                    replaced++;
                    break;
                default:
                    throw new DomainValidationException("Unsupported vCard import action.");
            }

            await StoreProvenanceAsync(
                connection,
                transaction,
                recordId,
                contact,
                effectiveMappedFields,
                timestamp,
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(created, merged, replaced, skipped);
    }

    public async Task<IReadOnlyList<VCardExportRecord>> ReadExportAsync(
        IReadOnlyList<Guid> recordIds,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        string[] ids = recordIds.Select(Key).ToArray();
        HashSet<string> people = (await connection.QueryAsync<string>(new CommandDefinition("""
            SELECT r.Id
            FROM Records r
            INNER JOIN RecordTypes rt ON rt.Id = r.RecordTypeId
            WHERE r.Id IN @Ids AND rt.PresetKey = @PresetKey;
            """, new { Ids = ids, PresetKey = PersonPresetKey }, cancellationToken: cancellationToken)).ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);
        PropertyRow[] propertyRows = (await connection.QueryAsync<PropertyRow>(new CommandDefinition("""
            SELECT RecordId, Ordinal, GroupName, PropertyName, ParametersJson, RawValue,
                   MappingKind, FieldDefinitionId, ValueOrdinal
            FROM VCardProperties WHERE RecordId IN @Ids ORDER BY RecordId, Ordinal;
            """, new { Ids = ids }, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToArray();

        List<VCardExportRecord> result = [];
        foreach (Guid id in recordIds)
        {
            if (!people.Contains(Key(id)))
            {
                continue;
            }

            RecordDetails? record = await records.GetRecordAsync(id, cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                continue;
            }

            VCardStoredProperty[] properties = propertyRows.Where(row => row.RecordId == Key(id)).Select(MapProperty).ToArray();
            result.Add(new(record, properties));
        }

        return result;
    }

    private static async Task InsertRecordAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid recordId,
        PreparedRecord record,
        string timestamp,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO Records (Id, RecordTypeId, DisplayName, CreatedAtUtc, UpdatedAtUtc)
            VALUES (@Id, @RecordTypeId, @DisplayName, @Now, @Now);
            """, new
        {
            Id = Key(recordId),
            RecordTypeId = Key(record.RecordTypeId),
            record.DisplayName,
            Now = timestamp,
        }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await InsertAliasesAsync(connection, transaction, recordId, record.Aliases, cancellationToken).ConfigureAwait(false);
        foreach (NormalizedFieldValue value in record.Values)
        {
            await InsertValueAsync(connection, transaction, recordId, value, timestamp, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task InsertAliasesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid recordId,
        IReadOnlyList<string> aliases,
        CancellationToken cancellationToken)
    {
        for (int ordinal = 0; ordinal < aliases.Count; ordinal++)
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO RecordAliases (RecordId, Ordinal, Value) VALUES (@RecordId, @Ordinal, @Value);
                """, new { RecordId = Key(recordId), Ordinal = ordinal, Value = aliases[ordinal] }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }

    private static async Task MergeAliasesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid recordId,
        PreparedRecord imported,
        CancellationToken cancellationToken)
    {
        string[] existing = (await connection.QueryAsync<string>(new CommandDefinition("""
            SELECT Value FROM RecordAliases WHERE RecordId = @RecordId ORDER BY Ordinal;
            """, new { RecordId = Key(recordId) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToArray();
        string[] merged = existing.Concat(imported.Aliases).Append(imported.DisplayName)
            .Where(alias => alias.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();
        string displayName = await connection.QuerySingleAsync<string>(new CommandDefinition(
            "SELECT DisplayName FROM Records WHERE Id = @RecordId;",
            new { RecordId = Key(recordId) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        merged = merged.Where(alias => !string.Equals(alias, displayName, StringComparison.OrdinalIgnoreCase)).ToArray();
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM RecordAliases WHERE RecordId = @RecordId;",
            new { RecordId = Key(recordId) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await InsertAliasesAsync(connection, transaction, recordId, merged, cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertValueAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid recordId,
        NormalizedFieldValue value,
        string timestamp,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO FieldValues
                (Id, RecordId, FieldDefinitionId, Ordinal, TextValue, NumberValue, NumberSortValue, DateValue,
                 TemporalValue, TemporalPrecision, TemporalSortKey, IsApproximate, ApproximationNote, CreatedAtUtc, UpdatedAtUtc)
            VALUES
                (@Id, @RecordId, @FieldDefinitionId, 0, @TextValue, @NumberValue, @NumberSortValue, @DateValue,
                 @TemporalValue, @TemporalPrecision, @TemporalSortKey, @IsApproximate, @ApproximationNote, @Now, @Now);
            """, new
        {
            Id = Key(value.Id),
            RecordId = Key(recordId),
            FieldDefinitionId = Key(value.FieldDefinitionId),
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
        }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static async Task StoreProvenanceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid recordId,
        VCardPreparedImport imported,
        HashSet<Guid> effectiveMappedFields,
        string timestamp,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT OR IGNORE INTO VCardImports (Fingerprint, RecordId, SourceVersion, ImportedAtUtc)
            VALUES (@Fingerprint, @RecordId, @SourceVersion, @Now);
            """, new
        {
            imported.Preview.Card.Fingerprint,
            RecordId = Key(recordId),
            SourceVersion = imported.Preview.Card.Version,
            Now = timestamp,
        }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        int nextOrdinal = await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COALESCE(MAX(Ordinal) + 1, 0) FROM VCardProperties WHERE RecordId = @RecordId;
            """, new { RecordId = Key(recordId) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        Dictionary<int, VCardFieldMapping> fieldMappings = imported.Preview.FieldMappings.ToDictionary(mapping => mapping.PropertyIndex);
        for (int index = 0; index < imported.Preview.Card.Properties.Count; index++)
        {
            VCardProperty property = imported.Preview.Card.Properties[index];
            if (property.Name == "VERSION")
            {
                continue;
            }

            VCardPropertyMappingKind kind = property.Name switch
            {
                _ when imported.Preview.OpaquePropertyIndexes.Contains(index) => VCardPropertyMappingKind.Opaque,
                "FN" => VCardPropertyMappingKind.DisplayName,
                "NICKNAME" => VCardPropertyMappingKind.Aliases,
                _ when fieldMappings.TryGetValue(index, out VCardFieldMapping? mapping) &&
                       effectiveMappedFields.Contains(mapping.FieldDefinitionId) => VCardPropertyMappingKind.FieldValue,
                _ => VCardPropertyMappingKind.Opaque,
            };
            VCardFieldMapping? fieldMapping = kind == VCardPropertyMappingKind.FieldValue ? fieldMappings[index] : null;
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO VCardProperties
                    (RecordId, Ordinal, GroupName, PropertyName, ParametersJson, RawValue,
                     MappingKind, FieldDefinitionId, ValueOrdinal)
                VALUES
                    (@RecordId, @Ordinal, @GroupName, @PropertyName, @ParametersJson, @RawValue,
                     @MappingKind, @FieldDefinitionId, @ValueOrdinal);
                """, new
            {
                RecordId = Key(recordId),
                Ordinal = nextOrdinal++,
                GroupName = property.Group,
                PropertyName = property.Name,
                ParametersJson = JsonSerializer.Serialize(property.Parameters),
                RawValue = property.Value,
                MappingKind = (int)kind,
                FieldDefinitionId = fieldMapping is null ? null : Key(fieldMapping.FieldDefinitionId),
                ValueOrdinal = fieldMapping is null ? null : (int?)0,
            }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }

    private static async Task<StoredScalar?> ReadScalarAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid recordId,
        Guid fieldDefinitionId,
        CancellationToken cancellationToken) =>
        await connection.QuerySingleOrDefaultAsync<StoredScalar>(new CommandDefinition("""
            SELECT COALESCE(TextValue, DateValue, TemporalValue) AS Value
            FROM FieldValues WHERE RecordId = @RecordId AND FieldDefinitionId = @FieldDefinitionId AND Ordinal = 0;
            """, new { RecordId = Key(recordId), FieldDefinitionId = Key(fieldDefinitionId) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

    private static async Task RequirePersonRecordAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid recordId,
        Guid recordTypeId,
        CancellationToken cancellationToken)
    {
        int exists = await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT COUNT(*) FROM Records WHERE Id = @RecordId AND RecordTypeId = @RecordTypeId;
            """, new { RecordId = Key(recordId), RecordTypeId = Key(recordTypeId) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (exists != 1)
        {
            throw new DomainValidationException("The selected duplicate changed or no longer belongs to the Person record type.");
        }
    }

    private static Task<int> TouchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid recordId,
        string timestamp,
        CancellationToken cancellationToken) => connection.ExecuteAsync(new CommandDefinition(
            "UPDATE Records SET UpdatedAtUtc = @Now WHERE Id = @RecordId;",
            new { RecordId = Key(recordId), Now = timestamp }, transaction, cancellationToken: cancellationToken));

    private static Guid RequireTarget(VCardPreparedImport imported) => imported.ExistingRecordId
        ?? throw new DomainValidationException("The selected import action requires an existing record.");

    private static string? Scalar(NormalizedFieldValue value) =>
        value.TextValue ?? value.DateValue ?? value.Temporal?.Value;

    private static VCardStoredProperty MapProperty(PropertyRow row) => new(
        row.Ordinal,
        new VCardProperty(
            row.GroupName,
            row.PropertyName,
            JsonSerializer.Deserialize<VCardParameter[]>(row.ParametersJson) ?? [],
            row.RawValue),
        (VCardPropertyMappingKind)row.MappingKind,
        row.FieldDefinitionId is null ? null : Parse(row.FieldDefinitionId),
        row.ValueOrdinal);

    private static string Key(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);
    private static Guid Parse(string value) => Guid.ParseExact(value, "D");
    private static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private sealed record StoredScalar(string Value);
    private sealed record ExistingRow(string Id, string DisplayName);
    private sealed record AliasRow(string RecordId, string Value);
    private sealed record ExistingValueRow(string RecordId, string CanonicalKey, string Value);
    private sealed record ImportRow(string RecordId, string Fingerprint);

    private sealed class PropertyRow
    {
        public required string RecordId { get; init; }
        public int Ordinal { get; init; }
        public string? GroupName { get; init; }
        public required string PropertyName { get; init; }
        public required string ParametersJson { get; init; }
        public required string RawValue { get; init; }
        public int MappingKind { get; init; }
        public string? FieldDefinitionId { get; init; }
        public int? ValueOrdinal { get; init; }
    }
}
