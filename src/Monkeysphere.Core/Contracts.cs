namespace Monkeysphere.Core;

public interface IMonkeysphereStore
{
    Task<IReadOnlyList<RecordType>> ListRecordTypesAsync(CancellationToken cancellationToken = default);

    Task<RecordTypeDetails?> GetRecordTypeAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FieldDefinition>> ListFieldDefinitionsAsync(CancellationToken cancellationToken = default);

    Task<RecordType> CreateRecordTypeAsync(Guid id, string name, DateTimeOffset now, CancellationToken cancellationToken = default);

    Task RenameRecordTypeAsync(Guid id, string name, DateTimeOffset now, CancellationToken cancellationToken = default);

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

    Task<RecordDetails> CreateRecordAsync(
        Guid id,
        Guid recordTypeId,
        string displayName,
        IReadOnlyList<NormalizedFieldValue> values,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<RecordDetails?> GetRecordAsync(Guid id, CancellationToken cancellationToken = default);

    Task<RecordDetails> UpdateRecordAsync(
        Guid id,
        string displayName,
        IReadOnlyList<NormalizedFieldValue> values,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteRecordAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PagedResult<RecordSummary>> SearchRecordsAsync(RecordSearch search, CancellationToken cancellationToken = default);
}

public interface IMonkeysphereService
{
    Task<IReadOnlyList<RecordType>> ListRecordTypesAsync(CancellationToken cancellationToken = default);

    Task<RecordTypeDetails?> GetRecordTypeAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FieldDefinition>> ListFieldDefinitionsAsync(CancellationToken cancellationToken = default);

    Task<RecordType> CreateRecordTypeAsync(string name, CancellationToken cancellationToken = default);

    Task RenameRecordTypeAsync(Guid id, string name, CancellationToken cancellationToken = default);

    Task<FieldDefinition> CreateAndAttachFieldAsync(Guid recordTypeId, CreateFieldRequest request, CancellationToken cancellationToken = default);

    Task AttachFieldAsync(Guid recordTypeId, Guid fieldDefinitionId, bool isRequired, CancellationToken cancellationToken = default);

    Task RenameFieldAsync(Guid id, string name, CancellationToken cancellationToken = default);

    Task RetireFieldAsync(Guid id, CancellationToken cancellationToken = default);

    Task<RecordDetails> CreateRecordAsync(
        Guid recordTypeId,
        string displayName,
        IReadOnlyList<FieldValueInput> values,
        CancellationToken cancellationToken = default);

    Task<RecordDetails?> GetRecordAsync(Guid id, CancellationToken cancellationToken = default);

    Task<RecordDetails> UpdateRecordAsync(
        Guid id,
        string displayName,
        IReadOnlyList<FieldValueInput> values,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteRecordAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PagedResult<RecordSummary>> SearchRecordsAsync(RecordSearch search, CancellationToken cancellationToken = default);
}
