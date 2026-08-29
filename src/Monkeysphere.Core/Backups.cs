namespace Monkeysphere.Core;

public sealed record BackupInfo(
    Guid Id,
    string FileName,
    DateTimeOffset CreatedAtUtc,
    long ByteLength);

public sealed record BackupValidation(
    BackupInfo Backup,
    int FormatVersion,
    int ApplicationSchemaVersion,
    int EntryCount,
    int OriginalImageCount);

public interface IBackupService
{
    Task<BackupInfo> CreateAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackupInfo>> ListAsync(CancellationToken cancellationToken = default);

    Task<BackupValidation> ValidateAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Stream?> OpenAsync(Guid id, CancellationToken cancellationToken = default);

    Task PruneAsync(int retentionCount, CancellationToken cancellationToken = default);
}
