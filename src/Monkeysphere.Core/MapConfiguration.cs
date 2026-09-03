namespace Monkeysphere.Core;

public sealed record MapConfiguration(bool ExternalTilesEnabled);

public interface IMapSettingsStore
{
    Task<MapConfiguration> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        MapConfiguration configuration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

public interface IMapSettingsService
{
    Task<MapConfiguration> GetAsync(CancellationToken cancellationToken = default);

    Task<MapConfiguration> SaveAsync(
        MapConfiguration configuration,
        CancellationToken cancellationToken = default);
}

public sealed class MapSettingsService(IMapSettingsStore store, TimeProvider timeProvider) : IMapSettingsService
{
    public Task<MapConfiguration> GetAsync(CancellationToken cancellationToken = default) =>
        store.GetAsync(cancellationToken);

    public async Task<MapConfiguration> SaveAsync(
        MapConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        await store.SaveAsync(configuration, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return configuration;
    }
}
