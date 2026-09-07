using System.ComponentModel;
using System.Data.Common;
using System.Text.Json;
using DnaX.RemoteAccess;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

public sealed record RemoteUploadTransferLimits(int MaximumChunkBytes, long MaximumContactBytes, int MaximumChunksPerUpload,
    long MaximumReservedBytes, int MaximumActiveSessions, int MaximumActiveSessionsPerDomain, int MaximumActiveSessionsPerOwner,
    int MaximumRetainedSessions, int LifetimeMinutes, int RetryWindowHours, int TombstoneDays)
{
    public static RemoteUploadTransferLimits For(long requestBytes)
    {
        long chunk = requestBytes <= 8192 ? 0 : Math.Min(RemoteUploadLimits.MaximumChunkBytes, (requestBytes - 8192) / 4 * 3);
        return new((int)chunk, Math.Min(RemoteUploadLimits.MaximumContactBytes, chunk * RemoteUploadLimits.MaximumChunksPerUpload),
            RemoteUploadLimits.MaximumChunksPerUpload, RemoteUploadLimits.MaximumReservedBytes, RemoteUploadLimits.MaximumActiveSessions,
            RemoteUploadLimits.MaximumActiveSessionsPerDomain, RemoteUploadLimits.MaximumActiveSessionsPerOwner, RemoteUploadLimits.MaximumRetainedSessions,
            RemoteUploadLimits.LifetimeMinutes, RemoteUploadLimits.RetryWindowHours, RemoteUploadLimits.TombstoneDays);
    }
}

public sealed record RemoteUploadCompletion(UploadStatus Upload, int ContactCount, bool ContentValidated);

