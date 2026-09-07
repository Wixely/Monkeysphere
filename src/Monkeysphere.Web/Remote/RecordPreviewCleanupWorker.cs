using System.Data.Common;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

public sealed class RecordPreviewCleanupWorker(IServiceScopeFactory scopes, IDomainCatalog domains,
    TimeProvider timeProvider, ILogger<RecordPreviewCleanupWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogCleanupFailure = LoggerMessage.Define(
        LogLevel.Warning, new EventId(1, "RecordPreviewCleanupFailure"), "Expired record previews could not be cleaned. Cleanup will retry.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            do
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMinutes(5), timeProvider, stoppingToken).ConfigureAwait(false);
            } while (!stoppingToken.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }

    public async Task SweepAsync(CancellationToken cancellationToken = default)
    {
        foreach (MonkeysphereDomain domain in domains.Snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                using IDisposable selection = scope.ServiceProvider.GetRequiredService<ICurrentDomainScope>().Use(domain.Id);
                using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                await scope.ServiceProvider.GetRequiredService<IRecordBatchStore>()
                    .CleanupExpiredPreviewsAsync(timeProvider.GetUtcNow(), deadline.Token).ConfigureAwait(false);
                IRecordDeletionStore deletions = scope.ServiceProvider.GetRequiredService<IRecordDeletionStore>();
                await deletions.CleanupDeletionPreviewsAsync(timeProvider.GetUtcNow(), deadline.Token).ConfigureAwait(false);
                await deletions.CleanupPendingRecordMediaAsync(timeProvider.GetUtcNow(), deadline.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is DbException or IOException ||
                exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                LogCleanupFailure(logger, null);
            }
        }
    }
}
