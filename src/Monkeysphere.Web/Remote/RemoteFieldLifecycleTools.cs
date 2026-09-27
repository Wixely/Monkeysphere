using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

/// <summary>
/// How much a reusable field is used: how many record types carry it, how many values exist, and how
/// many saved views name it. Counts only. A field's values are record content, which
/// <c>structure.write</c> is deliberately not a grant for, and a caller who wants the records asks for
/// the records.
///
/// <c>ExpectedUsageRevision</c> is the fingerprint over this field alone, which is what a conversion
/// checks. A merge checks the fingerprint over both fields, so it takes the one its own preview returns.
/// </summary>
public sealed record RemoteFieldUsage(
    RemoteReusableField Definition, string ExpectedUsageRevision, int AttachmentCount, int ValueCount, int SavedViewReferenceCount);

public sealed record RemoteFieldMergePreview(
    RemoteReusableField Source, RemoteReusableField Target, string ExpectedUsageRevision,
    bool IsCompatible, string? IncompatibilityReason,
    int SourceAttachmentCount, int SourceValueCount, int ConflictingValueCount, int SavedViewReferenceCount);

/// <summary>
/// One value a conversion cannot carry across. <c>RecordId</c> is null and the name withheld unless the
/// credential also holds <c>records.read</c>: which values fail is a structural fact, but whose record
/// holds them is record content.
/// </summary>
public sealed record RemoteFieldConversionIssue(Guid? RecordId, string RecordDisplayName, string Reason);

public sealed record RemoteFieldConversionPreview(
    RemoteReusableField Source, string ExpectedUsageRevision, string TargetName, string TargetTypeId,
    string TargetConfigurationJson, int AttachmentCount, int ValueCount, int SavedViewReferenceCount,
    int FailedValueCount, bool IssueRecordsWithheld, IReadOnlyList<RemoteFieldConversionIssue> Issues);

