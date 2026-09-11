using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Monkeysphere.Core;

/// <summary>
/// What to do with a date that falls on 29 February in a year that has no 29 February. Neither
/// answer is more correct than the other, so the field says which one it means rather than the
/// application deciding on everybody's behalf.
/// </summary>
public enum LeapDayRoll
{
    /// <summary>Observe it on 28 February, the last day of the same month.</summary>
    Backward,

    /// <summary>Observe it on 1 March, the next day that exists.</summary>
    Forward,
}

/// <summary>
/// Whether a date field's values come round again, and how often. A birthday repeats every year; a
/// leap-year anniversary every four; a release date does not repeat at all. Recurrence belongs to
/// the field rather than to a view, so every view agrees about what repeats.
/// </summary>
public sealed record FieldRecurrence(
    bool Repeats = false,
    int IntervalYears = 1,
    LeapDayRoll LeapDayRoll = LeapDayRoll.Backward)
{
    public const int MaximumIntervalYears = 100;

    public static readonly FieldRecurrence None = new();

    /// <summary>Repeats every year, which is what a birthday or an anniversary does.</summary>
    public static readonly FieldRecurrence Annual = new(true, 1);

    public FieldRecurrence Validated()
    {
        if (IntervalYears is < 1 or > MaximumIntervalYears)
        {
            throw new DomainValidationException(
                $"A repeat interval must be between 1 and {MaximumIntervalYears} years.");
        }

        if (!Enum.IsDefined(LeapDayRoll))
        {
            throw new DomainValidationException("The 29 February rule is not recognised.");
        }

        return this;
    }
}

/// <summary>One appearance of a dated value: either the day it happened, or a later repeat of it.</summary>
/// <param name="Date">When it falls, already adjusted for a 29 February that does not exist.</param>
/// <param name="YearsSince">Zero for the original occurrence, otherwise whole years since it.</param>
/// <param name="IsRepeat">False for the day itself, true for a later repeat.</param>
/// <param name="RolledFromLeapDay">True when 29 February was moved to make it land on a real day.</param>
public sealed record DateOccurrence(DateOnly Date, int YearsSince, bool IsRepeat, bool RolledFromLeapDay);

public static class FieldRecurrences
{
    /// <summary>
    /// The recurrence a field declares. Only date-bearing fields can repeat; anything else, and any
    /// configuration that cannot be read, is treated as not repeating rather than guessed at.
    /// </summary>
    public static FieldRecurrence Of(FieldDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!Supports(definition.TypeId)) return FieldRecurrence.None;
        try
        {
            RecurrenceConfiguration? configuration =
                JsonSerializer.Deserialize<RecurrenceConfiguration>(definition.ConfigurationJson, Options);
            return configuration?.Recurrence?.Validated() ?? FieldRecurrence.None;
        }
        catch (JsonException)
        {
            return FieldRecurrence.None;
        }
        catch (DomainValidationException)
        {
            return FieldRecurrence.None;
        }
    }

    /// <summary>True for the field types that carry a date and can therefore come round again.</summary>
    public static bool Supports(string typeId) =>
        string.Equals(typeId, FieldTypes.ExactDate, StringComparison.Ordinal) ||
        string.Equals(typeId, FieldTypes.Temporal, StringComparison.Ordinal);

    /// <summary>Writes a recurrence into a field's configuration, leaving other settings alone.</summary>
    public static string Configure(string typeId, string? existingConfigurationJson, FieldRecurrence recurrence)
    {
        ArgumentNullException.ThrowIfNull(recurrence);
        if (!Supports(typeId))
        {
            throw new DomainValidationException("Only date fields can be set to repeat.");
        }

        // Merged into whatever the field already carries: a date field may hold settings this type
        // knows nothing about, and ticking "comes round again" must not quietly discard them.
        JsonObject configuration = ReadObject(existingConfigurationJson);
        configuration["recurrence"] = JsonSerializer.SerializeToNode(recurrence.Validated(), Options);
        return configuration.ToJsonString(Options);
    }

    private static JsonObject ReadObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            // Configuration that cannot be read as an object is already lost; starting fresh is the
            // only honest option, and matches what Of does with the same unreadable text.
            return JsonNode.Parse(json) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Every appearance of <paramref name="original"/> between <paramref name="from"/> and
    /// <paramref name="to"/>. The day itself is always included when it falls in range, so browsing
    /// back through a calendar still shows what actually happened then; repeats are only produced
    /// after it, and only on the declared interval.
    /// </summary>
    public static IReadOnlyList<DateOccurrence> Occurrences(
        DateOnly original,
        FieldRecurrence recurrence,
        DateOnly from,
        DateOnly to)
    {
        ArgumentNullException.ThrowIfNull(recurrence);
        if (to < from) return [];

        List<DateOccurrence> occurrences = [];
        if (original >= from && original <= to)
        {
            occurrences.Add(new(original, 0, false, false));
        }

        if (!recurrence.Repeats) return occurrences;
        recurrence.Validated();

        // Only years at or after the range start can contribute, and never the original year: that
        // appearance is the day itself, already handled above.
        int firstYear = Math.Max(from.Year, original.Year + recurrence.IntervalYears);
        for (int year = firstYear; year <= to.Year; year++)
        {
            int elapsed = year - original.Year;
            if (elapsed <= 0 || elapsed % recurrence.IntervalYears != 0) continue;

            (DateOnly date, bool rolled) = InYear(original, year, recurrence.LeapDayRoll);
            if (date < from || date > to) continue;
            occurrences.Add(new(date, elapsed, true, rolled));
        }

        return occurrences;
    }

    /// <summary>
    /// The same day-of-year in another year, moved off 29 February when that year has none. Moving
    /// forward can land in March, which is deliberate: the field said that is what it wants.
    /// </summary>
    public static (DateOnly Date, bool Rolled) InYear(DateOnly original, int year, LeapDayRoll roll)
    {
        if (original.Month == 2 && original.Day == 29 && !DateTime.IsLeapYear(year))
        {
            return roll == LeapDayRoll.Forward
                ? (new DateOnly(year, 3, 1), true)
                : (new DateOnly(year, 2, 28), true);
        }

        return (new DateOnly(year, original.Month, original.Day), false);
    }

    /// <summary>Parses a stored day-precision value, or null when it does not name a whole day.</summary>
    public static DateOnly? ParseDay(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed)
            ? parsed
            : null;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private sealed record RecurrenceConfiguration(FieldRecurrence? Recurrence);
}
