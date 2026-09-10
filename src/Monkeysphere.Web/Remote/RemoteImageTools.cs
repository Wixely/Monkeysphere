using System.ComponentModel;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

public sealed record RemoteImageLimits(int MaximumChunkBytes = 16384, int MaximumResponseBytes = 131072,
    long MaximumUploadBytes = RemoteUploadLimits.MaximumImageBytes,
    int MaximumImagesPerRecord = IRecordImageService.MaximumImagesPerRecord,
    int MaximumFileNameCharacters = 200);

public sealed record RemoteImageContent(Guid RecordId, Guid ImageId, string Variant, string ContentType,
    int FormatVersion, string Encoding, string ContentDigest, int Offset, int TotalBytes, int? NextOffset, string ContentBase64);

public sealed class RemoteImageCommands(RecordImageUploadService attachments, IRecordImageService images,
    ICurrentDomainScope domains, RemoteCommandIdentityProvider identities, IRemoteUploadStore uploads,
    IHttpContextAccessor accessor, ILogger<RemoteImageCommands> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly RemoteImageLimits Limits = new();
    private static readonly Action<ILogger, Exception?> LogAuditFailure = LoggerMessage.Define(LogLevel.Warning,
        new EventId(1, "ImageAuditFailure"), "Image failure audit could not be recorded.");

    public Task<CallToolResult> AddAsync(Guid domainId, Guid recordId, Guid uploadId, string? fileName, CancellationToken cancellationToken) =>
        RunAsync(domainId, "images.add", "media.write", async owner =>
        {
            string name = string.IsNullOrWhiteSpace(fileName) ? "upload" : fileName.Trim();
            if (name.Length > Limits.MaximumFileNameCharacters)
                throw new DomainValidationException($"fileName must be at most {Limits.MaximumFileNameCharacters} characters.");
            RecordImage image = await attachments.AttachAsync(owner, uploadId, recordId, name, cancellationToken).ConfigureAwait(false);
            return image;
        }, cancellationToken);

    public Task<CallToolResult> ReadAsync(Guid domainId, Guid recordId, Guid imageId, string variant, int offset, int count,
        CancellationToken cancellationToken) =>
        RunAsync(domainId, "images.read", "media.read", async _ =>
        {
            if (count is < 1 || count > Limits.MaximumChunkBytes)
                throw new DomainValidationException($"Supply count 1-{Limits.MaximumChunkBytes}.");
            if (offset < 0) throw new DomainValidationException("Supply a non-negative offset.");
            RecordImageVariant selected = variant switch
            {
                "preview" => RecordImageVariant.Preview,
                "thumbnail" => RecordImageVariant.Thumbnail,
                "original" => RecordImageVariant.Original,
                _ => throw new DomainValidationException("variant must be preview, thumbnail or original."),
            };
            RecordImageFile file = await images.OpenAsync(recordId, imageId, selected, cancellationToken).ConfigureAwait(false)
                ?? throw new RecordCommandNotFoundException("The image was not found on that record in this domain.");
            byte[] content;
            await using (file.Content)
            {
                using MemoryStream buffer = new();
                await file.Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                content = buffer.ToArray();
            }

            // Matches read_contact_import_evidence and export_contacts: the end offset yields an
            // empty final range, beyond it fails.
            if (offset > content.Length)
                throw new DomainValidationException("The offset is beyond the image. Restart at offset 0.");
            int length = Math.Min(count, content.Length - offset);
            int next = offset + length;
            return new RemoteImageContent(recordId, imageId, variant, file.ContentType, 1, "base64-binary",
                Convert.ToHexString(SHA256.HashData(content)), offset, content.Length,
                next < content.Length ? next : null, Convert.ToBase64String(content, offset, length));
        }, cancellationToken);

    private async Task<CallToolResult> RunAsync<T>(Guid domainId, string action, string scope, Func<UploadOwner, Task<T>> execute,
        CancellationToken cancellationToken)
    {
        try
        {
            UploadOwner owner = identities.CreateUploadOwner(domainId, scope);
            using IDisposable selection = domains.Use(domainId);
            T value = await execute(owner).ConfigureAwait(false);
            await uploads.RecordAuditAsync(new(domainId, Guid.Empty, action, "accepted", owner.CorrelationId), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            JsonElement json = JsonSerializer.SerializeToElement(value, JsonOptions);
            CallToolResult result = new() { StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
            if (JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions).Length > Limits.MaximumResponseBytes)
                throw new UploadException("limit_exceeded", "The image response exceeds its byte limit. Request a smaller count.");
            return result;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or DomainValidationException or UploadException or
            RecordCommandNotFoundException or ConcurrencyConflictException or DbException or IOException or OperationCanceledException)
        {
            string code = exception switch
            {
                UnauthorizedAccessException => "permission_denied",
                DomainValidationException => "validation_failed",
                RecordCommandNotFoundException => "not_found",
                UploadException upload => upload.Code,
                ConcurrencyConflictException => "concurrency_conflict",
                _ => "temporarily_unavailable",
            };
            string message = code == "temporarily_unavailable"
                ? "The image request could not complete. Retry with the same identifiers."
                : exception.Message[..Math.Min(exception.Message.Length, 1024)];
            string correlation = accessor.HttpContext?.TraceIdentifier ?? "";
            correlation = correlation[..Math.Min(correlation.Length, 128)];
            if (domainId != Guid.Empty)
            {
                try
                {
                    using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(2));
                    await uploads.RecordAuditAsync(new(domainId, Guid.Empty, action, code, correlation), deadline.Token).ConfigureAwait(false);
                }
                catch (Exception auditException) when (auditException is DbException or IOException or OperationCanceledException) { LogAuditFailure(logger, null); }
            }
            JsonElement json = JsonSerializer.SerializeToElement(new { error = new RemoteWriteError(code, message, correlation) }, JsonOptions);
            return new() { IsError = true, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
        }
    }
}

[McpServerToolType]
public sealed class MonkeysphereImageTools
{
    [McpServerTool(Name = "add_record_image", ReadOnly = false, Destructive = false)]
    [RemoteToolScopes("media.write")]
    [Description("Attaches a completed record_image upload to a record as a new image. Requires media.write and an explicit domainId, recordId and uploadId. Stage the bytes first with begin_upload purpose record_image, write_upload_chunk and complete_upload. The optional fileName is retained as display metadata only, at most 200 characters; it never becomes a server path. The shared image pipeline still applies every existing check: supported format, at most 24 megapixels and 12,000 pixels per side, at most 50 images per record, an opaque stored original and metadata-stripped WebP derivatives. An upload staged for a different purpose fails with purpose_mismatch. Returns the created image metadata including its ID, ordinal and dimensions.")]
    public static Task<CallToolResult> AddAsync(RemoteImageCommands commands, Guid domainId, Guid recordId, Guid uploadId,
        string? fileName = null, CancellationToken cancellationToken = default) =>
        commands.AddAsync(domainId, recordId, uploadId, fileName, cancellationToken);

    [McpServerTool(Name = "read_record_image", ReadOnly = true)]
    [RemoteToolScopes("media.read")]
    [Description("Reads one record image in bounded byte ranges. Requires media.read and an explicit domainId, recordId and imageId. Variant is preview, thumbnail or original; preview and thumbnail are metadata-stripped WebP copies, while original returns the retained bytes as uploaded, which can still carry camera metadata such as location. Offset starts at 0; count 1-16384. Decode each contentBase64 chunk and concatenate the bytes in offset order, following nextOffset until null. contentDigest is the uppercase SHA-256 of the complete variant and must match across every chunk of one read; a change means the image was replaced, so restart at offset 0. An offset equal to totalBytes returns empty content, and a greater offset fails.")]
    public static Task<CallToolResult> ReadAsync(RemoteImageCommands commands, Guid domainId, Guid recordId, Guid imageId,
        string variant = "preview", int offset = 0, int count = 16384, CancellationToken cancellationToken = default) =>
        commands.ReadAsync(domainId, recordId, imageId, variant, offset, count, cancellationToken);
}
