using System.Text.Json;
using System.Text.RegularExpressions;

namespace Monkeysphere.Core;

public static partial class FieldTypes
{
    public const string Text = "text";
    public const string MultilineText = "multiline-text";
    public const string Number = "number";
    public const string ExactDate = "exact-date";
    public const string Choice = "choice";
    public const string Tags = "tags";
    public const string Temporal = "temporal";
    public const string PhoneNumber = "phone-number";
    public const string WebLink = "web-link";
    public const string Location = "location";

    public static IReadOnlyList<string> Recognized { get; } =
        [Text, MultilineText, Number, ExactDate, Choice, Tags, Temporal, PhoneNumber, WebLink, Location];

    public static string NormalizeTypeId(string value)
    {
        string normalized = Required(value, "Field type", 100).ToLowerInvariant();
        if (!TypeIdPattern().IsMatch(normalized))
        {
            throw new DomainValidationException(
                "Field type identifiers must start with a letter and contain only lowercase letters, numbers, '.', '_' or '-'.");
        }

        return normalized;
    }

    public static string NormalizeConfiguration(string typeId, IReadOnlyCollection<string>? choiceOptions)
    {
        if (!string.Equals(typeId, Choice, StringComparison.Ordinal))
        {
            return "{}";
        }

        string[] options = (choiceOptions ?? [])
            .Select(item => Required(item, "Choice option", 200))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (options.Length == 0)
        {
            throw new DomainValidationException("Choice fields require at least one option.");
        }

        if (options.Length != (choiceOptions?.Count ?? 0))
        {
            throw new DomainValidationException("Choice options cannot be empty or duplicated.");
        }

        return JsonSerializer.Serialize(new ChoiceConfiguration(options));
    }

    public static IReadOnlyList<string> ChoiceOptions(FieldDefinition definition)
    {
        if (!string.Equals(definition.TypeId, Choice, StringComparison.Ordinal))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<ChoiceConfiguration>(definition.ConfigurationJson)?.Options ?? [];
        }
        catch (JsonException exception)
        {
            throw new DomainValidationException($"Choice field '{definition.Name}' has invalid configuration.", exception);
        }
    }

    public static string Required(string value, string label, int maximumLength)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw new DomainValidationException($"{label} is required.");
        }

        if (normalized.Length > maximumLength)
        {
            throw new DomainValidationException($"{label} cannot exceed {maximumLength} characters.");
        }

        return normalized;
    }

    private sealed record ChoiceConfiguration(IReadOnlyList<string> Options);

    [GeneratedRegex("^[a-z][a-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex TypeIdPattern();
}

public sealed class DomainValidationException : InvalidOperationException
{
    public DomainValidationException(string message)
        : base(message)
    {
    }

    public DomainValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
