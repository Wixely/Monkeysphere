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
    DateTimeOffset UpdatedAtUtc)
{
    /// <summary>
    /// Whether the grid carries a column of the record's universal tags. Its own flag rather than
    /// an entry among the field columns, because tags belong to the record rather than to its type:
    /// there is no field definition to name, and every record in the view has them.
    /// </summary>
    public bool ShowTags { get; init; }
}

public sealed record SavedViewDetails(
    SavedView View,
    IReadOnlyList<Guid> ColumnFieldDefinitionIds,
    IReadOnlyList<RecordFilter> Filters)
{
    /// <summary>
    /// Universal tags every record in this view must carry, with the same meaning an ad-hoc search
    /// gives them: each one narrows the result, and matching ignores case as tag storage does.
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}

public sealed record SaveViewRequest(
    string Name,
    Guid RecordTypeId,
    string? Query,
    IReadOnlyList<Guid> ColumnFieldDefinitionIds,
    IReadOnlyList<RecordFilter> Filters,
    Guid? GroupByFieldDefinitionId = null,
    Guid? SortFieldDefinitionId = null,
    bool SortDescending = false,
    IReadOnlyList<string>? Tags = null,
    bool ShowTags = false);

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
    /// <summary>The same bound the remote record query puts on the same list, so the two agree.</summary>
    public const int MaximumTags = 10;

    /// <summary>
    /// The rest of the bounds a view is held to. Named rather than written into the checks below
    /// because a remote caller is told them by get_capabilities, and a number a client plans
    /// against should not be a literal that only exists inside one validation method.
    /// </summary>
    public const int MaximumColumns = 25;

    public const int MaximumFilters = 10;

    public const int MaximumNameLength = 200;

    public const int MaximumQueryLength = 500;

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
            source.View.SortDescending,
            source.Tags,
            source.View.ShowTags);
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
        Sort: new RecordSort(view.View.SortFieldDefinitionId, view.View.SortDescending),
        Tags: view.Tags);

    private async Task<SaveViewRequest> NormalizeAsync(
        SaveViewRequest request,
        CancellationToken cancellationToken)
    {
        string name = FieldTypes.Required(request.Name, "Saved view name", MaximumNameLength);
        string? query = string.IsNullOrWhiteSpace(request.Query) ? null : request.Query.Trim();
        if (query?.Length > MaximumQueryLength)
        {
            throw new DomainValidationException($"Saved view search text cannot exceed {MaximumQueryLength} characters.");
        }

        RecordTypeDetails type = await records.GetRecordTypeAsync(request.RecordTypeId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainValidationException("Record type was not found.");
        HashSet<Guid> attached = type.Fields.Select(field => field.Definition.Id).ToHashSet();

        Guid[] columns = request.ColumnFieldDefinitionIds.Distinct().ToArray();
        if (columns.Length > MaximumColumns)
        {
            throw new DomainValidationException($"A saved view cannot contain more than {MaximumColumns} columns.");
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

        if (request.Filters.Count > MaximumFilters)
        {
            throw new DomainValidationException($"A saved view cannot contain more than {MaximumFilters} filters.");
        }

        RecordFilter[] filters = request.Filters.Select(filter =>
        {
            string value = FieldTypes.Required(filter.Value, "Filter value", 2_000);
            return filter with { Value = value };
        }).ToArray();

        // Trimmed, de-duplicated and length-checked by the same rules that govern a record's own
        // tags, so a view cannot be saved asking for a tag no record could ever carry. The bound
        // matches the one query_records already applies to the same list.
        IReadOnlyList<string> tags = RecordTagRules.Normalize(request.Tags);
        if (tags.Count > MaximumTags)
        {
            throw new DomainValidationException($"A saved view cannot filter on more than {MaximumTags} tags.");
        }

        return request with
        {
            Name = name,
            Query = query,
            ColumnFieldDefinitionIds = columns,
            Filters = filters,
            Tags = tags,
        };
    }
}
