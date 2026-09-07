using Dapper;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

public sealed partial class SqliteMonkeysphereStore : IRelationshipCommandStore
{
    public Task<RecordCommandReceipt> CreateRelationshipTypeAsync(RecordCommandIdentity identity, RelationshipType type,
        DateTimeOffset now, CancellationToken cancellationToken = default) =>
        ExecuteEntityCommandAsync(identity, "relationship_types.create", now, async (connection, transaction) =>
        {
            RelationshipType created = await SqliteRelationshipStore.InsertTypeCoreAsync(connection, transaction, type, cancellationToken).ConfigureAwait(false);
            return [new(created.Id, created.Revision, "created")];
        }, cancellationToken);

    public Task<RecordCommandReceipt> CreateRelationshipAsync(RecordCommandIdentity identity, PreparedRelationship relationship,
        DateTimeOffset now, CancellationToken cancellationToken = default) =>
        ExecuteEntityCommandAsync(identity, "relationships.create", now, async (connection, transaction) =>
        {
            await RequireRelationshipRecordRevisionAsync(connection, transaction, relationship.SourceRecordId, relationship.SourceRevision, cancellationToken).ConfigureAwait(false);
            await RequireRelationshipRecordRevisionAsync(connection, transaction, relationship.TargetRecordId, relationship.TargetRevision, cancellationToken).ConfigureAwait(false);
            StoredRelationship created = await SqliteRelationshipStore.InsertCoreAsync(connection, transaction, relationship.Id,
                relationship.TypeId, relationship.SourceRecordId, relationship.TargetRecordId, relationship.Note, now,
                relationship.TypeRevision, cancellationToken).ConfigureAwait(false);
            return [new(created.Id, created.Revision, "created")];
        }, cancellationToken);

    public Task<RecordCommandReceipt> DeleteRelationshipAsync(RecordCommandIdentity identity, Guid id, string expectedRevision,
        DateTimeOffset now, CancellationToken cancellationToken = default) =>
        ExecuteEntityCommandAsync(identity, "relationships.delete", now, async (connection, transaction) =>
        {
            StoredRelationship current = await SqliteRelationshipStore.GetByIdAsync(connection, id, cancellationToken, transaction).ConfigureAwait(false)
                ?? throw new RecordCommandNotFoundException("Relationship was not found.");
            _ = await SqliteRelationshipStore.DeleteCoreAsync(connection, transaction, id, expectedRevision, cancellationToken).ConfigureAwait(false);
            return [new(id, current.Revision, "deleted")];
        }, cancellationToken);

    private static async Task RequireRelationshipRecordRevisionAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid id, string expectedRevision, CancellationToken cancellationToken)
    {
        string actual = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT Revision FROM Records WHERE Id = @Id;", new { Id = Key(id) }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false)
            ?? throw new RecordCommandNotFoundException("A referenced record was not found in this domain.");
        if (string.IsNullOrWhiteSpace(expectedRevision) || actual != expectedRevision)
            throw new ConcurrencyConflictException("A referenced record changed. Read both records before creating the relationship.");
    }
}
