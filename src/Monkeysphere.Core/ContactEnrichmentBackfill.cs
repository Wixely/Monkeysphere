namespace Monkeysphere.Core;

/// <summary>What a backfill would do for one enrichment, counted before anything is changed.</summary>
public sealed record ContactBackfillOffer(
    string Key,
    string Label,
    string Description,
    string? FieldName,
    bool FieldExists,
    /// <summary>Records whose retained source material holds a value this enrichment could take.</summary>
    int RecordCount,
    /// <summary>How many of those already hold a value, and so would be left alone.</summary>
    int AlreadyFilledCount);

public sealed record ContactBackfillPreview(int RecordsWithSource, IReadOnlyList<ContactBackfillOffer> Offers);

public sealed record ContactBackfillResult(int FieldsCreated, int RecordsUpdated, int ValuesWritten);

/// <summary>
/// Fills enrichment fields on records that were imported before those fields existed, by reading
/// the source material the import retained. Nothing is re-fetched and no file is needed: the card
/// each record came from is already kept verbatim.
/// </summary>
public interface IContactEnrichmentBackfill
{
    /// <summary>Counts what could be filled, without creating a field or writing a value.</summary>
    Task<ContactBackfillPreview> PreviewAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the fields the named enrichments need and fills them from retained source material.
    /// A record that already holds a value for a field keeps it: this adds what was lost, and never
    /// overwrites what somebody has since typed.
    /// </summary>
    Task<ContactBackfillResult> ApplyAsync(
        IReadOnlyList<string> enrichmentKeys, CancellationToken cancellationToken = default);
}

/// <summary>One record's retained vCard, rebuilt from source material so enrichment can read it.</summary>
public sealed record ContactSourceCard(Guid RecordId, VCard Card);

/// <summary>Reads back the vCards that imports retained, so a backfill has something to work from.</summary>
public interface IContactSourceCardStore
{
    /// <summary>
    /// Every record whose retained source material came from a vCard, newest import per record,
    /// rebuilt into a card. Records with no retained material are absent.
    /// </summary>
    Task<IReadOnlyList<ContactSourceCard>> ListAsync(Guid recordTypeId, CancellationToken cancellationToken = default);
}

