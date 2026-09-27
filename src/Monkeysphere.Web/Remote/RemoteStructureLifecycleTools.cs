using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

/// <summary>
/// What retiring a record type would take with it. The counts are the whole of the decision: a type
/// with records and saved views behind it is not the same act as retiring an empty one, and the page
/// shows both numbers before it offers the button.
/// </summary>
public sealed record RemoteRecordTypeRetirementPreview(
    RemoteTypeSummary RecordType, string ExpectedUsageRevision, int RecordCount, int SavedViewCount);

/// <summary>
/// What merging one record type into another would do. <c>RequiredDowngradeCount</c> is the one that
/// changes structure rather than moving it: a field required on one side and not the other cannot stay
/// required once both sets of records share a type, so the merge relaxes it rather than rejecting
/// records that were valid before.
/// </summary>
public sealed record RemoteRecordTypeMergePreview(
    RemoteTypeSummary Source, RemoteTypeSummary Target, string ExpectedUsageRevision,
    int SourceRecordCount, int TargetRecordCount, int SourceSavedViewCount, int SourceFieldCount,
    int SharedFieldCount, int AddedFieldCount, int RequiredDowngradeCount);

public sealed class MonkeysphereStructureLifecycleQueries(
    IMonkeysphereService records,
    ICurrentDomainScope currentDomain,
    IHttpContextAccessor accessor)
{
    public async Task<RemoteRecordTypeRetirementPreview> PreviewRetirementAsync(Guid domainId, Guid recordTypeId,
        CancellationToken cancellationToken)
    {
        // A retirement preview is scoped to writing rather than reading. It reports nothing a reader
        // could not count for themselves, but the revision it returns is only usable by somebody who
        // can retire, and pairing the preview with the act keeps the workflow behind one grant.
        RemoteTagAuthority.Demand(accessor, "structure.write");
        using IDisposable domain = currentDomain.Use(domainId);
        RecordTypeRetirementPreview preview = await records.PreviewRecordTypeRetirementAsync(recordTypeId, cancellationToken).ConfigureAwait(false);
        return new(Summary(preview.RecordType), preview.Revision, preview.RecordCount, preview.SavedViewCount);
    }

    public async Task<RemoteRecordTypeMergePreview> PreviewMergeAsync(Guid domainId, Guid sourceRecordTypeId,
        Guid targetRecordTypeId, CancellationToken cancellationToken)
    {
        RemoteTagAuthority.Demand(accessor, "structure.write");
        using IDisposable domain = currentDomain.Use(domainId);
        RecordTypeMergePreview preview = await records
            .PreviewRecordTypeMergeAsync(sourceRecordTypeId, targetRecordTypeId, cancellationToken).ConfigureAwait(false);
        return new(Summary(preview.Source), Summary(preview.Target), preview.Revision, preview.SourceRecordCount,
            preview.TargetRecordCount, preview.SourceSavedViewCount, preview.SourceFieldCount, preview.SharedFieldCount,
            preview.AddedFieldCount, preview.RequiredDowngradeCount);
    }

    private static RemoteTypeSummary Summary(RecordType type) =>
        new(type.Id, type.Name, type.Symbol, type.Lifecycle.ToString().ToLowerInvariant());
}

public sealed partial class RemoteRecordWriter
{
    public Task<CallToolResult> UpdateRecordTypeAsync(Guid domainId, Guid recordTypeId, string expectedRevision, string name,
        string? symbol, bool? tagsEnabled, Guid idempotencyKey, CancellationToken cancellationToken) =>
        RunAsync(domainId, "record_types.update", async () =>
    {
        string hash = CommandRequestHash.Compute(new { contract = 1, domainId, recordTypeId, expectedRevision, name, symbol, tagsEnabled });
        RecordCommandIdentity identity = identities.Create(domainId, "structure.write", "record_types.update", idempotencyKey, hash);
        using IDisposable domain = currentDomain.Use(domainId);
        return await structureCommands.UpdateTypeAsync(identity, recordTypeId, expectedRevision, name, symbol, tagsEnabled, cancellationToken).ConfigureAwait(false);
    });

    public Task<CallToolResult> RetireRecordTypeAsync(Guid domainId, Guid recordTypeId, string expectedUsageRevision,
        Guid idempotencyKey, CancellationToken cancellationToken) => RunAsync(domainId, "record_types.retire", async () =>
    {
        string hash = CommandRequestHash.Compute(new { contract = 1, domainId, recordTypeId, expectedUsageRevision });
        RecordCommandIdentity identity = identities.Create(domainId, "structure.write", "record_types.retire", idempotencyKey, hash);
        using IDisposable domain = currentDomain.Use(domainId);
        return await structureCommands.RetireTypeAsync(identity, recordTypeId, expectedUsageRevision, cancellationToken).ConfigureAwait(false);
    });

