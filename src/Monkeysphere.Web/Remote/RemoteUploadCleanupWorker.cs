using System.Data.Common;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

public sealed class RemoteUploadCleanupWorker(IRemoteUploadStore uploads, TimeProvider timeProvider,
    ILogger<RemoteUploadCleanupWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogCleanupFailure = LoggerMessage.Define(LogLevel.Warning,
        new EventId(1, "UploadCleanupFailure"), "Expired uploads could not be cleaned. Cleanup will retry.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            do
            {
                try
                {
                    using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(10));
                    await uploads.CleanupAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is DbException or IOException || exception is OperationCanceledException && !stoppingToken.IsCancellationRequested)
                {
                    LogCleanupFailure(logger, null);
                }
                await Task.Delay(TimeSpan.FromMinutes(5), timeProvider, stoppingToken).ConfigureAwait(false);
            } while (!stoppingToken.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }
}
