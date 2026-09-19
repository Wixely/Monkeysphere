using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

/// <summary>
/// Finishes tag renames that the catalogue has already accepted.
///
/// A rename commits in the registry at once but has to be carried into every domain database that
/// stores the old text, and no transaction spans those files. Startup drains the queue once; this
/// keeps at it, so a domain that was briefly locked when the rename happened catches up on its own
/// rather than waiting for the next restart.
/// </summary>
public sealed class TagRenameWorker(
    ITagMaintenance maintenance,
    TimeProvider timeProvider,
    ILogger<TagRenameWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogDrainFailure = LoggerMessage.Define(
        LogLevel.Warning, new EventId(1, "TagRenameDrainFailure"),
        "Queued tag renames could not be applied. They stay queued and will be retried.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            do
            {
                try
                {
                    await maintenance.DrainRenamesAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is System.Data.Common.DbException or IOException)
                {
                    // Nothing is lost: the work stays on the queue for the next pass.
                    LogDrainFailure(logger, exception);
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
