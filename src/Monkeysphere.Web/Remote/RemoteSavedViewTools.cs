using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

/// <summary>
/// One saved view as a remote caller sees it: the record type it selects, the narrowing it applies,
/// and the columns it would show. A view is a stored question rather than stored data — deleting one
/// loses no record — which is why it carries no revision and the last write wins, exactly as the
/// browser's own editor behaves. A remote-only concurrency rule here would disagree with the page.
/// </summary>
public sealed record RemoteSavedView(
    Guid Id,
    string Name,
    Guid RecordTypeId,
    string? Query,
    Guid? GroupByFieldDefinitionId,
    Guid? SortFieldDefinitionId,
    bool SortDescending,
    IReadOnlyList<Guid> ColumnFieldDefinitionIds,
    IReadOnlyList<RemoteRecordFilter> Filters,
    IReadOnlyList<string> Tags,
    bool ShowTags,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    /// <summary>
    /// How the view is drawn: <c>Grid</c> or <c>Gallery</c>. Last in the shape and defaulted, so a
    /// client written against an earlier contract deserializes a view unchanged.
    /// </summary>
    string Kind = nameof(SavedViewKind.Grid));

/// <summary>
/// One row of a view once it has been run: the record, and the values of the fields the view asks
/// for. Only those fields, because a view naming three columns is a caller saying it wants three
/// things about each record rather than everything the record holds.
/// </summary>
public sealed record RemoteSavedViewRow(
    RemoteRecordSummary Record,
    IReadOnlyList<RemoteRecordValue> Values,
    IReadOnlyList<string> Tags)
{
    /// <summary>
    /// The record's own images, for a gallery view. Ids only: the bytes come from
    /// <c>read_record_image</c> under the media grant, because a view manager reading pictures out of
    /// the deployment would be a different permission than the one it holds.
    /// </summary>
    public IReadOnlyList<RemoteGalleryImage> Images { get; init; } = [];

    /// <summary>The whole count, so a caller knows the collage is a sample rather than all of them.</summary>
    public int TotalImageCount { get; init; }

    /// <summary>
    /// What this record is connected to, for a gallery view. Carried because a gallery draws these as
    /// pictures rather than listing them, so a client rendering the same view needs them in the same
    /// call rather than one relationship query per row.
    /// </summary>
    public IReadOnlyList<RemoteGalleryRelation> Related { get; init; } = [];

    public int TotalRelatedCount { get; init; }
}

public sealed record RemoteGalleryImage(Guid Id, string? Caption, bool IsCover);

public sealed record RemoteGalleryRelation(
    Guid RecordId, string DisplayName, string Label, bool IsOutgoing, Guid? ImageId, bool IsExpired);

/// <summary>The bounds a saved view is held to, so a client can plan within them rather than discover them by being refused.</summary>
public sealed record RemoteSavedViewLimits(
    int MaximumColumns = SavedViewService.MaximumColumns,
    int MaximumFilters = SavedViewService.MaximumFilters,
    int MaximumTags = SavedViewService.MaximumTags,
    int MaximumNameLength = SavedViewService.MaximumNameLength,
    int MaximumQueryLength = SavedViewService.MaximumQueryLength,
    int MaximumRowsPerPageWithValues = RemoteSavedViewProjection.MaximumRowsWithValues,
    int MaximumCollageImages = SavedViewService.MaximumCollageImages,
    int MaximumRelatedLinks = SavedViewService.MaximumRelatedLinks);

public static class RemoteSavedViewProjection
{
    /// <summary>
    /// Running a view with its column values costs one read per row, the same way the browser's grid
    /// does. The browser shows 25 at a time; a remote caller may ask for more, but not for the
    /// hundred the plain record query allows, because a hundred reads answering one call is a
    /// different shape of request. Without values the ordinary page bound applies.
    /// </summary>
    public const int MaximumRowsWithValues = 50;

    internal static RemoteSavedView Map(SavedViewDetails details) => new(
        details.View.Id,
        details.View.Name,
        details.View.RecordTypeId,
        details.View.Query,
        details.View.GroupByFieldDefinitionId,
        details.View.SortFieldDefinitionId,
        details.View.SortDescending,
        details.ColumnFieldDefinitionIds,
        [.. details.Filters.Select(RemoteRecordFilterProjection.From)],
        details.Tags,
        details.View.ShowTags,
        details.View.CreatedAtUtc,
        details.View.UpdatedAtUtc,
        details.View.Kind.ToString());
}

