namespace Monkeysphere.Core;

public enum VCardImportAction
{
    CreateSeparately,
    Skip,
    MergeNonConflicting,
    ReplaceMappedValues,
}

public enum VCardPropertyMappingKind
{
    Opaque,
    DisplayName,
    Aliases,
    FieldValue,
}

public sealed record VCardFieldMapping(
    int PropertyIndex,
    Guid FieldDefinitionId,
    string FieldName,
    string CanonicalKey,
    FieldValueInput Input);

public sealed record VCardDuplicateCandidate(
    Guid RecordId,
    string DisplayName,
    IReadOnlyList<string> Reasons,
    bool IsExactPriorImport);

public sealed record VCardContactPreview(
    int Index,
    VCard Card,
    string DisplayName,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<VCardFieldMapping> FieldMappings,
    IReadOnlyList<int> OpaquePropertyIndexes,
    IReadOnlyList<VCardDuplicateCandidate> DuplicateCandidates,
    VCardImportAction RecommendedAction);

public sealed record VCardImportPreview(
    Guid RecordTypeId,
    string RecordTypeName,
    IReadOnlyList<VCardContactPreview> Contacts);

public sealed record VCardImportSelection(
    int ContactIndex,
    VCardImportAction Action,
    Guid? ExistingRecordId = null);

public sealed record VCardImportResult(int Created, int Merged, int Replaced, int Skipped);

public sealed record VCardExistingContact(
    Guid RecordId,
    string DisplayName,
    IReadOnlyList<string> Aliases,
    IReadOnlyDictionary<string, IReadOnlyList<string>> CanonicalValues,
    IReadOnlySet<string> ImportFingerprints);

public sealed record VCardPreparedImport(
    VCardContactPreview Preview,
    VCardImportAction Action,
    Guid? ExistingRecordId,
    PreparedRecord Record);

public sealed record VCardExportRecord(
    RecordDetails Record,
    IReadOnlyList<VCardStoredProperty> StoredProperties);

public sealed record VCardStoredProperty(
    int Ordinal,
    VCardProperty Property,
    VCardPropertyMappingKind MappingKind,
    Guid? FieldDefinitionId = null,
    int? ValueOrdinal = null);

public interface IVCardStore
{
    Task<IReadOnlyList<VCardExistingContact>> ListExistingAsync(
        Guid recordTypeId,
        CancellationToken cancellationToken = default);

    Task<VCardImportResult> ApplyAsync(
        IReadOnlyList<VCardPreparedImport> contacts,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VCardExportRecord>> ReadExportAsync(
        IReadOnlyList<Guid> recordIds,
        CancellationToken cancellationToken = default);
}

public interface IVCardService
{
    Task<VCardImportPreview> PreviewAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);

    Task<VCardImportResult> ApplyAsync(
        VCardImportPreview preview,
        IReadOnlyList<VCardImportSelection> selections,
        CancellationToken cancellationToken = default);

    Task<byte[]> ExportAsync(IReadOnlyList<Guid> recordIds, CancellationToken cancellationToken = default);
}

