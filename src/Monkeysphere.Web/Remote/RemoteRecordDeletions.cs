using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

public sealed partial class RemoteRecordWriter
{
    public Task<CallToolResult> PreviewDeletionAsync(Guid domainId, Guid id, string expectedRevision, Guid idempotencyKey,
        CancellationToken cancellationToken) => RunAsync(domainId, "records.delete.preview", async () =>
    {
        string hash = CommandRequestHash.Compute(new { contract = 1, domainId, id, expectedRevision });
        RecordCommandIdentity identity = identities.Create(domainId, "records.delete", "records.delete", idempotencyKey, hash);
        using IDisposable domain = currentDomain.Use(domainId);
        return await deletions.PreviewAsync(identity, id, expectedRevision, cancellationToken).ConfigureAwait(false);
    });

    public Task<CallToolResult> GetDeletionPreviewAsync(Guid domainId, Guid previewId, CancellationToken cancellationToken) =>
        RunAsync(domainId, "records.delete.inspect", async () =>
    {
        RecordCommandIdentity identity = identities.Create(domainId, "records.delete", "records.delete", Guid.CreateVersion7(), new string('0', 64));
        using IDisposable domain = currentDomain.Use(domainId);
        return await deletions.GetPreviewAsync(identity, previewId, cancellationToken).ConfigureAwait(false);
    });

    public Task<CallToolResult> DeleteAsync(Guid domainId, Guid id, string expectedRevision, Guid? previewId, Guid idempotencyKey,
        CancellationToken cancellationToken) => RunAsync(domainId, "records.delete", async () =>
    {
        string hash = CommandRequestHash.Compute(new { contract = 1, domainId, id, expectedRevision, previewId });
        RecordCommandIdentity identity = identities.Create(domainId, "records.delete", "records.delete", idempotencyKey, hash);
        using IDisposable domain = currentDomain.Use(domainId);
        return await deletions.DeleteAsync(identity, id, expectedRevision, previewId, cancellationToken).ConfigureAwait(false);
    });

    public Task<CallToolResult> GetDeletionStatusAsync(Guid domainId, Guid idempotencyKey, CancellationToken cancellationToken) =>
        RunAsync(domainId, "records.delete.status", async () =>
    {
        RecordCommandIdentity identity = identities.Create(domainId, "records.delete", "records.delete", idempotencyKey, new string('0', 64));
        using IDisposable domain = currentDomain.Use(domainId);
        return await deletions.GetStatusAsync(identity, cancellationToken).ConfigureAwait(false);
    });
}

[McpServerToolType]
[RemoteToolScopes("records.delete")]
public sealed class MonkeysphereRecordDeletionTools
{
    [McpServerTool(Name = "preview_record_deletion", ReadOnly = false, Destructive = false)]
    [Description("Previews deletion of one record and counts its fields, aliases, images, links, reminders, graph references and import provenance. Requires records.delete, explicit domainId, id, expectedRevision and preview idempotencyKey. Does not delete data. The credential-bound preview expires in 15 minutes; inspect its impact before applying.")]
    public static Task<CallToolResult> PreviewAsync(RemoteRecordWriter writer, Guid domainId, Guid id, string expectedRevision,
        Guid idempotencyKey, CancellationToken cancellationToken = default) =>
        writer.PreviewDeletionAsync(domainId, id, expectedRevision, idempotencyKey, cancellationToken);

    [McpServerTool(Name = "get_record_deletion_preview", ReadOnly = true, Destructive = false)]
    [Description("Gets a deletion impact preview and its expiry/applied state for the same credential and domain. Requires records.delete. Does not extend its lifetime or revalidate its impact.")]
    public static Task<CallToolResult> GetPreviewAsync(RemoteRecordWriter writer, Guid domainId, Guid previewId, CancellationToken cancellationToken = default) =>
        writer.GetDeletionPreviewAsync(domainId, previewId, cancellationToken);

    [McpServerTool(Name = "delete_record", ReadOnly = false, Destructive = true)]
    [Description("Deletes one record. Requires records.delete, domainId, id, expectedRevision and idempotencyKey. Dependent data requires a reviewed previewId; omission is allowed only when the transaction finds no dependencies. Record/impact changes reject the preview. Database deletion and receipt commit atomically; media cleanup is durable and may remain pending. Identical retries replay the receipt for 24 hours. Related records and view definitions remain.")]
    public static Task<CallToolResult> DeleteAsync(RemoteRecordWriter writer, Guid domainId, Guid id, string expectedRevision,
        Guid idempotencyKey, Guid? previewId = null, CancellationToken cancellationToken = default) =>
        writer.DeleteAsync(domainId, id, expectedRevision, previewId, idempotencyKey, cancellationToken);

    [McpServerTool(Name = "get_record_deletion_status", ReadOnly = true, Destructive = false)]
    [Description("Gets the committed deletion receipt and current mediaCleanupPending flag using the owning credential, domainId and apply idempotencyKey. Requires records.delete. Status is retained for the 24-hour receipt window; background cleanup continues independently.")]
    public static Task<CallToolResult> GetStatusAsync(RemoteRecordWriter writer, Guid domainId, Guid idempotencyKey, CancellationToken cancellationToken = default) =>
        writer.GetDeletionStatusAsync(domainId, idempotencyKey, cancellationToken);
}
