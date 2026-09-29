namespace Monkeysphere.Core;

/// <summary>What to do with a field both records hold a value for.</summary>
public enum RecordMergeResolution
{
    /// <summary>Keep the surviving record's value. The other is archived rather than deleted.</summary>
    KeepSurviving,

    /// <summary>Take the merged-away record's value instead. The survivor's own is archived.</summary>
    TakeMerged,

    /// <summary>
    /// Keep both. The field ends up holding the surviving record's values followed by the merged-away
    /// record's, because nothing in the model says a field holds only one — two phone numbers or two
    /// notes are a better answer than throwing one away. A tags field unions its lists instead of
    /// ending up with two lists.
    /// </summary>
    KeepBoth,
}

public sealed record RecordMergeChoice(Guid FieldDefinitionId, RecordMergeResolution Resolution);

/// <summary>
/// One field both records hold a value for, which is the only case a person has to decide.
///
/// The model has no cardinality flag, so "this field holds one value" is not something the
/// application can be told; it is inferred from the situation. A field only one record uses is
/// carried without asking. A field both use is a conflict, because silently keeping both would
/// invent a record neither side had and silently keeping one would lose data without saying so.
/// </summary>
public sealed record RecordMergeValueConflict(
    Guid FieldDefinitionId,
    string FieldName,
    string TypeId,
    IReadOnlyList<RecordValue> SurvivingValues,
    IReadOnlyList<RecordValue> MergedValues,
    RecordMergeResolution Resolution);

/// <summary>Why a value from the merged-away record did not become live data on the survivor.</summary>
public static class RecordMergeUncarriedReasons
{
    public const string SurvivingValueKept = "The surviving record's own value was kept.";

    /// <summary>
    /// The opposite case, and the one the promise is easiest to break on: somebody chose the other
    /// record's value, so the surviving record's own is displaced. It is archived like anything else,
    /// because a merge losing the data of the record that survived would be the worst version of this.
    /// </summary>
    public const string SurvivingValueReplaced = "The surviving record's own value was replaced by the other record's.";

    /// <summary>
    /// Only possible when the two records are of different types. The survivor's type has no such
    /// field, so there is nowhere for the value to live as record data. It is archived instead, which
    /// is the difference between merging across types and losing something.
    /// </summary>
    public const string FieldNotOnSurvivingType = "The surviving record's type does not have this field.";
}

public sealed record RecordMergeUncarriedValue(
    Guid FieldDefinitionId, string FieldName, string Reason, IReadOnlyList<RecordValue> Values);

/// <summary>
/// What a merge would do, counted. Every number is about the record being merged away, because that is
/// the record something happens to; the survivor's own data is only counted where the merge displaces
/// it, as a replaced field's reminders are.
///
/// Nothing here is a loss: a value that is not carried stops being live record data and becomes
/// readable as retained source material, which is the promise the merge makes and the reason it is not
/// just a delete.
/// </summary>
public sealed record RecordMergeImpact(
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

/// <summary>
/// What merging one record into another would do, and what the person has to decide first.
///
/// <see cref="Refusal"/> is non-null when the pair cannot be merged at all. It is reported rather
/// than thrown because asking whether two records can be merged is a reasonable thing for a picker
/// to do, and it needs to be able to say why one is unavailable.
/// </summary>
public sealed record RecordMergePreview(
    RecordSummary Surviving,
    RecordSummary Merged,
    string Revision,
    string? Refusal,
    RecordMergeImpact Impact,
    IReadOnlyList<RecordMergeValueConflict> Conflicts,
    IReadOnlyList<RecordMergeUncarriedValue> Archived,
    IReadOnlyList<string> AliasesAdded,
    IReadOnlyList<string> TagsAdded);

public static class RecordMergeLimits
{
    /// <summary>
    /// Conflicts and archived values listed in one preview. A merge of two records cannot exceed the
    /// fields their types carry, so this is a sanity bound rather than a paging scheme.
    /// </summary>
    public const int MaximumReportedValues = 500;
}

/// <summary>
/// The merge as a remote command: the same merge, with an idempotent receipt around it.
///
/// A merge deletes a record, so a retry that ran twice would be a retry that deleted two. The receipt
/// makes an identical retry replay rather than repeat, and the revision the preview handed back makes a
/// retry issued against stale information refuse rather than merge something else.
/// </summary>
public interface IRecordMergeStore
{
    Task<RecordCommandReceipt> MergeRecordsAsync(
        RecordCommandIdentity identity,
        Guid survivingRecordId,
        Guid mergedRecordId,
        IReadOnlyList<RecordMergeChoice> choices,
        string expectedRevision,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

public sealed class RecordMergeCommandService(
    IRecordMergeStore merges, IMonkeysphereService records, TimeProvider timeProvider)
{
    /// <summary>
    /// Asks what a merge would do. Null when either record is missing or out of the caller's sight; a
    /// pair that cannot be merged answers with a refusal, because a caller choosing between candidates
    /// needs to be told which are unavailable and why.
    /// </summary>
    public Task<RecordMergePreview?> PreviewAsync(Guid survivingRecordId, Guid mergedRecordId,
        IReadOnlyList<RecordMergeChoice>? choices, CancellationToken cancellationToken = default) =>
        records.PreviewRecordMergeAsync(survivingRecordId, mergedRecordId, choices, cancellationToken);

    public Task<RecordCommandReceipt> MergeAsync(RecordCommandIdentity identity, Guid survivingRecordId,
        Guid mergedRecordId, IReadOnlyList<RecordMergeChoice> choices, string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        if (choices is null || choices.Count > RecordMergeLimits.MaximumReportedValues || choices.Any(choice => choice is null))
        {
            throw new DomainValidationException(
                $"Supply at most {RecordMergeLimits.MaximumReportedValues} non-null merge choices.");
        }

        if (string.IsNullOrWhiteSpace(expectedRevision))
        {
            // Without one the merge would run on whatever the records happen to say now, which for a
            // command that deletes one of them is not a reasonable default.
            throw new DomainValidationException("Supply the revision from a merge preview.");
        }

        return merges.MergeRecordsAsync(identity, survivingRecordId, mergedRecordId, choices, expectedRevision,
            timeProvider.GetUtcNow(), cancellationToken);
    }
}
