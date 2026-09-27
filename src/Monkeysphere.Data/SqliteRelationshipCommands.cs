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

    public Task<RecordCommandReceipt> RenameRelationshipTypeAsync(RecordCommandIdentity identity, Guid id, string expectedRevision,
        string name, string? inverseName, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        ExecuteEntityCommandAsync(identity, "relationship_types.rename", now, async (connection, transaction) =>
        {
            RelationshipType current = await RequireRelationshipTypeAsync(connection, transaction, id, expectedRevision, requireActive: false, cancellationToken).ConfigureAwait(false);

            // Normalized against the directionality the type actually has, read here rather than sent:
            // a caller offering an inverse label for a symmetric type is told so instead of having it
            // quietly stored where nothing will ever read it.
            (string label, string? inverse) = RelationshipService.NormalizeLabels(name, inverseName, current.Directionality);
            await SqliteRelationshipStore.RenameTypeCoreAsync(connection, transaction, id, label, inverse, now, expectedRevision, cancellationToken).ConfigureAwait(false);
            RelationshipType renamed = (await SqliteRelationshipStore.QueryTypeCoreAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false))!;
            return [new(renamed.Id, renamed.Revision, "updated")];
        }, cancellationToken);

    public Task<RecordCommandReceipt> RetireRelationshipTypeAsync(RecordCommandIdentity identity, Guid id, string expectedRevision,
        DateTimeOffset now, CancellationToken cancellationToken = default) =>
        ExecuteEntityCommandAsync(identity, "relationship_types.retire", now, async (connection, transaction) =>
        {
            _ = await RequireRelationshipTypeAsync(connection, transaction, id, expectedRevision, requireActive: true, cancellationToken).ConfigureAwait(false);
            await SqliteRelationshipStore.RetireTypeCoreAsync(connection, transaction, id, now, expectedRevision, cancellationToken).ConfigureAwait(false);
            RelationshipType retired = (await SqliteRelationshipStore.QueryTypeCoreAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false))!;
            return [new(retired.Id, retired.Revision, "retired")];
        }, cancellationToken);

    private static async Task<RelationshipType> RequireRelationshipTypeAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid id, string expectedRevision, bool requireActive, CancellationToken cancellationToken)
    {
        RelationshipType type = await SqliteRelationshipStore.QueryTypeCoreAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false)
            ?? throw new RecordCommandNotFoundException("Relationship type was not found.");
        if (type.Revision != expectedRevision)
            throw new ConcurrencyConflictException("The relationship type changed. Read it again before changing it.");
        if (requireActive && type.Lifecycle != RelationshipLifecycle.Active)
            throw new DomainValidationException("The relationship type is already retired.");
        return type;
    }

    public Task<RecordCommandReceipt> CreateRelationshipAsync(RecordCommandIdentity identity, PreparedRelationship relationship,
        DateTimeOffset now, CancellationToken cancellationToken = default) =>
        ExecuteEntityCommandAsync(identity, "relationships.create", now, async (connection, transaction) =>
        {
            await RequireRelationshipRecordRevisionAsync(connection, transaction, relationship.SourceRecordId, relationship.SourceRevision, cancellationToken).ConfigureAwait(false);
            await RequireRelationshipRecordRevisionAsync(connection, transaction, relationship.TargetRecordId, relationship.TargetRevision, cancellationToken).ConfigureAwait(false);
            StoredRelationship created = await SqliteRelationshipStore.InsertCoreAsync(connection, transaction, relationship.Id,
                relationship.TypeId, relationship.SourceRecordId, relationship.TargetRecordId, relationship.Note, now,
                relationship.TypeRevision, cancellationToken).ConfigureAwait(false);
            if (relationship.Expiry.IsSet)
            {
                // Written as an edit rather than a second INSERT column list, so a relationship
                // created as already ended goes through exactly the rules that end an existing one.
                created = await SqliteRelationshipStore.UpdateCoreAsync(connection, transaction, created.Id,
                    relationship.TypeId, relationship.Note, relationship.Expiry, now, null, null, cancellationToken).ConfigureAwait(false);
            }

            return [new(created.Id, created.Revision, "created")];
        }, cancellationToken);

    public Task<RecordCommandReceipt> UpdateRelationshipAsync(RecordCommandIdentity identity, PreparedRelationshipEdit edit,
        DateTimeOffset now, CancellationToken cancellationToken = default) =>
        ExecuteEntityCommandAsync(identity, "relationships.update", now, async (connection, transaction) =>
        {
            StoredRelationship updated = await SqliteRelationshipStore.UpdateCoreAsync(connection, transaction, edit.Id,
                edit.TypeId, edit.Note, edit.Expiry, now, edit.ExpectedRevision, edit.TypeRevision, cancellationToken).ConfigureAwait(false);
            return [new(updated.Id, updated.Revision, "updated")];
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
