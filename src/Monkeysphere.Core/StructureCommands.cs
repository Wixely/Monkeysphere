namespace Monkeysphere.Core;

public sealed record PreparedFieldDefinition(Guid Id, string Name, string TypeId, string ConfigurationJson, bool IsRequired);

public interface IStructureCommandStore
{
    Task<RecordCommandReceipt> CreateTypeAsync(RecordCommandIdentity identity, Guid id, string name, string? symbol,
        DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<RecordCommandReceipt> CreateFieldAsync(RecordCommandIdentity identity, Guid recordTypeId, string expectedRevision,
        PreparedFieldDefinition field, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<RecordCommandReceipt> AttachFieldAsync(RecordCommandIdentity identity, Guid recordTypeId, Guid fieldId,
        string expectedRevision, string expectedFieldRevision, bool isRequired, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<RecordCommandReceipt> UpdateTypeAsync(RecordCommandIdentity identity, Guid id, string expectedRevision, string name,
        string? symbol, bool? tagsEnabled, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<RecordCommandReceipt> RetireTypeAsync(RecordCommandIdentity identity, Guid id, string expectedUsageRevision,
        DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<RecordCommandReceipt> MergeTypesAsync(RecordCommandIdentity identity, Guid sourceRecordTypeId, Guid targetRecordTypeId,
        string expectedUsageRevision, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<RecordCommandReceipt> RenameFieldAsync(RecordCommandIdentity identity, Guid id, string expectedFieldRevision,
        string name, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<RecordCommandReceipt> RetireFieldAsync(RecordCommandIdentity identity, Guid id, string expectedFieldRevision,
        DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<RecordCommandReceipt> MergeFieldsAsync(RecordCommandIdentity identity, Guid sourceFieldDefinitionId,
        Guid targetFieldDefinitionId, FieldMergeConflictResolution conflictResolution, string expectedUsageRevision,
        DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<RecordCommandReceipt> ConvertFieldAsync(RecordCommandIdentity identity, Guid sourceFieldDefinitionId,
        Guid targetFieldDefinitionId, string targetName, string targetTypeId, string targetConfigurationJson,
        IReadOnlyList<ConvertedFieldValue> convertedValues, string expectedUsageRevision, DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

public sealed class StructureCommandService(IStructureCommandStore commands, TimeProvider timeProvider)
{
    public Task<RecordCommandReceipt> CreateTypeAsync(RecordCommandIdentity identity, string name, string? symbol,
        CancellationToken cancellationToken = default) => commands.CreateTypeAsync(identity, Guid.CreateVersion7(),
            FieldTypes.Required(name, "Record type name", 200), MonkeysphereService.NormalizeRecordTypeSymbol(symbol), timeProvider.GetUtcNow(), cancellationToken);

    public Task<RecordCommandReceipt> CreateFieldAsync(RecordCommandIdentity identity, Guid recordTypeId, string expectedRevision,
        CreateFieldRequest request, CancellationToken cancellationToken = default)
    {
        RequireReference(recordTypeId, expectedRevision);
        string typeId = FieldTypes.NormalizeTypeId(request.TypeId);
        if (request.ChoiceOptions?.Count > 1000) throw new DomainValidationException("Supply at most 1000 choice options.");
        PreparedFieldDefinition field = new(Guid.CreateVersion7(), FieldTypes.Required(request.Name, "Field name", 200),
            typeId, FieldTypes.NormalizeConfiguration(typeId, request.ChoiceOptions), request.IsRequired);
        return commands.CreateFieldAsync(identity, recordTypeId, expectedRevision, field, timeProvider.GetUtcNow(), cancellationToken);
    }

    public Task<RecordCommandReceipt> AttachFieldAsync(RecordCommandIdentity identity, Guid recordTypeId, Guid fieldId,
        string expectedRevision, string expectedFieldRevision, bool isRequired, CancellationToken cancellationToken = default)
    {
        RequireReference(recordTypeId, expectedRevision);
        RequireReference(fieldId, expectedFieldRevision);
        return commands.AttachFieldAsync(identity, recordTypeId, fieldId, expectedRevision, expectedFieldRevision, isRequired,
            timeProvider.GetUtcNow(), cancellationToken);
    }

    /// <summary>
    /// Changes a record type's name, symbol or whether it carries tags.
    ///
    /// The revision checked here is the type's own discovery revision, the same one
    /// <see cref="AttachFieldAsync"/> demands. Retirement and merging check a usage fingerprint
    /// instead, because what they need to know is whether the records and views they are about to
    /// move have changed, which a rename does not touch.
    /// </summary>
    public Task<RecordCommandReceipt> UpdateTypeAsync(RecordCommandIdentity identity, Guid id, string expectedRevision,
        string name, string? symbol, bool? tagsEnabled, CancellationToken cancellationToken = default)
    {
        RequireReference(id, expectedRevision);
        return commands.UpdateTypeAsync(identity, id, expectedRevision, FieldTypes.Required(name, "Record type name", 200),
            MonkeysphereService.NormalizeRecordTypeSymbol(symbol), tagsEnabled, timeProvider.GetUtcNow(), cancellationToken);
    }

    public Task<RecordCommandReceipt> RetireTypeAsync(RecordCommandIdentity identity, Guid id, string expectedUsageRevision,
        CancellationToken cancellationToken = default)
    {
        RequireReference(id, expectedUsageRevision);
        return commands.RetireTypeAsync(identity, id, expectedUsageRevision, timeProvider.GetUtcNow(), cancellationToken);
    }

    public Task<RecordCommandReceipt> MergeTypesAsync(RecordCommandIdentity identity, Guid sourceRecordTypeId,
        Guid targetRecordTypeId, string expectedUsageRevision, CancellationToken cancellationToken = default)
    {
        // One fingerprint covers both types, so one reference check does too: the preview computed it
        // over the pair and neither half can have moved without it changing.
        RequireReference(sourceRecordTypeId, expectedUsageRevision);
        if (targetRecordTypeId == Guid.Empty) throw new DomainValidationException("Supply an entity ID and its discovery revision.");
        return commands.MergeTypesAsync(identity, sourceRecordTypeId, targetRecordTypeId, expectedUsageRevision,
            timeProvider.GetUtcNow(), cancellationToken);
    }

    /// <summary>
    /// Renames a reusable field. Checked against the field's own revision from field discovery, which
    /// is what a rename can collide with; the usage fingerprint the merge and conversion commands want
    /// would be needlessly strict here, since a record gaining a value is no reason to refuse a rename.
    /// </summary>
    public Task<RecordCommandReceipt> RenameFieldAsync(RecordCommandIdentity identity, Guid id, string expectedFieldRevision,
        string name, CancellationToken cancellationToken = default)
    {
        RequireReference(id, expectedFieldRevision);
        return commands.RenameFieldAsync(identity, id, expectedFieldRevision,
            FieldTypes.Required(name, "Field name", 200), timeProvider.GetUtcNow(), cancellationToken);
    }

    public Task<RecordCommandReceipt> RetireFieldAsync(RecordCommandIdentity identity, Guid id, string expectedFieldRevision,
        CancellationToken cancellationToken = default)
    {
        RequireReference(id, expectedFieldRevision);
        return commands.RetireFieldAsync(identity, id, expectedFieldRevision, timeProvider.GetUtcNow(), cancellationToken);
    }

    public Task<RecordCommandReceipt> MergeFieldsAsync(RecordCommandIdentity identity, Guid sourceFieldDefinitionId,
        Guid targetFieldDefinitionId, FieldMergeConflictResolution conflictResolution, string expectedUsageRevision,
        CancellationToken cancellationToken = default)
    {
        RequireReference(sourceFieldDefinitionId, expectedUsageRevision);
        if (targetFieldDefinitionId == Guid.Empty) throw new DomainValidationException("Supply an entity ID and its discovery revision.");
        if (!Enum.IsDefined(conflictResolution)) throw new DomainValidationException("Choose a supported merge conflict policy.");
        return commands.MergeFieldsAsync(identity, sourceFieldDefinitionId, targetFieldDefinitionId, conflictResolution,
            expectedUsageRevision, timeProvider.GetUtcNow(), cancellationToken);
    }

    /// <summary>
    /// Applies a conversion whose values were already transformed by
    /// <see cref="IMonkeysphereService.PreviewFieldConversionAsync"/>. The transformation stays in the
    /// service because that is where the field-type rules live and where the browser does it too; what
    /// this adds is the receipt, and the fingerprint check inside the transaction that makes applying a
    /// preview read a moment ago safe.
    /// </summary>
    public Task<RecordCommandReceipt> ConvertFieldAsync(RecordCommandIdentity identity, Guid sourceFieldDefinitionId,
        Guid targetFieldDefinitionId, string targetName, string targetTypeId, string targetConfigurationJson,
        IReadOnlyList<ConvertedFieldValue> convertedValues, string expectedUsageRevision, CancellationToken cancellationToken = default)
    {
        RequireReference(sourceFieldDefinitionId, expectedUsageRevision);
        if (targetFieldDefinitionId == Guid.Empty) throw new DomainValidationException("Supply an entity ID and its discovery revision.");
        return commands.ConvertFieldAsync(identity, sourceFieldDefinitionId, targetFieldDefinitionId, targetName, targetTypeId,
            targetConfigurationJson, convertedValues, expectedUsageRevision, timeProvider.GetUtcNow(), cancellationToken);
    }

    private static void RequireReference(Guid id, string revision)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(revision) || revision.Length > 128)
            throw new DomainValidationException("Supply an entity ID and its discovery revision.");
    }
}
