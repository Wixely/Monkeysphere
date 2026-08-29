using System.Globalization;
using System.Text.RegularExpressions;

namespace Monkeysphere.Core;

public enum TemporalPrecision
{
    Century,
    Decade,
    Year,
    Month,
    Day,
    Minute,
    Second,
}

public sealed record TemporalValueInput(
    string Value,
    TemporalPrecision Precision,
    bool IsApproximate = false,
    string? ApproximationNote = null);

public sealed record NormalizedTemporalValue(
    string Value,
    TemporalPrecision Precision,
    string SortKey,
    bool IsApproximate,
    string? ApproximationNote);

public static partial class TemporalValues
{
    public static NormalizedTemporalValue Normalize(TemporalValueInput input, string fieldName)
    {
        string raw = FieldTypes.Required(input.Value, fieldName, 32);
        (string value, string sortKey) = input.Precision switch
        {
            TemporalPrecision.Century => NormalizeCentury(raw, fieldName),
            TemporalPrecision.Decade => NormalizeDecade(raw, fieldName),
            TemporalPrecision.Year => NormalizeExact(raw, "yyyy", fieldName),
            TemporalPrecision.Month => NormalizeExact(raw, "yyyy-MM", fieldName),
            TemporalPrecision.Day => NormalizeExact(raw, "yyyy-MM-dd", fieldName),
            TemporalPrecision.Minute => NormalizeExact(raw, "yyyy-MM-dd'T'HH:mm", fieldName),
            TemporalPrecision.Second => NormalizeExact(raw, "yyyy-MM-dd'T'HH:mm:ss", fieldName),
            _ => throw new DomainValidationException($"{fieldName} has an unsupported temporal precision."),
        };
        string? note = string.IsNullOrWhiteSpace(input.ApproximationNote)
            ? null
            : FieldTypes.Required(input.ApproximationNote, $"{fieldName} approximation note", 500);
        return new(value, input.Precision, sortKey, input.IsApproximate, note);
    }

    public static string Format(string value, TemporalPrecision precision, bool isApproximate, string? note = null)
    {
        string display = precision switch
        {
            TemporalPrecision.Century => $"{Ordinal(int.Parse(value, CultureInfo.InvariantCulture))} century",
            TemporalPrecision.Decade => value + "s",
            TemporalPrecision.Month when DateOnly.TryParseExact(value + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly month) =>
                month.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
            TemporalPrecision.Day when DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day) =>
                day.ToString("d MMMM yyyy", CultureInfo.InvariantCulture),
            TemporalPrecision.Minute when DateTime.TryParseExact(value, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime minute) =>
                minute.ToString("d MMMM yyyy HH:mm", CultureInfo.InvariantCulture),
            TemporalPrecision.Second when DateTime.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime second) =>
                second.ToString("d MMMM yyyy HH:mm:ss", CultureInfo.InvariantCulture),
            _ => value,
        };
        string prefix = isApproximate ? "circa " : string.Empty;
        return string.IsNullOrWhiteSpace(note) ? prefix + display : $"{prefix}{display} ({note})";
    }

    public static string InputHint(TemporalPrecision precision) => precision switch
    {
        TemporalPrecision.Century => "Century number, for example 19",
        TemporalPrecision.Decade => "Decade start, for example 1980",
        TemporalPrecision.Year => "YYYY",
        TemporalPrecision.Month => "YYYY-MM",
        TemporalPrecision.Day => "YYYY-MM-DD",
        TemporalPrecision.Minute => "YYYY-MM-DDTHH:mm",
        TemporalPrecision.Second => "YYYY-MM-DDTHH:mm:ss",
        _ => string.Empty,
    };

    public static string NormalizeFilterSortKey(string value)
    {
        string raw = FieldTypes.Required(value, "Temporal filter", 32);
        TemporalValueInput input = raw switch
        {
            _ when raw.EndsWith('c') => new(raw[..^1], TemporalPrecision.Century),
            _ when raw.EndsWith('s') => new(raw, TemporalPrecision.Decade),
            _ when raw.Length == 4 => new(raw, TemporalPrecision.Year),
            _ when raw.Length == 7 => new(raw, TemporalPrecision.Month),
            _ when raw.Length == 10 => new(raw, TemporalPrecision.Day),
            _ when raw.Length == 16 => new(raw, TemporalPrecision.Minute),
            _ when raw.Length == 19 => new(raw, TemporalPrecision.Second),
            _ => throw new DomainValidationException(
                "Temporal filters require 19c, 1980s, YYYY, YYYY-MM, YYYY-MM-DD, YYYY-MM-DDTHH:mm, or YYYY-MM-DDTHH:mm:ss."),
        };

        return Normalize(input, "Temporal filter").SortKey;
    }

    private static (string Value, string SortKey) NormalizeCentury(string raw, string fieldName)
    {
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int century) || century is < 1 or > 100)
        {
            throw new DomainValidationException($"{fieldName} century must be a number from 1 through 100.");
        }

        int firstYear = ((century - 1) * 100) + 1;
        return (century.ToString(CultureInfo.InvariantCulture), $"{firstYear:D4}-01-01T00:00:00");
    }

    private static (string Value, string SortKey) NormalizeDecade(string raw, string fieldName)
    {
        Match match = DecadePattern().Match(raw);
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int decade) ||
            decade is < 10 or > 9990 ||
            decade % 10 != 0)
        {
            throw new DomainValidationException($"{fieldName} decade must be a four-digit decade start such as 1980.");
        }

        return (decade.ToString("D4", CultureInfo.InvariantCulture), $"{decade:D4}-01-01T00:00:00");
    }

    private static (string Value, string SortKey) NormalizeExact(string raw, string format, string fieldName)
    {
        if (format is "yyyy" && Regex.IsMatch(raw, "^[0-9]{4}$", RegexOptions.CultureInvariant) &&
            int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int year) && year is >= 1)
        {
            return (raw, raw + "-01-01T00:00:00");
        }

        if (format is "yyyy-MM" && DateOnly.TryParseExact(raw + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return (raw, raw + "-01T00:00:00");
        }

        if (format is "yyyy-MM-dd" && DateOnly.TryParseExact(raw, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return (raw, raw + "T00:00:00");
        }

        if (format.Contains("HH", StringComparison.Ordinal) && DateTime.TryParseExact(raw, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return (raw, raw.Length == 16 ? raw + ":00" : raw);
        }

        throw new DomainValidationException($"{fieldName} must use {format} for the selected precision.");
    }

    private static string Ordinal(int value)
    {
        int remainder100 = value % 100;
        string suffix = remainder100 is 11 or 12 or 13
            ? "th"
            : (value % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return value.ToString(CultureInfo.InvariantCulture) + suffix;
    }

    [GeneratedRegex("^([0-9]{4})s?$", RegexOptions.CultureInvariant)]
    private static partial Regex DecadePattern();
}
