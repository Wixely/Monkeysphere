using System.ComponentModel;
using System.Data.Common;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

public sealed record RemoteContactPreviewLimits(int MaximumPageSize = 25, int MaximumLabelCharacters = 160,
    int MaximumEvidenceChunkBytes = 16384, int MaximumResponseBytes = 131072,
    int MaximumStoredPreviewBytes = ContactImportPreviewLimits.MaximumBytes,
    long MaximumStoredBytes = ContactImportPreviewLimits.MaximumStoredBytes,
    int MaximumActive = ContactImportPreviewLimits.MaximumActive,
    int MaximumActivePerDomain = ContactImportPreviewLimits.MaximumActivePerDomain,
    int MaximumActivePerOwner = ContactImportPreviewLimits.MaximumActivePerOwner,
    int MaximumRetained = ContactImportPreviewLimits.MaximumRetained,
    int LifetimeMinutes = ContactImportPreviewLimits.LifetimeMinutes,
    int MaximumImportSelections = VCardParser.MaximumCards,
    int MaximumOutcomePageSize = 100,
    int MaximumRetainedImportCommandsPerDomain = ContactImportCommandLimits.MaximumRetainedCommandsPerDomain,
    int MaximumRetainedImportOutcomesPerDomain = ContactImportCommandLimits.MaximumRetainedOutcomesPerDomain,
    int ImportRetryWindowHours = ContactImportCommandLimits.RetryWindowHours,
    int ImportTombstoneDays = ContactImportCommandLimits.TombstoneDays);

public sealed record RemoteContactPreviewStatus(ContactImportPreviewHandle Preview, bool IsCurrent);
public sealed record RemoteContactPreviewSummary(int ContactIndex, string DisplayName, bool DisplayNameTruncated,
    string RecommendedAction, int AliasCount, int FieldMappingCount, int OpaquePropertyCount, int DuplicateCandidateCount, int InFileDuplicateCandidateCount);
public sealed record RemoteContactPreviewPage(RemoteContactPreviewStatus Status, int Page, int PageSize, IReadOnlyList<RemoteContactPreviewSummary> Contacts);
public sealed record RemoteContactPreviewEvidence(Guid PreviewId, int ContactIndex, int FormatVersion, string Encoding,
    int Offset, int TotalBytes, int? NextOffset, string ContentBase64);
public sealed record RemoteContactImportSelection(int ContactIndex, string Action, Guid? ExistingRecordId = null)
{
    public VCardImportSelection ToCore() => new(ContactIndex, Action switch
    {
        "create_separately" => VCardImportAction.CreateSeparately,
        "skip" => VCardImportAction.Skip,
        "merge_non_conflicting" => VCardImportAction.MergeNonConflicting,
        "replace_mapped_values" => VCardImportAction.ReplaceMappedValues,
        _ => throw new DomainValidationException("Import action must be create_separately, skip, merge_non_conflicting or replace_mapped_values."),
    }, ExistingRecordId);
}
public sealed record RemoteContactImportOutcome(int ContactIndex, string Action, Guid? RecordId, string? Revision);
public sealed record RemoteContactImportResultPage(ContactImportReceipt Receipt, int Page, int PageSize,
    IReadOnlyList<RemoteContactImportOutcome> Outcomes);

