namespace Monkeysphere.Core;

public sealed record RecordBatchInput(string Operation, Guid? RecordTypeId = null, string? DisplayName = null,
    IReadOnlyList<FieldValueInput>? Values = null, IReadOnlyList<string>? Aliases = null,
    Guid? Id = null, string? ExpectedRevision = null, IReadOnlyList<RecordPatchChange>? Changes = null);

public sealed record RecordBatchPreviewItem(int Index, string Operation, Guid Id, Guid? RecordTypeId,
    string? DisplayName, int SuppliedFieldCount, int PatchChangeCount);

public sealed record RecordBatchPreview(Guid PreviewId, DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<RecordBatchPreviewItem> Items, bool Applied = false);

public sealed class RecordPreviewException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public interface IRecordBatchStore
{
    Task<RecordBatchPreview?> FindPreviewAsync(RecordCommandIdentity identity, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<RecordBatchPreview> SavePreviewAsync(RecordCommandIdentity identity, IReadOnlyList<PreparedRecordMutation> mutations,
        IReadOnlyList<RecordBatchPreviewItem> items, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<RecordBatchPreview> GetPreviewAsync(RecordCommandIdentity identity, Guid previewId, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<RecordCommandReceipt> ApplyPreviewAsync(RecordCommandIdentity identity, Guid previewId, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task CleanupExpiredPreviewsAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
}

public sealed class RecordBatchService(RecordCommandService records, IRecordBatchStore previews, TimeProvider timeProvider)
{
    public async Task<RecordBatchPreview> PreviewAsync(RecordCommandIdentity identity, IReadOnlyList<RecordBatchInput> operations,
        CancellationToken cancellationToken = default)
    {
        RecordBatchPreview? replay = await previews.FindPreviewAsync(identity, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (replay is not null) return replay;
        if (operations.Count is < 1 or > RecordCommandLimits.MaximumBatchRecords)
            throw new DomainValidationException("A batch requires 1-100 record operations.");
        List<PreparedRecordMutation> prepared = [];
        List<RecordBatchPreviewItem> items = [];
        HashSet<Guid> recordIds = [];
        int preparedBytes = 0;
        foreach (RecordBatchInput operation in operations)
        {
            PreparedRecordMutation mutation;
            switch (operation.Operation)
            {
                case "create" when operation.RecordTypeId is Guid typeId && operation.DisplayName is not null && operation.Values is not null &&
                    operation.Id is null && operation.ExpectedRevision is null && operation.Changes is null:
                    PreparedRecord record = await records.PrepareCreateAsync(typeId, operation.DisplayName, operation.Values,
                        operation.Aliases, cancellationToken).ConfigureAwait(false);
                    mutation = new(RecordMutationKind.Create, Guid.CreateVersion7(), record);
                    items.Add(new(items.Count, "create", mutation.Id, typeId, record.DisplayName, operation.Values.Count, 0));
                    break;
                case "patch" when operation.Id is Guid id && operation.ExpectedRevision is not null && operation.Changes is not null &&
                    operation.RecordTypeId is null && operation.DisplayName is null && operation.Values is null && operation.Aliases is null:
                    mutation = await records.PreparePatchAsync(id, operation.ExpectedRevision, operation.Changes, cancellationToken).ConfigureAwait(false);
                    // Do not disclose unmentioned record contents to a write-only credential.
                    items.Add(new(items.Count, "patch", id, null, null, 0, operation.Changes.Count));
                    break;
                default:
                    throw new DomainValidationException("Use an explicit create or patch batch operation with only its applicable inputs.");
            }
            if (!recordIds.Add(mutation.Id)) throw new DomainValidationException("A batch cannot target a record more than once.");
            preparedBytes += System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(mutation).Length;
            if (preparedBytes > RecordCommandLimits.MaximumPreviewBytes)
                throw new RecordPreviewException("limit_exceeded", "The prepared preview exceeds 1 MiB. Split the batch into smaller requests.");
            prepared.Add(mutation);
        }
        return await previews.SavePreviewAsync(identity, prepared, items, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    public Task<RecordBatchPreview> GetAsync(RecordCommandIdentity identity, Guid previewId, CancellationToken cancellationToken = default) =>
        previews.GetPreviewAsync(identity, previewId, timeProvider.GetUtcNow(), cancellationToken);

    public Task<RecordCommandReceipt> ApplyAsync(RecordCommandIdentity identity, Guid previewId, CancellationToken cancellationToken = default) =>
        previews.ApplyPreviewAsync(identity, previewId, timeProvider.GetUtcNow(), cancellationToken);
}
