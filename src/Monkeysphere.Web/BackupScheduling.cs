using Microsoft.Extensions.Options;
using Monkeysphere.Core;

namespace Monkeysphere.Web;

public sealed class BackupScheduleOptions
{
    public string Frequency { get; set; } = "Off";
    public TimeOnly Time { get; set; } = new(2, 0);
    public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Sunday;
    public int DayOfMonth { get; set; } = 1;
    public string TimeZone { get; set; } = "UTC";
    public int RetentionCount { get; set; } = 12;
}

public static class BackupScheduleCalculator
{
    public static DateTimeOffset Next(DateTimeOffset nowUtc, BackupScheduleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone);
        DateTime localNow = TimeZoneInfo.ConvertTime(nowUtc, zone).DateTime;
        string frequency = options.Frequency.Trim().ToLowerInvariant();
        DateTime candidate = frequency switch
        {
            "daily" => localNow.Date + options.Time.ToTimeSpan(),
            "weekly" => localNow.Date.AddDays(((int)options.DayOfWeek - (int)localNow.DayOfWeek + 7) % 7) + options.Time.ToTimeSpan(),
            "monthly" => Monthly(localNow.Year, localNow.Month, options.DayOfMonth, options.Time),
            _ => throw new InvalidOperationException("Backup frequency must be Off, Daily, Weekly, or Monthly."),
        };

        if (candidate <= localNow)
        {
            candidate = frequency switch
            {
                "daily" => candidate.AddDays(1),
                "weekly" => candidate.AddDays(7),
                "monthly" => Monthly(localNow.AddMonths(1).Year, localNow.AddMonths(1).Month, options.DayOfMonth, options.Time),
                _ => candidate,
            };
        }

        while (zone.IsInvalidTime(candidate))
        {
            candidate = candidate.AddMinutes(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(candidate, DateTimeKind.Unspecified), zone);
    }

    private static DateTime Monthly(int year, int month, int day, TimeOnly time) =>
        new DateTime(year, month, Math.Clamp(day, 1, DateTime.DaysInMonth(year, month))) + time.ToTimeSpan();
}

public sealed partial class BackupScheduleWorker(
    IBackupService backups,
    IOptions<BackupScheduleOptions> configuredOptions,
    TimeProvider timeProvider,
    ILogger<BackupScheduleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        BackupScheduleOptions options = configuredOptions.Value;
        if (string.Equals(options.Frequency, "Off", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (options.RetentionCount is < 1 or > 1_000 || options.DayOfMonth is < 1 or > 31)
        {
            throw new InvalidOperationException("Backup retention must be 1–1,000 and day of month must be 1–31.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            DateTimeOffset due = BackupScheduleCalculator.Next(now, options);
            await Task.Delay(due - now, timeProvider, stoppingToken).ConfigureAwait(false);
            try
            {
                BackupInfo backup = await backups.CreateAsync(stoppingToken).ConfigureAwait(false);
                await backups.PruneAsync(options.RetentionCount, stoppingToken).ConfigureAwait(false);
                ScheduledBackupCompleted(logger, backup.Id);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                ScheduledBackupFailed(logger, exception);
            }
        }
    }

    [LoggerMessage(1, LogLevel.Information, "Scheduled backup {BackupId} completed and retention was applied.")]
    private static partial void ScheduledBackupCompleted(ILogger logger, Guid backupId);

    [LoggerMessage(2, LogLevel.Error, "Scheduled backup failed.")]
    private static partial void ScheduledBackupFailed(ILogger logger, Exception exception);
}