public sealed class RemoteUploadCommands(IRemoteUploadStore uploads, ContactUploadService validator, RemoteCommandIdentityProvider identities,
    IOptions<DnaXRemoteAccessOptions> options, IHttpContextAccessor accessor, ILogger<RemoteUploadCommands> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Action<ILogger, Exception?> LogAuditFailure = LoggerMessage.Define(LogLevel.Warning,
        new EventId(1, "UploadAuditFailure"), "Upload failure audit could not be recorded.");
    private RemoteUploadTransferLimits Limits => RemoteUploadTransferLimits.For(options.Value.Limits.MaximumRequestBodyBytes);

    public Task<CallToolResult> BeginAsync(Guid domainId, string purpose, long byteLength, string sha256, string contentType, Guid idempotencyKey,
        CancellationToken cancellationToken) => RunAsync(domainId, Guid.Empty, "uploads.begin", async owner =>
    {
        if (purpose != "contact_import") throw new DomainValidationException("Only the contact_import upload purpose is implemented.");
        return Adapt(await uploads.BeginAsync(owner, idempotencyKey, new(byteLength, sha256, contentType, Limits.MaximumChunkBytes), cancellationToken).ConfigureAwait(false));
    });

    public Task<CallToolResult> WriteAsync(Guid domainId, Guid uploadId, long offset, string contentBase64, string sha256,
        CancellationToken cancellationToken) => RunAsync(domainId, uploadId, "uploads.write", async owner =>
    {
        int maximum = Limits.MaximumChunkBytes;
        if (maximum == 0 || contentBase64 is null || contentBase64.Length > (maximum + 2) / 3 * 4)
            throw new UploadException("limit_exceeded", "The encoded chunk exceeds the effective transport limit.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(contentBase64); }
        catch (FormatException) { throw new DomainValidationException("Supply valid Base64 chunk content."); }
        if (bytes.Length > maximum) throw new UploadException("limit_exceeded", "The decoded chunk exceeds the effective transport limit.");
        return Adapt(await uploads.WriteAsync(owner, uploadId, offset, bytes, sha256, cancellationToken).ConfigureAwait(false));
    });

    public Task<CallToolResult> StatusAsync(Guid domainId, Guid uploadId, CancellationToken cancellationToken) =>
        RunAsync(domainId, uploadId, "uploads.status", async owner => Adapt(await uploads.GetAsync(owner, uploadId, cancellationToken).ConfigureAwait(false)));

    public Task<CallToolResult> CancelAsync(Guid domainId, Guid uploadId, CancellationToken cancellationToken) =>
        RunAsync(domainId, uploadId, "uploads.cancel", async owner => Adapt(await uploads.CancelAsync(owner, uploadId, cancellationToken).ConfigureAwait(false)));

    public Task<CallToolResult> CompleteAsync(Guid domainId, Guid uploadId, CancellationToken cancellationToken) =>
        RunAsync(domainId, uploadId, "uploads.complete", async owner =>
    {
        IReadOnlyList<VCard> cards = await validator.ValidateAsync(owner, uploadId, cancellationToken).ConfigureAwait(false);
        await uploads.RecordAuditAsync(new(domainId, uploadId, "uploads.complete", "validated", owner.CorrelationId), cancellationToken).ConfigureAwait(false);
        UploadStatus status = await uploads.GetAsync(owner, uploadId, cancellationToken).ConfigureAwait(false);
        if (status.State != "sealed") throw new UploadException("upload_not_ready", "The upload became unavailable during validation.");
        return new RemoteUploadCompletion(Adapt(status), cards.Count, true);
    });

    private UploadStatus Adapt(UploadStatus status) => status with { MaximumChunkBytes = Limits.MaximumChunkBytes };

    private async Task<CallToolResult> RunAsync<T>(Guid domainId, Guid uploadId, string action, Func<UploadOwner, Task<T>> execute)
    {
        try
        {
            UploadOwner owner = identities.CreateUploadOwner(domainId);
            T result = await execute(owner).ConfigureAwait(false);
            JsonElement json = JsonSerializer.SerializeToElement(result, JsonOptions);
            return new() { StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or DomainValidationException or UploadException or DbException or IOException or OperationCanceledException)
        {
            string code = exception switch
            {
                UnauthorizedAccessException => "permission_denied",
                DomainValidationException => "validation_failed",
                UploadException upload => upload.Code,
                _ => "temporarily_unavailable"
            };
            string message = code == "temporarily_unavailable" ? "The upload request could not complete. Inspect status and retry the same request."
                : exception.Message[..Math.Min(exception.Message.Length, 1024)];
            string correlation = accessor.HttpContext?.TraceIdentifier ?? "";
            if (domainId != Guid.Empty)
            {
                try
                {
                    using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(2));
                    await uploads.RecordAuditAsync(new(domainId, uploadId, action, code, correlation[..Math.Min(correlation.Length, 128)]), deadline.Token).ConfigureAwait(false);
                }
                catch (Exception auditException) when (auditException is DbException or IOException or OperationCanceledException) { LogAuditFailure(logger, null); }
            }
            JsonElement json = JsonSerializer.SerializeToElement(new { error = new RemoteWriteError(code, message, correlation) }, JsonOptions);
            return new() { IsError = true, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
        }
    }
}

[McpServerToolType]
[RemoteToolScopes("contacts.import")]
public sealed class MonkeysphereUploadTools
{
    [McpServerTool(Name = "begin_upload", ReadOnly = false, Destructive = false)]
    [Description("Begins a bounded credential/domain-owned upload. Requires contacts.import, explicit domainId, purpose contact_import, byteLength, SHA-256 hex digest, contentType text/vcard or text/x-vcard and retry UUID. Returns an opaque ID and effective chunk size; use compact Base64 JSON. Metadata replay lasts 24 hours; bytes expire after 60 minutes without extension. Uploading does not import records.")]
    public static Task<CallToolResult> BeginAsync(RemoteUploadCommands commands, Guid domainId, string purpose, long byteLength, string sha256,
        string contentType, Guid idempotencyKey, CancellationToken cancellationToken = default) =>
        commands.BeginAsync(domainId, purpose, byteLength, sha256, contentType, idempotencyKey, cancellationToken);

    [McpServerTool(Name = "write_upload_chunk", ReadOnly = false, Destructive = false)]
    [Description("Writes sequential Base64 bytes with SHA-256 hex digest. Requires contacts.import, explicit domainId and the owning credential. Identical offset/bytes retry safely; gaps, changed bytes, bad digests and effective size/chunk-count overflow fail. Discover the current chunk limit before writing.")]
    public static Task<CallToolResult> WriteAsync(RemoteUploadCommands commands, Guid domainId, Guid uploadId, long offset, string contentBase64,
        string sha256, CancellationToken cancellationToken = default) => commands.WriteAsync(domainId, uploadId, offset, contentBase64, sha256, cancellationToken);

    [McpServerTool(Name = "get_upload_status", ReadOnly = true)]
    [Description("Returns owned upload state, accepted bytes and expiry. Requires contacts.import and explicit domainId. Sealed means byte integrity only; use complete_upload for content validation. No uploaded contact content is returned.")]
    public static Task<CallToolResult> StatusAsync(RemoteUploadCommands commands, Guid domainId, Guid uploadId, CancellationToken cancellationToken = default) => commands.StatusAsync(domainId, uploadId, cancellationToken);

    [McpServerTool(Name = "complete_upload", ReadOnly = false, Destructive = false)]
    [Description("Verifies uploaded length/hash and streams strict UTF-8 vCard 3.0/4.0 validation. Requires contacts.import and explicit domainId. Returns contact count and contentValidated, not contact contents. Safe to retry while live; does not extend expiry, import records or create an import preview. Contact preview/apply tools remain unimplemented.")]
    public static Task<CallToolResult> CompleteAsync(RemoteUploadCommands commands, Guid domainId, Guid uploadId, CancellationToken cancellationToken = default) => commands.CompleteAsync(domainId, uploadId, cancellationToken);

    [McpServerTool(Name = "cancel_upload", ReadOnly = false, Destructive = true)]
    [Description("Cancels an owned upload and removes its staged bytes; repeated cancellation is safe. Requires contacts.import and explicit domainId. Terminal metadata remains for bounded retry retention. Existing records are unaffected.")]
    public static Task<CallToolResult> CancelAsync(RemoteUploadCommands commands, Guid domainId, Guid uploadId, CancellationToken cancellationToken = default) => commands.CancelAsync(domainId, uploadId, cancellationToken);
}
