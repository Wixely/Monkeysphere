namespace Monkeysphere.Core;

public sealed record CalendarQuery(
    DateOnly From,
    DateOnly To,
    Guid? RecordTypeId = null,
    Guid? FieldDefinitionId = null,
    int Limit = 500);

public sealed record CalendarEntry(
    Guid FieldValueId,
    Guid RecordId,
    Guid RecordTypeId,
    string RecordTypeName,
    string RecordDisplayName,
    Guid FieldDefinitionId,
    string FieldName,
    DateOnly Date);

public interface ICalendarStore
{
    Task<IReadOnlyList<CalendarEntry>> QueryAsync(
        CalendarQuery query,
        CancellationToken cancellationToken = default);
}

public interface ICalendarService
{
    Task<IReadOnlyList<CalendarEntry>> QueryAsync(
        CalendarQuery query,
        CancellationToken cancellationToken = default);
}

public sealed class CalendarService(ICalendarStore store) : ICalendarService
{
    public Task<IReadOnlyList<CalendarEntry>> QueryAsync(
        CalendarQuery query,
        CancellationToken cancellationToken = default)
    {
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

        return store.QueryAsync(query, cancellationToken);
    }
}