public sealed class MonkeysphereFieldLifecycleQueries(
    IMonkeysphereService records,
    ICurrentDomainScope currentDomain,
    IHttpContextAccessor accessor)
{
    public async Task<RemoteFieldUsage> GetUsageAsync(Guid domainId, Guid fieldDefinitionId, CancellationToken cancellationToken)
    {
        RemoteTagAuthority.Demand(accessor, "structure.write");
        using IDisposable domain = currentDomain.Use(domainId);
        FieldUsageCounts usage = await records.GetFieldUsageCountsAsync(fieldDefinitionId, cancellationToken).ConfigureAwait(false)
            ?? throw new RecordCommandNotFoundException("Field definition was not found.");
        return new(Field(usage.Definition), usage.Revision, usage.AttachmentCount, usage.ValueCount, usage.SavedViewReferenceCount);
    }

    public async Task<RemoteFieldMergePreview> PreviewMergeAsync(Guid domainId, Guid sourceFieldDefinitionId,
        Guid targetFieldDefinitionId, CancellationToken cancellationToken)
    {
        RemoteTagAuthority.Demand(accessor, "structure.write");
        using IDisposable domain = currentDomain.Use(domainId);
        FieldMergePreview preview = await records
            .PreviewFieldMergeAsync(sourceFieldDefinitionId, targetFieldDefinitionId, cancellationToken).ConfigureAwait(false);
        return new(Field(preview.Source), Field(preview.Target), preview.Revision, preview.IsCompatible,
            preview.IncompatibilityReason, preview.SourceAttachmentCount, preview.SourceValueCount,
            preview.ConflictingValueCount, preview.SavedViewReferenceCount);
    }

    public async Task<RemoteFieldConversionPreview> PreviewConversionAsync(Guid domainId, Guid fieldDefinitionId, string name,
        string typeId, IReadOnlyList<string>? choiceOptions, CancellationToken cancellationToken)
    {
        RemoteTagAuthority.Demand(accessor, "structure.write");

        // Whether the failing values can be named is a separate question from whether the conversion can
        // be previewed, so it is asked separately rather than by refusing the call.
        bool named = HoldsRecordsRead();
        using IDisposable domain = currentDomain.Use(domainId);
        FieldConversionPreview preview = await records
            .PreviewFieldConversionAsync(fieldDefinitionId, new(name, typeId, choiceOptions), cancellationToken).ConfigureAwait(false);
        return new(Field(preview.Source), preview.Revision, preview.TargetName, preview.TargetTypeId,
            preview.TargetConfigurationJson, preview.AttachmentCount, preview.ValueCount, preview.SavedViewReferenceCount,
            preview.FailedValueCount, IssueRecordsWithheld: !named,
            [.. preview.Issues.Select(issue => named
                ? new RemoteFieldConversionIssue(issue.RecordId, issue.RecordDisplayName, issue.Reason)
                : new RemoteFieldConversionIssue(null, "(withheld)", issue.Reason))]);
    }

    private bool HoldsRecordsRead()
    {
        try
        {
            RemoteTagAuthority.Demand(accessor, "records.read");
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static RemoteReusableField Field(FieldDefinition field) =>
        new(field.Id, field.Name, field.TypeId, field.ConfigurationJson, field.Lifecycle.ToString().ToLowerInvariant(),
            FieldTypes.ChoiceOptions(field), field.CanonicalKey, field.PresetKey, field.PresetVersion, field.Revision);
}

public sealed partial class RemoteRecordWriter
{
    public Task<CallToolResult> RenameFieldAsync(Guid domainId, Guid fieldDefinitionId, string expectedFieldRevision,
        string name, Guid idempotencyKey, CancellationToken cancellationToken) => RunAsync(domainId, "fields.rename", async () =>
    {
        string hash = CommandRequestHash.Compute(new { contract = 1, domainId, fieldDefinitionId, expectedFieldRevision, name });
        RecordCommandIdentity identity = identities.Create(domainId, "structure.write", "fields.rename", idempotencyKey, hash);
        using IDisposable domain = currentDomain.Use(domainId);
        return await structureCommands.RenameFieldAsync(identity, fieldDefinitionId, expectedFieldRevision, name, cancellationToken).ConfigureAwait(false);
    });

    public Task<CallToolResult> RetireFieldAsync(Guid domainId, Guid fieldDefinitionId, string expectedFieldRevision,
        Guid idempotencyKey, CancellationToken cancellationToken) => RunAsync(domainId, "fields.retire", async () =>
    {
        string hash = CommandRequestHash.Compute(new { contract = 1, domainId, fieldDefinitionId, expectedFieldRevision });
        RecordCommandIdentity identity = identities.Create(domainId, "structure.write", "fields.retire", idempotencyKey, hash);
        using IDisposable domain = currentDomain.Use(domainId);
        return await structureCommands.RetireFieldAsync(identity, fieldDefinitionId, expectedFieldRevision, cancellationToken).ConfigureAwait(false);
    });

    public Task<CallToolResult> MergeFieldsAsync(Guid domainId, Guid sourceFieldDefinitionId, Guid targetFieldDefinitionId,
        string conflictResolution, string expectedUsageRevision, Guid idempotencyKey, CancellationToken cancellationToken) =>
        RunAsync(domainId, "fields.merge", async () =>
    {
        string hash = CommandRequestHash.Compute(new { contract = 1, domainId, sourceFieldDefinitionId, targetFieldDefinitionId, conflictResolution, expectedUsageRevision });
        RecordCommandIdentity identity = identities.Create(domainId, "structure.write", "fields.merge", idempotencyKey, hash);
        using IDisposable domain = currentDomain.Use(domainId);
        return await structureCommands.MergeFieldsAsync(identity, sourceFieldDefinitionId, targetFieldDefinitionId,
            ParseConflictResolution(conflictResolution), expectedUsageRevision, cancellationToken).ConfigureAwait(false);
    });

    public Task<CallToolResult> ConvertFieldAsync(Guid domainId, Guid fieldDefinitionId, string expectedUsageRevision, string name,
        string typeId, IReadOnlyList<string>? choiceOptions, Guid idempotencyKey, CancellationToken cancellationToken) =>
        RunAsync(domainId, "fields.convert", async () =>
    {
        string hash = CommandRequestHash.Compute(new { contract = 1, domainId, fieldDefinitionId, expectedUsageRevision, name, typeId, choiceOptions });
        RecordCommandIdentity identity = identities.Create(domainId, "structure.write", "fields.convert", idempotencyKey, hash);
        using IDisposable domain = currentDomain.Use(domainId);

        // The values are transformed by the application service, against the identifier the new field
        // will take, so this applies the page's own conversion rather than a second one. The fingerprint
        // is not re-checked here: the command's transaction checks it authoritatively, and doing it twice
        // would report a race as a validation failure instead of stale_revision.
        Guid targetFieldDefinitionId = Guid.CreateVersion7();
        PreparedFieldConversion prepared = await records.PrepareFieldConversionAsync(fieldDefinitionId,
            targetFieldDefinitionId, new(name, typeId, choiceOptions), cancellationToken).ConfigureAwait(false);
        MonkeysphereService.RequireEveryValueConverts(prepared.Preview);
        return await structureCommands.ConvertFieldAsync(identity, fieldDefinitionId, targetFieldDefinitionId,
            prepared.Preview.TargetName, prepared.Preview.TargetTypeId, prepared.Preview.TargetConfigurationJson,
            prepared.Values, expectedUsageRevision, cancellationToken).ConfigureAwait(false);
    });

    private static FieldMergeConflictResolution ParseConflictResolution(string value) =>
        Enum.TryParse(value, ignoreCase: true, out FieldMergeConflictResolution resolution) && Enum.IsDefined(resolution)
            ? resolution
            : throw new DomainValidationException("Use a conflict policy of reject, keepTarget or keepSource.");
}

[McpServerToolType]
[RemoteToolScopes("structure.write")]
public sealed class MonkeysphereFieldLifecycleTools
{
    [McpServerTool(Name = "get_field_usage", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(RemoteFieldUsage))]
    [Description("Reports how much a reusable field is used: record types carrying it, values recorded against it, and saved views naming it, Counts only, never the values or the records holding them, because structure.write is not a grant to read records. expectedUsageRevision covers this one field, so it is the revision convert_field takes; merge_fields needs the pair's revision from preview_field_merge instead. Requires structure.write and an explicit domainId.")]
    public static Task<CallToolResult> GetUsageAsync(MonkeysphereFieldLifecycleQueries queries, IHttpContextAccessor accessor,
        Guid domainId, Guid fieldDefinitionId, CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, () => queries.GetUsageAsync(domainId, fieldDefinitionId, cancellationToken));

    [McpServerTool(Name = "preview_field_merge", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(RemoteFieldMergePreview))]
    [Description("Reports whether two reusable fields can be merged and what merging them would move. Fields must share a type and configuration; incompatible pairs return isCompatible false with the reason rather than an error. conflictingValueCount is how many records hold a value for both, which merge_fields needs a conflict policy for. Returns the usage revision merge_fields must be given. Requires structure.write and an explicit domainId.")]
    public static Task<CallToolResult> PreviewMergeAsync(MonkeysphereFieldLifecycleQueries queries, IHttpContextAccessor accessor,
        Guid domainId, Guid sourceFieldDefinitionId, Guid targetFieldDefinitionId, CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, () => queries.PreviewMergeAsync(domainId, sourceFieldDefinitionId, targetFieldDefinitionId, cancellationToken));

    [McpServerTool(Name = "preview_field_conversion", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(RemoteFieldConversionPreview))]
    [Description("Reports what converting a reusable field to another type would do, including failedValueCount: values that cannot be represented in the new type. A conversion with any failing value is refused, so this is how to find them first. At most 25 are listed while the count stays complete, and each one names its record only when the credential also holds records.read, with issueRecordsWithheld saying which happened. Returns the usage revision convert_field must be given. Requires structure.write and an explicit domainId.")]
    public static Task<CallToolResult> PreviewConversionAsync(MonkeysphereFieldLifecycleQueries queries, IHttpContextAccessor accessor,
        Guid domainId, Guid fieldDefinitionId, string name, string typeId, IReadOnlyList<string>? choiceOptions = null,
        CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, () => queries.PreviewConversionAsync(domainId, fieldDefinitionId, name, typeId, choiceOptions, cancellationToken));

    [McpServerTool(Name = "rename_field", ReadOnly = false, Destructive = false)]
    [Description("Renames a reusable field everywhere it is attached. Requires structure.write, explicit domainId, expectedFieldRevision from list_field_definitions and idempotencyKey. Name is trimmed and limited to 200 characters. A retired field can still be renamed, because its values are still shown. Returns an updated field ID/revision receipt with 24-hour identical retry replay.")]
    public static Task<CallToolResult> RenameAsync(RemoteRecordWriter writer, Guid domainId, Guid fieldDefinitionId,
        string expectedFieldRevision, string name, Guid idempotencyKey, CancellationToken cancellationToken = default) =>
        writer.RenameFieldAsync(domainId, fieldDefinitionId, expectedFieldRevision, name, idempotencyKey, cancellationToken);

    [McpServerTool(Name = "retire_field", ReadOnly = false, Destructive = true)]
    [Description("Retires an active reusable field. Requires structure.write, explicit domainId, expectedFieldRevision from list_field_definitions and idempotencyKey. Values already recorded are kept and still readable; the field stops being offered for new values and cannot be attached to further record types. Returns a retired field ID/revision receipt.")]
    public static Task<CallToolResult> RetireAsync(RemoteRecordWriter writer, Guid domainId, Guid fieldDefinitionId,
        string expectedFieldRevision, Guid idempotencyKey, CancellationToken cancellationToken = default) =>
        writer.RetireFieldAsync(domainId, fieldDefinitionId, expectedFieldRevision, idempotencyKey, cancellationToken);

    [McpServerTool(Name = "merge_fields", ReadOnly = false, Destructive = true)]
    [Description("Moves every value, attachment, reminder, import provenance row and saved-view reference from one reusable field to another and retires the source. Requires structure.write, explicit domainId, the expectedUsageRevision from preview_field_merge and idempotencyKey. Fields must share a type and configuration. conflictResolution is reject, keepTarget or keepSource: reject refuses when any record holds both values, and the other two say which value survives, with the reminder on the surviving value surviving too. Where either side required the field, the merged attachment is required. A stale preview fails with stale_revision, which retrying unchanged cannot fix. Returns a receipt naming the retired source then the updated target.")]
    public static Task<CallToolResult> MergeAsync(RemoteRecordWriter writer, Guid domainId, Guid sourceFieldDefinitionId,
        Guid targetFieldDefinitionId, string conflictResolution, string expectedUsageRevision, Guid idempotencyKey,
        CancellationToken cancellationToken = default) =>
        writer.MergeFieldsAsync(domainId, sourceFieldDefinitionId, targetFieldDefinitionId, conflictResolution,
            expectedUsageRevision, idempotencyKey, cancellationToken);

    [McpServerTool(Name = "convert_field", ReadOnly = false, Destructive = true)]
    [Description("Creates a new reusable field of another type, rewrites every value into it, moves attachments, reminders, import provenance and saved-view references across, and retires the original. Requires structure.write, explicit domainId, the expectedUsageRevision from preview_field_conversion and idempotencyKey. Refused outright if any value cannot be represented in the new type, rather than dropping it: use preview_field_conversion to find those first. Choice fields need their options. A stale preview fails with stale_revision. Returns a receipt naming the created field then the retired original.")]
    public static Task<CallToolResult> ConvertAsync(RemoteRecordWriter writer, Guid domainId, Guid fieldDefinitionId,
        string expectedUsageRevision, string name, string typeId, Guid idempotencyKey,
        IReadOnlyList<string>? choiceOptions = null, CancellationToken cancellationToken = default) =>
        writer.ConvertFieldAsync(domainId, fieldDefinitionId, expectedUsageRevision, name, typeId, choiceOptions, idempotencyKey, cancellationToken);
}
