using System.Security.Cryptography;
using System.Text;

namespace Monkeysphere.Core;

public sealed record VCardParameter(string Name, IReadOnlyList<string> Values);

public sealed record VCardProperty(
    string? Group,
    string Name,
    IReadOnlyList<VCardParameter> Parameters,
    string Value)
{
    public string TextValue => VCardText.Decode(Value);
}

public sealed record VCard(string Version, IReadOnlyList<VCardProperty> Properties, string Fingerprint)
{
    public IReadOnlyList<VCardProperty> Named(string name) =>
        Properties.Where(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
}

public static class VCardParser
{
    public const int MaximumBytes = 5 * 1024 * 1024;
    public const int MaximumCards = 1_000;
    public const int MaximumPropertiesPerCard = 2_000;

    public static IReadOnlyList<VCard> Parse(ReadOnlySpan<byte> content)
    {
        if (content.Length is 0 or > MaximumBytes)
        {
            throw new DomainValidationException($"vCard files must contain between 1 byte and {MaximumBytes} bytes.");
        }

        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(content);
        }
        catch (DecoderFallbackException exception)
        {
            throw new DomainValidationException("vCard files must use valid UTF-8.", exception);
        }

        List<string> unfolded = [];
        foreach (string physicalLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            if (physicalLine.Length > 0 && physicalLine[0] is ' ' or '\t')
            {
                if (unfolded.Count == 0)
                {
                    throw new DomainValidationException("A vCard continuation line has no preceding content line.");
                }

                unfolded[^1] += physicalLine[1..];
            }
            else if (physicalLine.Length > 0)
            {
                unfolded.Add(physicalLine);
            }
        }

        List<VCard> cards = [];
        List<VCardProperty>? properties = null;
        foreach (string line in unfolded)
        {
            if (string.Equals(line, "BEGIN:VCARD", StringComparison.OrdinalIgnoreCase))
            {
                if (properties is not null)
                {
                    throw new DomainValidationException("Nested vCards are not valid.");
                }

                properties = [];
                continue;
            }

            if (string.Equals(line, "END:VCARD", StringComparison.OrdinalIgnoreCase))
            {
                if (properties is null)
                {
                    throw new DomainValidationException("A vCard end marker has no matching start marker.");
                }

                cards.Add(CreateCard(properties));
                if (cards.Count > MaximumCards)
                {
                    throw new DomainValidationException($"A vCard file cannot contain more than {MaximumCards} contacts.");
                }

                properties = null;
                continue;
            }

            if (properties is null)
            {
                throw new DomainValidationException("Content outside a vCard is not supported.");
            }

            properties.Add(ParseProperty(line));
            if (properties.Count > MaximumPropertiesPerCard)
            {
                throw new DomainValidationException($"A contact cannot contain more than {MaximumPropertiesPerCard} properties.");
            }
        }

        if (properties is not null)
        {
            throw new DomainValidationException("A vCard is missing its end marker.");
        }

        if (cards.Count == 0)
        {
            throw new DomainValidationException("No vCards were found.");
        }

        return cards;
    }

    private static VCard CreateCard(IReadOnlyList<VCardProperty> properties)
    {
        string[] versions = properties
            .Where(property => property.Name == "VERSION")
            .Select(property => property.Value)
            .ToArray();
        if (versions.Length != 1 || versions[0] is not ("3.0" or "4.0"))
        {
            throw new DomainValidationException("Each vCard must declare exactly one supported VERSION: 3.0 or 4.0.");
        }

        if (!properties.Any(property => property.Name == "FN" && !string.IsNullOrWhiteSpace(property.TextValue)))
        {
            throw new DomainValidationException("Each vCard must contain a non-empty formatted name (FN).");
        }

        string canonical = string.Join('\n', properties.Select(VCardSerializer.PropertyLine));
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new(versions[0], properties, fingerprint);
    }

    private static VCardProperty ParseProperty(string line)
    {
        int colon = FindDelimiter(line, ':');
        if (colon <= 0)
        {
            throw new DomainValidationException("A vCard property is missing its ':' value delimiter.");
        }

        string[] parts = SplitAware(line[..colon], ';');
        string identifier = parts[0];
        int dot = identifier.IndexOf('.');
        string? group = dot < 0 ? null : NormalizeToken(identifier[..dot], "property group");
        string name = NormalizeToken(dot < 0 ? identifier : identifier[(dot + 1)..], "property name");
        List<VCardParameter> parameters = [];
        foreach (string part in parts.Skip(1))
        {
            int equals = part.IndexOf('=');
            string parameterName = equals < 0 ? "TYPE" : NormalizeToken(part[..equals], "parameter name");
            string parameterValue = equals < 0 ? part : part[(equals + 1)..];
            string[] values = SplitAware(parameterValue, ',')
                .Select(UnquoteParameter)
                .ToArray();
            if (values.Length == 0 || values.Any(string.IsNullOrWhiteSpace))
            {
                throw new DomainValidationException($"vCard parameter '{parameterName}' contains an empty value.");
            }

            parameters.Add(new(parameterName, values));
        }

        return new(group, name, parameters, line[(colon + 1)..]);
    }