internal static class RemoteRecordFilterProjection
{
    /// <summary>
    /// The inverse of <see cref="RemoteRecordFilter.ToCore"/>. A saved view read back must name its
    /// operators the way the caller would have to spell them to save the same view again.
    /// </summary>
    internal static RemoteRecordFilter From(RecordFilter filter) => new(
        filter.FieldDefinitionId,
        filter.Operator switch
        {
            FieldFilterOperator.Equals => "equals",
            FieldFilterOperator.Contains => "contains",
            FieldFilterOperator.GreaterThan => "greater_than",
            FieldFilterOperator.LessThan => "less_than",
            FieldFilterOperator.Before => "before",
            FieldFilterOperator.After => "after",
            _ => throw new DomainValidationException("A stored filter uses an operator this contract cannot name."),
        },
        filter.Value);
}

/// <summary>
/// Reading saved views, and running one. Reading a view's definition is allowed to a plain reader as
/// well as to a view manager, because a view discloses nothing a search could not already ask for.
/// Running one is a records.read operation and nothing else: it returns record content, so the grant
/// that manages views must not become a way to read records without holding the read grant.
/// </summary>
[McpServerToolType]
[RemoteToolScopes("records.read", "views.manage")]
public sealed class MonkeysphereSavedViewReadTools
{
    [McpServerTool(Name = "list_saved_views", ReadOnly = true)]
    [Description("Lists one domain's saved record views with the record type each selects, its search text, grouping, sort and how it is drawn (kind Grid or Gallery). Requires records.read or views.manage. Omitted domainId selects Default. Returns view definitions only, never record content; run_saved_view returns the records. Column and filter detail is omitted here — call get_saved_view for one view's full definition.")]
    public static Task<CallToolResult> ListAsync(
        ISavedViewService views,
        ICurrentDomainScope currentDomain,
        IHttpContextAccessor accessor,
        Guid? domainId = null,
        CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, async () =>
        {
            RemoteTagAuthority.Demand(accessor, "records.read", "views.manage");
            using IDisposable domain = currentDomain.Use(domainId ?? MonkeysphereDomains.DefaultId);
            IReadOnlyList<SavedView> stored = await views.ListAsync(cancellationToken).ConfigureAwait(false);

            // Listed without their columns and filters. Fetching every view's full detail to answer
            // a list is a read per view for information a caller listing them has not asked for.
            return stored.Select(view => new RemoteSavedView(
                view.Id, view.Name, view.RecordTypeId, view.Query,
                view.GroupByFieldDefinitionId, view.SortFieldDefinitionId, view.SortDescending,
                [], [], [], view.ShowTags, view.CreatedAtUtc, view.UpdatedAtUtc,
                view.Kind.ToString())).ToArray();
        });

    [McpServerTool(Name = "get_saved_view", ReadOnly = true)]
    [Description("Gets one saved view's full definition: its record type, search text, column fields, up to 10 AND-combined field filters, the universal tags every record must carry, grouping, sort, whether it shows tags, and kind — Grid draws a row per record, Gallery draws a panel per record led by a collage of its images and captioned with the same chosen fields. Requires records.read or views.manage and an explicit id. Omitted domainId selects Default. A view that does not exist in the selected domain returns empty rather than an error. Returns no record content.")]
    public static Task<CallToolResult> GetAsync(
        ISavedViewService views,
        ICurrentDomainScope currentDomain,
        IHttpContextAccessor accessor,
        Guid id,
        Guid? domainId = null,
        CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, async () =>
        {
            RemoteTagAuthority.Demand(accessor, "records.read", "views.manage");
            using IDisposable domain = currentDomain.Use(domainId ?? MonkeysphereDomains.DefaultId);
            SavedViewDetails? details = await views.GetAsync(id, cancellationToken).ConfigureAwait(false);

            // Null rather than an error, which is what get_record and get_record_type do for a
            // record absent from the selected domain: a domain isolation test asserts that shape.
            return details is null ? null : RemoteSavedViewProjection.Map(details);
        });

