using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

/// <summary>
/// One saved graph view: which records and record types it draws, and where the operator put them.
/// The positions are the part that could not be reconstructed — a layout somebody arranged by hand
/// has no rule behind it, so a caller that could filter the graph but not read the arrangement
/// could never reopen the view that was saved.
/// </summary>
public sealed record RemoteGraphView(
    Guid Id,
    string Name,
    string DisplayMode,
    IReadOnlyList<Guid> RecordIds,
    IReadOnlyList<Guid> RecordTypeIds,
    IReadOnlyList<RemoteGraphNodePosition> NodePositions,
    RemoteGraphViewport? Viewport,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record RemoteGraphNodePosition(Guid RecordId, double X, double Y);

public sealed record RemoteGraphViewport(double PanX, double PanY, double Zoom);

public sealed record RemoteGraphNode(
    Guid RecordId,
    Guid RecordTypeId,
    string RecordTypeName,
    string DisplayName,
    int Distance,
    Guid? ImageId,
    string? RecordTypeSymbol);

public sealed record RemoteGraphEdge(
    Guid RelationshipId,
    Guid RelationshipTypeId,
    string Label,
    string Directionality,
    Guid SourceRecordId,
    Guid TargetRecordId,
    string? Note,
    bool IsExpired);

/// <summary>
/// A drawn graph. The truncation flags and the limits actually applied travel with it, because a
/// graph that stopped early looks exactly like a graph that was complete, and a caller told only the
/// nodes would conclude it had the whole picture.
/// </summary>
public sealed record RemoteGraph(
    IReadOnlyList<RemoteGraphNode> Nodes,
    IReadOnlyList<RemoteGraphEdge> Edges,
    bool NodesTruncated,
    bool EdgesTruncated,
    int AppliedNodeLimit,
    int AppliedEdgeLimit);

/// <summary>The bounds a graph view and a graph query are held to, reported so a client plans inside them.</summary>
public sealed record RemoteGraphViewLimits(
    int MaximumNameLength = GraphViewService.MaximumNameLength,
    int MaximumSelectedRecords = RelationshipGraphService.MaximumSelectedRecords,
    int MaximumRecordTypes = RelationshipGraphService.MaximumRecordTypes,
    int MaximumNodePositions = RelationshipGraphService.MaximumNodes,
    double MaximumCoordinateMagnitude = GraphViewService.MaximumCoordinateMagnitude,
    double MinimumZoom = GraphViewService.MinimumZoom,
    double MaximumZoom = GraphViewService.MaximumZoom,
    int MaximumDepth = RelationshipGraphService.MaximumDepth,
    int MaximumSearchLength = RelationshipGraphService.MaximumSearchLength);

internal static class RemoteGraphProjection
{
    internal static RemoteGraphView Map(GraphView view) => new(
        view.Id,
        view.Name,
        view.DisplayMode.ToString().ToLowerInvariant(),
        view.RecordIds,
        view.RecordTypeIds,
        [.. view.NodePositions.Select(position => new RemoteGraphNodePosition(position.RecordId, position.X, position.Y))],
        view.Viewport is null ? null : new RemoteGraphViewport(view.Viewport.PanX, view.Viewport.PanY, view.Viewport.Zoom),
        view.CreatedAtUtc,
        view.UpdatedAtUtc);

    /// <summary>
    /// Spelled the way a caller has to send it back, which is why this is not
    /// <c>Enum.Parse</c>'s job in reverse only: an unnamed mode read out of storage would be a value
    /// no client could use as input.
    /// </summary>
    internal static RelationshipGraphDisplayMode ParseMode(string? mode) => mode switch
    {
        null or "all" => RelationshipGraphDisplayMode.All,
        "connected" => RelationshipGraphDisplayMode.Connected,
        "isolated" => RelationshipGraphDisplayMode.Isolated,
        _ => throw new DomainValidationException("Graph display mode must be all, connected or isolated."),
    };
}

/// <summary>
/// Reading saved graph views, and drawing a graph. The split follows the saved record views: the
/// definition of a view is readable by a plain reader and by a view manager, while drawing one
/// returns record names and links and therefore takes records.read alone.
/// </summary>
[McpServerToolType]
[RemoteToolScopes("records.read", "views.manage")]
public sealed class MonkeysphereGraphViewReadTools
{
    [McpServerTool(Name = "list_graph_views", ReadOnly = true)]
    [Description("Lists one domain's saved graph views with their display mode and the records and record types each draws. Requires records.read or views.manage. Omitted domainId selects Default. Node positions and viewport are omitted here — call get_graph_view for one view's arrangement. Returns no relationship data; query_graph draws the graph.")]
    public static Task<CallToolResult> ListAsync(
        IGraphViewService views,
        ICurrentDomainScope currentDomain,
        IHttpContextAccessor accessor,
        Guid? domainId = null,
        CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, async () =>
        {
            RemoteTagAuthority.Demand(accessor, "records.read", "views.manage");
            using IDisposable domain = currentDomain.Use(domainId ?? MonkeysphereDomains.DefaultId);

            // Without the positions, which are the bulk of a view and are of no use to a caller that
            // is listing rather than reopening.
            return (await views.ListAsync(cancellationToken).ConfigureAwait(false))
                .Select(view => RemoteGraphProjection.Map(view) with { NodePositions = [], Viewport = null })
                .ToArray();
        });

    [McpServerTool(Name = "get_graph_view", ReadOnly = true)]
    [Description("Gets one saved graph view including its remembered node positions and viewport, so the arrangement an operator made by hand can be reopened rather than laid out afresh. Requires records.read or views.manage and an explicit id. Omitted domainId selects Default. A view absent from the selected domain returns empty rather than an error.")]
    public static Task<CallToolResult> GetAsync(
        IGraphViewService views,
        ICurrentDomainScope currentDomain,
        IHttpContextAccessor accessor,
        Guid id,
        Guid? domainId = null,
        CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, async () =>
        {
            RemoteTagAuthority.Demand(accessor, "records.read", "views.manage");
            using IDisposable domain = currentDomain.Use(domainId ?? MonkeysphereDomains.DefaultId);
            GraphView? view = await views.GetAsync(id, cancellationToken).ConfigureAwait(false);
            return view is null ? null : RemoteGraphProjection.Map(view);
        });

    [RemoteToolScopes("records.read")]
    [McpServerTool(Name = "query_graph", ReadOnly = true)]
    [Description("Draws a bounded relationship graph: the matching records as nodes and the links between them as edges, each edge saying whether the relationship has ended. Requires records.read, because this returns record content; views.manage alone does not reach it. Pass graphViewId to draw a saved view's selection, or displayMode with selectedRecordIds and recordTypeIds directly — not both, because two selections would silently disagree. Omitted recordTypeIds draws every active type, as the graph page opens; an explicitly empty list is refused rather than answered with an empty graph. displayMode connected or isolated needs at least one selectedRecordId or a focusRecordId to be relative to. focusRecordId with depth 0-3 expands outward from one record. Search is at most 200 characters. nodeLimit and edgeLimit default to the domain's configured limits from get_graph_settings and are capped by them, so raising what a query may draw means raising the setting. nodesTruncated or edgesTruncated true means matching data was not drawn.")]
    public static Task<CallToolResult> QueryAsync(
        IRelationshipGraphService graph,
        IGraphViewService views,
        IGraphSettingsService settings,
        ICurrentDomainScope currentDomain,
        IHttpContextAccessor accessor,
        Guid? graphViewId = null,
        string? search = null,
        Guid? relationshipTypeId = null,
        Guid? focusRecordId = null,
        int depth = 1,
        string? displayMode = null,
        IReadOnlyList<Guid>? selectedRecordIds = null,
        IReadOnlyList<Guid>? recordTypeIds = null,
        int? nodeLimit = null,
        int? edgeLimit = null,
        Guid? domainId = null,
        CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, async () =>
        {
            RemoteTagAuthority.Demand(accessor, "records.read");
            using IDisposable domain = currentDomain.Use(domainId ?? MonkeysphereDomains.DefaultId);

            RelationshipGraphDisplayMode mode;
            IReadOnlyList<Guid> records;

            // Null and empty mean different things here, which is why this is not simply `?? []`.
            // Null is no record-type filter at all and draws every active type, the way the graph
            // page opens. An empty list is a filter matching no type, which draws nothing.
            IReadOnlyList<Guid>? types;
            if (graphViewId is Guid viewId)
            {
                // Refused rather than merged or overridden. A caller that has sent both a view and a
                // selection has two different pictures in mind and no answer here would be the one
                // it expected.
                if (displayMode is not null || selectedRecordIds is not null || recordTypeIds is not null)
                {
                    throw new DomainValidationException(
                        "Pass graphViewId or an explicit displayMode, selectedRecordIds and recordTypeIds, not both.");
                }

                GraphView view = await views.GetAsync(viewId, cancellationToken).ConfigureAwait(false)
                    ?? throw new RecordCommandNotFoundException("Graph view was not found in this domain.");

                // Reproduced verbatim, an empty record-type list included: that is what the operator
                // saved, and a view that draws nothing should draw nothing here too rather than be
                // silently widened into one that draws everything.
                (mode, records, types) = (view.DisplayMode, view.RecordIds, view.RecordTypeIds);
            }
            else
            {
                mode = RemoteGraphProjection.ParseMode(displayMode);
                records = selectedRecordIds ?? [];
                types = recordTypeIds;

                // Named rather than answered with an empty graph. A caller that sent an empty list
                // has almost certainly built it from a selection that came out empty, and telling it
                // so is far better than handing back a graph it will read as "nothing matches".
                if (types is { Count: 0 })
                {
                    throw new DomainValidationException(
                        "A graph filtered to no record types draws nothing. Omit recordTypeIds to draw every active type.");
                }

                // The same rule a saved graph view is held to: connected and isolated are relative to
                // something, so there has to be something to be relative to. Applying it here rather
                // than inventing it means the two surfaces agree about the same request.
                if (mode != RelationshipGraphDisplayMode.All && records.Count == 0 && focusRecordId is null)
                {
                    throw new DomainValidationException(
                        "A connected or isolated graph needs at least one selectedRecordId or a focusRecordId to be relative to.");
                }
            }

            // The domain's own limits, not this build's ceiling. A remote caller drawing more than
            // the operator configured would be drawing past the boundary the settings page exists to
            // set, and the way to draw more is to raise that setting.
            GraphConfiguration configuration = await settings.GetAsync(cancellationToken).ConfigureAwait(false);
            int nodes = Math.Min(nodeLimit ?? configuration.NodeLimit, configuration.NodeLimit);
            int edges = Math.Min(edgeLimit ?? configuration.EdgeLimit, configuration.EdgeLimit);

            RelationshipGraphResult result = await graph.QueryAsync(new RelationshipGraphQuery(
                search, relationshipTypeId, focusRecordId, depth, nodes, edges, mode, records, types),
                cancellationToken).ConfigureAwait(false);

            return new RemoteGraph(
                [.. result.Nodes.Select(node => new RemoteGraphNode(
                    node.RecordId, node.RecordTypeId, node.RecordTypeName, node.DisplayName,
                    node.Distance, node.ImageId, node.RecordTypeSymbol))],
                [.. result.Edges.Select(edge => new RemoteGraphEdge(
                    edge.RelationshipId, edge.RelationshipTypeId, edge.Label,
                    edge.Directionality.ToString().ToLowerInvariant(),
                    edge.SourceRecordId, edge.TargetRecordId, edge.Note, edge.IsExpired))],
                result.NodesTruncated,
                result.EdgesTruncated,
                nodes,
                edges);
        });
}

/// <summary>
/// Changing saved graph views, under the same <c>views.manage</c> grant the record views use and for
/// the same reason: an arrangement of a graph alters nothing a record can hold. There is no duplicate
/// tool because the application has no duplicate command for a graph view, and inventing one only for
/// the remote surface would make the two disagree.
/// </summary>
[McpServerToolType]
[RemoteToolScopes("views.manage")]
public sealed class MonkeysphereGraphViewWriteTools
{
    [McpServerTool(Name = "create_graph_view", ReadOnly = false, Destructive = false)]
    [Description("Saves a graph view in a domain. Requires views.manage, an explicit domainId and a name. displayMode is all, connected or isolated, and connected or isolated needs at least one selectedRecordId. Every record type must be active, and every selected or positioned record must belong to one of the record types listed, so a view cannot remember a node it would not draw. nodePositions remember a hand-made arrangement and viewport remembers where the canvas was looking. Not idempotent: calling twice saves two views.")]
    public static Task<CallToolResult> CreateAsync(
        IGraphViewService views,
        ICurrentDomainScope currentDomain,
        IHttpContextAccessor accessor,
        Guid domainId,
        string name,
        string? displayMode = null,
        IReadOnlyList<Guid>? selectedRecordIds = null,
        IReadOnlyList<Guid>? recordTypeIds = null,
        IReadOnlyList<RemoteGraphNodePosition>? nodePositions = null,
        RemoteGraphViewport? viewport = null,
        CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, async () =>
        {
            RemoteTagAuthority.Demand(accessor, "views.manage");
            using IDisposable domain = currentDomain.Use(domainId);
            return RemoteGraphProjection.Map(await views.CreateAsync(
                Request(name, displayMode, selectedRecordIds, recordTypeIds, nodePositions, viewport),
                cancellationToken).ConfigureAwait(false));
        });

    [McpServerTool(Name = "update_graph_view", ReadOnly = false, Destructive = false)]
    [Description("Replaces a saved graph view. Requires views.manage, an explicit domainId, id and name. This is a replacement rather than a merge: positions and a viewport left out are forgotten rather than kept, because a caller resetting an arrangement has no other way to say so. Read the view with get_graph_view first and send back what you mean to keep. A graph view carries no revision and the last write wins, as it does in the browser.")]
    public static Task<CallToolResult> UpdateAsync(
        IGraphViewService views,
        ICurrentDomainScope currentDomain,
        IHttpContextAccessor accessor,
        Guid domainId,
        Guid id,
        string name,
        string? displayMode = null,
        IReadOnlyList<Guid>? selectedRecordIds = null,
        IReadOnlyList<Guid>? recordTypeIds = null,
        IReadOnlyList<RemoteGraphNodePosition>? nodePositions = null,
        RemoteGraphViewport? viewport = null,
        CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, async () =>
        {
            RemoteTagAuthority.Demand(accessor, "views.manage");
            using IDisposable domain = currentDomain.Use(domainId);
            _ = await views.GetAsync(id, cancellationToken).ConfigureAwait(false)
                ?? throw new RecordCommandNotFoundException("Graph view was not found in this domain.");
            return RemoteGraphProjection.Map(await views.UpdateAsync(
                id,
                Request(name, displayMode, selectedRecordIds, recordTypeIds, nodePositions, viewport),
                cancellationToken).ConfigureAwait(false));
        });

    [McpServerTool(Name = "delete_graph_view", ReadOnly = false, Destructive = true)]
    [Description("Deletes a saved graph view. Requires views.manage, an explicit domainId and id. Destroys the arrangement and cannot be undone; no record, relationship or record type is touched. Deleting a view that is already gone reports not_found.")]
    public static Task<CallToolResult> DeleteAsync(
        IGraphViewService views,
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
                ? new RemoteGraphViewDeletion(id)
                : throw new RecordCommandNotFoundException("Graph view was not found in this domain.");
        });

    private static SaveGraphViewRequest Request(
        string name,
        string? displayMode,
        IReadOnlyList<Guid>? selectedRecordIds,
        IReadOnlyList<Guid>? recordTypeIds,
        IReadOnlyList<RemoteGraphNodePosition>? nodePositions,
        RemoteGraphViewport? viewport)
    {
        // Null entries are rejected here rather than reaching the service, which would see a
        // NullReferenceException where a caller deserves a named refusal.
        if (nodePositions is not null && nodePositions.Any(position => position is null))
        {
            throw new DomainValidationException("Node positions must not contain null entries.");
        }

        return new(
            name,
            RemoteGraphProjection.ParseMode(displayMode),
            selectedRecordIds ?? [],
            recordTypeIds ?? [],
            [.. (nodePositions ?? []).Select(position => new GraphViewNodePosition(position.RecordId, position.X, position.Y))],
            viewport is null ? null : new GraphViewViewport(viewport.PanX, viewport.PanY, viewport.Zoom));
    }
}

/// <summary>What a delete reports, so the result names the view that is now gone.</summary>
public sealed record RemoteGraphViewDeletion(Guid Id, bool Deleted = true);
