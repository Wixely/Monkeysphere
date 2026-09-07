namespace Monkeysphere.Core;

public sealed record PreparedRelationship(Guid Id, Guid TypeId, Guid SourceRecordId, Guid TargetRecordId, string? Note,
    string TypeRevision, string SourceRevision, string TargetRevision);

public interface IRelationshipCommandStore
{
    Task<RecordCommandReceipt> CreateRelationshipTypeAsync(RecordCommandIdentity identity, RelationshipType type,
        DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<RecordCommandReceipt> CreateRelationshipAsync(RecordCommandIdentity identity, PreparedRelationship relationship,
        DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<RecordCommandReceipt> DeleteRelationshipAsync(RecordCommandIdentity identity, Guid id, string expectedRevision,
        DateTimeOffset now, CancellationToken cancellationToken = default);
}

public sealed class RelationshipCommandService(IRelationshipCommandStore commands, TimeProvider timeProvider)
{
    public Task<RecordCommandReceipt> CreateTypeAsync(RecordCommandIdentity identity, CreateRelationshipTypeRequest request,
        CancellationToken cancellationToken = default)
    {
        (string name, string? inverse) = RelationshipService.NormalizeLabels(request.Name, request.InverseName, request.Directionality);
        DateTimeOffset now = timeProvider.GetUtcNow();
        RelationshipType type = new(Guid.CreateVersion7(), name, request.Directionality, inverse, RelationshipLifecycle.Active, now, now);
        return commands.CreateRelationshipTypeAsync(identity, type, now, cancellationToken);
    }

    public Task<RecordCommandReceipt> CreateAsync(RecordCommandIdentity identity, Guid typeId, Guid sourceRecordId, Guid targetRecordId,
        string expectedTypeRevision, string expectedSourceRevision, string expectedTargetRevision, string? note = null,
        CancellationToken cancellationToken = default)
    {
        if (typeId == Guid.Empty || sourceRecordId == Guid.Empty || targetRecordId == Guid.Empty || sourceRecordId == targetRecordId)
            throw new DomainValidationException("Supply a relationship type and two distinct records.");
        RequireRevision(expectedTypeRevision);
        RequireRevision(expectedSourceRevision);
        RequireRevision(expectedTargetRevision);
        return commands.CreateRelationshipAsync(identity, new(Guid.CreateVersion7(), typeId, sourceRecordId, targetRecordId,
            RelationshipService.NormalizeNote(note), expectedTypeRevision, expectedSourceRevision, expectedTargetRevision),
            timeProvider.GetUtcNow(), cancellationToken);
    }

    public Task<RecordCommandReceipt> DeleteAsync(RecordCommandIdentity identity, Guid id, string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty) throw new DomainValidationException("Supply a relationship ID.");
        RequireRevision(expectedRevision);
        return commands.DeleteRelationshipAsync(identity, id, expectedRevision, timeProvider.GetUtcNow(), cancellationToken);
    }

    private static void RequireRevision(string revision)
    {
        if (string.IsNullOrWhiteSpace(revision) || revision.Length > 128)
            throw new DomainValidationException("Supply the revision returned by discovery for each referenced entity.");
    }
}
