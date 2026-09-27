namespace Monkeysphere.Core;

/// <summary>
/// One field both records hold a value for, which is the only case a person has to decide.
///
/// The model has no cardinality flag, so "this field holds one value" is not something the
/// application can be told; it is inferred from the situation. A field only one of the two records
/// uses is carried without asking. A field both use is a conflict, whether that is one date of birth
/// against another or three phone numbers against two, because in every such case keeping both would
/// invent a record neither side had.
/// </summary>
public sealed record RecordMergeValueConflict(
    Guid FieldDefinitionId,
    string FieldName,
    string TypeId,
    IReadOnlyList<RecordValue> TargetValues,
    IReadOnlyList<RecordValue> SourceValues);

/// <summary>Take the merged-away record's values for this field instead of the survivor's.</summary>
public sealed record RecordMergeChoice(Guid FieldDefinitionId, bool TakeSourceValues);

/// <summary>
/// What a merge would do, counted. <see cref="FieldValuesRetainedOnly"/> is the number that lose
/// their conflict: they are not discarded, they stop being live record data and remain readable as
/// retained source material, which is the whole promise of the merge.
/// </summary>
public sealed record RecordMergeImpact(
    int FieldValuesCarried,
    int FieldValuesRetainedOnly,
    int AliasesAdded,
    int TagsAdded,
    int ImagesCarried,
    int RelationshipsRepointed,
    int RelationshipsDropped,
    int RemindersCarried,
    int RemindersCollapsed,
    int SourceImportsCarried,
    int GraphViewsUpdated);

/// <summary>
/// What merging one record into another would do, and what the person has to decide first.
///
/// <see cref="Refusal"/> is non-null when the pair cannot be merged at all. It is reported rather
/// than thrown because asking whether two records can be merged is a reasonable thing to do, and a
/// picker that offers a record needs to be able to say why it is unavailable.
/// </summary>
public sealed record RecordMergePreview(
    RecordSummary Target,
    RecordSummary Source,
    string Revision,
    string? Refusal,
    RecordMergeImpact Impact,
    IReadOnlyList<RecordMergeValueConflict> Conflicts,
    IReadOnlyList<string> AliasesAdded);

public static class RecordMergeLimits
{
    /// <summary>
    /// Conflicts listed in one preview. A merge of two records of one type cannot exceed the type's
    /// field count in practice, so this is a bound rather than a paging scheme.
    /// </summary>
    public const int MaximumConflicts = 500;
}