public sealed class VCardService(
    IMonkeysphereService records,
    IVCardStore store,
    TimeProvider timeProvider) : IVCardService
{
    private const string PersonPresetKey = "monkeysphere.person";
    private static readonly Dictionary<string, string> _propertyCanonicalKeys =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["EMAIL"] = "monkeysphere.person.email",
            ["TEL"] = "monkeysphere.person.phone",
            ["BDAY"] = "monkeysphere.person.birthday",
            ["URL"] = "monkeysphere.person.website",
            ["NOTE"] = "monkeysphere.person.notes",
        };

    public async Task<VCardImportPreview> PreviewAsync(
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<VCard> cards = VCardParser.Parse(content.Span);
        RecordType person = (await records.ListRecordTypesAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(type =>
                type.Lifecycle == RecordTypeLifecycle.Active &&
                string.Equals(type.PresetKey, PersonPresetKey, StringComparison.Ordinal))
            ?? throw new DomainValidationException("Install the Person preset before importing contacts.");
        RecordTypeDetails type = await records.GetRecordTypeAsync(person.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainValidationException("The installed Person record type could not be loaded.");
        Dictionary<string, FieldDefinition> fields = type.Fields
            .Where(field => field.Definition.CanonicalKey is not null && field.Definition.Lifecycle == FieldLifecycle.Active)
            .ToDictionary(field => field.Definition.CanonicalKey!, field => field.Definition, StringComparer.Ordinal);
        IReadOnlyList<VCardExistingContact> existing = await store.ListExistingAsync(person.Id, cancellationToken).ConfigureAwait(false);

        VCardContactPreview[] contacts = cards.Select((card, index) => PreviewCard(index, card, fields, existing)).ToArray();
        return new(person.Id, person.Name, contacts);
    }

    public async Task<VCardImportResult> ApplyAsync(
        VCardImportPreview preview,
        IReadOnlyList<VCardImportSelection> selections,
        CancellationToken cancellationToken = default)
    {
        if (selections.Count != preview.Contacts.Count ||
            selections.Select(selection => selection.ContactIndex).Distinct().Count() != preview.Contacts.Count)
        {
            throw new DomainValidationException("Choose exactly one import action for every previewed contact.");
        }

        Dictionary<int, VCardImportSelection> choices = selections.ToDictionary(selection => selection.ContactIndex);
        List<VCardPreparedImport> prepared = [];
        foreach (VCardContactPreview contact in preview.Contacts)
        {
            if (!choices.TryGetValue(contact.Index, out VCardImportSelection? selection))
            {
                throw new DomainValidationException("An import selection no longer matches its preview.");
            }

            if (selection.Action is VCardImportAction.MergeNonConflicting or VCardImportAction.ReplaceMappedValues)
            {
                if (selection.ExistingRecordId is not Guid target ||
                    !contact.DuplicateCandidates.Any(candidate => candidate.RecordId == target))
                {
                    throw new DomainValidationException("Merge and replace actions require a duplicate selected from the preview.");
                }
            }
            else if (selection.ExistingRecordId is not null)
            {
                throw new DomainValidationException("Only merge and replace actions can select an existing contact.");
            }

            PreparedRecord normalized = await records.PrepareRecordAsync(
                preview.RecordTypeId,
                contact.DisplayName,
                contact.FieldMappings.Select(mapping => mapping.Input).ToArray(),
                contact.Aliases,
                cancellationToken).ConfigureAwait(false);
            prepared.Add(new(contact, selection.Action, selection.ExistingRecordId, normalized));
        }

        return await store.ApplyAsync(prepared, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> ExportAsync(IReadOnlyList<Guid> recordIds, CancellationToken cancellationToken = default)
    {
        if (recordIds.Count is < 1 or > 100 || recordIds.Distinct().Count() != recordIds.Count)
        {
            throw new DomainValidationException("Select between 1 and 100 distinct contacts to export.");
        }

        IReadOnlyList<VCardExportRecord> sources = await store.ReadExportAsync(recordIds, cancellationToken).ConfigureAwait(false);
        if (sources.Count != recordIds.Count)
        {
            throw new DomainValidationException("One or more selected contacts were not found or are not Person records.");
        }

        return VCardSerializer.Serialize(sources.Select(BuildExportProperties).ToArray());
    }

    private static VCardContactPreview PreviewCard(
        int index,
        VCard card,
        Dictionary<string, FieldDefinition> fields,
        IReadOnlyList<VCardExistingContact> existing)
    {
        string displayName = FieldTypes.Required(card.Named("FN")[0].TextValue, "Formatted name", 300);
        HashSet<int> opaque = [];
        int[] nicknameIndexes = card.Properties.Select((property, propertyIndex) => (property, propertyIndex))
            .Where(item => item.property.Name == "NICKNAME")
            .Select(item => item.propertyIndex)
            .ToArray();
        foreach (int extraNickname in nicknameIndexes.Skip(1))
        {
            opaque.Add(extraNickname);
        }

        string[] aliases = nicknameIndexes.Take(1)
            .SelectMany(propertyIndex => VCardText.SplitList(card.Properties[propertyIndex].Value))
            .Select(alias => alias.Trim())
            .Where(alias => alias.Length > 0 && !string.Equals(alias, displayName, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();
        if (aliases.Any(alias => alias.Length > 300) && nicknameIndexes.Length > 0)
        {
            opaque.Add(nicknameIndexes[0]);
            aliases = [];
        }

        List<VCardFieldMapping> mappings = [];
        HashSet<Guid> mappedFields = [];
        for (int propertyIndex = 0; propertyIndex < card.Properties.Count; propertyIndex++)
        {
            VCardProperty property = card.Properties[propertyIndex];
            if (property.Name is "VERSION" or "FN" or "NICKNAME")
            {
                continue;
            }

            if (!_propertyCanonicalKeys.TryGetValue(property.Name, out string? canonicalKey) ||
                !fields.TryGetValue(canonicalKey, out FieldDefinition? field) ||
                !mappedFields.Add(field.Id) ||
                !TryInput(field, property, out FieldValueInput? input))
            {
                opaque.Add(propertyIndex);
                continue;
            }

            mappings.Add(new(propertyIndex, field.Id, field.Name, canonicalKey, input));
        }

        List<VCardDuplicateCandidate> duplicates = [];
        foreach (VCardExistingContact candidate in existing)
        {
            List<string> reasons = [];
            bool exact = candidate.ImportFingerprints.Contains(card.Fingerprint);
            if (exact)
            {
                reasons.Add("same previously imported vCard");
            }

            if (candidate.Aliases.Append(candidate.DisplayName).Any(name =>
                aliases.Append(displayName).Contains(name, StringComparer.OrdinalIgnoreCase)))
            {
                reasons.Add("matching name or alias");
            }

            foreach (VCardFieldMapping mapping in mappings)
            {
                string? value = MappingValue(mapping.Input);
                if (value is not null && candidate.CanonicalValues.GetValueOrDefault(mapping.CanonicalKey, []).Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    reasons.Add($"matching {mapping.FieldName}");
                }
            }

            if (reasons.Count > 0)
            {
                duplicates.Add(new(candidate.RecordId, candidate.DisplayName, reasons.Distinct().ToArray(), exact));
            }
        }

        VCardImportAction recommended = duplicates.Any(candidate => candidate.IsExactPriorImport)
            ? VCardImportAction.Skip
            : duplicates.Count == 1
                ? VCardImportAction.MergeNonConflicting
                : VCardImportAction.CreateSeparately;
        return new(index, card, displayName, aliases, mappings, opaque.Order().ToArray(), duplicates, recommended);
    }

    private static bool TryInput(FieldDefinition field, VCardProperty property, out FieldValueInput input)
    {
        string value = property.TextValue.Trim();
        if (property.Name == "TEL" && value.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
        {
            value = value[4..];
        }

        if (property.Name == "EMAIL" && value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            value = value[7..];
        }

        if (value.Length == 0)
        {
            input = default!;
            return false;
        }

        if (property.Name == "BDAY")
        {
            string normalized = value.Length == 8 && value.All(char.IsDigit)
                ? $"{value[..4]}-{value[4..6]}-{value[6..]}"
                : value;
            if (!DateOnly.TryParseExact(normalized, "yyyy-MM-dd", out _))
            {
                input = default!;
                return false;
            }

            input = field.TypeId == FieldTypes.Temporal
                ? new(field.Id, Temporal: new(normalized, TemporalPrecision.Day))
                : new(field.Id, normalized);
            return field.TypeId is FieldTypes.Temporal or FieldTypes.ExactDate;
        }

        input = new(field.Id, value);
        return property.Name switch
        {
            "TEL" => (field.TypeId is FieldTypes.PhoneNumber or FieldTypes.Text) &&
                     value.Length <= 200 && value.Count(char.IsDigit) >= 3 && value.All(character =>
                         char.IsDigit(character) || char.IsWhiteSpace(character) ||
                         character is '+' or '-' or '(' or ')' or '.' or '#' or 'x' or 'X'),
            "EMAIL" or "NOTE" => (field.TypeId is FieldTypes.Text or FieldTypes.MultilineText) && value.Length <= 20_000,
            "URL" => field.TypeId == FieldTypes.Text && value.Length <= 20_000 ||
                     field.TypeId == FieldTypes.WebLink && value.Length <= 2_048 &&
                     Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https" &&
                     !string.IsNullOrWhiteSpace(uri.Host),
            _ => false,
        };
    }

    private static string? MappingValue(FieldValueInput input) =>
        input.ScalarValue ?? input.Temporal?.Value;

    private static IReadOnlyList<VCardProperty> BuildExportProperties(VCardExportRecord source)
    {
        List<VCardProperty> result = [];
        VCardStoredProperty? formattedName = source.StoredProperties.FirstOrDefault(property =>
            property.MappingKind == VCardPropertyMappingKind.DisplayName);
        result.Add(formattedName is null
            ? new(null, "FN", [], VCardSerializer.EncodeText(source.Record.Record.DisplayName))
            : formattedName.Property with { Value = VCardSerializer.EncodeText(source.Record.Record.DisplayName) });
        if (source.Record.Aliases.Count > 0)
        {
            VCardStoredProperty? nicknames = source.StoredProperties.FirstOrDefault(property =>
                property.MappingKind == VCardPropertyMappingKind.Aliases);
            string value = string.Join(',', source.Record.Aliases.Select(VCardSerializer.EncodeText));
            result.Add(nicknames is null
                ? new(null, "NICKNAME", [], value)
                : nicknames.Property with { Value = value });
        }

        foreach (VCardStoredProperty stored in source.StoredProperties.Where(property =>
            property.MappingKind == VCardPropertyMappingKind.Opaque))
        {
            result.Add(stored.Property);
        }

        Dictionary<Guid, VCardStoredProperty> mapped = source.StoredProperties
            .Where(property => property.MappingKind == VCardPropertyMappingKind.FieldValue && property.FieldDefinitionId.HasValue)
            .GroupBy(property => property.FieldDefinitionId!.Value)
            .ToDictionary(group => group.Key, group => group.First());
        foreach (RecordValue value in source.Record.Values)
        {
            string? propertyName = CanonicalProperty(source.Record.AvailableFields, value.FieldDefinitionId);
            string? scalar = ExportValue(value);
            if (propertyName is null || scalar is null)
            {
                continue;
            }

            if (mapped.TryGetValue(value.FieldDefinitionId, out VCardStoredProperty? provenance))
            {
                result.Add(provenance.Property with { Value = VCardSerializer.EncodeText(scalar) });
            }
            else
            {
                result.Add(new(null, propertyName, [], VCardSerializer.EncodeText(scalar)));
            }
        }

        return result;
    }

    private static string? CanonicalProperty(IReadOnlyList<RecordTypeField> fields, Guid fieldId)
    {
        string? key = fields.Single(field => field.Definition.Id == fieldId).Definition.CanonicalKey;
        return _propertyCanonicalKeys.FirstOrDefault(pair => pair.Value == key).Key;
    }

    private static string? ExportValue(RecordValue value) => value.TypeId switch
    {
        FieldTypes.Temporal when value.TemporalPrecision == TemporalPrecision.Day && !value.IsApproximate => value.TemporalValue,
        FieldTypes.ExactDate => value.DateValue,
        _ => value.TextValue,
    };
}
