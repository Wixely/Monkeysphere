using System.Globalization;

namespace Monkeysphere.Core;

public sealed class MonkeysphereService(IMonkeysphereStore store, TimeProvider timeProvider) : IMonkeysphereService
{
    public Task<IReadOnlyList<RecordType>> ListRecordTypesAsync(CancellationToken cancellationToken = default) =>
        store.ListRecordTypesAsync(cancellationToken);

    public Task<RecordTypeDetails?> GetRecordTypeAsync(Guid id, CancellationToken cancellationToken = default) =>
        store.GetRecordTypeAsync(id, cancellationToken);

    public Task<IReadOnlyList<FieldDefinition>> ListFieldDefinitionsAsync(CancellationToken cancellationToken = default) =>
        store.ListFieldDefinitionsAsync(cancellationToken);

    public Task<RecordType> CreateRecordTypeAsync(string name, CancellationToken cancellationToken = default) =>
        store.CreateRecordTypeAsync(
            Guid.CreateVersion7(),
            FieldTypes.Required(name, "Record type name", 200),
            timeProvider.GetUtcNow(),
            cancellationToken);

    public Task RenameRecordTypeAsync(Guid id, string name, CancellationToken cancellationToken = default) =>
        store.RenameRecordTypeAsync(
            id,
            FieldTypes.Required(name, "Record type name", 200),
            timeProvider.GetUtcNow(),
            cancellationToken);

    public Task<FieldDefinition> CreateAndAttachFieldAsync(
        Guid recordTypeId,
        CreateFieldRequest request,
        CancellationToken cancellationToken = default)
    {
        string typeId = FieldTypes.NormalizeTypeId(request.TypeId);
        return store.CreateAndAttachFieldAsync(
            recordTypeId,
            Guid.CreateVersion7(),
            FieldTypes.Required(request.Name, "Field name", 200),
            typeId,
            FieldTypes.NormalizeConfiguration(typeId, request.ChoiceOptions),
            request.IsRequired,
            timeProvider.GetUtcNow(),
            cancellationToken);
    }

    public Task AttachFieldAsync(
        Guid recordTypeId,
        Guid fieldDefinitionId,
        bool isRequired,
        CancellationToken cancellationToken = default) =>
        store.AttachFieldAsync(recordTypeId, fieldDefinitionId, isRequired, timeProvider.GetUtcNow(), cancellationToken);

    public Task RenameFieldAsync(Guid id, string name, CancellationToken cancellationToken = default) =>
        store.RenameFieldAsync(id, FieldTypes.Required(name, "Field name", 200), timeProvider.GetUtcNow(), cancellationToken);

    public Task RetireFieldAsync(Guid id, CancellationToken cancellationToken = default) =>
        store.RetireFieldAsync(id, timeProvider.GetUtcNow(), cancellationToken);

