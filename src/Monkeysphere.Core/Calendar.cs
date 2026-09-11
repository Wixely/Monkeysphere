namespace Monkeysphere.Core;

public sealed record CalendarQuery(
    DateOnly From,
    DateOnly To,
    Guid? RecordTypeId = null,
    Guid? FieldDefinitionId = null,
    int Limit = 500)
{
    /// <summary>
    /// Record types to include, when more than one is wanted. Empty means every type, which is what
    /// a calendar showing everything for a month needs.
    /// </summary>
    public IReadOnlyList<Guid> RecordTypeIds { get; init; } = [];
}

public sealed record CalendarEntry(
    Guid FieldValueId,
    Guid RecordId,
    Guid RecordTypeId,
    string RecordTypeName,
    string RecordDisplayName,
    Guid FieldDefinitionId,
    string FieldName,
    DateOnly Date)
{
    /// <summary>Whole years between the day itself and this appearance. Zero for the day itself.</summary>
    public int YearsSince { get; init; }

    /// <summary>True when this is a repeat rather than the day the value actually names.</summary>
    public bool IsRepeat { get; init; }

    /// <summary>True when 29 February was moved to land on a day this year actually has.</summary>
    public bool RolledFromLeapDay { get; init; }

    /// <summary>The record's cover image, so a calendar can show who it belongs to.</summary>
    public Guid? ImageId { get; init; }

    /// <summary>The record type's symbol, shown when the record has no image.</summary>
    public string? RecordTypeSymbol { get; init; }
}

public interface ICalendarStore
{
    Task<IReadOnlyList<CalendarEntry>> QueryAsync(
        CalendarQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every day-precision value of the named fields, whatever year it falls in, so that repeats can
    /// be projected into a range the stored year is nowhere near.
    /// </summary>
    Task<IReadOnlyList<CalendarEntry>> ListRepeatingValuesAsync(
        IReadOnlyList<Guid> fieldDefinitionIds,
        CalendarQuery query,
        CancellationToken cancellationToken = default);
}

public interface ICalendarService
{
    Task<IReadOnlyList<CalendarEntry>> QueryAsync(
        CalendarQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads a range of the calendar. A stored date appears on the day it names, and again on each
/// repeat its field declares: without that, a birthday recorded in 1990 was only ever visible by
/// navigating to 1990, which made the calendar look empty for every real address book.
/// </summary>
public sealed class CalendarService(ICalendarStore store, IMonkeysphereService records) : ICalendarService
{
    public async Task<IReadOnlyList<CalendarEntry>> QueryAsync(
        CalendarQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.To < query.From)
        {
            throw new DomainValidationException("Calendar end date must be on or after its start date.");
        }

        if (query.To.DayNumber - query.From.DayNumber > 366)
        {
            throw new DomainValidationException("Calendar queries cannot cover more than 367 days.");
        }

        if (query.Limit is < 1 or > 1_000)
        {
            throw new DomainValidationException("Calendar result limit must be between 1 and 1,000.");
        }

        IReadOnlyList<CalendarEntry> onTheDay = await store.QueryAsync(query, cancellationToken).ConfigureAwait(false);

        Dictionary<Guid, FieldRecurrence> repeating = (await records.ListFieldDefinitionsAsync(cancellationToken).ConfigureAwait(false))
            .Where(field => field.Lifecycle == FieldLifecycle.Active)
            .Select(field => (field.Id, Recurrence: FieldRecurrences.Of(field)))
            .Where(item => item.Recurrence.Repeats)
            .Where(item => query.FieldDefinitionId is not Guid selected || item.Id == selected)
            .ToDictionary(item => item.Id, item => item.Recurrence);
        if (repeating.Count == 0)
        {
            return onTheDay;
        }

        IReadOnlyList<CalendarEntry> candidates = await store
            .ListRepeatingValuesAsync([.. repeating.Keys], query, cancellationToken).ConfigureAwait(false);

        List<CalendarEntry> entries = [.. onTheDay];
        foreach (CalendarEntry candidate in candidates)
        {
            if (!repeating.TryGetValue(candidate.FieldDefinitionId, out FieldRecurrence? recurrence)) continue;
            foreach (DateOccurrence occurrence in
                     FieldRecurrences.Occurrences(candidate.Date, recurrence, query.From, query.To))
            {
                // The day itself already came back from the range query; adding it again here would
                // show every birthday twice in the year it happened.
                if (!occurrence.IsRepeat) continue;
                entries.Add(candidate with
                {
                    Date = occurrence.Date,
                    YearsSince = occurrence.YearsSince,
                    IsRepeat = true,
                    RolledFromLeapDay = occurrence.RolledFromLeapDay,
                });
            }
        }

        return entries
            .OrderBy(entry => entry.Date)
            .ThenBy(entry => entry.RecordDisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.FieldName, StringComparer.CurrentCultureIgnoreCase)
            .Take(query.Limit)
            .ToArray();
    }
}
