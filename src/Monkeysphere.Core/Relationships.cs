namespace Monkeysphere.Core;

public enum RelationshipDirectionality
{
    Directional,
    Symmetric,
}

public enum RelationshipLifecycle
{
    Active,
    Retired,
}

public sealed record RelationshipType(
    Guid Id,
    string Name,
    RelationshipDirectionality Directionality,
    string? InverseName,
    RelationshipLifecycle Lifecycle,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? PresetKey = null,
    int? PresetVersion = null);

public sealed record StoredRelationship(
    Guid Id,
    RelationshipType Type,
    Guid SourceRecordId,
    string SourceDisplayName,
    Guid TargetRecordId,
    string TargetDisplayName,
    string? Note,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record RelationshipView(
    Guid Id,
    Guid RelationshipTypeId,
    string Label,
    Guid RelatedRecordId,
    string RelatedDisplayName,
    bool IsOutgoing,
    string? Note,
    DateTimeOffset UpdatedAtUtc);

public sealed record CreateRelationshipTypeRequest(
    string Name,
    RelationshipDirectionality Directionality,
    string? InverseName = null);

public interface IRelationshipStore
{
    Task<IReadOnlyList<RelationshipType>> ListTypesAsync(CancellationToken cancellationToken = default);
    Task<RelationshipType?> GetTypeAsync(Guid id, CancellationToken cancellationToken = default);
    Task<RelationshipType> CreateTypeAsync(RelationshipType type, CancellationToken cancellationToken = default);
    Task RenameTypeAsync(Guid id, string name, string? inverseName, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task RetireTypeAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<StoredRelationship> CreateAsync(Guid id, Guid typeId, Guid sourceRecordId, Guid targetRecordId, string? note, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StoredRelationship>> ListForRecordAsync(Guid recordId, int limit, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IRelationshipService
{
    Task<IReadOnlyList<RelationshipType>> ListTypesAsync(CancellationToken cancellationToken = default);
    Task<RelationshipType> CreateTypeAsync(CreateRelationshipTypeRequest request, CancellationToken cancellationToken = default);
    Task RenameTypeAsync(Guid id, string name, string? inverseName, CancellationToken cancellationToken = default);
    Task RetireTypeAsync(Guid id, CancellationToken cancellationToken = default);
    Task<RelationshipView> CreateAsync(Guid typeId, Guid sourceRecordId, Guid targetRecordId, string? note = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RelationshipView>> ListForRecordAsync(Guid recordId, int limit = 100, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class RelationshipService(IRelationshipStore store, TimeProvider timeProvider) : IRelationshipService
{
    public Task<IReadOnlyList<RelationshipType>> ListTypesAsync(CancellationToken cancellationToken = default) =>
        store.ListTypesAsync(cancellationToken);

    public Task<RelationshipType> CreateTypeAsync(CreateRelationshipTypeRequest request, CancellationToken cancellationToken = default)
    {
        (string name, string? inverse) = NormalizeLabels(request.Name, request.InverseName, request.Directionality);
        DateTimeOffset now = timeProvider.GetUtcNow();
        return store.CreateTypeAsync(new RelationshipType(
            Guid.CreateVersion7(), name, request.Directionality, inverse, RelationshipLifecycle.Active, now, now), cancellationToken);
    }

    public async Task RenameTypeAsync(Guid id, string name, string? inverseName, CancellationToken cancellationToken = default)
    {
        RelationshipType type = await store.GetTypeAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainValidationException("Relationship type was not found.");
        (string normalizedName, string? normalizedInverse) = NormalizeLabels(name, inverseName, type.Directionality);
        await store.RenameTypeAsync(id, normalizedName, normalizedInverse, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    public Task RetireTypeAsync(Guid id, CancellationToken cancellationToken = default) =>
        store.RetireTypeAsync(id, timeProvider.GetUtcNow(), cancellationToken);

    public async Task<RelationshipView> CreateAsync(
        Guid typeId,
        Guid sourceRecordId,
        Guid targetRecordId,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        if (sourceRecordId == targetRecordId)
        {
            throw new DomainValidationException("A record cannot be related to itself.");
        }

        string? normalizedNote = string.IsNullOrWhiteSpace(note) ? null : FieldTypes.Required(note, "Relationship note", 2_000);
        StoredRelationship created = await store.CreateAsync(
            Guid.CreateVersion7(), typeId, sourceRecordId, targetRecordId, normalizedNote, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return Map(created, sourceRecordId);
    }

    public async Task<IReadOnlyList<RelationshipView>> ListForRecordAsync(Guid recordId, int limit = 100, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 500)
        {
            throw new DomainValidationException("Relationship result limit must be between 1 and 500.");
        }

        IReadOnlyList<StoredRelationship> relationships = await store.ListForRecordAsync(recordId, limit, cancellationToken).ConfigureAwait(false);
        return relationships.Select(item => Map(item, recordId)).ToArray();
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) => store.DeleteAsync(id, cancellationToken);

    private static (string Name, string? Inverse) NormalizeLabels(
        string name,
        string? inverseName,
        RelationshipDirectionality directionality)
    {
        string normalizedName = FieldTypes.Required(name, "Relationship label", 200);
        if (directionality == RelationshipDirectionality.Symmetric)
        {
            return (normalizedName, null);
        }

        return (normalizedName, FieldTypes.Required(inverseName ?? string.Empty, "Inverse relationship label", 200));
    }

    private static RelationshipView Map(StoredRelationship relationship, Guid perspectiveRecordId)
    {
        bool outgoing = relationship.SourceRecordId == perspectiveRecordId;
        return new(
            relationship.Id,
            relationship.Type.Id,
            relationship.Type.Directionality == RelationshipDirectionality.Symmetric || outgoing
                ? relationship.Type.Name
                : relationship.Type.InverseName!,
            outgoing ? relationship.TargetRecordId : relationship.SourceRecordId,
            outgoing ? relationship.TargetDisplayName : relationship.SourceDisplayName,
            outgoing,
            relationship.Note,
            relationship.UpdatedAtUtc);
    }
}
