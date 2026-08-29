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
    DateTimeOffset UpdatedAtUtc);

public sealed record FieldDefinition(
    Guid Id,
    string Name,
    string TypeId,
    string ConfigurationJson,
    FieldLifecycle Lifecycle,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

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
    IReadOnlyList<string> Tags);

public sealed record RecordDetails(
    RecordSummary Record,
    IReadOnlyList<RecordValue> Values,
    IReadOnlyList<RecordTypeField> AvailableFields);

public sealed record FieldValueInput(
    Guid FieldDefinitionId,
    string? ScalarValue = null,
    IReadOnlyList<string>? Tags = null);

public sealed record NormalizedFieldValue(
    Guid Id,
    Guid FieldDefinitionId,
    int Ordinal,
    string? TextValue,
    string? NumberValue,
    double? NumberSortValue,
    string? DateValue,
    IReadOnlyList<string> Tags);

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
    int PageSize = 25);

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount);