public sealed class RemoteContactPreviewCommands(ContactImportPreviewService service, IContactImportPreviewStore previews,
    ContactImportCommandService imports, IContactImportCommandStore importCommands,
    IVCardStore vcards, ICurrentDomainScope domains, RemoteCommandIdentityProvider identities, IRemoteUploadStore uploads,
    IHttpContextAccessor accessor, ILogger<RemoteContactPreviewCommands> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly RemoteContactPreviewLimits Limits = new();
    private static readonly Action<ILogger, Exception?> LogAuditFailure = LoggerMessage.Define(LogLevel.Warning,
        new EventId(1, "ContactPreviewAuditFailure"), "Contact preview failure audit could not be recorded.");

    public Task<CallToolResult> CreateAsync(Guid domainId, Guid uploadId, Guid idempotencyKey, CancellationToken cancellationToken) =>
        RunAsync(domainId, uploadId, "contacts.preview", async owner =>
        {
            ContactImportPreviewHandle handle = await service.CreateAsync(owner, uploadId, idempotencyKey, cancellationToken).ConfigureAwait(false);
            return await StatusAsync(handle, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<CallToolResult> InspectAsync(Guid domainId, Guid previewId, int page, int pageSize, CancellationToken cancellationToken) =>
        RunAsync(domainId, Guid.Empty, "contacts.inspect", async owner =>
        {
            if (page is < 1 or > 10000 || pageSize < 1 || pageSize > Limits.MaximumPageSize)
                throw new DomainValidationException("Supply page 1-10000 and page size 1-25.");
            ContactImportPreviewHandle handle = await previews.GetAsync(owner, previewId, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<VCardContactPreview> contacts = await previews.ReadContactsAsync(owner, previewId, page, pageSize, cancellationToken).ConfigureAwait(false);
            RemoteContactPreviewSummary[] summaries = contacts.Select(contact => new RemoteContactPreviewSummary(contact.Index,
                Label(contact.DisplayName), contact.DisplayName.Length > Limits.MaximumLabelCharacters,
                JsonNamingPolicy.SnakeCaseLower.ConvertName(contact.RecommendedAction.ToString()), contact.Aliases.Count, contact.FieldMappings.Count,
                contact.OpaquePropertyIndexes.Count, contact.DuplicateCandidates.Count, contact.ImportDuplicateCandidates.Count)).ToArray();
            await uploads.RecordAuditAsync(new(domainId, handle.UploadId, "contacts.inspect", "accepted", owner.CorrelationId), cancellationToken).ConfigureAwait(false);
            return new RemoteContactPreviewPage(await StatusAsync(handle, cancellationToken).ConfigureAwait(false), page, pageSize, summaries);
        }, cancellationToken);

    public Task<CallToolResult> EvidenceAsync(Guid domainId, Guid previewId, int contactIndex, int offset, int count, CancellationToken cancellationToken) =>
        RunAsync(domainId, Guid.Empty, "contacts.evidence", async owner =>
        {
            ContactImportPreviewHandle handle = await previews.GetAsync(owner, previewId, cancellationToken).ConfigureAwait(false);
            ContactImportEvidenceChunk chunk = await previews.ReadEvidenceAsync(owner, previewId, contactIndex, offset, count, cancellationToken).ConfigureAwait(false);
            await uploads.RecordAuditAsync(new(domainId, handle.UploadId, "contacts.evidence", "accepted", owner.CorrelationId), cancellationToken).ConfigureAwait(false);
            int next = chunk.Offset + chunk.Content.Length;
            return new RemoteContactPreviewEvidence(previewId, contactIndex, 1, "base64-utf8-json", offset, chunk.TotalBytes,
                next < chunk.TotalBytes ? next : null, Convert.ToBase64String(chunk.Content));
        }, cancellationToken);

    public Task<CallToolResult> ApplyAsync(Guid domainId, Guid previewId, string expectedRevision, Guid idempotencyKey,
        IReadOnlyList<RemoteContactImportSelection> selections, CancellationToken cancellationToken) =>
        RunAsync(domainId, Guid.Empty, "contacts.apply", async owner =>
        {
            if (selections is null || selections.Any(selection => selection is null))
                throw new DomainValidationException("Supply non-null contact selections.");
            return await imports.ApplyAsync(owner, previewId, expectedRevision, idempotencyKey,
                selections.Select(selection => selection.ToCore()).ToArray(), cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<CallToolResult> ResultAsync(Guid domainId, Guid idempotencyKey, int page, int pageSize, CancellationToken cancellationToken) =>
        RunAsync(domainId, Guid.Empty, "contacts.result", async owner =>
        {
            ContactImportReceipt receipt = await importCommands.GetAsync(owner, idempotencyKey, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<ContactImportOutcome> outcomes = await importCommands.ReadOutcomesAsync(
                owner, idempotencyKey, page, pageSize, cancellationToken).ConfigureAwait(false);
            return new RemoteContactImportResultPage(receipt, page, pageSize, outcomes.Select(outcome => new RemoteContactImportOutcome(
                outcome.ContactIndex, JsonNamingPolicy.SnakeCaseLower.ConvertName(outcome.Action.ToString()), outcome.RecordId, outcome.Revision)).ToArray());
        }, cancellationToken);

    private async Task<RemoteContactPreviewStatus> StatusAsync(ContactImportPreviewHandle handle, CancellationToken cancellationToken) =>
        new(handle, handle.Revision == await vcards.GetImportRevisionAsync(cancellationToken).ConfigureAwait(false));

    private static string Label(string value)
    {
        if (value.Length <= Limits.MaximumLabelCharacters) return value;
        int length = Limits.MaximumLabelCharacters;
        if (char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length];
    }

    private async Task<CallToolResult> RunAsync<T>(Guid domainId, Guid uploadId, string action, Func<UploadOwner, Task<T>> execute, CancellationToken cancellationToken)
    {
        try
        {
            UploadOwner owner = identities.CreateUploadOwner(domainId);
            using IDisposable selection = domains.Use(domainId);
            T value = await execute(owner).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            JsonElement json = JsonSerializer.SerializeToElement(value, JsonOptions);
            CallToolResult result = new() { StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
            if (JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions).Length > Limits.MaximumResponseBytes)
                throw new UploadException("limit_exceeded", "The inspection response exceeds its byte limit. Request fewer contacts.");
            return result;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or DomainValidationException or UploadException or ConcurrencyConflictException or
            CommandReplayException or RecordCommandNotFoundException or DbException or IOException or OperationCanceledException)
        {
            string code = exception switch
            {
                UnauthorizedAccessException => "permission_denied",
                DomainValidationException => "validation_failed",
                UploadException upload => upload.Code,
                ConcurrencyConflictException => "concurrency_conflict",
                CommandReplayException replay => replay.Code,
                RecordCommandNotFoundException => "not_found",
                _ => "temporarily_unavailable"
            };
            string message = code == "temporarily_unavailable" ? "The preview request could not complete. Retry with the same identifiers." : exception.Message[..Math.Min(exception.Message.Length, 1024)];
            string correlation = accessor.HttpContext?.TraceIdentifier ?? "";
            correlation = correlation[..Math.Min(correlation.Length, 128)];
            if (domainId != Guid.Empty)
            {
                try
                {
                    using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(2));
                    await uploads.RecordAuditAsync(new(domainId, uploadId, action, code, correlation), deadline.Token).ConfigureAwait(false);
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
public sealed class MonkeysphereContactPreviewTools
{
    [McpServerTool(Name = "preview_contact_import", ReadOnly = false, Destructive = false)]
    [Description("Creates a durable contact import preview from a complete upload using the domain's Person preset. Requires contacts.import and explicit domainId, uploadId and retry UUID. No records are changed. Retries preserve the original preview and expiry; isCurrent reports whether its captured domain revision still matches. Inspect before choosing import actions. Previews expire within 15 minutes or earlier upload expiry/cancellation.")]
    public static Task<CallToolResult> CreateAsync(RemoteContactPreviewCommands commands, Guid domainId, Guid uploadId, Guid idempotencyKey,
        CancellationToken cancellationToken = default) => commands.CreateAsync(domainId, uploadId, idempotencyKey, cancellationToken);

    [McpServerTool(Name = "get_contact_import_preview", ReadOnly = true)]
    [Description("Pages through credential/domain-owned contact preview summaries. Requires contacts.import. Page 1-10000, pageSize 1-25. Includes counts, recommendations and isCurrent; display names are limited to 160 UTF-16 characters with explicit truncation flags. Read complete mappings, duplicates and opaque properties with read_contact_import_evidence. No records are changed.")]
    public static Task<CallToolResult> InspectAsync(RemoteContactPreviewCommands commands, Guid domainId, Guid previewId, int page = 1, int pageSize = 25,
        CancellationToken cancellationToken = default) => commands.InspectAsync(domainId, previewId, page, pageSize, cancellationToken);

    [McpServerTool(Name = "read_contact_import_evidence", ReadOnly = true)]
    [Description("Reads complete evidence for one zero-based preview contact in bounded byte ranges. Requires contacts.import and explicit domainId/previewId. Offset starts at 0; count 1-16384. Decode each contentBase64 chunk, concatenate bytes in offset order, then parse the UTF-8 JSON (formatVersion 1, camelCase property names and snake_case enum strings). Follow nextOffset until null. Includes full source properties, mappings, aliases, duplicate reasons/record IDs and in-file matches. Ranges may split UTF-8 characters; decode text only after assembling bytes. Ownership and expiry are checked on every call; cancellation/revocation denies subsequent reads.")]
    public static Task<CallToolResult> EvidenceAsync(RemoteContactPreviewCommands commands, Guid domainId, Guid previewId, int contactIndex, int offset = 0, int count = 16384,
        CancellationToken cancellationToken = default) => commands.EvidenceAsync(domainId, previewId, contactIndex, offset, count, cancellationToken);

    [McpServerTool(Name = "apply_contact_import", ReadOnly = false, Destructive = false)]
    [Description("Applies every explicit decision from a credential/domain-owned contact preview in one transaction. Requires contacts.import, domainId, previewId, expectedRevision, retry UUID and exactly one ordered selection per contact. Actions are create_separately, skip, merge_non_conflicting or replace_mapped_values; merge/replace require an existingRecordId listed by that contact's preview evidence. The preview and source must still be live on the first attempt. The domain revision is checked inside the mutation transaction. Records, provenance, per-contact outcomes, receipt and audit commit together. Exact retries return the receipt for 24 hours even after preview expiry/cancellation; changed payloads fail.")]
    public static Task<CallToolResult> ApplyAsync(RemoteContactPreviewCommands commands, Guid domainId, Guid previewId, string expectedRevision,
        Guid idempotencyKey, IReadOnlyList<RemoteContactImportSelection> selections, CancellationToken cancellationToken = default) =>
        commands.ApplyAsync(domainId, previewId, expectedRevision, idempotencyKey, selections, cancellationToken);

    [McpServerTool(Name = "get_contact_import_result", ReadOnly = true)]
    [Description("Gets a committed contact-import receipt and paged per-contact outcomes by the original retry UUID. Requires contacts.import and explicit domainId. Page 1-10000, pageSize 1-100. Outcomes include contactIndex, action and the committed record ID/revision for create/merge/replace; skip has null record data. Results remain available during the 24-hour retry window and do not depend on temporary preview/upload availability.")]
    public static Task<CallToolResult> ResultAsync(RemoteContactPreviewCommands commands, Guid domainId, Guid idempotencyKey,
        int page = 1, int pageSize = 100, CancellationToken cancellationToken = default) =>
        commands.ResultAsync(domainId, idempotencyKey, page, pageSize, cancellationToken);
}
