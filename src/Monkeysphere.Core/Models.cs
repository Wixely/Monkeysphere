namespace Monkeysphere.Core;

public enum FieldLifecycle
{
    Active,
    Retired,
}

public enum RecordTypeLifecycle
{
    Active,
    Retired,
}

public sealed record RecordType(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? PresetKey = null,
    int? PresetVersion = null,
    RecordTypeLifecycle Lifecycle = RecordTypeLifecycle.Active);

public sealed record RecordTypeRetirementPreview(
    RecordType RecordType,
    string Revision,
    int RecordCount,
    int SavedViewCount);

public sealed record RecordTypeMergePreview(
    RecordType Source,
    RecordType Target,
    string Revision,
    int SourceRecordCount,
    int TargetRecordCount,
    int SourceSavedViewCount,
    int SourceFieldCount,
    int SharedFieldCount,
    int AddedFieldCount,
    int RequiredDowngradeCount);

public sealed record FieldDefinition(
    Guid Id,
    string Name,
    string TypeId,
    string ConfigurationJson,
    FieldLifecycle Lifecycle,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? CanonicalKey = null,
    string? PresetKey = null,
    int? PresetVersion = null);

public sealed record RecordTypeField(
    FieldDefinition Definition,
    int SortOrder,
    bool IsRequired);

public sealed record RecordTypeDetails(
    RecordType RecordType,
    IReadOnlyList<RecordTypeField> Fields);

public sealed record RecordSummary(
    Guid Id,
    Guid RecordTypeId,
    string RecordTypeName,
    string DisplayName,
    DateTimeOffset UpdatedAtUtc);

public sealed record RecordValue(
    Guid Id,
    Guid FieldDefinitionId,
    string FieldName,
    string TypeId,
    int Ordinal,
    string? TextValue,
    string? NumberValue,
    double? NumberSortValue,
    string? DateValue,
    IReadOnlyList<string> Tags,
    string? TemporalValue = null,
    TemporalPrecision? TemporalPrecision = null,
    string? TemporalSortKey = null,
    bool IsApproximate = false,
    string? ApproximationNote = null,
    LocationValue? Location = null);

public sealed record RecordDetails(
    RecordSummary Record,
    IReadOnlyList<RecordValue> Values,
    IReadOnlyList<RecordTypeField> AvailableFields,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<RecordImage> Images);

public sealed record RecordImage(
    Guid Id,
    Guid RecordId,
    int Ordinal,
    string OriginalFileName,
    string OriginalContentType,
    long OriginalByteLength,
    int Width,
    int Height,
    DateTimeOffset CreatedAtUtc,
    string? Caption = null,
    bool IsCover = false,
    ImageCorrection? Correction = null);

public sealed record ImageCorrection(
    int RotationQuarterTurns = 0,
    int? CropX = null,
    int? CropY = null,
    int? CropWidth = null,
    int? CropHeight = null);

public enum RecordImageVariant
{
    Preview,
    Thumbnail,
    Original,
}

public sealed record RecordImageFile(Stream Content, string ContentType, string? DownloadFileName = null);

public sealed record FieldValueInput(
    Guid FieldDefinitionId,
    string? ScalarValue = null,
    IReadOnlyList<string>? Tags = null,
    TemporalValueInput? Temporal = null,
    LocationValueInput? Location = null);

public sealed record NormalizedFieldValue(
    Guid Id,
    Guid FieldDefinitionId,
    int Ordinal,
    string? TextValue,
    string? NumberValue,
    double? NumberSortValue,
    string? DateValue,
    IReadOnlyList<string> Tags,
    NormalizedTemporalValue? Temporal = null,
    LocationValue? Location = null);

public sealed record PreparedRecord(
    Guid RecordTypeId,
    string DisplayName,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<NormalizedFieldValue> Values);

public sealed record CreateFieldRequest(
    string Name,
    string TypeId,
    bool IsRequired,
    IReadOnlyCollection<string>? ChoiceOptions = null);

public enum FieldMergeConflictResolution
{
    Reject,
    KeepTarget,
    KeepSource,
}

public sealed record FieldMergePreview(
    FieldDefinition Source,
    FieldDefinition Target,
    string Revision,
    bool IsCompatible,
    string? IncompatibilityReason,
    int SourceAttachmentCount,
    int SourceValueCount,
    int ConflictingValueCount,
    int SavedViewReferenceCount);

public sealed record ConvertFieldRequest(
    string Name,
    string TypeId,
    IReadOnlyCollection<string>? ChoiceOptions = null);

public sealed record FieldConversionIssue(
    Guid RecordId,
    string RecordDisplayName,
    string Reason);

public sealed record FieldConversionPreview(
    FieldDefinition Source,
    string Revision,
    string TargetName,
    string TargetTypeId,
    string TargetConfigurationJson,
    int AttachmentCount,
    int ValueCount,
    int SavedViewReferenceCount,
    int FailedValueCount,
    IReadOnlyList<FieldConversionIssue> Issues);

public sealed record FieldValueUsage(
    Guid RecordId,
    string RecordDisplayName,
    RecordValue Value);

public sealed record FieldUsageSnapshot(
    FieldDefinition Definition,
    string Revision,
    int AttachmentCount,
    int SavedViewReferenceCount,
    IReadOnlyList<FieldValueUsage> Values);

public sealed record ConvertedFieldValue(
    Guid SourceValueId,
    Guid RecordId,
    NormalizedFieldValue Value);

public enum FieldFilterOperator
{
    Equals,
    Contains,
    GreaterThan,
    LessThan,
    Before,
    After,
}

public sealed record RecordSearch(
    string? Query = null,
    Guid? RecordTypeId = null,
    Guid? FieldDefinitionId = null,
    FieldFilterOperator? Operator = null,
    string? FilterValue = null,
    int Page = 1,
    int PageSize = 25,
    IReadOnlyList<RecordFilter>? Filters = null,
    RecordSort? Sort = null);

public sealed record RecordFilter(
    Guid FieldDefinitionId,
    FieldFilterOperator Operator,
    string Value);

public sealed record RecordSort(
    Guid? FieldDefinitionId = null,
    bool Descending = false);

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount);
