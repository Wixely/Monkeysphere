namespace Monkeysphere.Core;

public sealed record SavedView(
    Guid Id,
    string Name,
    Guid RecordTypeId,
    string? Query,
    Guid? GroupByFieldDefinitionId,
    Guid? SortFieldDefinitionId,
    bool SortDescending,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record SavedViewDetails(
    SavedView View,
    IReadOnlyList<Guid> ColumnFieldDefinitionIds,
    IReadOnlyList<RecordFilter> Filters);

public sealed record SaveViewRequest(
    string Name,
    Guid RecordTypeId,
    string? Query,
    IReadOnlyList<Guid> ColumnFieldDefinitionIds,
    IReadOnlyList<RecordFilter> Filters,
    Guid? GroupByFieldDefinitionId = null,
    Guid? SortFieldDefinitionId = null,
    bool SortDescending = false);

public interface ISavedViewStore
{
    Task<IReadOnlyList<SavedView>> ListAsync(CancellationToken cancellationToken = default);

    Task<SavedViewDetails?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<SavedViewDetails> CreateAsync(
        Guid id,
        SaveViewRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<SavedViewDetails> UpdateAsync(
        Guid id,
        SaveViewRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface ISavedViewService
{
    Task<IReadOnlyList<SavedView>> ListAsync(CancellationToken cancellationToken = default);

    Task<SavedViewDetails?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<SavedViewDetails> CreateAsync(SaveViewRequest request, CancellationToken cancellationToken = default);

    Task<SavedViewDetails> UpdateAsync(Guid id, SaveViewRequest request, CancellationToken cancellationToken = default);

    Task<SavedViewDetails> DuplicateAsync(Guid id, string name, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    RecordSearch ToSearch(SavedViewDetails view, int page = 1, int pageSize = 25);
}

public sealed class SavedViewService(
    ISavedViewStore store,
    IMonkeysphereStore records,
    TimeProvider timeProvider) : ISavedViewService
{
    public Task<IReadOnlyList<SavedView>> ListAsync(CancellationToken cancellationToken = default) =>
        store.ListAsync(cancellationToken);

    public Task<SavedViewDetails?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        store.GetAsync(id, cancellationToken);

    public async Task<SavedViewDetails> CreateAsync(
        SaveViewRequest request,
        CancellationToken cancellationToken = default)
    {
        SaveViewRequest normalized = await NormalizeAsync(request, cancellationToken).ConfigureAwait(false);
        return await store.CreateAsync(Guid.CreateVersion7(), normalized, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<SavedViewDetails> UpdateAsync(
        Guid id,
        SaveViewRequest request,
        CancellationToken cancellationToken = default)
    {
        SaveViewRequest normalized = await NormalizeAsync(request, cancellationToken).ConfigureAwait(false);
        return await store.UpdateAsync(id, normalized, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<SavedViewDetails> DuplicateAsync(
        Guid id,
        string name,
        CancellationToken cancellationToken = default)
    {
        SavedViewDetails source = await store.GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainValidationException("Saved view was not found.");
        SaveViewRequest request = new(
            name,
            source.View.RecordTypeId,
            source.View.Query,
            source.ColumnFieldDefinitionIds,
            source.Filters,
            source.View.GroupByFieldDefinitionId,
            source.View.SortFieldDefinitionId,
            source.View.SortDescending);
        return await CreateAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        store.DeleteAsync(id, cancellationToken);

    public RecordSearch ToSearch(SavedViewDetails view, int page = 1, int pageSize = 25) => new(
        Query: view.View.Query,
        RecordTypeId: view.View.RecordTypeId,
        Page: page,
        PageSize: pageSize,
        Filters: view.Filters,
        Sort: new RecordSort(view.View.SortFieldDefinitionId, view.View.SortDescending));

    private async Task<SaveViewRequest> NormalizeAsync(
        SaveViewRequest request,
        CancellationToken cancellationToken)
    {
        string name = FieldTypes.Required(request.Name, "Saved view name", 200);
        string? query = string.IsNullOrWhiteSpace(request.Query) ? null : request.Query.Trim();
        if (query?.Length > 500)
        {
            throw new DomainValidationException("Saved view search text cannot exceed 500 characters.");
        }

        RecordTypeDetails type = await records.GetRecordTypeAsync(request.RecordTypeId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainValidationException("Record type was not found.");
        HashSet<Guid> attached = type.Fields.Select(field => field.Definition.Id).ToHashSet();

        Guid[] columns = request.ColumnFieldDefinitionIds.Distinct().ToArray();
        if (columns.Length > 25)
        {
            throw new DomainValidationException("A saved view cannot contain more than 25 columns.");
        }

        Guid[] referenced = columns
            .Concat(request.Filters.Select(filter => filter.FieldDefinitionId))
            .Concat(request.GroupByFieldDefinitionId is Guid group ? [group] : [])
            .Concat(request.SortFieldDefinitionId is Guid sort ? [sort] : [])
            .ToArray();
        if (referenced.Any(fieldId => !attached.Contains(fieldId)))
        {
            throw new DomainValidationException("Saved view fields must belong to the selected record type.");
        }

        if (request.Filters.Count > 10)
        {
            throw new DomainValidationException("A saved view cannot contain more than 10 filters.");
        }

        RecordFilter[] filters = request.Filters.Select(filter =>
        {
            string value = FieldTypes.Required(filter.Value, "Filter value", 2_000);
            return filter with { Value = value };
        }).ToArray();

        return request with
        {
            Name = name,
            Query = query,
            ColumnFieldDefinitionIds = columns,
            Filters = filters,
        };
    }
}
