using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

public sealed partial class SqliteMonkeysphereStore : IStructureCommandStore
{
    public Task<RecordCommandReceipt> CreateTypeAsync(RecordCommandIdentity identity, Guid id, string name, string? symbol,
        DateTimeOffset now, CancellationToken cancellationToken = default) =>
        ExecuteEntityCommandAsync(identity, "record_types.create", now, async (connection, transaction) =>
        {
            RecordType created = await CreateRecordTypeCoreAsync(connection, transaction, id, name, symbol, now, cancellationToken).ConfigureAwait(false);
            return [new(created.Id, created.Revision, "created")];
        }, cancellationToken);

    public Task<RecordCommandReceipt> CreateFieldAsync(RecordCommandIdentity identity, Guid recordTypeId, string expectedRevision,
        PreparedFieldDefinition field, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        ExecuteEntityCommandAsync(identity, "fields.create_attach", now, async (connection, transaction) =>
        {
            _ = await RequireStructureTypeAsync(connection, transaction, recordTypeId, expectedRevision, cancellationToken).ConfigureAwait(false);
            FieldDefinition created = await CreateAndAttachFieldCoreAsync(connection, transaction, recordTypeId, field.Id,
                field.Name, field.TypeId, field.ConfigurationJson, field.IsRequired, now, cancellationToken).ConfigureAwait(false);
            RecordType updated = (await QueryRecordTypeAsync(connection, recordTypeId, transaction, cancellationToken).ConfigureAwait(false))!;
            return [new(created.Id, created.Revision, "created"), new(updated.Id, updated.Revision, "updated")];
        }, cancellationToken);

    public Task<RecordCommandReceipt> AttachFieldAsync(RecordCommandIdentity identity, Guid recordTypeId, Guid fieldId,
        string expectedRevision, string expectedFieldRevision, bool isRequired, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        ExecuteEntityCommandAsync(identity, "fields.attach", now, async (connection, transaction) =>
        {
            _ = await RequireStructureTypeAsync(connection, transaction, recordTypeId, expectedRevision, cancellationToken).ConfigureAwait(false);
            FieldDefinition field = await QueryFieldDefinitionAsync(connection, fieldId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new RecordCommandNotFoundException("Field definition was not found.");
            if (field.Revision != expectedFieldRevision) throw new ConcurrencyConflictException("The field definition changed. Read it again before attaching.");
            await AttachFieldCoreAsync(connection, transaction, recordTypeId, fieldId, isRequired, now, cancellationToken).ConfigureAwait(false);
            RecordType updated = (await QueryRecordTypeAsync(connection, recordTypeId, transaction, cancellationToken).ConfigureAwait(false))!;
            return [new(updated.Id, updated.Revision, "updated")];
        }, cancellationToken);

    private static async Task<RecordType> RequireStructureTypeAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid id, string expectedRevision, CancellationToken cancellationToken)
    {
        RecordType type = await QueryRecordTypeAsync(connection, id, transaction, cancellationToken).ConfigureAwait(false)
            ?? throw new RecordCommandNotFoundException("Record type was not found.");
        if (type.Revision != expectedRevision) throw new ConcurrencyConflictException("The record type changed. Read it again before changing fields.");
        if (type.Lifecycle != RecordTypeLifecycle.Active) throw new DomainValidationException("The record type is retired.");
        return type;
    }
}
