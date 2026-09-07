namespace Monkeysphere.Core;

public static class RemoteUploadLimits
{
    public const int MaximumChunkBytes = 256 * 1024;
    public const int MaximumChunksPerUpload = 256;
    public const int MaximumContactBytes = VCardParser.MaximumBytes;
    public const long MaximumReservedBytes = 64L * 1024 * 1024;
    public const int MaximumActiveSessions = 64;
    public const int MaximumActiveSessionsPerDomain = 8;
    public const int MaximumActiveSessionsPerOwner = 4;
    public const int MaximumRetainedSessions = 1000;
    public const int LifetimeMinutes = 60;
    public const int RetryWindowHours = 24;
    public const int TombstoneDays = 7;
}

public sealed record UploadOwner(Guid DomainId, string CredentialFingerprint, string CorrelationId = "")
{
    public void Validate()
    {
        if (DomainId == Guid.Empty || CredentialFingerprint is not { Length: 64 } || !CredentialFingerprint.All(char.IsAsciiHexDigit) || CorrelationId.Length > 128)
            throw new DomainValidationException("The upload owner is invalid.");
    }
}

public sealed record UploadRequest(long ByteLength, string Sha256, string ContentType, int MaximumChunkBytes = RemoteUploadLimits.MaximumChunkBytes);
public sealed record UploadAudit(Guid DomainId, Guid UploadId, string Action, string Outcome, string CorrelationId);
public sealed record UploadStatus(Guid UploadId, long ByteLength, long AcceptedBytes, string State,
    DateTimeOffset ExpiresAtUtc, int MaximumChunkBytes = RemoteUploadLimits.MaximumChunkBytes);
public sealed class UploadException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>Credential-bound contact staging. Sealing verifies bytes, not vCard semantics or permission to import.</summary>
public interface IRemoteUploadStore
{
    Task<UploadStatus> BeginAsync(UploadOwner owner, Guid idempotencyKey, UploadRequest request, CancellationToken cancellationToken = default);
    Task<UploadStatus> WriteAsync(UploadOwner owner, Guid uploadId, long offset, byte[] content, string sha256, CancellationToken cancellationToken = default);
    Task<UploadStatus> GetAsync(UploadOwner owner, Guid uploadId, CancellationToken cancellationToken = default);
    Task<UploadStatus> SealAsync(UploadOwner owner, Guid uploadId, CancellationToken cancellationToken = default);
    Task<UploadStatus> CancelAsync(UploadOwner owner, Guid uploadId, CancellationToken cancellationToken = default);
    Task CopySealedToAsync(UploadOwner owner, Guid uploadId, Stream destination, CancellationToken cancellationToken = default);
    Task<T> ReadSealedAsync<T>(UploadOwner owner, Guid uploadId, Func<Stream, CancellationToken, Task<T>> read,
        CancellationToken cancellationToken = default);
    Task CleanupAsync(CancellationToken cancellationToken = default);
    Task RecordAuditAsync(UploadAudit entry, CancellationToken cancellationToken = default);
}

public sealed class ContactUploadService(IRemoteUploadStore uploads)
{
    public async Task<IReadOnlyList<VCard>> ValidateAsync(UploadOwner owner, Guid uploadId, CancellationToken cancellationToken = default)
    {
        _ = await uploads.SealAsync(owner, uploadId, cancellationToken).ConfigureAwait(false);
        return await uploads.ReadSealedAsync(owner, uploadId, VCardParser.ParseAsync, cancellationToken).ConfigureAwait(false);
    }
}
