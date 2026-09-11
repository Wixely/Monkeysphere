namespace Monkeysphere.Core;

public enum VCardImportAction
{
    CreateSeparately,
    Skip,
    MergeNonConflicting,
    ReplaceMappedValues,
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

public sealed record VCardImportDuplicateCandidate(
    int ContactIndex,
    string DisplayName,
    IReadOnlyList<string> Reasons,
    bool IsExactCard,
    bool IsStrongMatch);

public sealed record VCardContactPreview(
    int Index,
    VCard Card,
    string DisplayName,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<VCardFieldMapping> FieldMappings,
    IReadOnlyList<int> OpaquePropertyIndexes,
    IReadOnlyList<VCardDuplicateCandidate> DuplicateCandidates,
    VCardImportAction RecommendedAction)
{
    public IReadOnlyList<VCardImportDuplicateCandidate> ImportDuplicateCandidates { get; init; } = [];

    /// <summary>Enrichments this card could produce, whether or not their field exists yet.</summary>
    public IReadOnlyList<string> DetectedEnrichments { get; init; } = [];

    /// <summary>Photos this card carries or references. Remote ones are only fetched if asked for.</summary>
    public IReadOnlyList<ContactPhotoCandidate> Photos { get; init; } = [];

    /// <summary>True when a vendor block was taken out of the note rather than imported verbatim.</summary>
    public bool NoteWasTidied { get; init; }
}

/// <summary>
/// One enrichment offered for an import, and whether the field it needs already exists. A kind
/// whose field is missing is not applied: the operator chooses to create it first.
/// </summary>
public sealed record VCardEnrichmentOffer(
    string Key,
    string Label,
    string Description,
    string? FieldName,
    bool FieldExists,
    bool CreatesRecordImage,
    int ContactCount,
    int RemotePhotoCount);

public sealed record VCardImportPreview(
    Guid RecordTypeId,
    string RecordTypeName,
    IReadOnlyList<VCardContactPreview> Contacts,
    string Revision = "")
{
    /// <summary>
    /// Cards the file contained that could not be read. They are reported rather than imported, and
    /// their presence never blocks the contacts that could be read.
    /// </summary>
    public IReadOnlyList<VCardRejectedCard> Rejected { get; init; } = [];

    /// <summary>
    /// What this file carries beyond the fields already installed. Each offer says how many
    /// contacts it would affect and whether its field has to be created first.
    /// </summary>
    public IReadOnlyList<VCardEnrichmentOffer> EnrichmentOffers { get; init; } = [];
}

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
    RecordSourceMapping MappingKind,
    Guid? FieldDefinitionId = null,
    int? ValueOrdinal = null);

public interface IVCardStore
{
    Task<string> GetImportRevisionAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VCardExistingContact>> ListExistingAsync(
        Guid recordTypeId,
        CancellationToken cancellationToken = default);

