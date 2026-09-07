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

    private static void RequireReference(Guid id, string revision)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(revision) || revision.Length > 128)
            throw new DomainValidationException("Supply an entity ID and its discovery revision.");
    }
}