    public async Task<RecordDetails> CreateRecordAsync(
        Guid recordTypeId,
        string displayName,
        IReadOnlyList<FieldValueInput> values,
        CancellationToken cancellationToken = default)
    {
        RecordTypeDetails type = await RequireRecordTypeAsync(recordTypeId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<NormalizedFieldValue> normalized = NormalizeValues(type, values);
        return await store.CreateRecordAsync(
            Guid.CreateVersion7(),
            recordTypeId,
            FieldTypes.Required(displayName, "Display name", 300),
            normalized,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
    }

    public Task<RecordDetails?> GetRecordAsync(Guid id, CancellationToken cancellationToken = default) =>
        store.GetRecordAsync(id, cancellationToken);

    public async Task<RecordDetails> UpdateRecordAsync(
        Guid id,
        string displayName,
        IReadOnlyList<FieldValueInput> values,
        CancellationToken cancellationToken = default)
    {
        RecordDetails current = await store.GetRecordAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainValidationException("Record was not found.");
        RecordTypeDetails type = new(
            new RecordType(current.Record.RecordTypeId, current.Record.RecordTypeName, default, default),
            current.AvailableFields);
        HashSet<Guid> editableRetiredFields = current.Values
            .Where(value => current.AvailableFields.Any(field =>
                field.Definition.Id == value.FieldDefinitionId &&
                field.Definition.Lifecycle == FieldLifecycle.Retired))
            .Select(value => value.FieldDefinitionId)
            .ToHashSet();
        IReadOnlyList<NormalizedFieldValue> normalized = NormalizeValues(type, values, editableRetiredFields);
        return await store.UpdateRecordAsync(
            id,
            FieldTypes.Required(displayName, "Display name", 300),
            normalized,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> DeleteRecordAsync(Guid id, CancellationToken cancellationToken = default) =>
        store.DeleteRecordAsync(id, cancellationToken);

    public Task<PagedResult<RecordSummary>> SearchRecordsAsync(RecordSearch search, CancellationToken cancellationToken = default)
    {
        if (search.Page < 1)
        {
            throw new DomainValidationException("Page must be at least one.");
        }

        if (search.PageSize is < 1 or > 100)
        {
            throw new DomainValidationException("Page size must be between 1 and 100.");
        }

        if (search.FieldDefinitionId.HasValue != search.Operator.HasValue ||
            search.FieldDefinitionId.HasValue != !string.IsNullOrWhiteSpace(search.FilterValue))
        {
            throw new DomainValidationException("Typed filters require a field, operator, and value together.");
        }

        return store.SearchRecordsAsync(search with { Query = search.Query?.Trim(), FilterValue = search.FilterValue?.Trim() }, cancellationToken);
    }

    private async Task<RecordTypeDetails> RequireRecordTypeAsync(Guid id, CancellationToken cancellationToken) =>
        await store.GetRecordTypeAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainValidationException("Record type was not found.");

    private static List<NormalizedFieldValue> NormalizeValues(
        RecordTypeDetails type,
        IReadOnlyList<FieldValueInput> inputs,
        HashSet<Guid>? editableRetiredFields = null)
    {
        Dictionary<Guid, FieldValueInput> supplied = new();
        foreach (FieldValueInput input in inputs)
        {
            if (!supplied.TryAdd(input.FieldDefinitionId, input))
            {
                throw new DomainValidationException("A field can be supplied only once per record.");
            }
        }

        List<NormalizedFieldValue> normalized = [];
        foreach (RecordTypeField field in type.Fields.Where(item =>
            item.Definition.Lifecycle == FieldLifecycle.Active ||
            editableRetiredFields?.Contains(item.Definition.Id) == true))
        {
            supplied.Remove(field.Definition.Id, out FieldValueInput? input);
            NormalizedFieldValue? value = NormalizeValue(field.Definition, input);
            if (value is null)
            {
                if (field.IsRequired && field.Definition.Lifecycle == FieldLifecycle.Active)
                {
                    throw new DomainValidationException($"{field.Definition.Name} is required.");
                }

                continue;
            }

            normalized.Add(value);
        }

        if (supplied.Count != 0)
        {
            throw new DomainValidationException("One or more values refer to fields that are not active on this record type.");
        }

        return normalized;
    }

    private static NormalizedFieldValue? NormalizeValue(FieldDefinition definition, FieldValueInput? input)
    {
        if (input is null)
        {
            return null;
        }

        if (string.Equals(definition.TypeId, FieldTypes.Tags, StringComparison.Ordinal))
        {
            string[] tags = (input.Tags ?? []).Select(item => item.Trim()).ToArray();
            if (tags.Length > 100)
            {
                throw new DomainValidationException($"{definition.Name} cannot contain more than 100 tags.");
            }

            if (tags.Any(item => item.Length == 0))
            {
                throw new DomainValidationException($"{definition.Name} cannot contain an empty tag.");
            }

            if (tags.Any(item => item.Length > 200))
            {
                throw new DomainValidationException($"{definition.Name} contains a tag longer than 200 characters.");
            }

            tags = tags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            return tags.Length == 0
                ? null
                : new(Guid.CreateVersion7(), definition.Id, 0, null, null, null, null, tags);
        }

        if (string.Equals(definition.TypeId, FieldTypes.Temporal, StringComparison.Ordinal))
        {
            return input.Temporal is null || string.IsNullOrWhiteSpace(input.Temporal.Value)
                ? null
                : new(
                    Guid.CreateVersion7(),
                    definition.Id,
                    0,
                    null,
                    null,
                    null,
                    null,
                    [],
                    TemporalValues.Normalize(input.Temporal, definition.Name));
        }

        string raw = input.ScalarValue ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (raw.Length > 20_000)
        {
            throw new DomainValidationException($"{definition.Name} is too long.");
        }

        return definition.TypeId switch
        {
            FieldTypes.Number => NormalizeNumber(definition, raw.Trim()),
            FieldTypes.ExactDate => NormalizeDate(definition, raw.Trim()),
            FieldTypes.Choice => NormalizeChoice(definition, raw.Trim()),
            FieldTypes.Text => new(Guid.CreateVersion7(), definition.Id, 0, raw.Trim(), null, null, null, []),
            FieldTypes.PhoneNumber => NormalizePhoneNumber(definition, raw.Trim()),
            FieldTypes.WebLink => NormalizeWebLink(definition, raw.Trim()),
            _ => new(Guid.CreateVersion7(), definition.Id, 0, raw, null, null, null, []),
        };
    }

    private static NormalizedFieldValue NormalizeNumber(FieldDefinition definition, string scalar)
    {
        if (!decimal.TryParse(scalar, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal number))
        {
            throw new DomainValidationException($"{definition.Name} must be a valid invariant number.");
        }

        return new(
            Guid.CreateVersion7(),
            definition.Id,
            0,
            null,
            number.ToString(CultureInfo.InvariantCulture),
            (double)number,
            null,
            []);
    }

    private static NormalizedFieldValue NormalizeDate(FieldDefinition definition, string scalar)
    {
        if (!DateOnly.TryParseExact(scalar, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
        {
            throw new DomainValidationException($"{definition.Name} must use YYYY-MM-DD.");
        }

        return new(Guid.CreateVersion7(), definition.Id, 0, null, null, null, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), []);
    }

    private static NormalizedFieldValue NormalizeChoice(FieldDefinition definition, string scalar)
    {
        string? choice = FieldTypes.ChoiceOptions(definition)
            .FirstOrDefault(option => string.Equals(option, scalar, StringComparison.OrdinalIgnoreCase));
        if (choice is null)
        {
            throw new DomainValidationException($"{definition.Name} must be one of its configured choices.");
        }

        return new(Guid.CreateVersion7(), definition.Id, 0, choice, null, null, null, []);
    }

    private static NormalizedFieldValue NormalizePhoneNumber(FieldDefinition definition, string scalar)
    {
        if (scalar.Length > 200 || scalar.Count(char.IsDigit) < 3 || scalar.Any(character =>
            !char.IsDigit(character) &&
            !char.IsWhiteSpace(character) &&
            character is not ('+' or '-' or '(' or ')' or '.' or '#' or 'x' or 'X')))
        {
            throw new DomainValidationException($"{definition.Name} must be a plausible phone number.");
        }

        return new(Guid.CreateVersion7(), definition.Id, 0, scalar, null, null, null, []);
    }

    private static NormalizedFieldValue NormalizeWebLink(FieldDefinition definition, string scalar)
    {
        if (scalar.Length > 2_048 ||
            !Uri.TryCreate(scalar, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new DomainValidationException($"{definition.Name} must be an absolute HTTP or HTTPS link.");
        }

        return new(Guid.CreateVersion7(), definition.Id, 0, uri.AbsoluteUri, null, null, null, []);
    }
}
