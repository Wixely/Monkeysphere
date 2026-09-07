namespace Monkeysphere.Core;

public static class ContactImportPreviewLimits
{
    public const int MaximumBytes = 32 * 1024 * 1024;
    public const long MaximumStoredBytes = 128L * 1024 * 1024;
    public const int MaximumActive = 32;
    public const int MaximumActivePerDomain = 8;
    public const int MaximumActivePerOwner = 4;
    public const int MaximumRetained = 1000;
    public const int LifetimeMinutes = 15;
    public const int MaximumPageSize = 100;
}

public sealed record ContactImportPreviewHandle(Guid PreviewId, Guid UploadId, Guid RecordTypeId,
    string RecordTypeName, string Revision, int ContactCount, DateTimeOffset ExpiresAtUtc);

public sealed record ContactImportEvidenceChunk(int ContactIndex, int Offset, int TotalBytes, byte[] Content);

/// <summary>Server-owned temporary previews. Payloads are trusted application results, never client-supplied previews.</summary>
public interface IContactImportPreviewStore
{
    Task<T> WithPreviewAsync<T>(UploadOwner owner, Guid previewId, Func<VCardImportPreview, DateTimeOffset, CancellationToken, Task<T>> use,
        CancellationToken cancellationToken = default);
    Task<ContactImportPreviewHandle?> FindAsync(UploadOwner owner, Guid uploadId, Guid idempotencyKey, CancellationToken cancellationToken = default);
    Task<ContactImportPreviewHandle> SaveAsync(UploadOwner owner, Guid uploadId, Guid idempotencyKey, VCardImportPreview preview,
        CancellationToken cancellationToken = default);
    Task<ContactImportPreviewHandle> GetAsync(UploadOwner owner, Guid previewId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VCardContactPreview>> ReadContactsAsync(UploadOwner owner, Guid previewId, int page, int pageSize,
        CancellationToken cancellationToken = default);
    Task<ContactImportEvidenceChunk> ReadEvidenceAsync(UploadOwner owner, Guid previewId, int contactIndex, int offset, int count,
        CancellationToken cancellationToken = default);
}

public sealed class ContactImportPreviewService(IRemoteUploadStore uploads, IContactImportPreviewStore previews,
    IVCardService vcards, ICurrentDomain currentDomain)
{
    public async Task<ContactImportPreviewHandle> CreateAsync(UploadOwner owner, Guid uploadId, Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        owner.Validate();
        if (owner.DomainId != currentDomain.Id) throw new DomainValidationException("The preview domain does not match the selected domain.");
        ContactImportPreviewHandle? existing = await previews.FindAsync(owner, uploadId, idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null) return existing;
        _ = await uploads.SealAsync(owner, uploadId, cancellationToken).ConfigureAwait(false);
        VCardImportPreview preview = await uploads.ReadSealedAsync(owner, uploadId,
            vcards.PreviewAsync, cancellationToken).ConfigureAwait(false);
        return await previews.SaveAsync(owner, uploadId, idempotencyKey, preview, cancellationToken).ConfigureAwait(false);
    }
}
