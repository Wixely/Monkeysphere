namespace Monkeysphere.Core;

public sealed record DashboardConfiguration(
    IReadOnlyList<Guid> RecordTypeIds,
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
    TemporalPrecision Precision)
{
    /// <summary>The record's cover image, so an upcoming date names its record the way every list does.</summary>
    public Guid? ImageId { get; init; }

    /// <summary>The record type's symbol, shown when there is no image.</summary>
    public string? RecordTypeSymbol { get; init; }
}

public sealed record DashboardUpcomingDate(
    DashboardDateSource Source,
    DateTimeOffset OccursAt,
    bool HasTime);

public interface IDashboardStore
{
    /// <summary>
    /// The arrangement an operator saved, or null where none was. This is what they chose and the
    /// order they chose it in, not what the dashboard shows: a record type created since is in
    /// neither this list nor the dismissals, and <see cref="IDashboardService"/> resolves that.
    /// </summary>
    Task<DashboardConfiguration?> GetConfigurationAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The record types an operator has taken off the dashboard. Kept apart from the arrangement so
    /// that a type named by neither is new rather than unwanted, which is the whole point: an
    /// arrangement alone cannot tell those two apart.
    /// </summary>
    Task<IReadOnlyList<Guid>> ListDismissedCategoriesAsync(CancellationToken cancellationToken = default);

    Task SaveConfigurationAsync(
        DashboardConfiguration configuration,
        IReadOnlyList<Guid> dismissedRecordTypeIds,
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

    /// <summary>
    /// How many categories the dashboard will draw. This became a bound worth having when a new
    /// record type started arriving on its own: each category costs a search and a read per row it
    /// shows, so the page's work is now a function of how many types a deployment has rather than of
    /// how many somebody chose. A deployment with forty types wants a dashboard, not a directory.
    /// </summary>
    public const int MaximumCategories = 12;

    /// <summary>
    /// What the dashboard should show, which is not simply what was saved. The stored arrangement is
    /// the operator's order and their choices; a record type created since it was written appears in
    /// neither that list nor the dismissals, and is appended rather than dropped. That asymmetry is
    /// the point: absence from an arrangement used to mean "unwanted", so a new type never showed up.
    /// </summary>
    public async Task<DashboardConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Guid> active = (await records.ListRecordTypesAsync(cancellationToken).ConfigureAwait(false))
            .Where(type => type.Lifecycle == RecordTypeLifecycle.Active)
            .Select(type => type.Id)
            .ToArray();
        DashboardConfiguration? saved = await store.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        if (saved is null)
        {
            return await DeriveAsync(active, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<Guid> dismissed = await store.ListDismissedCategoriesAsync(cancellationToken).ConfigureAwait(false);
        return saved with { RecordTypeIds = Resolve(active, saved.RecordTypeIds, dismissed) };
    }

    /// <summary>
    /// The arrangement first, in the order it was saved, then every active type that is neither
    /// arranged nor dismissed. Bounded, because the tail is now as long as the deployment's type list
    /// and a page of forty categories helps nobody; the arrangement wins the places, since those were
    /// asked for and the rest were not.
    /// </summary>
    private static List<Guid> Resolve(
        IReadOnlyList<Guid> active,
        IReadOnlyList<Guid> arranged,
        IReadOnlyList<Guid> dismissed)
    {
        HashSet<Guid> live = [.. active];
        HashSet<Guid> excluded = [.. dismissed];
        List<Guid> resolved = [.. arranged.Where(live.Contains).Distinct()];
        excluded.UnionWith(resolved);
        resolved.AddRange(active.Where(id => !excluded.Contains(id)));
        return resolved.Count <= MaximumCategories ? resolved : resolved[..MaximumCategories];
    }

    /// <summary>
    /// What a deployment that has never opened the settings page shows: everything it has, with
    /// people first, because that is what a records application is usually about. Before a new type
    /// could arrive on its own this returned that one type alone, which meant an operator who never
    /// configured the dashboard never saw anything they created afterwards either.
    /// </summary>
    private async Task<DashboardConfiguration> DeriveAsync(
        IReadOnlyList<Guid> active,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<RecordType> types = await records.ListRecordTypesAsync(cancellationToken).ConfigureAwait(false);
        Guid? peopleFirst = types.FirstOrDefault(type =>
                type.Lifecycle == RecordTypeLifecycle.Active &&
                string.Equals(type.PresetKey, "monkeysphere.person", StringComparison.Ordinal))?.Id
            ?? types.FirstOrDefault(type =>
                type.Lifecycle == RecordTypeLifecycle.Active &&
                (string.Equals(type.Name, "Person", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(type.Name, "People", StringComparison.OrdinalIgnoreCase)))?.Id;
        Guid[] birthdayFields = (await records.ListFieldDefinitionsAsync(cancellationToken).ConfigureAwait(false))
            .Where(IsEligibleDateField)
            .Where(field => string.Equals(
                field.CanonicalKey,
                "monkeysphere.person.birthday",
                StringComparison.Ordinal))
            .Select(field => field.Id)
            .ToArray();
        return new(
            Resolve(active, peopleFirst is Guid id ? [id] : [], []),
            birthdayFields,
            DefaultUpcomingDays);
    }

    public async Task<DashboardConfiguration> SaveConfigurationAsync(
        DashboardConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        if (configuration.UpcomingDays is < 1 or > MaximumUpcomingDays)
        {
            throw new DomainValidationException($"Dashboard look-ahead must be between 1 and {MaximumUpcomingDays} days.");
        }

        Guid[] typeIds = configuration.RecordTypeIds.Distinct().ToArray();
        Dictionary<Guid, RecordType> activeTypes = (await records.ListRecordTypesAsync(cancellationToken).ConfigureAwait(false))
            .Where(type => type.Lifecycle == RecordTypeLifecycle.Active)
            .ToDictionary(type => type.Id);
        if (typeIds.Any(id => !activeTypes.ContainsKey(id)))
        {
            throw new DomainValidationException("Dashboard record categories must be active.");
        }

        if (typeIds.Length > MaximumCategories)
        {
            throw new DomainValidationException(
                $"The dashboard cannot show more than {MaximumCategories} record categories.");
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

        DashboardConfiguration normalized = configuration with
        {
            RecordTypeIds = typeIds,
            RecurringFieldDefinitionIds = fieldIds,
        };

        // Everything active that was not chosen is recorded as dismissed. That is what makes a save
        // stick: without it the next read would append the unchosen types straight back, since a type
        // absent from an arrangement is otherwise indistinguishable from one created since.
        Guid[] dismissed = [.. activeTypes.Keys.Where(id => !typeIds.Contains(id))];
        await store.SaveConfigurationAsync(normalized, dismissed, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
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
