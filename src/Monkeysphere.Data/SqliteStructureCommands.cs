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

    public Task<RecordCommandReceipt> UpdateTypeAsync(RecordCommandIdentity identity, Guid id, string expectedRevision,
        string name, string? symbol, bool? tagsEnabled, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        ExecuteEntityCommandAsync(identity, "record_types.update", now, async (connection, transaction) =>
        {
            _ = await RequireStructureTypeAsync(connection, transaction, id, expectedRevision, cancellationToken).ConfigureAwait(false);
            await UpdateRecordTypeCoreAsync(connection, transaction, id, name, symbol, now, tagsEnabled, cancellationToken).ConfigureAwait(false);
            RecordType updated = (await QueryRecordTypeAsync(connection, id, transaction, cancellationToken).ConfigureAwait(false))!;
            return [new(updated.Id, updated.Revision, "updated")];
        }, cancellationToken);

    public Task<RecordCommandReceipt> RetireTypeAsync(RecordCommandIdentity identity, Guid id, string expectedUsageRevision,
        DateTimeOffset now, CancellationToken cancellationToken = default) =>
        ExecuteEntityCommandAsync(identity, "record_types.retire", now, async (connection, transaction) =>
        {
            await RequireUsageRevisionAsync(connection, transaction, [id], expectedUsageRevision, cancellationToken).ConfigureAwait(false);
            await RetireRecordTypeCoreAsync(connection, transaction, id, now, cancellationToken).ConfigureAwait(false);
            RecordType retired = (await QueryRecordTypeAsync(connection, id, transaction, cancellationToken).ConfigureAwait(false))!;
            return [new(retired.Id, retired.Revision, "retired")];
        }, cancellationToken);

    public Task<RecordCommandReceipt> MergeTypesAsync(RecordCommandIdentity identity, Guid sourceRecordTypeId,
        Guid targetRecordTypeId, string expectedUsageRevision, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        ExecuteEntityCommandAsync(identity, "record_types.merge", now, async (connection, transaction) =>
        {
            await RequireUsageRevisionAsync(connection, transaction, [sourceRecordTypeId, targetRecordTypeId],
                expectedUsageRevision, cancellationToken).ConfigureAwait(false);
            await MergeRecordTypesCoreAsync(connection, transaction, sourceRecordTypeId, targetRecordTypeId, now, cancellationToken).ConfigureAwait(false);
            RecordType source = (await QueryRecordTypeAsync(connection, sourceRecordTypeId, transaction, cancellationToken).ConfigureAwait(false))!;
            RecordType target = (await QueryRecordTypeAsync(connection, targetRecordTypeId, transaction, cancellationToken).ConfigureAwait(false))!;

            // The source is reported first and named retired, because that is the half a caller is
            // most likely to get wrong: a merge moves records and then ends the type they came from.
            return [new(source.Id, source.Revision, "retired"), new(target.Id, target.Revision, "updated")];
        }, cancellationToken);

    public Task<RecordCommandReceipt> RenameFieldAsync(RecordCommandIdentity identity, Guid id, string expectedFieldRevision,
        string name, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        ExecuteEntityCommandAsync(identity, "fields.rename", now, async (connection, transaction) =>
        {
            _ = await RequireFieldAsync(connection, transaction, id, expectedFieldRevision, requireActive: false, cancellationToken).ConfigureAwait(false);
            await RenameFieldCoreAsync(connection, transaction, id, name, now, cancellationToken).ConfigureAwait(false);
            FieldDefinition renamed = (await QueryFieldDefinitionAsync(connection, id, transaction, cancellationToken).ConfigureAwait(false))!;
            return [new(renamed.Id, renamed.Revision, "updated")];
        }, cancellationToken);

    public Task<RecordCommandReceipt> RetireFieldAsync(RecordCommandIdentity identity, Guid id, string expectedFieldRevision,
        DateTimeOffset now, CancellationToken cancellationToken = default) =>
        ExecuteEntityCommandAsync(identity, "fields.retire", now, async (connection, transaction) =>
        {
            _ = await RequireFieldAsync(connection, transaction, id, expectedFieldRevision, requireActive: true, cancellationToken).ConfigureAwait(false);
            await RetireFieldCoreAsync(connection, transaction, id, now, cancellationToken).ConfigureAwait(false);
            FieldDefinition retired = (await QueryFieldDefinitionAsync(connection, id, transaction, cancellationToken).ConfigureAwait(false))!;
            return [new(retired.Id, retired.Revision, "retired")];
        }, cancellationToken);

    public Task<RecordCommandReceipt> MergeFieldsAsync(RecordCommandIdentity identity, Guid sourceFieldDefinitionId,
        Guid targetFieldDefinitionId, FieldMergeConflictResolution conflictResolution, string expectedUsageRevision,
        DateTimeOffset now, CancellationToken cancellationToken = default) =>
        ExecuteEntityCommandAsync(identity, "fields.merge", now, async (connection, transaction) =>
        {
            await RequireFieldUsageRevisionAsync(connection, transaction, [sourceFieldDefinitionId, targetFieldDefinitionId],
                expectedUsageRevision, cancellationToken).ConfigureAwait(false);
            await MergeFieldsCoreAsync(connection, transaction, sourceFieldDefinitionId, targetFieldDefinitionId,
                conflictResolution, now, cancellationToken).ConfigureAwait(false);
            FieldDefinition source = (await QueryFieldDefinitionAsync(connection, sourceFieldDefinitionId, transaction, cancellationToken).ConfigureAwait(false))!;
            FieldDefinition target = (await QueryFieldDefinitionAsync(connection, targetFieldDefinitionId, transaction, cancellationToken).ConfigureAwait(false))!;
            return [new(source.Id, source.Revision, "retired"), new(target.Id, target.Revision, "updated")];
        }, cancellationToken);

    public Task<RecordCommandReceipt> ConvertFieldAsync(RecordCommandIdentity identity, Guid sourceFieldDefinitionId,
        Guid targetFieldDefinitionId, string targetName, string targetTypeId, string targetConfigurationJson,
        IReadOnlyList<ConvertedFieldValue> convertedValues, string expectedUsageRevision, DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        ExecuteEntityCommandAsync(identity, "fields.convert", now, async (connection, transaction) =>
        {
            await RequireFieldUsageRevisionAsync(connection, transaction, [sourceFieldDefinitionId],
                expectedUsageRevision, cancellationToken).ConfigureAwait(false);
            FieldDefinition created = await ConvertFieldCoreAsync(connection, transaction, sourceFieldDefinitionId,
                targetFieldDefinitionId, targetName, targetTypeId, targetConfigurationJson, convertedValues, now, cancellationToken).ConfigureAwait(false);
            FieldDefinition source = (await QueryFieldDefinitionAsync(connection, sourceFieldDefinitionId, transaction, cancellationToken).ConfigureAwait(false))!;

            // The new field first, because its ID is the one a caller has no other way of learning:
            // a conversion makes a definition rather than editing one.
            return [new(created.Id, created.Revision, "created"), new(source.Id, source.Revision, "retired")];
        }, cancellationToken);

    private static async Task<FieldDefinition> RequireFieldAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid id, string expectedFieldRevision, bool requireActive, CancellationToken cancellationToken)
    {
        FieldDefinition field = await QueryFieldDefinitionAsync(connection, id, transaction, cancellationToken).ConfigureAwait(false)
            ?? throw new RecordCommandNotFoundException("Field definition was not found.");
        if (field.Revision != expectedFieldRevision)
            throw new ConcurrencyConflictException("The field definition changed. Read it again before changing it.");
        if (requireActive && field.Lifecycle != FieldLifecycle.Active)
            throw new DomainValidationException("The field definition is already retired.");
        return field;
    }

    private static async Task RequireFieldUsageRevisionAsync(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<Guid> fieldDefinitionIds, string expectedUsageRevision, CancellationToken cancellationToken)
    {
        if (!await FieldRevisionMatchesAsync(connection, transaction, fieldDefinitionIds, expectedUsageRevision, cancellationToken).ConfigureAwait(false))
            throw new ConcurrencyConflictException("Field usage changed after the preview. Preview it again.");
    }

    /// <summary>
    /// Refuses a command whose preview has gone stale. Reported as a concurrency conflict, so the
    /// remote surface answers <c>stale_revision</c> rather than <c>validation_failed</c>: the request
    /// is well formed and retrying it unchanged will never succeed, which is the distinction the code
    /// exists to draw. The page path says the same thing in its own words.
    /// </summary>
    private static async Task RequireUsageRevisionAsync(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<Guid> recordTypeIds, string expectedUsageRevision, CancellationToken cancellationToken)
    {
        if (!await RecordTypeRevisionMatchesAsync(connection, transaction, recordTypeIds, expectedUsageRevision, cancellationToken).ConfigureAwait(false))
            throw new ConcurrencyConflictException("Record-type usage changed after the preview. Preview it again.");
    }

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
