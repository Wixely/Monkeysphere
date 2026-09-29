using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RemoteMergeChoice(Guid FieldDefinitionId, string Resolution)
{
    public RecordMergeChoice ToCore()
    {
        if (!Enum.TryParse(Resolution, ignoreCase: true, out RecordMergeResolution resolution) ||
            !Enum.IsDefined(resolution))
        {
            throw new DomainValidationException(
                $"Resolution must be one of: {string.Join(", ", Enum.GetNames<RecordMergeResolution>())}.");
        }

        return new(FieldDefinitionId, resolution);
    }
}

public sealed record RemoteMergeRecord(Guid Id, Guid RecordTypeId, string RecordTypeName, string DisplayName);

public sealed record RemoteMergeValue(Guid FieldDefinitionId, string FieldName, string TypeId, int Ordinal, string Value);

/// <summary>
/// One field both records hold a value for. <c>resolution</c> is what this preview was computed with,
/// so a caller that supplied nothing can see the default it is about to get rather than having to know
/// what the default is.
/// </summary>
public sealed record RemoteMergeConflict(
    Guid FieldDefinitionId,
    string FieldName,
    string TypeId,
    string Resolution,
    IReadOnlyList<RemoteMergeValue> SurvivingValues,
    IReadOnlyList<RemoteMergeValue> MergedValues);

public sealed record RemoteMergeArchivedValue(
    Guid FieldDefinitionId, string FieldName, string Reason, IReadOnlyList<RemoteMergeValue> Values);

public sealed record RemoteMergeImpact(
    int FieldValuesCarried,
    int FieldValuesArchivedOnly,
    int AliasesAdded,
    int TagsAdded,
    int ImagesCarried,
    int RelationshipsRepointed,
    int RelationshipsDropped,
    int RemindersCarried,
    int RemindersDropped,
    int SourceImportsCarried,
    int GraphViewsUpdated);

public sealed record RemoteMergePreview(
    RemoteMergeRecord Surviving,
    RemoteMergeRecord Merged,
    string Revision,
    string? Refusal,
    RemoteMergeImpact Impact,
    IReadOnlyList<RemoteMergeConflict> Conflicts,
    IReadOnlyList<RemoteMergeArchivedValue> Archived,
    IReadOnlyList<string> AliasesAdded,
    IReadOnlyList<string> TagsAdded);

public sealed partial class RemoteRecordWriter
{
    public Task<CallToolResult> PreviewMergeAsync(Guid domainId, Guid survivingRecordId, Guid mergedRecordId,
        IReadOnlyList<RemoteMergeChoice>? choices, CancellationToken cancellationToken) =>
        RunAsync(domainId, "records.merge.preview", async () =>
    {
        DemandMergeScopes(domainId, Guid.CreateVersion7(), new string('0', 64));
        using IDisposable domain = currentDomain.Use(domainId);
        RecordMergePreview? preview = await merges.PreviewAsync(survivingRecordId, mergedRecordId,
            ToCore(choices), cancellationToken).ConfigureAwait(false);
        return preview is null
            ? throw new RecordCommandNotFoundException("One or both records were not found.")
            : Project(preview);
    });

    public Task<CallToolResult> MergeRecordsAsync(Guid domainId, Guid survivingRecordId, Guid mergedRecordId,
        string expectedRevision, IReadOnlyList<RemoteMergeChoice>? choices, Guid idempotencyKey,
        CancellationToken cancellationToken) => RunAsync(domainId, "records.merge", async () =>
    {
        string hash = CommandRequestHash.Compute(
            new { contract = 1, domainId, survivingRecordId, mergedRecordId, expectedRevision, choices });
        RecordCommandIdentity identity = DemandMergeScopes(domainId, idempotencyKey, hash);
        using IDisposable domain = currentDomain.Use(domainId);
        return await merges.MergeAsync(identity, survivingRecordId, mergedRecordId, ToCore(choices) ?? [],
            expectedRevision, cancellationToken).ConfigureAwait(false);
    });

    /// <summary>
    /// A merge deletes one record and writes to another, so it needs both permissions and not either.
    ///
    /// Without records.delete a credential that may only edit could destroy any record by merging it
    /// away, and without records.write it could add another record's data to a record it is not
    /// otherwise allowed to touch. Both are checked before anything is read, so a credential holding
    /// one of the two learns nothing about whether the records exist.
    /// </summary>
    private RecordCommandIdentity DemandMergeScopes(Guid domainId, Guid idempotencyKey, string requestHash)
    {
        _ = identities.Create(domainId, "records.write", "records.merge", idempotencyKey, requestHash);
        return identities.Create(domainId, "records.delete", "records.merge", idempotencyKey, requestHash);
    }

    private static IReadOnlyList<RecordMergeChoice>? ToCore(IReadOnlyList<RemoteMergeChoice>? choices)
    {
        if (choices is null) return null;
        if (choices.Count > RecordMergeLimits.MaximumReportedValues || choices.Any(choice => choice is null))
        {
            throw new DomainValidationException(
                $"Supply at most {RecordMergeLimits.MaximumReportedValues} non-null merge choices.");
        }

        return [.. choices.Select(choice => choice.ToCore())];
    }

