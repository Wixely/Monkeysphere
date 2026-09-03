namespace Monkeysphere.Core;

public sealed record GraphConfiguration(bool WarnUnsavedChanges = true);

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
        await store.SaveAsync(configuration, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return configuration;
    }
}