    public Task<CallToolResult> MergeRecordTypesAsync(Guid domainId, Guid sourceRecordTypeId, Guid targetRecordTypeId,
        string expectedUsageRevision, Guid idempotencyKey, CancellationToken cancellationToken) =>
        RunAsync(domainId, "record_types.merge", async () =>
    {
        string hash = CommandRequestHash.Compute(new { contract = 1, domainId, sourceRecordTypeId, targetRecordTypeId, expectedUsageRevision });
        RecordCommandIdentity identity = identities.Create(domainId, "structure.write", "record_types.merge", idempotencyKey, hash);
        using IDisposable domain = currentDomain.Use(domainId);
        return await structureCommands.MergeTypesAsync(identity, sourceRecordTypeId, targetRecordTypeId, expectedUsageRevision, cancellationToken).ConfigureAwait(false);
    });
}

[McpServerToolType]
[RemoteToolScopes("structure.write")]
public sealed class MonkeysphereStructureLifecycleTools
{
    [McpServerTool(Name = "preview_record_type_retirement", ReadOnly = true, UseStructuredContent = true,
        OutputSchemaType = typeof(RemoteRecordTypeRetirementPreview))]
    [Description("Reports what retiring a record type would leave behind: how many records carry it and how many saved views point at it, with the usage revision retire_record_type must be given. Requires structure.write and an explicit domainId. Retirement hides the type from new work and keeps everything already recorded against it.")]
    public static Task<CallToolResult> PreviewRetirementAsync(MonkeysphereStructureLifecycleQueries queries, IHttpContextAccessor accessor,
        Guid domainId, Guid recordTypeId, CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, () => queries.PreviewRetirementAsync(domainId, recordTypeId, cancellationToken));

    [McpServerTool(Name = "preview_record_type_merge", ReadOnly = true, UseStructuredContent = true,
        OutputSchemaType = typeof(RemoteRecordTypeMergePreview))]
    [Description("Reports what merging one record type into another would do: records and saved views moved, fields shared and added, and how many required fields would be relaxed so records valid before the merge stay valid after it. Returns the usage revision merge_record_types must be given. Requires structure.write and an explicit domainId.")]
    public static Task<CallToolResult> PreviewMergeAsync(MonkeysphereStructureLifecycleQueries queries, IHttpContextAccessor accessor,
        Guid domainId, Guid sourceRecordTypeId, Guid targetRecordTypeId, CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, () => queries.PreviewMergeAsync(domainId, sourceRecordTypeId, targetRecordTypeId, cancellationToken));

    [McpServerTool(Name = "update_record_type", ReadOnly = false, Destructive = false)]
    [Description("Renames an active record type and sets its symbol and whether it carries universal tags. Requires structure.write, explicit domainId, expectedRevision from get_record_type and idempotencyKey. Name is trimmed and limited to 200 characters; symbol allows at most four visible characters/emoji. Omitting tagsEnabled leaves the setting alone; setting it to false keeps the tags already recorded so re-enabling restores them. Returns an updated type ID/revision receipt with 24-hour identical retry replay.")]
    public static Task<CallToolResult> UpdateTypeAsync(RemoteRecordWriter writer, Guid domainId, Guid recordTypeId,
        string expectedRevision, string name, Guid idempotencyKey, string? symbol = null, bool? tagsEnabled = null,
        CancellationToken cancellationToken = default) =>
        writer.UpdateRecordTypeAsync(domainId, recordTypeId, expectedRevision, name, symbol, tagsEnabled, idempotencyKey, cancellationToken);

    [McpServerTool(Name = "retire_record_type", ReadOnly = false, Destructive = true)]
    [Description("Retires an active record type. Requires structure.write, explicit domainId, the expectedUsageRevision from preview_record_type_retirement and idempotencyKey. Records, values and saved views are kept; the type stops being offered for new work. A preview overtaken by other changes fails with stale_revision, which retrying unchanged cannot fix: preview again. Returns a retired type ID/revision receipt.")]
    public static Task<CallToolResult> RetireTypeAsync(RemoteRecordWriter writer, Guid domainId, Guid recordTypeId,
        string expectedUsageRevision, Guid idempotencyKey, CancellationToken cancellationToken = default) =>
        writer.RetireRecordTypeAsync(domainId, recordTypeId, expectedUsageRevision, idempotencyKey, cancellationToken);

    [McpServerTool(Name = "merge_record_types", ReadOnly = false, Destructive = true)]
    [Description("Moves every record and saved view from one record type to another and retires the source. Requires structure.write, explicit domainId, the expectedUsageRevision from preview_record_type_merge and idempotencyKey. The target must be active and different from the source. Fields the source has and the target lacks are appended optional; a field required on only one side is relaxed, as is a target field required while the source has records, so nothing already recorded becomes invalid. A stale preview fails with stale_revision. Returns a receipt naming the retired source then the updated target.")]
    public static Task<CallToolResult> MergeTypesAsync(RemoteRecordWriter writer, Guid domainId, Guid sourceRecordTypeId,
        Guid targetRecordTypeId, string expectedUsageRevision, Guid idempotencyKey, CancellationToken cancellationToken = default) =>
        writer.MergeRecordTypesAsync(domainId, sourceRecordTypeId, targetRecordTypeId, expectedUsageRevision, idempotencyKey, cancellationToken);
}
