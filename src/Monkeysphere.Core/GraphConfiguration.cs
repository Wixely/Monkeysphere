namespace Monkeysphere.Core;

/// <summary>
/// How much of the graph a deployment draws, and whether it guards unsaved layout changes. The
/// limits ship at the boundary the performance notes cover and can be raised from Settings, which
/// is the point: an operator who wants to see more of their own data should be able to try, and
/// find out on their own hardware rather than be told no.
/// </summary>
public sealed record GraphConfiguration(
    bool WarnUnsavedChanges = true,
    int NodeLimit = RelationshipGraphService.DefaultNodes,
    int EdgeLimit = RelationshipGraphService.DefaultEdges,
    bool KeepRecordsApart = true)
{
    /// <summary>True when the graph is drawing more than the boundary the load evidence covers.</summary>
    public bool IsRaised =>
        NodeLimit > RelationshipGraphService.DefaultNodes || EdgeLimit > RelationshipGraphService.DefaultEdges;

    /// <summary>
    /// Clamped rather than rejected, for values arriving from storage. A row written by an older
    /// build, or edited by hand, must leave the graph drawing something rather than nothing; the
    /// service validates instead when the value comes from an operator who can be told.
    /// </summary>
    public static GraphConfiguration Clamped(bool warnUnsavedChanges, int nodeLimit, int edgeLimit, bool keepRecordsApart) =>
        new(warnUnsavedChanges,
            Math.Clamp(nodeLimit, RelationshipGraphService.MinimumNodes, RelationshipGraphService.MaximumNodes),
            Math.Clamp(edgeLimit, RelationshipGraphService.MinimumEdges, RelationshipGraphService.MaximumEdges),
            keepRecordsApart);
}

public interface IGraphSettingsStore
{
    Task<GraphConfiguration> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        GraphConfiguration configuration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

public interface IGraphSettingsService
{
    Task<GraphConfiguration> GetAsync(CancellationToken cancellationToken = default);

    Task<GraphConfiguration> SaveAsync(
        GraphConfiguration configuration,
        CancellationToken cancellationToken = default);
}

public sealed class GraphSettingsService(IGraphSettingsStore store, TimeProvider timeProvider) : IGraphSettingsService
{
    public Task<GraphConfiguration> GetAsync(CancellationToken cancellationToken = default) =>
        store.GetAsync(cancellationToken);

    public async Task<GraphConfiguration> SaveAsync(
        GraphConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        // Rejected rather than clamped here. This value came from somebody who typed it, and
        // silently saving a different number than they asked for teaches them nothing.
        if (configuration.NodeLimit < RelationshipGraphService.MinimumNodes ||
            configuration.NodeLimit > RelationshipGraphService.MaximumNodes)
        {
            throw new DomainValidationException(
                $"Graph node limit must be between {RelationshipGraphService.MinimumNodes:N0} and {RelationshipGraphService.MaximumNodes:N0}.");
        }

        if (configuration.EdgeLimit < RelationshipGraphService.MinimumEdges ||
            configuration.EdgeLimit > RelationshipGraphService.MaximumEdges)
        {
            throw new DomainValidationException(
                $"Graph edge limit must be between {RelationshipGraphService.MinimumEdges:N0} and {RelationshipGraphService.MaximumEdges:N0}.");
        }

        await store.SaveAsync(configuration, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return configuration;
    }
}