public sealed class ContactEnrichmentBackfill(
    IMonkeysphereService records,
    IContactSourceCardStore sources) : IContactEnrichmentBackfill
{
    private const string PersonPresetKey = "monkeysphere.person";

    public async Task<ContactBackfillPreview> PreviewAsync(CancellationToken cancellationToken = default)
    {
        (RecordTypeDetails type, IReadOnlyList<ContactSourceCard> cards) =
            await LoadAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, FieldDefinition> fields = ActiveFields(type);

        List<ContactBackfillOffer> offers = [];
        foreach (ContactEnrichmentKind kind in ContactEnrichments.All)
        {
            // A photo is not a field value, so a backfill has nothing to write for it. Retained
            // material holds the reference, not the picture, and fetching belongs to an import.
            if (kind.Outcome != ContactEnrichmentOutcome.Field || kind.CanonicalKey is null) continue;

            fields.TryGetValue(kind.CanonicalKey, out FieldDefinition? field);
            int candidates = 0;
            int filled = 0;
            foreach (ContactSourceCard source in cards)
            {
                if (ContactEnrichments.Extract(kind.Key, source.Card) is null) continue;
                candidates++;
                if (field is not null &&
                    await HasValueAsync(source.RecordId, field.Id, cancellationToken).ConfigureAwait(false))
                {
                    filled++;
                }
            }

            if (candidates == 0) continue;
            offers.Add(new(kind.Key, kind.Label, kind.Description, kind.FieldName, field is not null, candidates, filled));
        }

        return new(cards.Count, offers);
    }

    public async Task<ContactBackfillResult> ApplyAsync(
        IReadOnlyList<string> enrichmentKeys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(enrichmentKeys);
        string[] keys = enrichmentKeys.Distinct(StringComparer.Ordinal).ToArray();
        if (keys.Length == 0) return new(0, 0, 0);

        (RecordTypeDetails type, IReadOnlyList<ContactSourceCard> cards) =
            await LoadAsync(cancellationToken).ConfigureAwait(false);

        int fieldsCreated = 0;
        foreach (string key in keys)
        {
            ContactEnrichmentKind kind = ContactEnrichments.Require(key);
            if (kind.Outcome != ContactEnrichmentOutcome.Field || kind.CanonicalKey is null)
            {
                throw new DomainValidationException($"'{kind.Label}' cannot be filled from retained source material.");
            }

            if (ActiveFields(type).ContainsKey(kind.CanonicalKey)) continue;
            _ = await records.CreateEnrichmentFieldAsync(
                type.RecordType.Id, kind.FieldName!, kind.FieldTypeId!, kind.CanonicalKey, cancellationToken).ConfigureAwait(false);
            fieldsCreated++;
            type = await RequireTypeAsync(type.RecordType.Id, cancellationToken).ConfigureAwait(false);
        }

        Dictionary<string, FieldDefinition> fields = ActiveFields(type);
        int recordsUpdated = 0;
        int valuesWritten = 0;
        foreach (ContactSourceCard source in cards)
        {
            RecordDetails? record = await records.GetRecordAsync(source.RecordId, cancellationToken).ConfigureAwait(false);
            if (record is null) continue;

            List<FieldValueInput> additions = [];
            foreach (string key in keys)
            {
                ContactEnrichmentKind kind = ContactEnrichments.Require(key);
                if (!fields.TryGetValue(kind.CanonicalKey!, out FieldDefinition? field)) continue;
                if (record.Values.Any(value => value.FieldDefinitionId == field.Id)) continue;
                if (ContactEnrichments.Extract(key, source.Card) is not ContactEnrichmentValue extracted) continue;
                additions.Add(new(field.Id, extracted.ScalarValue, extracted.Tags));
            }

            if (additions.Count == 0) continue;

            // Existing values are resent unchanged: the update replaces the whole value set, so
            // omitting them would delete data this operation is not meant to touch.
            List<FieldValueInput> all = [.. record.Values.Select(Existing), .. additions];
            await records.UpdateRecordAsync(source.RecordId, record.Record.DisplayName, all, record.Aliases,
                record.Revision, cancellationToken).ConfigureAwait(false);
            recordsUpdated++;
            valuesWritten += additions.Count;
        }

        return new(fieldsCreated, recordsUpdated, valuesWritten);
    }

    private static FieldValueInput Existing(RecordValue value) => new(
        value.FieldDefinitionId,
        value.TextValue ?? value.NumberValue ?? value.DateValue,
        value.Tags.Count == 0 ? null : value.Tags,
        value.TemporalValue is null
            ? null
            : new TemporalValueInput(value.TemporalValue, value.TemporalPrecision ?? TemporalPrecision.Day,
                value.IsApproximate, value.ApproximationNote),
        value.Location is null
            ? null
            : new LocationValueInput(
                value.Location.DisplayContext,
                value.Location.Latitude?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                value.Location.Longitude?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                value.Location.AccuracyMetres?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                value.Location.ApproximationRadiusKilometres?.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private async Task<bool> HasValueAsync(Guid recordId, Guid fieldId, CancellationToken cancellationToken)
    {
        RecordDetails? record = await records.GetRecordAsync(recordId, cancellationToken).ConfigureAwait(false);
        return record is not null && record.Values.Any(value => value.FieldDefinitionId == fieldId);
    }

    private static Dictionary<string, FieldDefinition> ActiveFields(RecordTypeDetails type) => type.Fields
        .Where(field => field.Definition.CanonicalKey is not null && field.Definition.Lifecycle == FieldLifecycle.Active)
        .ToDictionary(field => field.Definition.CanonicalKey!, field => field.Definition, StringComparer.Ordinal);

    private async Task<(RecordTypeDetails Type, IReadOnlyList<ContactSourceCard> Cards)> LoadAsync(
        CancellationToken cancellationToken)
    {
        RecordType person = (await records.ListRecordTypesAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(type =>
                type.Lifecycle == RecordTypeLifecycle.Active &&
                string.Equals(type.PresetKey, PersonPresetKey, StringComparison.Ordinal))
            ?? throw new DomainValidationException("Install the Person preset before filling contact fields.");
        RecordTypeDetails type = await RequireTypeAsync(person.Id, cancellationToken).ConfigureAwait(false);
        return (type, await sources.ListAsync(person.Id, cancellationToken).ConfigureAwait(false));
    }

    private async Task<RecordTypeDetails> RequireTypeAsync(Guid id, CancellationToken cancellationToken) =>
        await records.GetRecordTypeAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainValidationException("The installed Person record type could not be loaded.");
}
