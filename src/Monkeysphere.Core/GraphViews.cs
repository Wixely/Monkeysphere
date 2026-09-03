namespace Monkeysphere.Core;

public sealed record GraphView(
    Guid Id,
    string Name,
    RelationshipGraphDisplayMode DisplayMode,
    IReadOnlyList<Guid> RecordIds,
    IReadOnlyList<Guid> RecordTypeIds,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record SaveGraphViewRequest(
    string Name,
    RelationshipGraphDisplayMode DisplayMode,
    IReadOnlyList<Guid> RecordIds,
    IReadOnlyList<Guid> RecordTypeIds);

public interface IGraphViewStore
{
    Task<IReadOnlyList<GraphView>> ListAsync(CancellationToken cancellationToken = default);
    Task<GraphView?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<GraphView> CreateAsync(Guid id, SaveGraphViewRequest request, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<GraphView> UpdateAsync(Guid id, SaveGraphViewRequest request, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IGraphViewService
{
    Task<IReadOnlyList<GraphView>> ListAsync(CancellationToken cancellationToken = default);
    Task<GraphView?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<GraphView> CreateAsync(SaveGraphViewRequest request, CancellationToken cancellationToken = default);
    Task<GraphView> UpdateAsync(Guid id, SaveGraphViewRequest request, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class GraphViewService(
    IGraphViewStore store,
    IMonkeysphereStore records,
    TimeProvider timeProvider) : IGraphViewService
{
    public Task<IReadOnlyList<GraphView>> ListAsync(CancellationToken cancellationToken = default) =>
        store.ListAsync(cancellationToken);

    public Task<GraphView?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        store.GetAsync(id, cancellationToken);

    public async Task<GraphView> CreateAsync(SaveGraphViewRequest request, CancellationToken cancellationToken = default)
    {
        SaveGraphViewRequest normalized = await NormalizeAsync(request, cancellationToken).ConfigureAwait(false);
        return await store.CreateAsync(Guid.CreateVersion7(), normalized, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<GraphView> UpdateAsync(Guid id, SaveGraphViewRequest request, CancellationToken cancellationToken = default)
    {
        SaveGraphViewRequest normalized = await NormalizeAsync(request, cancellationToken).ConfigureAwait(false);
        return await store.UpdateAsync(id, normalized, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        store.DeleteAsync(id, cancellationToken);

    private async Task<SaveGraphViewRequest> NormalizeAsync(
        SaveGraphViewRequest request,
        CancellationToken cancellationToken)
    {
        string name = FieldTypes.Required(request.Name, "Graph view name", 200);
        if (!Enum.IsDefined(request.DisplayMode))
        {
            throw new DomainValidationException("Graph display mode is invalid.");
        }

        Guid[] recordIds = request.RecordIds.Distinct().ToArray();
        Guid[] recordTypeIds = request.RecordTypeIds.Distinct().ToArray();
        if (recordIds.Length > RelationshipGraphService.MaximumSelectedRecords)
        {
            throw new DomainValidationException($"A graph view cannot contain more than {RelationshipGraphService.MaximumSelectedRecords} selected records.");
        }

        if (recordTypeIds.Length > RelationshipGraphService.MaximumRecordTypes)
        {
            throw new DomainValidationException($"A graph view cannot contain more than {RelationshipGraphService.MaximumRecordTypes} record types.");
        }

        if (request.DisplayMode != RelationshipGraphDisplayMode.All && recordIds.Length == 0)
        {
            throw new DomainValidationException("Connected and isolated graph views require at least one selected record.");
        }

        Dictionary<Guid, RecordType> availableTypes = (await records.ListRecordTypesAsync(cancellationToken).ConfigureAwait(false))
            .Where(type => type.Lifecycle == RecordTypeLifecycle.Active)
            .ToDictionary(type => type.Id);
        if (recordTypeIds.Any(id => !availableTypes.ContainsKey(id)))
        {
            throw new DomainValidationException("Graph view record types must be active.");
        }

        HashSet<Guid> selectedTypes = recordTypeIds.ToHashSet();
        foreach (Guid recordId in recordIds)
        {
            RecordDetails record = await records.GetRecordAsync(recordId, cancellationToken).ConfigureAwait(false)
                ?? throw new DomainValidationException("A selected graph record was not found.");
            if (!selectedTypes.Contains(record.Record.RecordTypeId))
            {
                throw new DomainValidationException("Selected graph records must belong to a visible record type.");
            }
        }

        return request with
        {
            Name = name,
            RecordIds = recordIds,
            RecordTypeIds = recordTypeIds,
        };
    }
}
