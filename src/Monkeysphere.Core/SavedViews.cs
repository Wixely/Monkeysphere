namespace Monkeysphere.Core;

/// <summary>
/// How a saved view draws the records it selects. The selection itself is identical either way: the
/// same search text, filters, tags, sort and grouping produce the same records, and only the drawing
/// differs. That is why this is one flag on one view rather than two kinds of saved view.
/// </summary>
public enum SavedViewKind
{
    /// <summary>A row per record, a column per chosen field. What every saved view was before this.</summary>
    Grid,

    /// <summary>
    /// A large panel per record, led by a collage of that record's own images.
    ///
    /// For subjects where the pictures are the point and a row of text is nearly useless: a
    /// locomotive photographed nine times, a plant through a season, a place across years. The
    /// chosen columns become the details printed under the collage rather than a table header, and
    /// the record's relationships are drawn as pictures too, because what a photographed thing is
    /// connected to is usually another photographed thing.
    /// </summary>
    Gallery,
}

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

    /// <summary>
    /// How the view draws. An init property with a default rather than a constructor parameter, for
    /// the same reason <see cref="ShowTags"/> is one: every view that existed before this is a grid,
    /// and nothing that reads a view should have to say so.
    /// </summary>
    public SavedViewKind Kind { get; init; }
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
    bool ShowTags = false,
    SavedViewKind Kind = SavedViewKind.Grid);

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
    /// Pictures drawn in one gallery panel's collage. A record with forty photographs of the same
    /// locomotive should not make one panel forty images tall: the collage is a way in, and the
    /// record's own page is where all of them live. The rest are counted, not drawn.
    /// </summary>
    public const int MaximumCollageImages = 5;

    /// <summary>
    /// Related records drawn under a gallery panel. What a photographed thing is connected to is
    /// usually another photographed thing, so these are pictures rather than a list of names, and a
    /// bound keeps a well-connected record from burying its own collage.
    /// </summary>
    public const int MaximumRelatedLinks = 8;

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
            source.View.ShowTags,
            source.View.Kind);
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

        if (!Enum.IsDefined(request.Kind))
        {
            throw new DomainValidationException(
                $"A saved view must be drawn one of the named ways: {string.Join(", ", Enum.GetNames<SavedViewKind>())}.");
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