    Task<VCardImportResult> ApplyAsync(
        IReadOnlyList<VCardPreparedImport> contacts,
        string expectedRevision,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VCardExportRecord>> ReadExportAsync(
        IReadOnlyList<Guid> recordIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The record each imported card became, by the card's own fingerprint. Answered from the
    /// provenance an import records, so it needs nothing carried through the import result.
    /// </summary>
    Task<IReadOnlyDictionary<string, Guid>> MapImportedRecordsAsync(
        IReadOnlyList<string> fingerprints,
        CancellationToken cancellationToken = default);
}

public interface IVCardService
{
    Task<VCardImportPreview> PreviewAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);
    Task<VCardImportPreview> PreviewAsync(Stream content, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VCardPreparedImport>> PrepareImportAsync(VCardImportPreview preview,
        IReadOnlyList<VCardImportSelection> selections, CancellationToken cancellationToken = default);

    Task<VCardImportResult> ApplyAsync(
        VCardImportPreview preview,
        IReadOnlyList<VCardImportSelection> selections,
        CancellationToken cancellationToken = default);

    Task<byte[]> ExportAsync(IReadOnlyList<Guid> recordIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the fields the named enrichments need, so that a later preview maps their values.
    /// Creating a field is a structure change, so an existing preview is stale afterwards and the
    /// caller must preview again. Enrichments whose field already exists are left alone.
    /// </summary>
    Task<IReadOnlyList<FieldDefinition>> EnableEnrichmentsAsync(
        IReadOnlyList<string> enrichmentKeys, CancellationToken cancellationToken = default);
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
        => await PreviewCardsAsync(VCardParser.Parse(content.Span), cancellationToken).ConfigureAwait(false);

    public async Task<VCardImportPreview> PreviewAsync(Stream content, CancellationToken cancellationToken = default)
        => await PreviewCardsAsync(await VCardParser.ParseAsync(content, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    private async Task<VCardImportPreview> PreviewCardsAsync(VCardParseResult parsed, CancellationToken cancellationToken)
    {
        IReadOnlyList<VCard> cards = parsed.Cards;
        string revision = await store.GetImportRevisionAsync(cancellationToken).ConfigureAwait(false);
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
        for (int index = 0; index < contacts.Length; index++)
        {
            VCardImportDuplicateCandidate[] importDuplicates = contacts[..index]
                .Select(previous => CompareImportedContacts(contacts[index], previous))
                .Where(candidate => candidate is not null)
                .Cast<VCardImportDuplicateCandidate>()
                .ToArray();
            bool shouldSkip = importDuplicates.Any(candidate => candidate.IsExactCard) ||
                              importDuplicates.Count(candidate => candidate.IsStrongMatch) == 1;
            contacts[index] = contacts[index] with
            {
                ImportDuplicateCandidates = importDuplicates,
                RecommendedAction = shouldSkip ? VCardImportAction.Skip : contacts[index].RecommendedAction,
            };
        }

        if (revision != await store.GetImportRevisionAsync(cancellationToken).ConfigureAwait(false))
            throw new ConcurrencyConflictException("Contacts or structures changed while preparing this preview. Preview the file again.");
        return new(person.Id, person.Name, contacts, revision)
        {
            Rejected = parsed.Rejected,
            EnrichmentOffers = BuildOffers(contacts, fields),
        };
    }

    public async Task<VCardImportResult> ApplyAsync(
        VCardImportPreview preview,
        IReadOnlyList<VCardImportSelection> selections,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<VCardPreparedImport> prepared = await PrepareImportAsync(preview, selections, cancellationToken).ConfigureAwait(false);
        return await store.ApplyAsync(prepared, preview.Revision, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VCardPreparedImport>> PrepareImportAsync(VCardImportPreview preview,
        IReadOnlyList<VCardImportSelection> selections, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(preview.Revision) || preview.Revision != await store.GetImportRevisionAsync(cancellationToken).ConfigureAwait(false))
            throw new ConcurrencyConflictException("Contacts or structures changed since this preview. Preview the file again.");
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

        return prepared;
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

    /// <summary>
    /// What this file carries beyond the installed fields. An offer is reported whether or not its
    /// field exists, because knowing a file has 200 addresses is the point even once it is enabled.
    /// </summary>
    private static VCardEnrichmentOffer[] BuildOffers(
        IReadOnlyList<VCardContactPreview> contacts,
        Dictionary<string, FieldDefinition> fields)
    {
        List<VCardEnrichmentOffer> offers = [];
        foreach (ContactEnrichmentKind kind in ContactEnrichments.All)
        {
            int count = contacts.Count(contact => contact.DetectedEnrichments.Contains(kind.Key, StringComparer.Ordinal));
            if (count == 0) continue;
            bool image = kind.Outcome == ContactEnrichmentOutcome.RecordImage;
            int remote = image
                ? contacts.Sum(contact => contact.Photos.Count(photo => photo.IsRemote))
                : 0;
            offers.Add(new(
                kind.Key,
                kind.Label,
                kind.Description,
                kind.FieldName,
                // A record image needs no field, so it is always ready to apply.
                image || (kind.CanonicalKey is not null && fields.ContainsKey(kind.CanonicalKey)),
                image,
                count,
                remote));
        }

        return offers.ToArray();
    }

    public async Task<IReadOnlyList<FieldDefinition>> EnableEnrichmentsAsync(
        IReadOnlyList<string> enrichmentKeys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(enrichmentKeys);
        RecordType person = (await records.ListRecordTypesAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(type =>
                type.Lifecycle == RecordTypeLifecycle.Active &&
                string.Equals(type.PresetKey, PersonPresetKey, StringComparison.Ordinal))
            ?? throw new DomainValidationException("Install the Person preset before importing contacts.");

        List<FieldDefinition> created = [];
        foreach (string key in enrichmentKeys.Distinct(StringComparer.Ordinal))
        {
            ContactEnrichmentKind kind = ContactEnrichments.Require(key);
            if (kind.Outcome != ContactEnrichmentOutcome.Field) continue;

            // Re-read the type each time: creating a field changes its revision, and a stale read
            // would either duplicate a field or fail the next creation.
            RecordTypeDetails type = await records.GetRecordTypeAsync(person.Id, cancellationToken).ConfigureAwait(false)
                ?? throw new DomainValidationException("The installed Person record type could not be loaded.");
            if (type.Fields.Any(field => string.Equals(field.Definition.CanonicalKey, kind.CanonicalKey, StringComparison.Ordinal)))
            {
                continue;
            }

            created.Add(await records.CreateEnrichmentFieldAsync(
                person.Id, kind.FieldName!, kind.FieldTypeId!, kind.CanonicalKey!, cancellationToken).ConfigureAwait(false));
        }

        return created;
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

        // Enrichments are mapped separately from the one-property-one-field table above, because
        // one property can feed two fields (N) and two properties can feed one (social handles).
        // Only kinds whose field is already installed are mapped; the rest are offered instead.
        IReadOnlyList<string> detected = ContactEnrichments.Detect(card);
        foreach (string key in detected)
        {
            ContactEnrichmentKind kind = ContactEnrichments.Require(key);
            if (kind.Outcome != ContactEnrichmentOutcome.Field ||
                kind.CanonicalKey is null ||
                !fields.TryGetValue(kind.CanonicalKey, out FieldDefinition? enrichmentField) ||
                !mappedFields.Add(enrichmentField.Id))
            {
                continue;
            }

            ContactEnrichmentValue? value = ContactEnrichments.Extract(key, card);
            if (value is null) continue;
            int propertyIndex = FirstPropertyIndex(card, kind.PropertyNames);
            mappings.Add(new(propertyIndex, enrichmentField.Id, enrichmentField.Name, kind.CanonicalKey,
                new FieldValueInput(enrichmentField.Id, value.ScalarValue, value.Tags)));
            opaque.Remove(propertyIndex);
        }

        // A note carrying a vendor block is imported without it. The block itself is never lost:
        // the whole property is retained as source material either way.
        bool noteTidied = false;
        for (int mappingIndex = 0; mappingIndex < mappings.Count; mappingIndex++)
        {
            VCardFieldMapping mapping = mappings[mappingIndex];
            if (!string.Equals(mapping.CanonicalKey, "monkeysphere.person.notes", StringComparison.Ordinal)) continue;
            string? text = mapping.Input.ScalarValue;
            if (text is null || !ContactEnrichments.NoteCarriesVendorBlock(text)) continue;
            string cleaned = ContactEnrichments.CleanNote(text);
            noteTidied = true;
            if (cleaned.Length == 0)
            {
                mappings.RemoveAt(mappingIndex--);
                opaque.Add(mapping.PropertyIndex);
            }
            else
            {
                mappings[mappingIndex] = mapping with { Input = mapping.Input with { ScalarValue = cleaned } };
            }
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
                aliases.Append(displayName).Any(importedName => NamesEqual(name, importedName))))
            {
                reasons.Add("matching name or alias");
            }

            foreach (VCardFieldMapping mapping in mappings)
            {
                string? value = MappingValue(mapping.Input);
                if (value is not null && candidate.CanonicalValues.GetValueOrDefault(mapping.CanonicalKey, [])
                    .Any(existingValue => ValuesEqual(mapping.CanonicalKey, value, existingValue)))
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
        return new(index, card, displayName, aliases, mappings, opaque.Order().ToArray(), duplicates, recommended)
        {
            DetectedEnrichments = detected,
            Photos = ContactEnrichments.Photos(card),
            NoteWasTidied = noteTidied,
        };
    }

    private static VCardImportDuplicateCandidate? CompareImportedContacts(
        VCardContactPreview contact,
        VCardContactPreview previous)
    {
        List<string> reasons = [];
        bool exact = string.Equals(contact.Card.Fingerprint, previous.Card.Fingerprint, StringComparison.Ordinal);
        if (exact)
        {
            reasons.Add("same vCard appears earlier in this file");
        }

        if (previous.Aliases.Append(previous.DisplayName).Any(previousName =>
            contact.Aliases.Append(contact.DisplayName).Any(name => NamesEqual(previousName, name))))
        {
            reasons.Add("matching name or alias");
        }

        bool strong = exact;
        foreach (VCardFieldMapping mapping in contact.FieldMappings)
        {
            string? value = MappingValue(mapping.Input);
            if (value is null || !previous.FieldMappings.Any(previousMapping =>
                    string.Equals(previousMapping.CanonicalKey, mapping.CanonicalKey, StringComparison.Ordinal) &&
                    MappingValue(previousMapping.Input) is string previousValue &&
                    ValuesEqual(mapping.CanonicalKey, value, previousValue)))
            {
                continue;
            }

            reasons.Add($"matching {mapping.FieldName}");
            strong |= mapping.CanonicalKey is "monkeysphere.person.email" or "monkeysphere.person.phone";
        }

        return reasons.Count == 0
            ? null
            : new(previous.Index, previous.DisplayName, reasons.Distinct().ToArray(), exact, strong);
    }

    private static bool NamesEqual(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool ValuesEqual(string canonicalKey, string left, string right)
    {
        string normalizedLeft = NormalizeComparableValue(canonicalKey, left);
        string normalizedRight = NormalizeComparableValue(canonicalKey, right);
        return normalizedLeft.Length > 0 &&
               string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeComparableValue(string canonicalKey, string value)
    {
        string trimmed = value.Trim();
        if (canonicalKey == "monkeysphere.person.email")
        {
            return trimmed.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? trimmed[7..].Trim() : trimmed;
        }

        if (canonicalKey == "monkeysphere.person.phone")
        {
            bool international = trimmed.StartsWith('+');
            string digits = new(trimmed.Where(char.IsDigit).ToArray());
            return international && digits.Length > 0 ? $"+{digits}" : digits;
        }

        return trimmed;
    }

    /// <summary>Where an enrichment's value came from, so the preview can stop calling it unused.</summary>
    private static int FirstPropertyIndex(VCard card, IReadOnlyList<string> propertyNames)
    {
        for (int index = 0; index < card.Properties.Count; index++)
        {
            if (propertyNames.Contains(card.Properties[index].Name, StringComparer.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
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
            property.MappingKind == RecordSourceMapping.DisplayName);
        result.Add(formattedName is null
            ? new(null, "FN", [], VCardSerializer.EncodeText(source.Record.Record.DisplayName))
            : formattedName.Property with { Value = VCardSerializer.EncodeText(source.Record.Record.DisplayName) });
        if (source.Record.Aliases.Count > 0)
        {
            VCardStoredProperty? nicknames = source.StoredProperties.FirstOrDefault(property =>
                property.MappingKind == RecordSourceMapping.Aliases);
            string value = string.Join(',', source.Record.Aliases.Select(VCardSerializer.EncodeText));
            result.Add(nicknames is null
                ? new(null, "NICKNAME", [], value)
                : nicknames.Property with { Value = value });
        }

        foreach (VCardStoredProperty stored in source.StoredProperties.Where(property =>
            property.MappingKind == RecordSourceMapping.Opaque))
        {
            result.Add(stored.Property);
        }

        Dictionary<Guid, VCardStoredProperty> mapped = source.StoredProperties
            .Where(property => property.MappingKind == RecordSourceMapping.FieldValue && property.FieldDefinitionId.HasValue)
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
