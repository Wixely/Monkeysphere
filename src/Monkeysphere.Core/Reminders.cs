namespace Monkeysphere.Core;

public sealed record Reminder(
    Guid Id,
    Guid RecordId,
    Guid FieldDefinitionId,
    int ValueOrdinal,
    int LeadDays,
    DateTimeOffset CreatedAtUtc);

public sealed record ReminderItem(
    Reminder Reminder,
    CalendarEntry Entry,
    DateOnly DueDate);

public interface IReminderStore
{
    Task<Reminder> CreateAsync(
        Guid id,
        Guid fieldValueId,
        int leadDays,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReminderItem>> ListActiveAsync(CancellationToken cancellationToken = default);

    Task<bool> DismissAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken = default);
}

public interface IReminderService
{
    Task<Reminder> CreateAsync(Guid fieldValueId, int leadDays, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReminderItem>> ListActiveAsync(CancellationToken cancellationToken = default);

    Task<bool> DismissAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class ReminderService(IReminderStore store, TimeProvider timeProvider) : IReminderService
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

    public Task<IReadOnlyList<ReminderItem>> ListActiveAsync(CancellationToken cancellationToken = default) =>
        store.ListActiveAsync(cancellationToken);

    public Task<bool> DismissAsync(Guid id, CancellationToken cancellationToken = default) =>
        store.DismissAsync(id, timeProvider.GetUtcNow(), cancellationToken);
}
