namespace Monkeysphere.Core;

public static class RecordCommandLimits
{
    public const int MaximumFields = 1000;
    public const int MaximumPatchChanges = 1000;
    public const int MaximumBatchRecords = 100;
    public const int MaximumRetainedCommandsPerDomain = 10_000;
    public const int MaximumReceiptBytes = 65_536;
    public const int RetryWindowHours = 24;
    public const int TombstoneRetentionDays = 7;
    public const int PreviewLifetimeMinutes = 15;
    public const int MaximumRetainedPreviewsPerDomain = 100;
    public const int MaximumPreviewBytes = 1_048_576;
    public const int MaximumPendingMediaCleanupPerDomain = 1000;
}
