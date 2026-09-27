namespace Monkeysphere.Core;

public sealed record PreparedRelationship(Guid Id, Guid TypeId, Guid SourceRecordId, Guid TargetRecordId, string? Note,
    string TypeRevision, string SourceRevision, string TargetRevision)
{
    /// <summary>When this relationship stops being true, if that is already known when it is made.</summary>
    public RelationshipExpiry Expiry { get; init; } = RelationshipExpiry.None;
}

/// <summary>
/// An edit to a relationship that already exists. Its two ends are not here, deliberately: changing
/// who is related is a different relationship, and is made by deleting this one and creating that.
/// </summary>
public sealed record PreparedRelationshipEdit(Guid Id, Guid TypeId, string? Note, RelationshipExpiry Expiry,
    string ExpectedRevision, string TypeRevision);

public interface IRelationshipCommandStore
{
    Task<RecordCommandReceipt> CreateRelationshipTypeAsync(RecordCommandIdentity identity, RelationshipType type,
        DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<RecordCommandReceipt> CreateRelationshipAsync(RecordCommandIdentity identity, PreparedRelationship relationship,
        DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<RecordCommandReceipt> UpdateRelationshipAsync(RecordCommandIdentity identity, PreparedRelationshipEdit edit,
        DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<RecordCommandReceipt> DeleteRelationshipAsync(RecordCommandIdentity identity, Guid id, string expectedRevision,
        DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<RecordCommandReceipt> RenameRelationshipTypeAsync(RecordCommandIdentity identity, Guid id, string expectedRevision,
        string name, string? inverseName, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<RecordCommandReceipt> RetireRelationshipTypeAsync(RecordCommandIdentity identity, Guid id, string expectedRevision,
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

    /// <summary>
    /// Relabels a relationship type. The labels are normalized against the type's own directionality,
    /// which the command reads inside its transaction rather than taking on trust: a symmetric type has
    /// no inverse label to set, and a directional one must have one.
    /// </summary>
    public Task<RecordCommandReceipt> RenameTypeAsync(RecordCommandIdentity identity, Guid id, string expectedRevision,
        string name, string? inverseName, CancellationToken cancellationToken = default)
    {
        RequireTypeReference(id, expectedRevision);
        return commands.RenameRelationshipTypeAsync(identity, id, expectedRevision, name, inverseName,
            timeProvider.GetUtcNow(), cancellationToken);
    }

    public Task<RecordCommandReceipt> RetireTypeAsync(RecordCommandIdentity identity, Guid id, string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        RequireTypeReference(id, expectedRevision);
        return commands.RetireRelationshipTypeAsync(identity, id, expectedRevision, timeProvider.GetUtcNow(), cancellationToken);
    }

    private static void RequireTypeReference(Guid id, string revision)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(revision) || revision.Length > 128)
            throw new DomainValidationException("Supply a relationship type ID and its discovery revision.");
    }

    public Task<RecordCommandReceipt> CreateAsync(RecordCommandIdentity identity, Guid typeId, Guid sourceRecordId, Guid targetRecordId,
        string expectedTypeRevision, string expectedSourceRevision, string expectedTargetRevision, string? note = null,
        RelationshipExpiry? expiry = null, CancellationToken cancellationToken = default)
    {
        if (typeId == Guid.Empty || sourceRecordId == Guid.Empty || targetRecordId == Guid.Empty || sourceRecordId == targetRecordId)
            throw new DomainValidationException("Supply a relationship type and two distinct records.");
        RequireRevision(expectedTypeRevision);
        RequireRevision(expectedSourceRevision);
        RequireRevision(expectedTargetRevision);
        return commands.CreateRelationshipAsync(identity, new(Guid.CreateVersion7(), typeId, sourceRecordId, targetRecordId,
            RelationshipService.NormalizeNote(note), expectedTypeRevision, expectedSourceRevision, expectedTargetRevision)
        {
            Expiry = expiry ?? RelationshipExpiry.None,
        }, timeProvider.GetUtcNow(), cancellationToken);
    }

    /// <summary>
    /// Changes a relationship in place: its type, its note, and whether it has ended. Not its two
    /// ends — relating different records is a different relationship, and pretending otherwise
    /// would let one ID quietly come to mean something else.
    /// </summary>
    public Task<RecordCommandReceipt> UpdateAsync(RecordCommandIdentity identity, Guid id, Guid typeId,
        string expectedRevision, string expectedTypeRevision, string? note = null, RelationshipExpiry? expiry = null,
        CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty || typeId == Guid.Empty)
            throw new DomainValidationException("Supply a relationship ID and a relationship type.");
        RequireRevision(expectedRevision);
        RequireRevision(expectedTypeRevision);
        return commands.UpdateRelationshipAsync(identity, new(id, typeId, RelationshipService.NormalizeNote(note),
            expiry ?? RelationshipExpiry.None, expectedRevision, expectedTypeRevision),
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
