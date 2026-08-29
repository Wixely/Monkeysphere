using System.Globalization;

namespace Monkeysphere.Core;

public sealed record LocationValueInput(
    string? DisplayContext = null,
    string? Latitude = null,
    string? Longitude = null,
    string? AccuracyMetres = null,
    string? ApproximationRadiusKilometres = null);

public sealed record LocationValue(
    string? DisplayContext,
    double? Latitude,
    double? Longitude,
    double? AccuracyMetres,
    double? ApproximationRadiusKilometres);

public static class LocationValues
{
    public static LocationValue? Normalize(LocationValueInput? input, string fieldName)
    {
        if (input is null)
        {
            return null;
        }

        string? context = OptionalText(input.DisplayContext, fieldName, 2_000);
        bool hasLatitude = !string.IsNullOrWhiteSpace(input.Latitude);
        bool hasLongitude = !string.IsNullOrWhiteSpace(input.Longitude);
        if (hasLatitude != hasLongitude)
        {
            throw new DomainValidationException($"{fieldName} requires both latitude and longitude.");
        }

        double? latitude = hasLatitude ? Coordinate(input.Latitude!, "latitude", -90, 90, fieldName) : null;
        double? longitude = hasLongitude ? Coordinate(input.Longitude!, "longitude", -180, 180, fieldName) : null;
        double? accuracy = OptionalNonNegativeNumber(input.AccuracyMetres, "accuracy in metres", fieldName, 40_100_000);
        double? radius = OptionalNonNegativeNumber(
            input.ApproximationRadiusKilometres,
            "approximation radius in kilometres",
            fieldName,
            20_050);

        if (accuracy.HasValue && !latitude.HasValue)
        {
            throw new DomainValidationException($"{fieldName} can specify coordinate accuracy only when coordinates are present.");
        }

        if (context is null && !latitude.HasValue)
        {
            if (radius.HasValue)
            {
                throw new DomainValidationException($"{fieldName} requires a description or coordinates when an approximation radius is supplied.");
            }

            return null;
        }

        return new(context, latitude, longitude, accuracy, radius);
    }

    public static string Format(LocationValue value)
    {
        List<string> parts = [];
        if (value.DisplayContext is not null)
        {
            parts.Add(value.DisplayContext);
        }

        if (value.Latitude is double latitude && value.Longitude is double longitude)
        {
            parts.Add($"{FormatNumber(latitude)}, {FormatNumber(longitude)}");
        }

        if (value.AccuracyMetres is double accuracy && accuracy > 0)
        {
            parts.Add($"accuracy {FormatNumber(accuracy)} m");
        }

        if (value.ApproximationRadiusKilometres is double radius && radius > 0)
        {
            parts.Add($"approx. radius {FormatNumber(radius)} km");
        }

        return string.Join(" \u00B7 ", parts);
    }

    private static string? OptionalText(string? value, string fieldName, int maximumLength)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length > maximumLength)
        {
            throw new DomainValidationException($"{fieldName} description cannot exceed {maximumLength} characters.");
        }

        return normalized.Length == 0 ? null : normalized;
    }

    private static double Coordinate(string value, string component, double minimum, double maximum, string fieldName)
    {
        if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ||
            !double.IsFinite(parsed) || parsed < minimum || parsed > maximum)
        {
            throw new DomainValidationException($"{fieldName} {component} must be between {minimum} and {maximum}.");
        }

        return Math.Round(parsed, 7, MidpointRounding.AwayFromZero);
    }

    private static double? OptionalNonNegativeNumber(
        string? value,
        string component,
        string fieldName,
        double maximum)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ||
            !double.IsFinite(parsed) || parsed < 0 || parsed > maximum)
        {
            throw new DomainValidationException($"{fieldName} {component} must be between 0 and {maximum}.");
        }

        return parsed == 0 ? null : parsed;
    }

    private static string FormatNumber(double value) => value.ToString("0.#######", CultureInfo.InvariantCulture);
}
