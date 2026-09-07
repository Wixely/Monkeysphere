namespace Monkeysphere.Core;

public sealed record RecordDeletionImpact(long FieldValueCount, long AliasCount, long ImageCount, long OriginalImageBytes,
    long RelationshipCount, long ReminderCount, long GraphSelectionCount, long GraphPositionCount, long AffectedGraphViewCount,
    long ImportFingerprintCount, long ImportedPropertyCount);

public sealed record RecordDeletionPreview(Guid PreviewId, Guid RecordId, string RecordRevision, string ImpactRevision,
    RecordDeletionImpact Impact, DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc, bool Applied = false);

public sealed record RecordDeletionStatus(RecordCommandReceipt Receipt, bool MediaCleanupPending);

public interface IRecordDeletionStore
{
    Task<RecordDeletionPreview> PreviewDeletionAsync(RecordCommandIdentity identity, Guid id, string expectedRevision,
        DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<RecordDeletionPreview> GetDeletionPreviewAsync(RecordCommandIdentity identity, Guid previewId,
        DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<RecordCommandReceipt> ApplyDeletionAsync(RecordCommandIdentity identity, Guid id, string expectedRevision, Guid? previewId,
        DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<RecordDeletionStatus> GetDeletionStatusAsync(RecordCommandIdentity identity, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task CleanupDeletionPreviewsAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<bool> TryCleanupRecordMediaAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task CleanupPendingRecordMediaAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
}

public sealed class RecordDeletionService(IRecordDeletionStore deletions, TimeProvider timeProvider)
{
    public Task<RecordDeletionPreview> PreviewAsync(RecordCommandIdentity identity, Guid id, string expectedRevision,
        CancellationToken cancellationToken = default) => deletions.PreviewDeletionAsync(identity, id, expectedRevision, timeProvider.GetUtcNow(), cancellationToken);

    public Task<RecordDeletionPreview> GetPreviewAsync(RecordCommandIdentity identity, Guid previewId,
        CancellationToken cancellationToken = default) => deletions.GetDeletionPreviewAsync(identity, previewId, timeProvider.GetUtcNow(), cancellationToken);

    public async Task<RecordDeletionStatus> DeleteAsync(RecordCommandIdentity identity, Guid id, string expectedRevision, Guid? previewId,
        CancellationToken cancellationToken = default)
    {
        RecordCommandReceipt receipt = await deletions.ApplyDeletionAsync(identity, id, expectedRevision, previewId, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        _ = await deletions.TryCleanupRecordMediaAsync(receipt.Items.Single().Id, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return await deletions.GetDeletionStatusAsync(identity, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    public Task<RecordDeletionStatus> GetStatusAsync(RecordCommandIdentity identity, CancellationToken cancellationToken = default) =>
        deletions.GetDeletionStatusAsync(identity, timeProvider.GetUtcNow(), cancellationToken);
}
