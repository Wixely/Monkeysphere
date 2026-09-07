using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RemoteRecordBatchInput(string Operation, Guid? RecordTypeId = null, string? DisplayName = null,
    IReadOnlyList<RemoteFieldInput>? Values = null, IReadOnlyList<string>? Aliases = null,
    Guid? Id = null, string? ExpectedRevision = null, IReadOnlyList<RemoteRecordPatchChange>? Changes = null)
{
    public RecordBatchInput ToCore()
    {
        if (Values is not null && (Values.Count > RecordCommandLimits.MaximumFields || Values.Any(value => value is null)) ||
            Changes is not null && (Changes.Count is < 1 or > RecordCommandLimits.MaximumPatchChanges || Changes.Any(change => change is null)))
            throw new DomainValidationException("Batch field and patch inputs must be bounded and non-null.");
        return new(Operation, RecordTypeId, DisplayName, Values?.Select(value => value.ToCore()).ToArray(), Aliases,
            Id, ExpectedRevision, Changes?.Select(change => change.ToCore()).ToArray());
    }
}

public sealed partial class RemoteRecordWriter
{
    public Task<CallToolResult> PreviewBatchAsync(Guid domainId, Guid idempotencyKey, IReadOnlyList<RemoteRecordBatchInput> operations,
        CancellationToken cancellationToken) => RunAsync(domainId, "records.batch.preview", async () =>
    {
        string hash = CommandRequestHash.Compute(new { contract = 1, domainId, operations });
        RecordCommandIdentity identity = identities.Create(domainId, "records.write", "records.batch", idempotencyKey, hash);
        using IDisposable domain = currentDomain.Use(domainId);
        if (operations is null || operations.Count is < 1 or > RecordCommandLimits.MaximumBatchRecords || operations.Any(operation => operation is null))
            throw new DomainValidationException("Supply 1-100 non-null create or patch operations in one domain.");
        return await batches.PreviewAsync(identity, operations.Select(operation => operation.ToCore()).ToArray(), cancellationToken).ConfigureAwait(false);
    });

    public Task<CallToolResult> GetBatchPreviewAsync(Guid domainId, Guid previewId, CancellationToken cancellationToken) =>
        RunAsync(domainId, "records.batch.inspect", async () =>
    {
        RecordCommandIdentity identity = identities.Create(domainId, "records.write", "records.batch", Guid.CreateVersion7(), new string('0', 64));
        using IDisposable domain = currentDomain.Use(domainId);
        return await batches.GetAsync(identity, previewId, cancellationToken).ConfigureAwait(false);
    });

    public Task<CallToolResult> ApplyBatchAsync(Guid domainId, Guid previewId, Guid idempotencyKey, CancellationToken cancellationToken) =>
        RunAsync(domainId, "records.batch", async () =>
    {
        string hash = CommandRequestHash.Compute(new { contract = 1, domainId, previewId });
        RecordCommandIdentity identity = identities.Create(domainId, "records.write", "records.batch", idempotencyKey, hash);
        using IDisposable domain = currentDomain.Use(domainId);
        return await batches.ApplyAsync(identity, previewId, cancellationToken).ConfigureAwait(false);
    });
}

[McpServerToolType]
[RemoteToolScopes("records.write")]
public sealed class MonkeysphereRecordBatchTools
{
    [McpServerTool(Name = "preview_record_batch", ReadOnly = false, Destructive = false)]
    [Description("Validates and stores a 15-minute preview of 1-100 explicit create/patch operations in one domain without changing records. Requires records.write, explicit domainId and a preview idempotencyKey. Returns ordered operation summaries and a credential-bound previewId. Inspect the submitted operations and summary before applying; no delete or inter-item references are supported.")]
    public static Task<CallToolResult> PreviewAsync(RemoteRecordWriter writer, Guid domainId, Guid idempotencyKey,
        IReadOnlyList<RemoteRecordBatchInput> operations, CancellationToken cancellationToken = default) =>
        writer.PreviewBatchAsync(domainId, idempotencyKey, operations, cancellationToken);

    [McpServerTool(Name = "get_record_batch_preview", ReadOnly = true, Destructive = false)]
    [Description("Retrieves a record batch preview summary for the same credential and domain, including expiry and whether it was applied. Requires records.write. Does not extend its lifetime or return unmentioned record contents.")]
    public static Task<CallToolResult> GetAsync(RemoteRecordWriter writer, Guid domainId, Guid previewId, CancellationToken cancellationToken = default) =>
        writer.GetBatchPreviewAsync(domainId, previewId, cancellationToken);

    [McpServerTool(Name = "apply_record_batch", ReadOnly = false, Destructive = true)]
    [Description("Applies all operations in a reviewed record batch preview atomically, rechecking record/schema revisions. Requires records.write, explicit domainId, previewId and an apply idempotencyKey. No partial success; identical retries return the committed receipt for 24 hours. Expired, stale or previously consumed previews reject a new apply.")]
    public static Task<CallToolResult> ApplyAsync(RemoteRecordWriter writer, Guid domainId, Guid previewId, Guid idempotencyKey, CancellationToken cancellationToken = default) =>
        writer.ApplyBatchAsync(domainId, previewId, idempotencyKey, cancellationToken);
}