    private static RemoteMergePreview Project(RecordMergePreview preview) => new(
        Project(preview.Surviving),
        Project(preview.Merged),
        preview.Revision,
        preview.Refusal,
        new(preview.Impact.FieldValuesCarried, preview.Impact.FieldValuesArchivedOnly, preview.Impact.AliasesAdded,
            preview.Impact.TagsAdded, preview.Impact.ImagesCarried, preview.Impact.RelationshipsRepointed,
            preview.Impact.RelationshipsDropped, preview.Impact.RemindersCarried, preview.Impact.RemindersDropped,
            preview.Impact.SourceImportsCarried, preview.Impact.GraphViewsUpdated),
        [.. preview.Conflicts.Select(conflict => new RemoteMergeConflict(
            conflict.FieldDefinitionId, conflict.FieldName, conflict.TypeId, conflict.Resolution.ToString(),
            Project(conflict.SurvivingValues), Project(conflict.MergedValues)))],
        [.. preview.Archived.Select(archived => new RemoteMergeArchivedValue(
            archived.FieldDefinitionId, archived.FieldName, archived.Reason, Project(archived.Values)))],
        preview.AliasesAdded,
        preview.TagsAdded);

    // Projected rather than returned whole: a record summary carries its backstage state, and a merge
    // preview is not the place that decides who may see that.
    private static RemoteMergeRecord Project(RecordSummary record) =>
        new(record.Id, record.RecordTypeId, record.RecordTypeName, record.DisplayName);

    private static IReadOnlyList<RemoteMergeValue> Project(IReadOnlyList<RecordValue> values) =>
        [.. values.Select(value => new RemoteMergeValue(value.FieldDefinitionId, value.FieldName, value.TypeId,
            value.Ordinal, Describe(value)))];

    /// <summary>
    /// One readable string per value, because the caller is deciding between two values rather than
    /// rebuilding them: a merge choice names a field, never a value's parts.
    /// </summary>
    private static string Describe(RecordValue value) =>
        value.Tags.Count > 0 ? string.Join(", ", value.Tags)
        : value.Location is { } location
            ? location.DisplayContext ?? FormattableString.Invariant($"{location.Latitude}, {location.Longitude}")
        : value.TemporalValue ?? value.DateValue ?? value.NumberValue ?? value.TextValue ?? string.Empty;
}

[McpServerToolType]
[RemoteToolScopes("records.write", "records.delete")]
public sealed class MonkeysphereRecordMergeTools
{
    [McpServerTool(Name = "preview_record_merge", ReadOnly = true, Destructive = false)]
    [Description("Previews folding one record into another when both describe the same thing, and returns the revision needed to apply it. Requires BOTH records.write and records.delete, plus domainId, survivingRecordId and mergedRecordId. Reports the fields both records hold a value for as conflicts, the values that would be kept as source data only, the aliases and tags gained, and counts of relationships, reminders, images and graph views affected. Optional choices override the per-field default. Writes nothing. A pair that cannot be merged returns a refusal rather than failing. Revision covers both records and everything hanging off them, so any change to either invalidates it.")]
    public static Task<CallToolResult> PreviewAsync(RemoteRecordWriter writer, Guid domainId, Guid survivingRecordId,
        Guid mergedRecordId, IReadOnlyList<RemoteMergeChoice>? choices = null, CancellationToken cancellationToken = default) =>
        writer.PreviewMergeAsync(domainId, survivingRecordId, mergedRecordId, choices, cancellationToken);

    [McpServerTool(Name = "merge_records", ReadOnly = false, Destructive = true)]
    [Description("Merges mergedRecordId into survivingRecordId and deletes it. Requires BOTH records.write and records.delete, plus domainId, both ids, expectedRevision from preview_record_merge and an idempotencyKey. Nothing is lost: values the surviving record's type can hold become its data, and everything else -- values a choice discarded, and fields its type has no place for -- is written to that record's retained source material under kind 'merge', readable with get_record_source. The other record's name becomes an alias; its tags, images and relationships move across. choices sets each conflicting field to KeepSurviving (the default), TakeMerged, or KeepBoth, which appends both values and combines the lists on a tags field. A link between the two records, a relationship duplicating one the survivor already has, and a reminder about a value that is not kept are dropped. Any change to either record after the preview rejects the merge. Identical retries replay the receipt for 24 hours. The merge is not reversible.")]
    public static Task<CallToolResult> MergeAsync(RemoteRecordWriter writer, Guid domainId, Guid survivingRecordId,
        Guid mergedRecordId, string expectedRevision, Guid idempotencyKey,
        IReadOnlyList<RemoteMergeChoice>? choices = null, CancellationToken cancellationToken = default) =>
        writer.MergeRecordsAsync(domainId, survivingRecordId, mergedRecordId, expectedRevision, choices,
            idempotencyKey, cancellationToken);
}
