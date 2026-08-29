using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

public sealed class SqliteSavedViewStore(MonkeysphereConnectionFactory connections) : ISavedViewStore
{
    public async Task<IReadOnlyList<SavedView>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<SavedViewRow> rows = await connection.QueryAsync<SavedViewRow>(new CommandDefinition("""
            SELECT Id, Name, RecordTypeId, Query, GroupByFieldDefinitionId, SortFieldDefinitionId,
                   SortDescending, CreatedAtUtc, UpdatedAtUtc
            FROM SavedViews
            ORDER BY Name COLLATE NOCASE, Id;
            """, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(Map).ToArray();
    }

    public async Task<SavedViewDetails?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SavedViewRow? row = await connection.QuerySingleOrDefaultAsync<SavedViewRow>(new CommandDefinition("""
            SELECT Id, Name, RecordTypeId, Query, GroupByFieldDefinitionId, SortFieldDefinitionId,
                   SortDescending, CreatedAtUtc, UpdatedAtUtc
            FROM SavedViews
            WHERE Id = @Id;
            """,
            new { Id = Key(id) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        string[] columns = (await connection.QueryAsync<string>(new CommandDefinition("""
            SELECT FieldDefinitionId
            FROM SavedViewColumns
            WHERE SavedViewId = @Id
            ORDER BY SortOrder;
            """,
            new { Id = Key(id) },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToArray();
        FilterRow[] filters = (await connection.QueryAsync<FilterRow>(new CommandDefinition("""
            SELECT FieldDefinitionId, Operator, Value
            FROM SavedViewFilters
            WHERE SavedViewId = @Id
            ORDER BY SortOrder;
            """,
            new { Id = Key(id) },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToArray();

        return new SavedViewDetails(
            Map(row),
            columns.Select(ParseGuid).ToArray(),
            filters.Select(filter => new RecordFilter(
                ParseGuid(filter.FieldDefinitionId),
                (FieldFilterOperator)filter.Operator,
                filter.Value)).ToArray());
    }

    public Task<SavedViewDetails> CreateAsync(
        Guid id,
        SaveViewRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        SaveAsync(id, request, now, isUpdate: false, cancellationToken);

    public Task<SavedViewDetails> UpdateAsync(
        Guid id,
        SaveViewRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        SaveAsync(id, request, now, isUpdate: true, cancellationToken);

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        int changed = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM SavedViews WHERE Id = @Id;",
            new { Id = Key(id) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return changed == 1;
    }

    private async Task<SavedViewDetails> SaveAsync(
        Guid id,
        SaveViewRequest request,
        DateTimeOffset now,
        bool isUpdate,
        CancellationToken cancellationToken)
    {
        string timestamp = Timestamp(now);
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (isUpdate)
            {
                int changed = await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE SavedViews
                    SET Name = @Name,
                        RecordTypeId = @RecordTypeId,
                        Query = @Query,
                        GroupByFieldDefinitionId = @GroupByFieldDefinitionId,
                        SortFieldDefinitionId = @SortFieldDefinitionId,
                        SortDescending = @SortDescending,
                        UpdatedAtUtc = @Now
                    WHERE Id = @Id;
                    """, Parameters(id, request, timestamp), transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
                if (changed != 1)
                {
                    throw new DomainValidationException("Saved view was not found.");
                }

                await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM SavedViewColumns WHERE SavedViewId = @Id; DELETE FROM SavedViewFilters WHERE SavedViewId = @Id;",
                    new { Id = Key(id) },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
            else
            {
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO SavedViews
                        (Id, Name, RecordTypeId, Query, GroupByFieldDefinitionId, SortFieldDefinitionId,
                         SortDescending, CreatedAtUtc, UpdatedAtUtc)
                    VALUES
                        (@Id, @Name, @RecordTypeId, @Query, @GroupByFieldDefinitionId, @SortFieldDefinitionId,
                         @SortDescending, @Now, @Now);
                    """, Parameters(id, request, timestamp), transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            for (int index = 0; index < request.ColumnFieldDefinitionIds.Count; index++)
            {
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO SavedViewColumns (SavedViewId, FieldDefinitionId, SortOrder)
                    VALUES (@SavedViewId, @FieldDefinitionId, @SortOrder);
                    """,
                    new
                    {
                        SavedViewId = Key(id),
                        FieldDefinitionId = Key(request.ColumnFieldDefinitionIds[index]),
                        SortOrder = index,
                    },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            for (int index = 0; index < request.Filters.Count; index++)
            {
                RecordFilter filter = request.Filters[index];
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO SavedViewFilters (SavedViewId, SortOrder, FieldDefinitionId, Operator, Value)
                    VALUES (@SavedViewId, @SortOrder, @FieldDefinitionId, @Operator, @Value);
                    """,
                    new
                    {
                        SavedViewId = Key(id),
                        SortOrder = index,
                        FieldDefinitionId = Key(filter.FieldDefinitionId),
                        Operator = (int)filter.Operator,
                        filter.Value,
                    },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new DomainValidationException(
                exception.SqliteExtendedErrorCode == 2067
                    ? "A saved view with that name already exists."
                    : "The saved view refers to a record type or field that is no longer available.",
                exception);
        }

        return await GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Saved view could not be read back.");
    }

    private static object Parameters(Guid id, SaveViewRequest request, string timestamp) => new
    {
        Id = Key(id),
        request.Name,
        RecordTypeId = Key(request.RecordTypeId),
        request.Query,
        GroupByFieldDefinitionId = request.GroupByFieldDefinitionId is Guid group ? Key(group) : null,
        SortFieldDefinitionId = request.SortFieldDefinitionId is Guid sort ? Key(sort) : null,
        SortDescending = request.SortDescending ? 1 : 0,
        Now = timestamp,
    };

    private static SavedView Map(SavedViewRow row) => new(
        ParseGuid(row.Id),
        row.Name,
        ParseGuid(row.RecordTypeId),
        row.Query,
        ParseNullableGuid(row.GroupByFieldDefinitionId),
        ParseNullableGuid(row.SortFieldDefinitionId),
        row.SortDescending != 0,
        ParseTimestamp(row.CreatedAtUtc),
        ParseTimestamp(row.UpdatedAtUtc));

    private static string Key(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);
    private static Guid ParseGuid(string value) => Guid.ParseExact(value, "D");
    private static Guid? ParseNullableGuid(string? value) => value is null ? null : ParseGuid(value);
    private static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed class SavedViewRow
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string RecordTypeId { get; init; }
        public string? Query { get; init; }
        public string? GroupByFieldDefinitionId { get; init; }
        public string? SortFieldDefinitionId { get; init; }
        public int SortDescending { get; init; }
        public required string CreatedAtUtc { get; init; }
        public required string UpdatedAtUtc { get; init; }
    }

    private sealed class FilterRow
    {
        public required string FieldDefinitionId { get; init; }
        public int Operator { get; init; }
        public required string Value { get; init; }
    }
}
