namespace Monkeysphere.Core;

public sealed record DashboardConfiguration(
    Guid? RecordTypeId,
    IReadOnlyList<Guid> RecurringFieldDefinitionIds,
    int UpcomingDays = 90);

public sealed record DashboardDateSource(
    Guid FieldValueId,
    Guid RecordId,
    Guid RecordTypeId,
    string RecordTypeName,
    string RecordDisplayName,
    Guid FieldDefinitionId,
    string FieldName,
    string Value,
    TemporalPrecision Precision);

public sealed record DashboardUpcomingDate(
    DashboardDateSource Source,
    DateTimeOffset OccursAt,
    bool HasTime);

public interface IDashboardStore
{
    Task<DashboardConfiguration?> GetConfigurationAsync(CancellationToken cancellationToken = default);

    Task SaveConfigurationAsync(
        DashboardConfiguration configuration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DashboardDateSource>> ListDateSourcesAsync(
        IReadOnlyList<Guid> fieldDefinitionIds,
        CancellationToken cancellationToken = default);
}

public interface IDashboardService
{
    Task<DashboardConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default);

    Task<DashboardConfiguration> SaveConfigurationAsync(
        DashboardConfiguration configuration,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DashboardUpcomingDate>> ListUpcomingAsync(
        DashboardConfiguration? configuration = null,
        CancellationToken cancellationToken = default);
}

public sealed class DashboardService(
    IDashboardStore store,
    IMonkeysphereStore records,
    TimeProvider timeProvider) : IDashboardService
{
    public const int DefaultUpcomingDays = 90;
    public const int MaximumUpcomingDays = 366;
    public const int MaximumRecurringFields = 50;
    public const int MaximumUpcomingItems = 100;

    public async Task<DashboardConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        DashboardConfiguration? saved = await store.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        if (saved is not null)
        {
            return saved;
        }

        IReadOnlyList<RecordType> types = await records.ListRecordTypesAsync(cancellationToken).ConfigureAwait(false);
        Guid? defaultTypeId = types.FirstOrDefault(type =>
                type.Lifecycle == RecordTypeLifecycle.Active &&
                string.Equals(type.PresetKey, "monkeysphere.person", StringComparison.Ordinal))?.Id
            ?? types.FirstOrDefault(type =>
                type.Lifecycle == RecordTypeLifecycle.Active &&
                (string.Equals(type.Name, "Person", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(type.Name, "People", StringComparison.OrdinalIgnoreCase)))?.Id
            ?? types.FirstOrDefault(type => type.Lifecycle == RecordTypeLifecycle.Active)?.Id;
        Guid[] birthdayFields = (await records.ListFieldDefinitionsAsync(cancellationToken).ConfigureAwait(false))
            .Where(IsEligibleDateField)
            .Where(field => string.Equals(
                field.CanonicalKey,
                "monkeysphere.person.birthday",
                StringComparison.Ordinal))
            .Select(field => field.Id)
            .ToArray();
        return new(defaultTypeId, birthdayFields, DefaultUpcomingDays);
    }

    public async Task<DashboardConfiguration> SaveConfigurationAsync(
        DashboardConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        if (configuration.UpcomingDays is < 1 or > MaximumUpcomingDays)
        {
            throw new DomainValidationException($"Dashboard look-ahead must be between 1 and {MaximumUpcomingDays} days.");
        }

        if (configuration.RecordTypeId is Guid typeId)
        {
            RecordTypeDetails type = await records.GetRecordTypeAsync(typeId, cancellationToken).ConfigureAwait(false)
                ?? throw new DomainValidationException("Dashboard record type was not found.");
            if (type.RecordType.Lifecycle != RecordTypeLifecycle.Active)
            {
                throw new DomainValidationException("Dashboard record type must be active.");
            }
        }

        Guid[] fieldIds = configuration.RecurringFieldDefinitionIds.Distinct().ToArray();
        if (fieldIds.Length > MaximumRecurringFields)
        {
            throw new DomainValidationException($"Dashboard cannot include more than {MaximumRecurringFields} recurring date fields.");
        }

        Dictionary<Guid, FieldDefinition> fields = (await records.ListFieldDefinitionsAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(field => field.Id);
        if (fieldIds.Any(id => !fields.TryGetValue(id, out FieldDefinition? field) || !IsEligibleDateField(field)))
        {
            throw new DomainValidationException("Dashboard recurring fields must be active date or temporal fields.");
        }

        DashboardConfiguration normalized = configuration with { RecurringFieldDefinitionIds = fieldIds };
        await store.SaveConfigurationAsync(normalized, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return normalized;
    }

    public async Task<IReadOnlyList<DashboardUpcomingDate>> ListUpcomingAsync(
        DashboardConfiguration? configuration = null,
        CancellationToken cancellationToken = default)
    {
        DashboardConfiguration selected = configuration ?? await GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = timeProvider.GetLocalNow();
        DateTimeOffset end = now.AddDays(selected.UpcomingDays);
        IReadOnlyList<DashboardDateSource> sources = await store.ListDateSourcesAsync(
            selected.RecurringFieldDefinitionIds,
            cancellationToken).ConfigureAwait(false);

        return sources
            .Select(source => NextOccurrence(source, now, timeProvider.LocalTimeZone))
            .Where(item => item.OccursAt <= end)
            .OrderBy(item => item.OccursAt)
            .ThenBy(item => item.Source.RecordDisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Source.FieldName, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaximumUpcomingItems)
            .ToArray();
    }

    private static DashboardUpcomingDate NextOccurrence(
        DashboardDateSource source,
        DateTimeOffset now,
        TimeZoneInfo timeZone)
    {
        DateTime parsed = DateTime.Parse(source.Value, System.Globalization.CultureInfo.InvariantCulture);
        bool hasTime = source.Precision is TemporalPrecision.Minute or TemporalPrecision.Second;
        DateTime local = CreateOccurrence(now.Year, parsed, hasTime);
        DateTimeOffset occurrence = ResolveOccurrence(local, timeZone);
        bool hasElapsed = hasTime
            ? occurrence < now
            : DateOnly.FromDateTime(occurrence.DateTime) < DateOnly.FromDateTime(now.DateTime);
        if (hasElapsed)
        {
            local = CreateOccurrence(now.Year + 1, parsed, hasTime);
            occurrence = ResolveOccurrence(local, timeZone);
        }

        return new(source, occurrence, hasTime);
    }

    private static DateTimeOffset ResolveOccurrence(DateTime local, TimeZoneInfo timeZone)
    {
        while (timeZone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        TimeSpan offset = timeZone.IsAmbiguousTime(local)
            ? timeZone.GetAmbiguousTimeOffsets(local).Max()
            : timeZone.GetUtcOffset(local);
        return new(local, offset);
    }

    private static DateTime CreateOccurrence(int year, DateTime source, bool hasTime)
    {
        int day = Math.Min(source.Day, DateTime.DaysInMonth(year, source.Month));
        return new DateTime(
            year,
            source.Month,
            day,
            hasTime ? source.Hour : 0,
            hasTime ? source.Minute : 0,
            hasTime && source.Second > 0 ? source.Second : 0,
            DateTimeKind.Unspecified);
    }

    private static bool IsEligibleDateField(FieldDefinition field) =>
        field.Lifecycle == FieldLifecycle.Active &&
        field.TypeId is FieldTypes.ExactDate or FieldTypes.Temporal;
}
