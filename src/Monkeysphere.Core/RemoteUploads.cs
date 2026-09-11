namespace Monkeysphere.Core;

public static class RemoteUploadLimits
{
    public const int MaximumChunkBytes = 256 * 1024;
    public const int MaximumChunksPerUpload = 256;
    public const int MaximumContactBytes = VCardParser.MaximumBytes;
    public const int MaximumImageBytes = 10 * 1024 * 1024;
    public const long MaximumReservedBytes = 64L * 1024 * 1024;
    public const int MaximumActiveSessions = 64;
    public const int MaximumActiveSessionsPerDomain = 8;
    public const int MaximumActiveSessionsPerOwner = 4;
    public const int MaximumRetainedSessions = 1000;
    public const int LifetimeMinutes = 60;
    public const int RetryWindowHours = 24;
    public const int TombstoneDays = 7;
}

/// <summary>
/// What a staged upload is allowed to become. The purpose is declared when the session begins,
/// persisted with it, and rechecked when the bytes are consumed, so contact bytes cannot be
/// attached as a record image or the reverse.
/// </summary>
public static class UploadPurposes
{
    public const string ContactImport = "contact_import";
    public const string RecordImage = "record_image";

    public static readonly IReadOnlyList<string> All = [ContactImport, RecordImage];

    public static long MaximumBytes(string purpose) => purpose switch
    {
        ContactImport => RemoteUploadLimits.MaximumContactBytes,
        RecordImage => RemoteUploadLimits.MaximumImageBytes,
        _ => throw Invalid(),
    };

    public static bool Accepts(string purpose, string contentType) => purpose switch
    {
        ContactImport => contentType is "text/vcard" or "text/x-vcard",
        RecordImage => contentType is "image/jpeg" or "image/png" or "image/webp",
        _ => throw Invalid(),
    };

    public static string Normalize(string? purpose) =>
        purpose is not null && All.Contains(purpose, StringComparer.Ordinal) ? purpose : throw Invalid();

    private static DomainValidationException Invalid() =>
        new("Upload purpose must be contact_import or record_image.");
}

public sealed record UploadOwner(Guid DomainId, string CredentialFingerprint, string CorrelationId = "")
{
    public void Validate()
    {
        if (DomainId == Guid.Empty || CredentialFingerprint is not { Length: 64 } || !CredentialFingerprint.All(char.IsAsciiHexDigit) || CorrelationId.Length > 128)
            throw new DomainValidationException("The upload owner is invalid.");
    }
}

public sealed record UploadRequest(string Purpose, long ByteLength, string Sha256, string ContentType, int MaximumChunkBytes = RemoteUploadLimits.MaximumChunkBytes);
public sealed record UploadAudit(Guid DomainId, Guid UploadId, string Action, string Outcome, string CorrelationId);
public sealed record UploadStatus(Guid UploadId, string Purpose, long ByteLength, long AcceptedBytes, string State,
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
    public async Task<VCardParseResult> ValidateAsync(UploadOwner owner, Guid uploadId, CancellationToken cancellationToken = default)
    {
        _ = await uploads.SealAsync(owner, uploadId, cancellationToken).ConfigureAwait(false);
        return await uploads.ReadSealedAsync(owner, uploadId, VCardParser.ParseAsync, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Attaches staged bytes to a record as an image. The upload must have been staged for the
/// record_image purpose, so contact bytes cannot arrive here, and the shared image service still
/// performs every existing check: decoding, dimension and pixel bounds, the per-record limit,
/// opaque storage paths and metadata-stripped derivatives.
/// </summary>
public sealed class RecordImageUploadService(IRemoteUploadStore uploads, IRecordImageService images)
{
    public async Task<RecordImage> AttachAsync(UploadOwner owner, Guid uploadId, Guid recordId, string originalFileName,
        CancellationToken cancellationToken = default)
    {
        UploadStatus status = await uploads.GetAsync(owner, uploadId, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(status.Purpose, UploadPurposes.RecordImage, StringComparison.Ordinal))
            throw new UploadException("purpose_mismatch", "This upload was not staged for the record_image purpose.");
        _ = await uploads.SealAsync(owner, uploadId, cancellationToken).ConfigureAwait(false);
        return await uploads.ReadSealedAsync(owner, uploadId,
            (stream, token) => images.AddAsync(recordId, stream, originalFileName, token), cancellationToken).ConfigureAwait(false);
    }
}
