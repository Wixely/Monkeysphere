using System.ComponentModel;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

public sealed record RemoteContactExportLimits(int MaximumContacts = 100, int MaximumChunkBytes = 16384,
    int MaximumResponseBytes = 131072);

public sealed record RemoteContactExport(int ContactCount, int FormatVersion, string Encoding, string ContentDigest,
    int Offset, int TotalBytes, int? NextOffset, string ContentBase64);

public sealed class RemoteContactExportCommands(IVCardService vcards, ICurrentDomainScope domains,
    RemoteCommandIdentityProvider identities, IRemoteUploadStore uploads,
    IHttpContextAccessor accessor, ILogger<RemoteContactExportCommands> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly RemoteContactExportLimits Limits = new();
    private static readonly Action<ILogger, Exception?> LogAuditFailure = LoggerMessage.Define(LogLevel.Warning,
        new EventId(1, "ContactExportAuditFailure"), "Contact export failure audit could not be recorded.");

    public async Task<CallToolResult> ExportAsync(Guid domainId, IReadOnlyList<Guid> recordIds, int offset, int count,
        CancellationToken cancellationToken)
    {
        try
        {
            UploadOwner owner = identities.CreateUploadOwner(domainId, "contacts.export");
            using IDisposable selection = domains.Use(domainId);
            if (recordIds is null || recordIds.Count == 0)
                throw new DomainValidationException($"Supply between 1 and {Limits.MaximumContacts} distinct contact record IDs.");
            if (count is < 1 || count > Limits.MaximumChunkBytes)
                throw new DomainValidationException($"Supply count 1-{Limits.MaximumChunkBytes}.");
            if (offset < 0)
                throw new DomainValidationException("Supply a non-negative offset.");

            // Core validates the 1-100 distinct bound and that every selected record is an existing Person record.
            byte[] content = await vcards.ExportAsync(recordIds, cancellationToken).ConfigureAwait(false);
            // Matches read_contact_import_evidence: the end offset yields an empty final range, beyond it fails.
            if (offset > content.Length)
                throw new DomainValidationException("The offset is beyond the exported document. Restart at offset 0.");

            int length = Math.Min(count, content.Length - offset);
            int next = offset + length;
            RemoteContactExport value = new(recordIds.Count, 1, "base64-utf8-vcard",
                Convert.ToHexString(SHA256.HashData(content)), offset, content.Length,
                next < content.Length ? next : null, Convert.ToBase64String(content, offset, length));
            await uploads.RecordAuditAsync(new(domainId, Guid.Empty, "contacts.export", "accepted", owner.CorrelationId), cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            JsonElement json = JsonSerializer.SerializeToElement(value, JsonOptions);
            CallToolResult result = new() { StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
            if (JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions).Length > Limits.MaximumResponseBytes)
                throw new UploadException("limit_exceeded", "The export response exceeds its byte limit. Request a smaller count.");
            return result;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or DomainValidationException or UploadException or
            DbException or IOException or OperationCanceledException)
        {
            string code = exception switch
            {
                UnauthorizedAccessException => "permission_denied",
                DomainValidationException => "validation_failed",
                UploadException upload => upload.Code,
                _ => "temporarily_unavailable"
            };
            string message = code == "temporarily_unavailable"
                ? "The export request could not complete. Retry with the same identifiers."
                : exception.Message[..Math.Min(exception.Message.Length, 1024)];
            string correlation = accessor.HttpContext?.TraceIdentifier ?? "";
            correlation = correlation[..Math.Min(correlation.Length, 128)];
            if (domainId != Guid.Empty)
            {
                try
                {
                    using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(2));
                    await uploads.RecordAuditAsync(new(domainId, Guid.Empty, "contacts.export", code, correlation), deadline.Token).ConfigureAwait(false);
                }
                catch (Exception auditException) when (auditException is DbException or IOException or OperationCanceledException) { LogAuditFailure(logger, null); }
            }
            JsonElement json = JsonSerializer.SerializeToElement(new { error = new RemoteWriteError(code, message, correlation) }, JsonOptions);
            return new() { IsError = true, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
        }
    }
}

[McpServerToolType]
[RemoteToolScopes("contacts.export")]
public sealed class MonkeysphereContactExportTools
{
    [McpServerTool(Name = "export_contacts", ReadOnly = true)]
    [Description("Exports explicitly selected Person records as one vCard 4.0 document, read in bounded byte ranges. Requires contacts.export and an explicit domainId. Supply 1-100 distinct recordIds; every ID must be an existing record on the domain's Person preset or the whole export fails. Offset starts at 0; count 1-16384. Decode each contentBase64 chunk, concatenate bytes in offset order and follow nextOffset until null to rebuild the UTF-8 document. contentDigest is the uppercase SHA-256 hexadecimal digest of the complete document: it must be identical across every chunk of one export, and a change means the records were edited mid-read, so restart at offset 0. No records are changed and no data is staged; the export is regenerated per call. Selecting the same recordIds in a different order produces a different document. Exporting sends contact data out of the deployment and is audited.")]
    public static Task<CallToolResult> ExportAsync(RemoteContactExportCommands commands, Guid domainId, IReadOnlyList<Guid> recordIds,
        int offset = 0, int count = 16384, CancellationToken cancellationToken = default) =>
        commands.ExportAsync(domainId, recordIds, offset, count, cancellationToken);
}
