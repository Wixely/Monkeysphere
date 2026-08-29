namespace Monkeysphere.Core;

public enum FieldLifecycle
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
    int? PresetVersion = null);

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
    string? ApproximationNote = null);

public sealed record RecordDetails(
    RecordSummary Record,
    IReadOnlyList<RecordValue> Values,
    IReadOnlyList<RecordTypeField> AvailableFields,
    IReadOnlyList<string> Aliases);

public sealed record FieldValueInput(
    Guid FieldDefinitionId,
    string? ScalarValue = null,
    IReadOnlyList<string>? Tags = null,
    TemporalValueInput? Temporal = null);

public sealed record NormalizedFieldValue(
    Guid Id,
    Guid FieldDefinitionId,
    int Ordinal,
    string? TextValue,
    string? NumberValue,
    double? NumberSortValue,
    string? DateValue,
    IReadOnlyList<string> Tags,
    NormalizedTemporalValue? Temporal = null);

public sealed record CreateFieldRequest(
    string Name,
    string TypeId,
    bool IsRequired,
    IReadOnlyCollection<string>? ChoiceOptions = null);

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
