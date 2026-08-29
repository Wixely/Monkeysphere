namespace Monkeysphere.Core;

public interface IMonkeysphereStore
{
    Task<IReadOnlyList<RecordType>> ListRecordTypesAsync(CancellationToken cancellationToken = default);

    Task<RecordTypeDetails?> GetRecordTypeAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FieldDefinition>> ListFieldDefinitionsAsync(CancellationToken cancellationToken = default);

    Task<RecordType> CreateRecordTypeAsync(Guid id, string name, DateTimeOffset now, CancellationToken cancellationToken = default);

    Task RenameRecordTypeAsync(Guid id, string name, DateTimeOffset now, CancellationToken cancellationToken = default);

    Task<RecordTypeRetirementPreview?> PreviewRecordTypeRetirementAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task RetireRecordTypeAsync(
        Guid id,
        string expectedRevision,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<RecordTypeMergePreview?> PreviewRecordTypeMergeAsync(
        Guid sourceRecordTypeId,
        Guid targetRecordTypeId,
        CancellationToken cancellationToken = default);

    Task MergeRecordTypesAsync(
        Guid sourceRecordTypeId,
        Guid targetRecordTypeId,
        string expectedRevision,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<FieldDefinition> CreateAndAttachFieldAsync(
        Guid recordTypeId,
        Guid fieldDefinitionId,
        string name,
        string typeId,
        string configurationJson,
        bool isRequired,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task AttachFieldAsync(
        Guid recordTypeId,
        Guid fieldDefinitionId,
        bool isRequired,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task RenameFieldAsync(Guid id, string name, DateTimeOffset now, CancellationToken cancellationToken = default);

    Task RetireFieldAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken = default);

    Task<FieldMergePreview?> PreviewFieldMergeAsync(
        Guid sourceFieldDefinitionId,
        Guid targetFieldDefinitionId,
        CancellationToken cancellationToken = default);

    Task MergeFieldsAsync(
        Guid sourceFieldDefinitionId,
        Guid targetFieldDefinitionId,
        FieldMergeConflictResolution conflictResolution,
        string expectedRevision,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<FieldUsageSnapshot?> GetFieldUsageAsync(Guid id, CancellationToken cancellationToken = default);

    Task<FieldDefinition> ConvertFieldAsync(
        Guid sourceFieldDefinitionId,
        Guid targetFieldDefinitionId,
        string targetName,
        string targetTypeId,
        string targetConfigurationJson,
        IReadOnlyList<ConvertedFieldValue> convertedValues,
        string expectedRevision,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<RecordDetails> CreateRecordAsync(
        Guid id,
        Guid recordTypeId,
        string displayName,
        IReadOnlyList<string> aliases,
        IReadOnlyList<NormalizedFieldValue> values,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<RecordDetails?> GetRecordAsync(Guid id, CancellationToken cancellationToken = default);

    Task<RecordDetails> UpdateRecordAsync(
        Guid id,
        string displayName,
        IReadOnlyList<string> aliases,
        IReadOnlyList<NormalizedFieldValue> values,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteRecordAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PagedResult<RecordSummary>> SearchRecordsAsync(RecordSearch search, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RecordImage>> ListRecordImagesAsync(Guid recordId, CancellationToken cancellationToken = default);

    Task<RecordImage> AddRecordImageAsync(RecordImage image, CancellationToken cancellationToken = default);

    Task<bool> DeleteRecordImageAsync(
        Guid recordId,
        Guid imageId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<RecordImage> UpdateRecordImageAsync(
        Guid recordId,
        Guid imageId,
        string? caption,
        bool isCover,
        ImageCorrection correction,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task ReorderRecordImagesAsync(
        Guid recordId,
        IReadOnlyList<Guid> imageIds,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

public interface IMonkeysphereService
{
    Task<IReadOnlyList<RecordType>> ListRecordTypesAsync(CancellationToken cancellationToken = default);

    Task<RecordTypeDetails?> GetRecordTypeAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FieldDefinition>> ListFieldDefinitionsAsync(CancellationToken cancellationToken = default);

    Task<RecordType> CreateRecordTypeAsync(string name, CancellationToken cancellationToken = default);

    Task RenameRecordTypeAsync(Guid id, string name, CancellationToken cancellationToken = default);

    Task<RecordTypeRetirementPreview> PreviewRecordTypeRetirementAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task RetireRecordTypeAsync(
        Guid id,
        string expectedRevision,
        CancellationToken cancellationToken = default);

    Task<RecordTypeMergePreview> PreviewRecordTypeMergeAsync(
        Guid sourceRecordTypeId,
        Guid targetRecordTypeId,
        CancellationToken cancellationToken = default);

    Task MergeRecordTypesAsync(
        Guid sourceRecordTypeId,
        Guid targetRecordTypeId,
        string expectedRevision,
        CancellationToken cancellationToken = default);

    Task<FieldDefinition> CreateAndAttachFieldAsync(Guid recordTypeId, CreateFieldRequest request, CancellationToken cancellationToken = default);

    Task AttachFieldAsync(Guid recordTypeId, Guid fieldDefinitionId, bool isRequired, CancellationToken cancellationToken = default);

    Task RenameFieldAsync(Guid id, string name, CancellationToken cancellationToken = default);

    Task RetireFieldAsync(Guid id, CancellationToken cancellationToken = default);

    Task<FieldMergePreview> PreviewFieldMergeAsync(
        Guid sourceFieldDefinitionId,
        Guid targetFieldDefinitionId,
        CancellationToken cancellationToken = default);

    Task MergeFieldsAsync(
        Guid sourceFieldDefinitionId,
        Guid targetFieldDefinitionId,
        FieldMergeConflictResolution conflictResolution,
        string expectedRevision,
        CancellationToken cancellationToken = default);

    Task<FieldConversionPreview> PreviewFieldConversionAsync(
        Guid sourceFieldDefinitionId,
        ConvertFieldRequest request,
        CancellationToken cancellationToken = default);

    Task<FieldDefinition> ConvertFieldAsync(
        Guid sourceFieldDefinitionId,
        ConvertFieldRequest request,
        string expectedRevision,
        CancellationToken cancellationToken = default);

    Task<RecordDetails> CreateRecordAsync(
        Guid recordTypeId,
        string displayName,
        IReadOnlyList<FieldValueInput> values,
        IReadOnlyList<string>? aliases = null,
        CancellationToken cancellationToken = default);

    Task<PreparedRecord> PrepareRecordAsync(
        Guid recordTypeId,
        string displayName,
        IReadOnlyList<FieldValueInput> values,
        IReadOnlyList<string>? aliases = null,
        CancellationToken cancellationToken = default);

    Task<RecordDetails?> GetRecordAsync(Guid id, CancellationToken cancellationToken = default);

    Task<RecordDetails> UpdateRecordAsync(
        Guid id,
        string displayName,
        IReadOnlyList<FieldValueInput> values,
        IReadOnlyList<string>? aliases = null,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteRecordAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PagedResult<RecordSummary>> SearchRecordsAsync(RecordSearch search, CancellationToken cancellationToken = default);
}

public interface IRecordImageService
{
    const int MaximumImagesPerRecord = 50;
    const long MaximumUploadBytes = 10 * 1024 * 1024;

    Task<RecordImage> AddAsync(
        Guid recordId,
        Stream content,
        string originalFileName,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid recordId, Guid imageId, CancellationToken cancellationToken = default);

    Task<RecordImage> UpdateMetadataAsync(
        Guid recordId,
        Guid imageId,
        string? caption,
        bool isCover,
        CancellationToken cancellationToken = default);

    Task ReorderAsync(
        Guid recordId,
        IReadOnlyList<Guid> imageIds,
        CancellationToken cancellationToken = default);

    Task<RecordImage> CorrectAsync(
        Guid recordId,
        Guid imageId,
        ImageCorrection correction,
        CancellationToken cancellationToken = default);

    Task<RecordImageFile?> OpenAsync(
        Guid recordId,
        Guid imageId,
        RecordImageVariant variant,
        CancellationToken cancellationToken = default);
}
