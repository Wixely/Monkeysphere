namespace Monkeysphere.Core;

public sealed record Reminder(
    Guid Id,
    Guid RecordId,
    Guid FieldDefinitionId,
    int ValueOrdinal,
    int LeadDays,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// A reminder as it is stored, with the occurrence it was last dismissed for. The store cannot say
/// whether it is currently dismissed, because that depends on which occurrence is next and only the
/// field's recurrence knows: a birthday dismissed in June is dismissed for that June and armed for
/// the following one.
/// </summary>
public sealed record StoredReminder(
    Reminder Reminder,
    CalendarEntry Entry,
    DateOnly? DismissedForDate);

/// <summary>
/// A reminder that is currently asking to be seen. <see cref="Entry"/> carries the occurrence it is
/// about rather than the day the value stores, so <see cref="DueDate"/> is a date somebody can act on:
/// counting back from the stored day made a reminder on a birthday recorded in 1990 due in 1990.
/// </summary>
public sealed record ReminderItem(
    Reminder Reminder,
    CalendarEntry Entry,
    DateOnly DueDate)
{
    /// <summary>
    /// The day the value actually names, kept beside the occurrence because the two differ for
    /// anything that repeats and a reader given only one of them cannot recover the other.
    /// </summary>
    public DateOnly StoredDate { get; init; }
}

public interface IReminderStore
{
    Task<Reminder> CreateAsync(
        Guid id,
        Guid fieldValueId,
        int leadDays,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every reminder whose value is still eligible, dismissed or not, with the day the value stores.
    /// Filtering is <see cref="IReminderService"/>'s, because a dismissal now applies to one
    /// occurrence and working out which occurrence is next needs the field's recurrence.
    /// </summary>
    Task<IReadOnlyList<StoredReminder>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that <paramref name="occurrenceDate"/> has been dealt with. False when this reminder is
    /// already dismissed for that same occurrence, or is not there at all, so a caller can tell a
    /// completed dismissal from a mistaken one.
    /// </summary>
    Task<bool> DismissAsync(
        Guid id,
        DateOnly occurrenceDate,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

public interface IReminderService
{
    Task<Reminder> CreateAsync(Guid fieldValueId, int leadDays, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReminderItem>> ListActiveAsync(CancellationToken cancellationToken = default);

    Task<bool> DismissAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class ReminderService(
    IReminderStore store,
    IMonkeysphereService records,
    TimeProvider timeProvider) : IReminderService
{
    /// <summary>
    /// How far ahead a reminder may look. Ten years, which is long enough for anything worth
    /// remembering and short enough that a mistyped value is refused rather than stored. Named
    /// because a remote caller is told it by get_capabilities.
    /// </summary>
    public const int MaximumLeadDays = 3_650;

    public Task<Reminder> CreateAsync(
        Guid fieldValueId,
        int leadDays,
        CancellationToken cancellationToken = default)
    {
        if (fieldValueId == Guid.Empty)
        {
            throw new DomainValidationException("A calendar value is required for a reminder.");
        }

        if (leadDays < 0 || leadDays > MaximumLeadDays)
        {
            throw new DomainValidationException($"Reminder lead time must be between 0 and {MaximumLeadDays:N0} days.");
        }

        return store.CreateAsync(Guid.CreateVersion7(), fieldValueId, leadDays, timeProvider.GetUtcNow(), cancellationToken);
    }

    public async Task<IReadOnlyList<ReminderItem>> ListActiveAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<StoredReminder> stored = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        if (stored.Count == 0)
        {
            return [];
        }

        Dictionary<Guid, FieldRecurrence> recurrences = await ReadRecurrencesAsync(cancellationToken).ConfigureAwait(false);
        DateOnly today = DateOnly.FromDateTime(timeProvider.GetLocalNow().DateTime);
        List<ReminderItem> active = [];
        foreach (StoredReminder reminder in stored)
        {
            DateOccurrence occurrence = NextOccurrence(reminder.Entry.Date, Recurrence(recurrences, reminder), today);

            // Dismissed for this occurrence and no other. A one-off has only ever one, so dismissing it
            // is permanent without needing a second rule; a repeating one comes back when the
            // occurrence it was dismissed for is no longer the next.
            if (reminder.DismissedForDate == occurrence.Date)
            {
                continue;
            }

            active.Add(new ReminderItem(
                reminder.Reminder,
                reminder.Entry with
                {
                    Date = occurrence.Date,
                    YearsSince = occurrence.YearsSince,
                    IsRepeat = occurrence.IsRepeat,
                    RolledFromLeapDay = occurrence.RolledFromLeapDay,
                },
                occurrence.Date.AddDays(-reminder.Reminder.LeadDays))
            {
                StoredDate = reminder.Entry.Date,
            });
        }

        // Ordered by when they fall due rather than by the day their values store, which is the order
        // the page showed all along and now the only order that means anything.
        return
        [.. active
            .OrderBy(item => item.DueDate)
            .ThenByDescending(item => item.Reminder.LeadDays)
            .ThenBy(item => item.Entry.RecordDisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Reminder.Id)];
    }

    public async Task<bool> DismissAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Dismissed for the occurrence it is currently showing, which is why this reads the list first:
        // "dealt with" is a statement about one occurrence and the caller is looking at that one.
        ReminderItem? showing = (await ListActiveAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(item => item.Reminder.Id == id);
        return showing is not null
            && await store.DismissAsync(id, showing.Entry.Date, timeProvider.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);
    }

    private static FieldRecurrence Recurrence(
        Dictionary<Guid, FieldRecurrence> recurrences,
        StoredReminder reminder) =>
        recurrences.TryGetValue(reminder.Entry.FieldDefinitionId, out FieldRecurrence? recurrence)
            ? recurrence
            : FieldRecurrence.None;

    private async Task<Dictionary<Guid, FieldRecurrence>> ReadRecurrencesAsync(CancellationToken cancellationToken) =>
        (await records.ListFieldDefinitionsAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(field => field.Id, FieldRecurrences.Of);

    /// <summary>
    /// The next time a stored date comes round, or the date itself when its field does not repeat. The
    /// window is an interval and a year so that a four-yearly date is found as reliably as an annual
    /// one, and a value whose own day is still ahead resolves to that day rather than to a repeat of it.
    /// </summary>
    private static DateOccurrence NextOccurrence(DateOnly stored, FieldRecurrence recurrence, DateOnly today)
    {
        if (!recurrence.Repeats)
        {
            return new(stored, 0, false, false);
        }

        IReadOnlyList<DateOccurrence> upcoming = FieldRecurrences
            .Occurrences(stored, recurrence, today, today.AddYears(recurrence.IntervalYears + 1));
        DateOccurrence? next = upcoming.Count > 0 ? upcoming[0] : null;

        // A repeating value with nothing in that window has a stored date the recurrence cannot reach
        // from here, which is a configuration nobody can act on; the stored day is the honest answer
        // rather than silently hiding the reminder.
        return next ?? new(stored, 0, false, false);
    }
}
