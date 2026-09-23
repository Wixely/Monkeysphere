using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

/// <summary>
/// How much of the graph a domain draws, and whether it nudges records clear of one another. This
/// is the deployment's own configuration rather than a record, so it carries no revision and no
/// idempotency key: there is one row, the last write wins, and writing the same values twice is
/// the same as writing them once.
/// </summary>
public sealed record RemoteGraphSettings(
    bool WarnUnsavedChanges,
    int NodeLimit,
    int EdgeLimit,
    bool KeepRecordsApart,
    bool IsRaised,
    int MinimumNodeLimit,
    int MaximumNodeLimit,
    int MinimumEdgeLimit,
    int MaximumEdgeLimit,
    int DefaultNodeLimit,
    int DefaultEdgeLimit);

[McpServerToolType]
public sealed class MonkeysphereGraphSettingsTools
{
    [RemoteToolScopes("records.read", "structure.write")]
    [McpServerTool(Name = "get_graph_settings", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(RemoteGraphSettings))]
    [Description("Gets one domain's relationship-graph configuration: how many records and relationships it will draw, whether it warns about unsaved layout changes, and whether it keeps records from overlapping. Also returns the bounds a limit must lie within and the shipped defaults, so a caller can tell a raised limit from the measured one without knowing this build's numbers. Requires records.read or structure.write. Omitted domainId selects Default.")]
    public static Task<CallToolResult> GetAsync(
        IGraphSettingsService settings,
        ICurrentDomainScope currentDomain,
        IHttpContextAccessor accessor,
        Guid? domainId = null,
        CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, async () =>
        {
            RemoteReadAuthorization.Demand(accessor);
            using IDisposable domain = currentDomain.Use(domainId ?? MonkeysphereDomains.DefaultId);
            return Map(await settings.GetAsync(cancellationToken).ConfigureAwait(false));
        });

    [RemoteToolScopes("structure.write")]
    [McpServerTool(Name = "set_graph_settings", ReadOnly = false, Destructive = false)]
    [Description("Sets one domain's relationship-graph configuration. Requires structure.write and an explicit domainId. Every value is optional and an omitted one is left as it stands. A limit outside the bounds get_graph_settings reports is refused rather than quietly adjusted, because it came from somebody who can be told. Raising a limit past the shipped default draws more than the measured performance boundary covers. Affects drawing only; no record changes.")]
    public static Task<CallToolResult> SetAsync(
        IGraphSettingsService settings,
        ICurrentDomainScope currentDomain,
        IHttpContextAccessor accessor,
        Guid domainId,
        bool? warnUnsavedChanges = null,
        int? nodeLimit = null,
        int? edgeLimit = null,
        bool? keepRecordsApart = null,
        CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, async () =>
        {
            RemoteTagAuthority.Demand(accessor, "structure.write");
            using IDisposable domain = currentDomain.Use(domainId);
            GraphConfiguration current = await settings.GetAsync(cancellationToken).ConfigureAwait(false);

            // Merged onto what is stored rather than replacing it. A caller raising the node limit
            // should not have to restate the other three to avoid resetting them.
            GraphConfiguration next = current with
            {
                WarnUnsavedChanges = warnUnsavedChanges ?? current.WarnUnsavedChanges,
                NodeLimit = nodeLimit ?? current.NodeLimit,
                EdgeLimit = edgeLimit ?? current.EdgeLimit,
                KeepRecordsApart = keepRecordsApart ?? current.KeepRecordsApart,
            };
            return Map(await settings.SaveAsync(next, cancellationToken).ConfigureAwait(false));
        });

    private static RemoteGraphSettings Map(GraphConfiguration configuration) => new(
        configuration.WarnUnsavedChanges,
        configuration.NodeLimit,
        configuration.EdgeLimit,
        configuration.KeepRecordsApart,
        configuration.IsRaised,
        RelationshipGraphService.MinimumNodes,
        RelationshipGraphService.MaximumNodes,
        RelationshipGraphService.MinimumEdges,
        RelationshipGraphService.MaximumEdges,
        RelationshipGraphService.DefaultNodes,
        RelationshipGraphService.DefaultEdges);
}