    private static string NormalizeToken(string value, string label)
    {
        string normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length == 0 || normalized.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new DomainValidationException($"A vCard {label} contains invalid characters.");
        }

        return normalized;
    }

    private static int FindDelimiter(string value, char delimiter)
    {
        bool quoted = false;
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] == '"')
            {
                quoted = !quoted;
            }
            else if (value[index] == delimiter && !quoted)
            {
                return index;
            }
        }

        return -1;
    }

    private static string[] SplitAware(string value, char delimiter)
    {
        List<string> parts = [];
        int start = 0;
        bool quoted = false;
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] == '"')
            {
                quoted = !quoted;
            }
            else if (value[index] == delimiter && !quoted)
            {
                parts.Add(value[start..index]);
                start = index + 1;
            }
        }

        if (quoted)
        {
            throw new DomainValidationException("A vCard parameter contains an unterminated quoted value.");
        }

        parts.Add(value[start..]);
        return [.. parts];
    }

    private static string UnquoteParameter(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            trimmed = trimmed[1..^1];
        }

        return trimmed
            .Replace("^n", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("^'", "\"", StringComparison.Ordinal)
            .Replace("^^", "^", StringComparison.Ordinal);
    }
}

public static class VCardSerializer
{
    public static byte[] Serialize(IReadOnlyList<IReadOnlyList<VCardProperty>> cards)
    {
        StringBuilder output = new();
        foreach (IReadOnlyList<VCardProperty> properties in cards)
        {
            AppendFolded(output, "BEGIN:VCARD");
            AppendFolded(output, "VERSION:4.0");
            foreach (VCardProperty property in properties.Where(property => property.Name is not ("VERSION" or "BEGIN" or "END")))
            {
                AppendFolded(output, PropertyLine(property));
            }

            AppendFolded(output, "END:VCARD");
        }

        return Encoding.UTF8.GetBytes(output.ToString());
    }

    public static string PropertyLine(VCardProperty property)
    {
        StringBuilder line = new();
        if (!string.IsNullOrWhiteSpace(property.Group))
        {
            line.Append(property.Group).Append('.');
        }

        line.Append(property.Name);
        foreach (VCardParameter parameter in property.Parameters)
        {
            line.Append(';').Append(parameter.Name).Append('=');
            line.AppendJoin(',', parameter.Values.Select(EncodeParameter));
        }

        return line.Append(':').Append(property.Value).ToString();
    }

    public static string EncodeText(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\r\n", "\\n", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\n", StringComparison.Ordinal)
        .Replace(",", "\\,", StringComparison.Ordinal)
        .Replace(";", "\\;", StringComparison.Ordinal);

    private static string EncodeParameter(string value)
    {
        string encoded = value
            .Replace("^", "^^", StringComparison.Ordinal)
            .Replace("\"", "^'", StringComparison.Ordinal)
            .Replace("\r\n", "^n", StringComparison.Ordinal)
            .Replace("\n", "^n", StringComparison.Ordinal)
            .Replace("\r", "^n", StringComparison.Ordinal);
        return encoded.IndexOfAny([',', ';', ':']) >= 0 ? $"\"{encoded}\"" : encoded;
    }

    private static void AppendFolded(StringBuilder target, string line)
    {
        int octets = 0;
        int limit = 75;
        foreach (Rune rune in line.EnumerateRunes())
        {
            int runeOctets = rune.Utf8SequenceLength;
            if (octets > 0 && octets + runeOctets > limit)
            {
                target.Append("\r\n ");
                octets = 0;
                limit = 74;
            }

            target.Append(rune.ToString());
            octets += runeOctets;
        }

        target.Append("\r\n");
    }
}

public static class VCardText
{
    public static string Decode(string value)
    {
        StringBuilder decoded = new(value.Length);
        bool escaped = false;
        foreach (char character in value)
        {
            if (!escaped && character == '\\')
            {
                escaped = true;
                continue;
            }

            if (escaped)
            {
                decoded.Append(character is 'n' or 'N' ? '\n' : character);
                escaped = false;
            }
            else
            {
                decoded.Append(character);
            }
        }

        if (escaped)
        {
            decoded.Append('\\');
        }

        return decoded.ToString();
    }
}
