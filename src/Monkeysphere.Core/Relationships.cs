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
    int? PresetVersion = null,
    string Revision = "");

/// <summary>
/// When a relationship stopped being true. A relationship that has ended is not the same as one
/// that never happened, so ending it is a change to the relationship rather than a deletion.
/// </summary>
/// <param name="Expired">
/// Said outright, for an ending with no useful date. This wins wherever both are set, so marking
/// something over never has to wait for a date to catch up with it.
/// </param>
/// <param name="ExpiresAtUtc">When it ends, which becomes true on its own once that moment passes.</param>
public sealed record RelationshipExpiry(bool Expired = false, DateTimeOffset? ExpiresAtUtc = null)
{
    public static readonly RelationshipExpiry None = new();

    public bool IsExpiredAt(DateTimeOffset now) => Expired || (ExpiresAtUtc is { } ends && ends <= now);

    /// <summary>True when anything about this relationship's ending has been recorded at all.</summary>
    public bool IsSet => Expired || ExpiresAtUtc is not null;
}

public sealed record StoredRelationship(
    Guid Id,
    RelationshipType Type,
    Guid SourceRecordId,
    string SourceDisplayName,
    Guid TargetRecordId,
    string TargetDisplayName,
    string? Note,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string Revision = "")
{
    /// <summary>When this relationship stopped, or stops, being true.</summary>
    public RelationshipExpiry Expiry { get; init; } = RelationshipExpiry.None;

    /// <summary>Each end's cover image and type symbol, so a related record can be shown the way it is everywhere else.</summary>
    public Guid? SourceImageId { get; init; }

    public string? SourceRecordTypeSymbol { get; init; }

    public Guid? TargetImageId { get; init; }

    public string? TargetRecordTypeSymbol { get; init; }
}

public sealed record RelationshipView(
    Guid Id,
    Guid RelationshipTypeId,
    string Label,
    Guid RelatedRecordId,
    string RelatedDisplayName,
    bool IsOutgoing,
    string? Note,
    DateTimeOffset UpdatedAtUtc,
    string Revision = "")
{
    /// <summary>When this relationship stopped, or stops, being true.</summary>
    public RelationshipExpiry Expiry { get; init; } = RelationshipExpiry.None;

    /// <summary>The related record's cover image, so a relationship reads as a picture and a name.</summary>
    public Guid? ImageId { get; init; }

    /// <summary>The related record type's symbol, shown when there is no image.</summary>
    public string? RecordTypeSymbol { get; init; }
}

public sealed record CreateRelationshipTypeRequest(
    string Name,
    RelationshipDirectionality Directionality,
    string? InverseName = null);