    [RemoteToolScopes("records.read")]
    [McpServerTool(Name = "run_saved_view", ReadOnly = true)]
    [Description("Runs a saved view and returns its rows: the matching records with the values of the fields the view lists as columns, plus its group-by field, and the record's universal tags when the view shows them. A Gallery view additionally returns each record's image ids with its whole image count, and what the record is connected to with each related record's cover image id, because a gallery draws those as pictures and one call should render one page; those extras arrive with includeValues true, since includeValues false is the cheap mode that reads no record. Image bytes are not returned — read_record_image serves them under the media grant. Requires records.read, because this returns record content; views.manage alone does not reach it. Needs an explicit id. Omitted domainId selects Default. Pages are 1-10000. pageSize defaults to 25 and may be 1-50 while values are included, or 1-100 with includeValues false, because each row with values costs a record read exactly as the browser's grid does. Results and totalCount share a database snapshot; pages across separate calls are live, not pinned.")]
    public static Task<CallToolResult> RunAsync(
        MonkeysphereRemoteQueries queries,
        ISavedViewService views,
        IGalleryViewService gallery,
        IHttpContextAccessor accessor,
        Guid id,
        int page = 1,
        int pageSize = 25,
        bool includeValues = true,
        Guid? domainId = null,
        CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, () =>
            queries.RunSavedViewAsync(views, gallery, id, page, pageSize, includeValues, domainId, cancellationToken));
}

/// <summary>
/// Changing saved views. Its own grant rather than <c>structure.write</c>, which authorizes creating
/// record types, creating and attaching fields and installing presets: changes that alter what every
/// record in a domain can hold. A view alters nothing about the records — it is a stored question —
/// so requiring the schema grant to tidy a list of views would hand out far more authority than the
/// task needs. It confers no reading either: a credential holding only this can save a view and
/// cannot run it.
/// </summary>
[McpServerToolType]
[RemoteToolScopes("views.manage")]
public sealed class MonkeysphereSavedViewWriteTools
{
    [McpServerTool(Name = "create_saved_view", ReadOnly = false, Destructive = false)]
    [Description("Creates a saved view in a domain. Requires views.manage, an explicit domainId, a name and a recordTypeId. kind chooses how it is drawn: Grid, the default, gives a row per record with the named fields as columns; Gallery gives a panel per record led by a collage of that record's images, with the same named fields as the caption beneath and its relationships drawn as pictures. Everything else means the same thing either way, because the kind changes only the drawing and never which records are selected. Every field named as a column, filter, group or sort must be attached to that record type, and a field belonging to another type is refused rather than dropped. Tags are trimmed and de-duplicated by the same rules a record's own tags follow, and every one of them narrows the view. Not idempotent: calling twice creates two views, because a view has no natural identity beyond its name and two views may legitimately share one.")]
    public static Task<CallToolResult> CreateAsync(
        ISavedViewService views,
        ICurrentDomainScope currentDomain,
        IHttpContextAccessor accessor,
        Guid domainId,
        string name,
        Guid recordTypeId,
        string? query = null,
        IReadOnlyList<Guid>? columnFieldDefinitionIds = null,
        IReadOnlyList<RemoteRecordFilter>? filters = null,
        IReadOnlyList<string>? tags = null,
        Guid? groupByFieldDefinitionId = null,
        Guid? sortFieldDefinitionId = null,
        bool sortDescending = false,
        bool showTags = false,
        string? kind = null,
        CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, async () =>
        {
            RemoteTagAuthority.Demand(accessor, "views.manage");
            using IDisposable domain = currentDomain.Use(domainId);
            SaveViewRequest request = Request(
                name, recordTypeId, query, columnFieldDefinitionIds, filters, tags,
                groupByFieldDefinitionId, sortFieldDefinitionId, sortDescending, showTags, kind);
            return RemoteSavedViewProjection.Map(
                await views.CreateAsync(request, cancellationToken).ConfigureAwait(false));
        });

    [McpServerTool(Name = "update_saved_view", ReadOnly = false, Destructive = false)]
    [Description("Replaces a saved view's definition. Requires views.manage, an explicit domainId and id, a name and a recordTypeId. This is a replacement rather than a merge: a list left out is stored empty, not left as it stands, because a caller clearing a view's filters has no other way to say so — and an omitted kind reverts the view to Grid, so send it back to keep a Gallery. Read the view with get_saved_view first and send back what you intend to keep. A saved view carries no revision and the last write wins, as it does in the browser.")]
    public static Task<CallToolResult> UpdateAsync(
        ISavedViewService views,
        ICurrentDomainScope currentDomain,
        IHttpContextAccessor accessor,
        Guid domainId,
        Guid id,
        string name,
        Guid recordTypeId,
        string? query = null,
        IReadOnlyList<Guid>? columnFieldDefinitionIds = null,
        IReadOnlyList<RemoteRecordFilter>? filters = null,
        IReadOnlyList<string>? tags = null,
        Guid? groupByFieldDefinitionId = null,
        Guid? sortFieldDefinitionId = null,
        bool sortDescending = false,
        bool showTags = false,
        string? kind = null,
        CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, async () =>
        {
            RemoteTagAuthority.Demand(accessor, "views.manage");
            using IDisposable domain = currentDomain.Use(domainId);

            // Checked before the write so a view absent from this domain fails as a missing view
            // rather than as whatever the store makes of an unknown identifier.
            _ = await views.GetAsync(id, cancellationToken).ConfigureAwait(false)
                ?? throw new RecordCommandNotFoundException("Saved view was not found in this domain.");
            SaveViewRequest request = Request(
                name, recordTypeId, query, columnFieldDefinitionIds, filters, tags,
                groupByFieldDefinitionId, sortFieldDefinitionId, sortDescending, showTags, kind);
            return RemoteSavedViewProjection.Map(
                await views.UpdateAsync(id, request, cancellationToken).ConfigureAwait(false));
        });

