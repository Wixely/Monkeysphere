using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Security;

/// <summary>
/// Warms the activation cache, and — when the deployment gate is off while hidden records exist —
/// says so loudly at startup. Those records stay hidden: nothing observes them until the gate is
/// turned back on, so an operator who has forgotten the setting would otherwise see records simply
/// missing with no explanation anywhere.
/// </summary>
public sealed partial class BackstageStartupWorker(
    IServiceScopeFactory scopes,
    CachedBackstageSessions sessions,
    BackstageAvailability availability,
    IDomainCatalog domains,
    ILogger<BackstageStartupWorker> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await sessions.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (availability.Enabled)
        {
            return;
        }

        int total = 0;
        List<string> affected = [];
        foreach (MonkeysphereDomain domain in domains.Snapshot)
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            using IDisposable _ = scope.ServiceProvider.GetRequiredService<ICurrentDomainScope>().Use(domain.Id);
            int count = await scope.ServiceProvider.GetRequiredService<IBackstageRecordStore>()
                .CountAsync(cancellationToken).ConfigureAwait(false);
            if (count > 0)
            {
                total += count;
                affected.Add($"{domain.Name} ({count})");
            }
        }

        if (total > 0)
        {
            BackstageRecordsStranded(logger, total, string.Join(", ", affected));
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(1, LogLevel.Warning,
        "Backstage is disabled but {Count} record(s) are still held back by backstage policy and cannot be seen or "
        + "edited by anyone: {Domains}. Set Monkeysphere__Backstage__Available to true "
        + "(MONKEYSPHERE_BACKSTAGE_AVAILABLE in the supplied compose file) and restart to reach them again.")]
    private static partial void BackstageRecordsStranded(ILogger logger, int count, string domains);
}
