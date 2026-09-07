using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

public sealed class SqliteRelationshipStore(MonkeysphereConnectionFactory connections) : IRelationshipStore
{
    public async Task<IReadOnlyList<RelationshipType>> ListTypesAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<RelationshipTypeRow> rows = await connection.QueryAsync<RelationshipTypeRow>(new CommandDefinition("""
            SELECT Id, Name, Directionality, InverseName, Lifecycle, CreatedAtUtc, UpdatedAtUtc, PresetKey, PresetVersion, Revision
            FROM RelationshipTypes
            ORDER BY Lifecycle, Name COLLATE NOCASE, Id;
            """, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(MapType).ToArray();
    }

    public async Task<RelationshipType?> GetTypeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        RelationshipTypeRow? row = await connection.QuerySingleOrDefaultAsync<RelationshipTypeRow>(new CommandDefinition("""
            SELECT Id, Name, Directionality, InverseName, Lifecycle, CreatedAtUtc, UpdatedAtUtc, PresetKey, PresetVersion, Revision
            FROM RelationshipTypes WHERE Id = @Id;
            """, new { Id = Key(id) }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null ? null : MapType(row);
    }

    public async Task<RelationshipType> CreateTypeAsync(RelationshipType type, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction();
        RelationshipType created = await InsertTypeCoreAsync(connection, transaction, type, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return created;
    }

    internal static async Task<RelationshipType> InsertTypeCoreAsync(SqliteConnection connection, SqliteTransaction transaction,
        RelationshipType type, CancellationToken cancellationToken)
    {
        try
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO RelationshipTypes
                    (Id, Name, Directionality, InverseName, Lifecycle, CreatedAtUtc, UpdatedAtUtc)
                VALUES (@Id, @Name, @Directionality, @InverseName, @Lifecycle, @CreatedAtUtc, @UpdatedAtUtc);
                """, new
            {
                Id = Key(type.Id),
                type.Name,
                Directionality = (int)type.Directionality,
                type.InverseName,
                Lifecycle = (int)type.Lifecycle,
                CreatedAtUtc = Timestamp(type.CreatedAtUtc),
                UpdatedAtUtc = Timestamp(type.UpdatedAtUtc),
            }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (IsConstraint(exception))
        {
            throw new DomainValidationException("A relationship type with that label already exists.", exception);
        }

        string revision = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT Revision FROM RelationshipTypes WHERE Id = @Id;", new { Id = Key(type.Id) }, transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false) ?? throw new InvalidOperationException("Created relationship type could not be read back.");
        return type with { Revision = revision };
    }

    public async Task RenameTypeAsync(Guid id, string name, string? inverseName, DateTimeOffset now, string? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        int changed;
        try
        {
            changed = await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE RelationshipTypes
                SET Name = @Name, InverseName = @InverseName, UpdatedAtUtc = @Now
                WHERE Id = @Id AND (@ExpectedRevision IS NULL OR Revision = @ExpectedRevision);
                """, new { Id = Key(id), Name = name, InverseName = inverseName, Now = Timestamp(now), ExpectedRevision = expectedRevision }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (IsConstraint(exception))
        {
            throw new DomainValidationException("A relationship type with that label already exists.", exception);
        }

        RequireChanged(changed, "Relationship type was not found.", expectedRevision);
    }

    public async Task RetireTypeAsync(Guid id, DateTimeOffset now, string? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        int changed = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE RelationshipTypes SET Lifecycle = 1, UpdatedAtUtc = @Now
            WHERE Id = @Id AND Lifecycle = 0 AND (@ExpectedRevision IS NULL OR Revision = @ExpectedRevision);
            """, new { Id = Key(id), Now = Timestamp(now), ExpectedRevision = expectedRevision }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        RequireChanged(changed, "Active relationship type was not found.", expectedRevision);
    }

    public async Task<StoredRelationship> CreateAsync(
        Guid id,
        Guid typeId,
        Guid sourceRecordId,
        Guid targetRecordId,
        string? note,
        DateTimeOffset now,
        string? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction();
        StoredRelationship created = await InsertCoreAsync(connection, transaction, id, typeId, sourceRecordId, targetRecordId,
            note, now, expectedRevision, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return created;
    }

    internal static async Task<StoredRelationship> InsertCoreAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid id, Guid typeId, Guid sourceRecordId, Guid targetRecordId, string? note, DateTimeOffset now,
        string? expectedRevision, CancellationToken cancellationToken)
    {
        RelationshipTypeRow? typeRow = await connection.QuerySingleOrDefaultAsync<RelationshipTypeRow>(new CommandDefinition("""
            SELECT Id, Name, Directionality, InverseName, Lifecycle, CreatedAtUtc, UpdatedAtUtc, PresetKey, PresetVersion, Revision
            FROM RelationshipTypes WHERE Id = @Id;
            """, new { Id = Key(typeId) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (typeRow is null)
        {
            throw new RecordCommandNotFoundException("Relationship type was not found.");
        }

        RelationshipType type = MapType(typeRow);
        if (expectedRevision is not null && expectedRevision != type.Revision)
            throw new ConcurrencyConflictException("The relationship type changed. Reload it before creating the relationship.");
        if (type.Lifecycle != RelationshipLifecycle.Active) throw new DomainValidationException("The relationship type is retired.");
        Guid source = sourceRecordId;
        Guid target = targetRecordId;
        if (type.Directionality == RelationshipDirectionality.Symmetric && source.CompareTo(target) > 0)
        {
            (source, target) = (target, source);
        }

        string timestamp = Timestamp(now);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO Relationships
                    (Id, RelationshipTypeId, SourceRecordId, TargetRecordId, Note, CreatedAtUtc, UpdatedAtUtc)
                VALUES (@Id, @TypeId, @SourceId, @TargetId, @Note, @Now, @Now);
                """, new
            {
                Id = Key(id),
                TypeId = Key(typeId),
                SourceId = Key(source),
                TargetId = Key(target),
                Note = note,
                Now = timestamp,
            }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (IsConstraint(exception))
        {
            throw new DomainValidationException("The relationship is invalid, duplicates an existing relationship, or refers to a missing record.", exception);
        }

        return await GetByIdAsync(connection, id, cancellationToken, transaction).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Created relationship could not be read back.");
    }

    public async Task<IReadOnlyList<StoredRelationship>> ListForRecordAsync(Guid recordId, int limit, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<RelationshipRow> rows = await connection.QueryAsync<RelationshipRow>(new CommandDefinition(RelationshipSelect + """
             WHERE r.SourceRecordId = @RecordId OR r.TargetRecordId = @RecordId
            ORDER BY rt.Name COLLATE NOCASE, source.DisplayName COLLATE NOCASE, target.DisplayName COLLATE NOCASE, r.Id
            LIMIT @Limit;
            """, new { RecordId = Key(recordId), Limit = limit }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(MapRelationship).ToArray();
    }

    public async Task<PagedResult<StoredRelationship>> QueryForRecordAsync(Guid recordId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        DiscoveryPagination.Validate(page, pageSize);
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        var parameters = new { RecordId = Key(recordId), Limit = pageSize, Offset = (page - 1) * pageSize };
        int total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM Relationships WHERE SourceRecordId = @RecordId OR TargetRecordId = @RecordId;",
            parameters, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        IEnumerable<RelationshipRow> rows = await connection.QueryAsync<RelationshipRow>(new CommandDefinition(RelationshipSelect + """
             WHERE r.SourceRecordId = @RecordId OR r.TargetRecordId = @RecordId
            ORDER BY rt.Name COLLATE NOCASE, source.DisplayName COLLATE NOCASE, target.DisplayName COLLATE NOCASE, r.Id
            LIMIT @Limit OFFSET @Offset;
            """, parameters, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return new(rows.Select(MapRelationship).ToArray(), page, pageSize, total);
    }

    public async Task<bool> DeleteAsync(Guid id, string? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await DeleteCoreAsync(connection, null, id, expectedRevision, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<bool> DeleteCoreAsync(SqliteConnection connection, SqliteTransaction? transaction,
        Guid id, string? expectedRevision, CancellationToken cancellationToken)
    {
        int changed = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM Relationships WHERE Id = @Id AND (@ExpectedRevision IS NULL OR Revision = @ExpectedRevision);",
            new { Id = Key(id), ExpectedRevision = expectedRevision },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (expectedRevision is not null) RequireChanged(changed, "Relationship was not found.", expectedRevision);
        return changed == 1;
    }

    internal static async Task<StoredRelationship?> GetByIdAsync(SqliteConnection connection, Guid id, CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        RelationshipRow? row = await connection.QuerySingleOrDefaultAsync<RelationshipRow>(new CommandDefinition(
            RelationshipSelect + " WHERE r.Id = @Id;",
            new { Id = Key(id) },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null ? null : MapRelationship(row);
    }

    private const string RelationshipSelect = """
        SELECT r.Id, r.RelationshipTypeId, rt.Name AS TypeName, rt.Directionality, rt.InverseName,
               rt.Lifecycle AS TypeLifecycle, rt.CreatedAtUtc AS TypeCreatedAtUtc, rt.UpdatedAtUtc AS TypeUpdatedAtUtc,
               rt.PresetKey AS TypePresetKey, rt.PresetVersion AS TypePresetVersion, rt.Revision AS TypeRevision,
               r.SourceRecordId, source.DisplayName AS SourceDisplayName,
               r.TargetRecordId, target.DisplayName AS TargetDisplayName,
               r.Note, r.CreatedAtUtc, r.UpdatedAtUtc, r.Revision
        FROM Relationships r
        JOIN RelationshipTypes rt ON rt.Id = r.RelationshipTypeId
        JOIN Records source ON source.Id = r.SourceRecordId
        JOIN Records target ON target.Id = r.TargetRecordId
        """;

    private static RelationshipType MapType(RelationshipTypeRow row) => new(
        Guid.Parse(row.Id), row.Name, (RelationshipDirectionality)row.Directionality, row.InverseName,
        (RelationshipLifecycle)row.Lifecycle, ParseTimestamp(row.CreatedAtUtc), ParseTimestamp(row.UpdatedAtUtc), row.PresetKey, row.PresetVersion, row.Revision);

    private static StoredRelationship MapRelationship(RelationshipRow row) => new(
        Guid.Parse(row.Id),
        new RelationshipType(
            Guid.Parse(row.RelationshipTypeId), row.TypeName, (RelationshipDirectionality)row.Directionality, row.InverseName,
            (RelationshipLifecycle)row.TypeLifecycle, ParseTimestamp(row.TypeCreatedAtUtc), ParseTimestamp(row.TypeUpdatedAtUtc),
            row.TypePresetKey, row.TypePresetVersion, row.TypeRevision),
        Guid.Parse(row.SourceRecordId), row.SourceDisplayName,
        Guid.Parse(row.TargetRecordId), row.TargetDisplayName,
        row.Note, ParseTimestamp(row.CreatedAtUtc), ParseTimestamp(row.UpdatedAtUtc), row.Revision);

    private static string Key(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);
    private static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static bool IsConstraint(SqliteException exception) => exception.SqliteErrorCode == 19;
    private static void RequireChanged(int changed, string message, string? expectedRevision = null)
    {
        if (changed != 1)
        {
            if (expectedRevision is not null) throw new ConcurrencyConflictException("The relationship or type changed or was deleted. Reload it before retrying.");
            throw new DomainValidationException(message);
        }
    }

    private sealed class RelationshipTypeRow
    {
        public required string Revision { get; init; }
        public required string Id { get; init; }
        public required string Name { get; init; }
        public int Directionality { get; init; }
        public string? InverseName { get; init; }
        public int Lifecycle { get; init; }
        public required string CreatedAtUtc { get; init; }
        public required string UpdatedAtUtc { get; init; }
        public string? PresetKey { get; init; }
        public int? PresetVersion { get; init; }
    }

    private sealed class RelationshipRow
    {
        public required string Revision { get; init; }
        public required string Id { get; init; }
        public required string TypeRevision { get; init; }
        public required string RelationshipTypeId { get; init; }
        public required string TypeName { get; init; }
        public int Directionality { get; init; }
        public string? InverseName { get; init; }
        public int TypeLifecycle { get; init; }
        public required string TypeCreatedAtUtc { get; init; }
        public required string TypeUpdatedAtUtc { get; init; }
        public string? TypePresetKey { get; init; }
        public int? TypePresetVersion { get; init; }
        public required string SourceRecordId { get; init; }
        public required string SourceDisplayName { get; init; }
        public required string TargetRecordId { get; init; }
        public required string TargetDisplayName { get; init; }
        public string? Note { get; init; }
        public required string CreatedAtUtc { get; init; }
        public required string UpdatedAtUtc { get; init; }
    }
}