public interface IRelationshipStore
{
    Task<IReadOnlyList<RelationshipType>> ListTypesAsync(CancellationToken cancellationToken = default);
    Task<RelationshipType?> GetTypeAsync(Guid id, CancellationToken cancellationToken = default);
    Task<RelationshipType> CreateTypeAsync(RelationshipType type, CancellationToken cancellationToken = default);
    Task RenameTypeAsync(Guid id, string name, string? inverseName, DateTimeOffset now, string? expectedRevision = null, CancellationToken cancellationToken = default);
    Task RetireTypeAsync(Guid id, DateTimeOffset now, string? expectedRevision = null, CancellationToken cancellationToken = default);
    Task<StoredRelationship> CreateAsync(Guid id, Guid typeId, Guid sourceRecordId, Guid targetRecordId, string? note, DateTimeOffset now, string? expectedRevision = null, CancellationToken cancellationToken = default);
    Task<StoredRelationship> UpdateAsync(Guid id, Guid typeId, string? note, RelationshipExpiry expiry, DateTimeOffset now, string? expectedRevision = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StoredRelationship>> ListForRecordAsync(Guid recordId, int limit, CancellationToken cancellationToken = default);
    Task<PagedResult<StoredRelationship>> QueryForRecordAsync(Guid recordId, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid id, string? expectedRevision = null, CancellationToken cancellationToken = default);
}

public interface IRelationshipService
{
    Task<IReadOnlyList<RelationshipType>> ListTypesAsync(CancellationToken cancellationToken = default);
    Task<RelationshipType> CreateTypeAsync(CreateRelationshipTypeRequest request, CancellationToken cancellationToken = default);
    Task RenameTypeAsync(Guid id, string name, string? inverseName, string? expectedRevision = null, CancellationToken cancellationToken = default);
    Task RetireTypeAsync(Guid id, string? expectedRevision = null, CancellationToken cancellationToken = default);
    Task<RelationshipView> CreateAsync(Guid typeId, Guid sourceRecordId, Guid targetRecordId, string? note = null, string? expectedRevision = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes an existing relationship's type or note in place. Deliberately not delete-and-recreate:
    /// the relationship keeps its identity, so anything already holding its ID still refers to the
    /// same thing afterwards. <paramref name="perspectiveRecordId"/> only chooses which way round
    /// the returned view reads.
    /// </summary>
    Task<RelationshipView> UpdateAsync(Guid id, Guid typeId, string? note, Guid perspectiveRecordId, string? expectedRevision = null, RelationshipExpiry? expiry = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RelationshipView>> ListForRecordAsync(Guid recordId, int limit = 100, CancellationToken cancellationToken = default);
    Task<PagedResult<RelationshipView>> QueryForRecordAsync(Guid recordId, int page = 1, int pageSize = 25, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid id, string? expectedRevision = null, CancellationToken cancellationToken = default);
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

    public async Task RenameTypeAsync(Guid id, string name, string? inverseName, string? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        RelationshipType type = await store.GetTypeAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainValidationException("Relationship type was not found.");
        (string normalizedName, string? normalizedInverse) = NormalizeLabels(name, inverseName, type.Directionality);
        await store.RenameTypeAsync(id, normalizedName, normalizedInverse, timeProvider.GetUtcNow(), expectedRevision, cancellationToken).ConfigureAwait(false);
    }

    public Task RetireTypeAsync(Guid id, string? expectedRevision = null, CancellationToken cancellationToken = default) =>
        store.RetireTypeAsync(id, timeProvider.GetUtcNow(), expectedRevision, cancellationToken);

    public async Task<RelationshipView> CreateAsync(
        Guid typeId,
        Guid sourceRecordId,
        Guid targetRecordId,
        string? note = null,
        string? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        if (sourceRecordId == targetRecordId)
        {
            throw new DomainValidationException("A record cannot be related to itself.");
        }

        string? normalizedNote = NormalizeNote(note);
        StoredRelationship created = await store.CreateAsync(
            Guid.CreateVersion7(), typeId, sourceRecordId, targetRecordId, normalizedNote, timeProvider.GetUtcNow(), expectedRevision, cancellationToken).ConfigureAwait(false);
        return Map(created, sourceRecordId);
    }

    public async Task<RelationshipView> UpdateAsync(
        Guid id,
        Guid typeId,
        string? note,
        Guid perspectiveRecordId,
        string? expectedRevision = null,
        RelationshipExpiry? expiry = null,
        CancellationToken cancellationToken = default)
    {
        string? normalizedNote = NormalizeNote(note);
        StoredRelationship updated = await store.UpdateAsync(
            id, typeId, normalizedNote, expiry ?? RelationshipExpiry.None, timeProvider.GetUtcNow(), expectedRevision, cancellationToken).ConfigureAwait(false);
        // The caller may be looking from either end, or from neither when the change came from
        // somewhere with no record in hand; mapping from the stored source is then the honest default.
        return Map(updated, perspectiveRecordId == updated.TargetRecordId ? updated.TargetRecordId : updated.SourceRecordId);
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

    public async Task<PagedResult<RelationshipView>> QueryForRecordAsync(Guid recordId, int page = 1, int pageSize = 25, CancellationToken cancellationToken = default)
    {
        DiscoveryPagination.Validate(page, pageSize);
        PagedResult<StoredRelationship> result = await store.QueryForRecordAsync(recordId, page, pageSize, cancellationToken).ConfigureAwait(false);
        return new(result.Items.Select(item => Map(item, recordId)).ToArray(), result.Page, result.PageSize, result.TotalCount);
    }

    public Task<bool> DeleteAsync(Guid id, string? expectedRevision = null, CancellationToken cancellationToken = default) => store.DeleteAsync(id, expectedRevision, cancellationToken);

    internal static string? NormalizeNote(string? note) => string.IsNullOrWhiteSpace(note) ? null : FieldTypes.Required(note, "Relationship note", 2_000);

    internal static (string Name, string? Inverse) NormalizeLabels(
        string name,
        string? inverseName,
        RelationshipDirectionality directionality)
    {
        if (!Enum.IsDefined(directionality)) throw new DomainValidationException("Relationship directionality is invalid.");
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
            relationship.UpdatedAtUtc, relationship.Revision)
        {
            // The view looks outward from the record being read, so it carries the other end's picture.
            ImageId = outgoing ? relationship.TargetImageId : relationship.SourceImageId,
            RecordTypeSymbol = outgoing ? relationship.TargetRecordTypeSymbol : relationship.SourceRecordTypeSymbol,
        };
    }
}

/// <summary>What became of one record in a bulk relationship assignment.</summary>
public enum RecordRelationshipOutcome
{
    /// <summary>The relationship was created.</summary>
    Created,

    /// <summary>These two were already related by this type, so nothing was written.</summary>
    AlreadyRelated,

    /// <summary>No such record, or none this caller may see.</summary>
    NotFound,

    /// <summary>It is the record being related to, and nothing can be related to itself.</summary>
    SameRecord,
}

public sealed record RecordRelationshipChange(
    Guid RecordId,
    RecordRelationshipOutcome Outcome,
    string DisplayName,
    Guid? RelationshipId);

/// <summary>
/// Relates many records to one other record in a single stroke: a set of people to the place they
/// work, say. <paramref name="SelectedAreSource"/> chooses which end the selection is, which for a
/// directional type is the difference between "works at" and "employs".
/// </summary>
public sealed record BulkRelationshipRequest(
    IReadOnlyList<Guid> RecordIds,
    Guid RelationshipTypeId,
    Guid OtherRecordId,
    bool SelectedAreSource = true,
    string? Note = null,
    RelationshipExpiry? Expiry = null);

public interface IRecordRelationshipCommandStore
{
    Task<IReadOnlyList<RecordRelationshipChange>> ApplyAsync(
        BulkRelationshipRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

public interface IRecordRelationshipCommandService
{
    Task<IReadOnlyList<RecordRelationshipChange>> ApplyAsync(
        BulkRelationshipRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class RecordRelationshipCommandService(
    IRecordRelationshipCommandStore store,
    TimeProvider timeProvider) : IRecordRelationshipCommandService
{
    /// <summary>The same bound as a graph filter and a bulk tag edit, so the three agree.</summary>
    public const int MaximumRecords = 100;

    public Task<IReadOnlyList<RecordRelationshipChange>> ApplyAsync(
        BulkRelationshipRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Guid[] recordIds = [.. request.RecordIds.Distinct()];
        if (recordIds.Length == 0)
        {
            throw new DomainValidationException("Choose at least one record to relate.");
        }

        if (recordIds.Length > MaximumRecords)
        {
            throw new DomainValidationException(
                $"A relationship assignment cannot cover more than {MaximumRecords} records.");
        }

        if (request.RelationshipTypeId == Guid.Empty)
        {
            throw new DomainValidationException("Choose a relationship type.");
        }

        if (request.OtherRecordId == Guid.Empty)
        {
            throw new DomainValidationException("Choose the record to relate them to.");
        }

        return store.ApplyAsync(
            request with { RecordIds = recordIds, Note = RelationshipService.NormalizeNote(request.Note) },
            timeProvider.GetUtcNow(),
            cancellationToken);
    }
}
