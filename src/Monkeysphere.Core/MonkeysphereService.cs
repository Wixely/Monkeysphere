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

    public Task<RecordType> CreateRecordTypeAsync(string name, string? symbol = null, CancellationToken cancellationToken = default) =>
        store.CreateRecordTypeAsync(
            Guid.CreateVersion7(),
            FieldTypes.Required(name, "Record type name", 200),
            NormalizeRecordTypeSymbol(symbol),
            timeProvider.GetUtcNow(),
            cancellationToken);

    public Task RenameRecordTypeAsync(Guid id, string name, CancellationToken cancellationToken = default) =>
        store.RenameRecordTypeAsync(
            id,
            FieldTypes.Required(name, "Record type name", 200),
            timeProvider.GetUtcNow(),
            cancellationToken);

    public Task UpdateRecordTypeAsync(
        Guid id,
        string name,
        string? symbol,
        CancellationToken cancellationToken = default) =>
        store.UpdateRecordTypeAsync(
            id,
            FieldTypes.Required(name, "Record type name", 200),
            NormalizeRecordTypeSymbol(symbol),
            timeProvider.GetUtcNow(),
            cancellationToken);

    private static string? NormalizeRecordTypeSymbol(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return null;
        }

        string normalized = symbol.Trim();
        if (normalized.Length > 32 ||
            StringInfo.ParseCombiningCharacters(normalized).Length > 4 ||
            normalized.Any(char.IsControl))
        {
            throw new DomainValidationException("Record type symbol must contain at most four visible characters or emoji.");
        }

        return normalized;
    }

    public async Task<RecordTypeRetirementPreview> PreviewRecordTypeRetirementAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        await store.PreviewRecordTypeRetirementAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainValidationException("Record type was not found.");

    public async Task RetireRecordTypeAsync(
        Guid id,
        string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        RecordTypeRetirementPreview preview = await PreviewRecordTypeRetirementAsync(id, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(preview.Revision, expectedRevision, StringComparison.Ordinal))
        {
            throw new DomainValidationException("Record-type usage changed after the preview. Preview retirement again.");
        }

        await store.RetireRecordTypeAsync(
            id,
            expectedRevision,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<RecordTypeMergePreview> PreviewRecordTypeMergeAsync(
        Guid sourceRecordTypeId,
        Guid targetRecordTypeId,
        CancellationToken cancellationToken = default)
    {
        if (sourceRecordTypeId == targetRecordTypeId)
        {
            throw new DomainValidationException("Choose a different target record type.");
        }

        return await store.PreviewRecordTypeMergeAsync(
            sourceRecordTypeId,
            targetRecordTypeId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new DomainValidationException("One or both record types were not found.");
    }

    public async Task MergeRecordTypesAsync(
        Guid sourceRecordTypeId,
        Guid targetRecordTypeId,
        string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        RecordTypeMergePreview preview = await PreviewRecordTypeMergeAsync(
            sourceRecordTypeId,
            targetRecordTypeId,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(preview.Revision, expectedRevision, StringComparison.Ordinal))
        {
            throw new DomainValidationException("Record-type usage changed after the preview. Preview the merge again.");
        }

        if (preview.Target.Lifecycle != RecordTypeLifecycle.Active)
        {
            throw new DomainValidationException("The merge target must be active.");
        }

        await store.MergeRecordTypesAsync(
            sourceRecordTypeId,
            targetRecordTypeId,
            expectedRevision,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
    }

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

    public async Task<FieldMergePreview> PreviewFieldMergeAsync(
        Guid sourceFieldDefinitionId,
        Guid targetFieldDefinitionId,
        CancellationToken cancellationToken = default)
    {
        if (sourceFieldDefinitionId == targetFieldDefinitionId)
        {
            throw new DomainValidationException("Choose two different field definitions to merge.");
        }

        return await store.PreviewFieldMergeAsync(
            sourceFieldDefinitionId,
            targetFieldDefinitionId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new DomainValidationException("One or both field definitions were not found.");
    }

    public async Task MergeFieldsAsync(
        Guid sourceFieldDefinitionId,
        Guid targetFieldDefinitionId,
        FieldMergeConflictResolution conflictResolution,
        string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(conflictResolution))
        {
            throw new DomainValidationException("Choose a supported merge conflict policy.");
        }

        FieldMergePreview preview = await PreviewFieldMergeAsync(
            sourceFieldDefinitionId,
            targetFieldDefinitionId,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(preview.Revision, expectedRevision, StringComparison.Ordinal))
        {
            throw new DomainValidationException("Field usage changed after the preview. Preview the merge again.");
        }
        if (!preview.IsCompatible)
        {
            throw new DomainValidationException(preview.IncompatibilityReason ?? "The field definitions are not compatible.");
        }

        if (preview.ConflictingValueCount > 0 && conflictResolution == FieldMergeConflictResolution.Reject)
        {
            throw new DomainValidationException(
                $"{preview.ConflictingValueCount} record(s) contain both fields. Choose which value to keep before merging.");
        }

        await store.MergeFieldsAsync(
            sourceFieldDefinitionId,
            targetFieldDefinitionId,
            conflictResolution,
            expectedRevision,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<FieldConversionPreview> PreviewFieldConversionAsync(
        Guid sourceFieldDefinitionId,
        ConvertFieldRequest request,
        CancellationToken cancellationToken = default)
    {
        (FieldConversionPreview preview, _) = await PrepareConversionAsync(
            sourceFieldDefinitionId,
            request,
            Guid.Empty,
            cancellationToken).ConfigureAwait(false);
        return preview;
    }

    public async Task<FieldDefinition> ConvertFieldAsync(
        Guid sourceFieldDefinitionId,
        ConvertFieldRequest request,
        string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        Guid targetFieldDefinitionId = Guid.CreateVersion7();
        (FieldConversionPreview preview, IReadOnlyList<ConvertedFieldValue> values) = await PrepareConversionAsync(
            sourceFieldDefinitionId,
            request,
            targetFieldDefinitionId,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(preview.Revision, expectedRevision, StringComparison.Ordinal))
        {
            throw new DomainValidationException("Field usage changed after the preview. Preview the conversion again.");
        }
        if (preview.FailedValueCount != 0)
        {
            throw new DomainValidationException(
                $"Conversion cannot continue because {preview.FailedValueCount} value(s) cannot be represented safely.");
        }

        return await store.ConvertFieldAsync(
            sourceFieldDefinitionId,
            targetFieldDefinitionId,
            preview.TargetName,
            preview.TargetTypeId,
            preview.TargetConfigurationJson,
            values,
            expectedRevision,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<RecordDetails> CreateRecordAsync(
        Guid recordTypeId,
        string displayName,
        IReadOnlyList<FieldValueInput> values,
        IReadOnlyList<string>? aliases = null,
        CancellationToken cancellationToken = default)
    {
        PreparedRecord prepared = await PrepareRecordAsync(
            recordTypeId,
            displayName,
            values,
            aliases,
            cancellationToken).ConfigureAwait(false);
        return await store.CreateRecordAsync(
            Guid.CreateVersion7(),
            prepared.RecordTypeId,
            prepared.DisplayName,
            prepared.Aliases,
            prepared.Values,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<PreparedRecord> PrepareRecordAsync(
        Guid recordTypeId,
        string displayName,
        IReadOnlyList<FieldValueInput> values,
        IReadOnlyList<string>? aliases = null,
        CancellationToken cancellationToken = default)
    {
        RecordTypeDetails type = await RequireRecordTypeAsync(recordTypeId, cancellationToken).ConfigureAwait(false);
        if (type.RecordType.Lifecycle != RecordTypeLifecycle.Active)
        {
            throw new DomainValidationException("New records cannot be added to a retired record type.");
        }

        string primaryName = FieldTypes.Required(displayName, "Display name", 300);
        return new(
            recordTypeId,
            primaryName,
            NormalizeAliases(primaryName, aliases),
            NormalizeValues(type, values));
    }

    public Task<RecordDetails?> GetRecordAsync(Guid id, CancellationToken cancellationToken = default) =>
        store.GetRecordAsync(id, cancellationToken);

    public async Task<RecordDetails> UpdateRecordAsync(
        Guid id,
        string displayName,
        IReadOnlyList<FieldValueInput> values,
        IReadOnlyList<string>? aliases = null,
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
        string primaryName = FieldTypes.Required(displayName, "Display name", 300);
        return await store.UpdateRecordAsync(
            id,
            primaryName,
            NormalizeAliases(primaryName, aliases),
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

        if (search.Filters?.Count > 10)
        {
            throw new DomainValidationException("A search cannot contain more than 10 typed filters.");
        }

        RecordFilter[] filters = (search.Filters ?? [])
            .Select(filter => string.IsNullOrWhiteSpace(filter.Value)
                ? throw new DomainValidationException("Typed filters require a non-blank value.")
                : filter with { Value = filter.Value.Trim() })
            .ToArray();

        return store.SearchRecordsAsync(search with
        {
            Query = search.Query?.Trim(),
            FilterValue = search.FilterValue?.Trim(),
            Filters = filters,
        }, cancellationToken);
    }

    private async Task<RecordTypeDetails> RequireRecordTypeAsync(Guid id, CancellationToken cancellationToken) =>
        await store.GetRecordTypeAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainValidationException("Record type was not found.");

    private async Task<(FieldConversionPreview Preview, IReadOnlyList<ConvertedFieldValue> Values)> PrepareConversionAsync(
        Guid sourceFieldDefinitionId,
        ConvertFieldRequest request,
        Guid targetFieldDefinitionId,
        CancellationToken cancellationToken)
    {
        FieldUsageSnapshot usage = await store.GetFieldUsageAsync(sourceFieldDefinitionId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainValidationException("Field definition was not found.");
        string targetName = FieldTypes.Required(request.Name, "Field name", 200);
        string targetTypeId = FieldTypes.NormalizeTypeId(request.TypeId);
        string targetConfiguration = FieldTypes.NormalizeConfiguration(targetTypeId, request.ChoiceOptions);
        FieldDefinition target = new(
            targetFieldDefinitionId,
            targetName,
            targetTypeId,
            targetConfiguration,
            FieldLifecycle.Active,
            timeProvider.GetUtcNow(),
            timeProvider.GetUtcNow());

        List<ConvertedFieldValue> converted = [];
        List<FieldConversionIssue> issues = [];
        int failed = 0;
        foreach (FieldValueUsage value in usage.Values)
        {
            try
            {
                FieldValueInput input = ConversionInput(usage.Definition, target, value.Value);
                NormalizedFieldValue normalized = NormalizeValue(target, input)
                    ?? throw new DomainValidationException("The value would become empty.");
                converted.Add(new ConvertedFieldValue(value.Value.Id, value.RecordId, normalized));
            }
            catch (DomainValidationException exception)
            {
                failed++;
                if (issues.Count < 25)
                {
                    issues.Add(new FieldConversionIssue(value.RecordId, value.RecordDisplayName, exception.Message));
                }
            }
        }

        return (new FieldConversionPreview(
            usage.Definition,
            usage.Revision,
            targetName,
            targetTypeId,
            targetConfiguration,
            usage.AttachmentCount,
            usage.Values.Count,
            usage.SavedViewReferenceCount,
            failed,
            issues), converted);
    }

    private static FieldValueInput ConversionInput(
        FieldDefinition source,
        FieldDefinition target,
        RecordValue value)
    {
        if (string.Equals(target.TypeId, FieldTypes.Tags, StringComparison.Ordinal))
        {
            if (!string.Equals(source.TypeId, FieldTypes.Tags, StringComparison.Ordinal))
            {
                throw new DomainValidationException("Structured tag values cannot be inferred from this field type.");
            }

            return new(target.Id, Tags: value.Tags);
        }

        if (string.Equals(target.TypeId, FieldTypes.Location, StringComparison.Ordinal))
        {
            if (!string.Equals(source.TypeId, FieldTypes.Location, StringComparison.Ordinal) || value.Location is null)
            {
                throw new DomainValidationException("Structured locations can only convert to another location field.");
            }

            return new(target.Id, Location: new LocationValueInput(
                value.Location.DisplayContext,
                Invariant(value.Location.Latitude),
                Invariant(value.Location.Longitude),
                Invariant(value.Location.AccuracyMetres),
                Invariant(value.Location.ApproximationRadiusKilometres)));
        }

        if (string.Equals(target.TypeId, FieldTypes.Temporal, StringComparison.Ordinal))
        {
            if (string.Equals(source.TypeId, FieldTypes.Temporal, StringComparison.Ordinal) &&
                value.TemporalValue is not null && value.TemporalPrecision is TemporalPrecision precision)
            {
                return new(target.Id, Temporal: new TemporalValueInput(
                    value.TemporalValue,
                    precision,
                    value.IsApproximate,
                    value.ApproximationNote));
            }

            if (string.Equals(source.TypeId, FieldTypes.ExactDate, StringComparison.Ordinal) && value.DateValue is not null)
            {
                return new(target.Id, Temporal: new TemporalValueInput(value.DateValue, TemporalPrecision.Day));
            }

            throw new DomainValidationException("Temporal precision cannot be inferred safely from this field type.");
        }

        if (string.Equals(source.TypeId, FieldTypes.Tags, StringComparison.Ordinal) ||
            string.Equals(source.TypeId, FieldTypes.Location, StringComparison.Ordinal))
        {
            throw new DomainValidationException("This structured value cannot be flattened without losing information.");
        }

        if (string.Equals(source.TypeId, FieldTypes.Temporal, StringComparison.Ordinal))
        {
            if (string.Equals(target.TypeId, FieldTypes.ExactDate, StringComparison.Ordinal) &&
                value.TemporalPrecision == TemporalPrecision.Day &&
                !value.IsApproximate &&
                string.IsNullOrWhiteSpace(value.ApproximationNote) &&
                value.TemporalValue is not null)
            {
                return new(target.Id, value.TemporalValue);
            }

            throw new DomainValidationException("Temporal precision or approximation metadata would be lost.");
        }

        string scalar = value.TextValue ?? value.NumberValue ?? value.DateValue
            ?? throw new DomainValidationException("The stored value has no safe scalar representation.");
        return new(target.Id, scalar);
    }

    private static string? Invariant(double? value) =>
        value?.ToString("0.#######", CultureInfo.InvariantCulture);

    private static string[] NormalizeAliases(string primaryName, IReadOnlyList<string>? aliases)
    {
        string[] normalized = (aliases ?? [])
            .Select(alias => FieldTypes.Required(alias, "Alias", 300))
            .ToArray();
        if (normalized.Length > 100)
        {
            throw new DomainValidationException("A record cannot have more than 100 aliases.");
        }

        if (normalized.Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length)
        {
            throw new DomainValidationException("Aliases cannot be duplicated.");
        }

        if (normalized.Contains(primaryName, StringComparer.OrdinalIgnoreCase))
        {
            throw new DomainValidationException("An alias cannot duplicate the primary display name.");
        }

        return normalized;
    }

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

        if (string.Equals(definition.TypeId, FieldTypes.Location, StringComparison.Ordinal))
        {
            LocationValueInput? locationInput = input.Location;
            if (locationInput is null && !string.IsNullOrWhiteSpace(input.ScalarValue))
            {
                locationInput = new LocationValueInput(input.ScalarValue);
            }

            LocationValue? location = LocationValues.Normalize(locationInput, definition.Name);
            return location is null
                ? null
                : new(Guid.CreateVersion7(), definition.Id, 0, null, null, null, null, [], Location: location);
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
