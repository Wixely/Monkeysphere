using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

public sealed class SqliteGraphViewStore(MonkeysphereConnectionFactory connections) : IGraphViewStore
{
    public async Task<IReadOnlyList<GraphView>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        GraphViewRow[] rows = (await connection.QueryAsync<GraphViewRow>(new CommandDefinition("""
            SELECT Id, Name, DisplayMode, CreatedAtUtc, UpdatedAtUtc
            FROM GraphViews
            ORDER BY Name COLLATE NOCASE, Id;
            """, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToArray();
        List<GraphView> result = [];
        foreach (GraphViewRow row in rows)
        {
            result.Add(await MapAsync(connection, row, cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    public async Task<GraphView?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        GraphViewRow? row = await connection.QuerySingleOrDefaultAsync<GraphViewRow>(new CommandDefinition("""
            SELECT Id, Name, DisplayMode, CreatedAtUtc, UpdatedAtUtc
            FROM GraphViews
            WHERE Id = @Id;
            """, new { Id = Key(id) }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null ? null : await MapAsync(connection, row, cancellationToken).ConfigureAwait(false);
    }

    public Task<GraphView> CreateAsync(
        Guid id,
        SaveGraphViewRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        SaveAsync(id, request, now, isUpdate: false, cancellationToken);

    public Task<GraphView> UpdateAsync(
        Guid id,
        SaveGraphViewRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        SaveAsync(id, request, now, isUpdate: true, cancellationToken);

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        int changed = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM GraphViews WHERE Id = @Id;",
            new { Id = Key(id) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return changed == 1;
    }

    private async Task<GraphView> SaveAsync(
        Guid id,
        SaveGraphViewRequest request,
        DateTimeOffset now,
        bool isUpdate,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            object parameters = new
            {
                Id = Key(id),
                request.Name,
                DisplayMode = (int)request.DisplayMode,
                Now = Timestamp(now),
            };
            if (isUpdate)
            {
                int changed = await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE GraphViews
                    SET Name = @Name, DisplayMode = @DisplayMode, UpdatedAtUtc = @Now
                    WHERE Id = @Id;
                    """, parameters, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
                if (changed != 1)
                {
                    throw new DomainValidationException("Graph view was not found.");
                }

                await connection.ExecuteAsync(new CommandDefinition("""
                    DELETE FROM GraphViewRecords WHERE GraphViewId = @Id;
                    DELETE FROM GraphViewRecordTypes WHERE GraphViewId = @Id;
                    DELETE FROM GraphViewNodePositions WHERE GraphViewId = @Id;
                    """, new { Id = Key(id) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
            else
            {
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO GraphViews (Id, Name, DisplayMode, CreatedAtUtc, UpdatedAtUtc)
                    VALUES (@Id, @Name, @DisplayMode, @Now, @Now);
                    """, parameters, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            for (int index = 0; index < request.RecordIds.Count; index++)
            {
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO GraphViewRecords (GraphViewId, RecordId, SortOrder)
                    VALUES (@GraphViewId, @RecordId, @SortOrder);
                    """, new { GraphViewId = Key(id), RecordId = Key(request.RecordIds[index]), SortOrder = index }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            for (int index = 0; index < request.RecordTypeIds.Count; index++)
            {
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO GraphViewRecordTypes (GraphViewId, RecordTypeId, SortOrder)
                    VALUES (@GraphViewId, @RecordTypeId, @SortOrder);
                    """, new { GraphViewId = Key(id), RecordTypeId = Key(request.RecordTypeIds[index]), SortOrder = index }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            foreach (GraphViewNodePosition position in request.NodePositions ?? [])
            {
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO GraphViewNodePositions (GraphViewId, RecordId, X, Y)
                    VALUES (@GraphViewId, @RecordId, @X, @Y);
                    """, new { GraphViewId = Key(id), RecordId = Key(position.RecordId), position.X, position.Y }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new DomainValidationException(
                exception.SqliteExtendedErrorCode == 2067
                    ? "A saved view with that name already exists."
                    : "The graph view refers to a record or record type that is no longer available.",
                exception);
        }

        return await GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Graph view could not be read back.");
    }

    private static async Task<GraphView> MapAsync(
        SqliteConnection connection,
        GraphViewRow row,
        CancellationToken cancellationToken)
    {
        string[] recordIds = (await connection.QueryAsync<string>(new CommandDefinition("""
            SELECT RecordId FROM GraphViewRecords
            WHERE GraphViewId = @Id ORDER BY SortOrder;
            """, new { row.Id }, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToArray();
        string[] recordTypeIds = (await connection.QueryAsync<string>(new CommandDefinition("""
            SELECT RecordTypeId FROM GraphViewRecordTypes
            WHERE GraphViewId = @Id ORDER BY SortOrder;
            """, new { row.Id }, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToArray();
        GraphViewPositionRow[] positions = (await connection.QueryAsync<GraphViewPositionRow>(new CommandDefinition("""
            SELECT RecordId, X, Y FROM GraphViewNodePositions
            WHERE GraphViewId = @Id ORDER BY RecordId;
            """, new { row.Id }, cancellationToken: cancellationToken)).ConfigureAwait(false)).ToArray();
        return new(
            ParseGuid(row.Id),
            row.Name,
            (RelationshipGraphDisplayMode)row.DisplayMode,
            recordIds.Select(ParseGuid).ToArray(),
            recordTypeIds.Select(ParseGuid).ToArray(),
            positions.Select(position => new GraphViewNodePosition(ParseGuid(position.RecordId), position.X, position.Y)).ToArray(),
            ParseTimestamp(row.CreatedAtUtc),
            ParseTimestamp(row.UpdatedAtUtc));
    }

    private static string Key(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);
    private static Guid ParseGuid(string value) => Guid.ParseExact(value, "D");
    private static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed class GraphViewRow
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public int DisplayMode { get; init; }
        public required string CreatedAtUtc { get; init; }
        public required string UpdatedAtUtc { get; init; }
    }

    private sealed class GraphViewPositionRow
    {
        public required string RecordId { get; init; }
        public double X { get; init; }
        public double Y { get; init; }
    }
}