    [McpServerTool(Name = "duplicate_saved_view", ReadOnly = false, Destructive = false)]
    [Description("Copies a saved view under a new name, keeping its record type, search text, columns, filters, tags, grouping, sort and kind. Requires views.manage, an explicit domainId, id and name. The copy is a new view with its own id; the original is untouched.")]
    public static Task<CallToolResult> DuplicateAsync(
        ISavedViewService views,
        ICurrentDomainScope currentDomain,
        IHttpContextAccessor accessor,
        Guid domainId,
        Guid id,
        string name,
        CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, async () =>
        {
            RemoteTagAuthority.Demand(accessor, "views.manage");
            using IDisposable domain = currentDomain.Use(domainId);
            return RemoteSavedViewProjection.Map(
                await views.DuplicateAsync(id, name, cancellationToken).ConfigureAwait(false));
        });

    [McpServerTool(Name = "delete_saved_view", ReadOnly = false, Destructive = true)]
    [Description("Deletes a saved view. Requires views.manage, an explicit domainId and id. Destroys the view's definition and cannot be undone; no record, value or tag is touched, so nothing but the stored question is lost. Deleting a view that is already gone reports not_found rather than succeeding quietly, so a caller can tell a completed delete from a mistaken identifier.")]
    public static Task<CallToolResult> DeleteAsync(
        ISavedViewService views,
        ICurrentDomainScope currentDomain,
        IHttpContextAccessor accessor,
        Guid domainId,
        Guid id,
        CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, async () =>
        {
            RemoteTagAuthority.Demand(accessor, "views.manage");
            using IDisposable domain = currentDomain.Use(domainId);
            return await views.DeleteAsync(id, cancellationToken).ConfigureAwait(false)
                ? new RemoteSavedViewDeletion(id)
                : throw new RecordCommandNotFoundException("Saved view was not found in this domain.");
        });

    private static SaveViewRequest Request(
        string name,
        Guid recordTypeId,
        string? query,
        IReadOnlyList<Guid>? columns,
        IReadOnlyList<RemoteRecordFilter>? filters,
        IReadOnlyList<string>? tags,
        Guid? groupBy,
        Guid? sortBy,
        bool sortDescending,
        bool showTags,
        string? kind)
    {
        // Named rather than numbered, and rejected here so an unknown spelling reads as the caller's
        // mistake rather than as a validation failure about an integer they never sent.
        SavedViewKind drawnAs = kind is null
            ? SavedViewKind.Grid
            : Enum.TryParse(kind, ignoreCase: true, out SavedViewKind parsed) && Enum.IsDefined(parsed)
                ? parsed
                : throw new DomainValidationException(
                    $"kind must be one of: {string.Join(", ", Enum.GetNames<SavedViewKind>())}.");

        // Rejected here rather than counted after normalization, because a caller sending eleven
        // filters has made a mistake worth naming and the service's own bound would report it
        // against the de-duplicated list instead of the one that was sent.
        if (filters is not null && (filters.Count > SavedViewService.MaximumFilters || filters.Any(filter => filter is null)))
        {
            throw new DomainValidationException(
                $"Supply at most {SavedViewService.MaximumFilters} non-null filters.");
        }

        return new(
            name,
            recordTypeId,
            query,
            columns ?? [],
            [.. (filters ?? []).Select(filter => filter.ToCore())],
            groupBy,
            sortBy,
            sortDescending,
            tags,
            showTags,
            drawnAs);
    }
}

/// <summary>What a delete reports. A bare true would not say which view is now gone.</summary>
public sealed record RemoteSavedViewDeletion(Guid Id, bool Deleted = true);
