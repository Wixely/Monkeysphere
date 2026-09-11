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

/// <summary>
/// A card the file contained that could not be read. One unusable contact in a bulk export must
/// not cost the caller the other 199, so the parser sets it aside and carries on.
/// </summary>
/// <param name="Position">Its position in the file, counting from 1, including rejected cards.</param>
/// <param name="Label">Whatever identified it, if anything did. Null when nothing usable was found.</param>
/// <param name="Reason">Why it could not be read, in the same words the whole file used to fail with.</param>
public sealed record VCardRejectedCard(int Position, string? Label, string Reason);

/// <summary>What a file yielded: the cards that can be imported, and the ones that cannot.</summary>
public sealed record VCardParseResult(IReadOnlyList<VCard> Cards, IReadOnlyList<VCardRejectedCard> Rejected);

public static class VCardParser
{
    public const int MaximumBytes = 5 * 1024 * 1024;
    public const int MaximumCards = 1_000;
    public const int MaximumPropertiesPerCard = 2_000;

    public static VCardParseResult Parse(ReadOnlySpan<byte> content)
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

        ParserState parser = new();
        parser.Append(text.AsSpan());
        return parser.Complete();
    }

    public static async Task<VCardParseResult> ParseAsync(Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        byte[] bytes = new byte[16 * 1024];
        char[] characters = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        Decoder decoder = new UTF8Encoding(false, true).GetDecoder();
        ParserState parser = new();
        int total = 0;
        try
        {
            while (true)
            {
                int count = await content.ReadAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                total += count;
                if (total > MaximumBytes) throw new DomainValidationException($"vCard files cannot exceed {MaximumBytes} bytes.");
                int decoded = decoder.GetChars(bytes, 0, count, characters, 0, flush: count == 0);
                parser.Append(characters.AsSpan(0, decoded));
                if (count == 0) break;
            }
        }
        catch (DecoderFallbackException exception)
        {
            throw new DomainValidationException("vCard files must use valid UTF-8.", exception);
        }
        if (total == 0) throw new DomainValidationException("vCard files cannot be empty.");
        return parser.Complete();
    }

    private sealed class ParserState
    {
        private readonly StringBuilder _physical = new();
        private readonly StringBuilder _logical = new();
        private readonly List<VCard> _cards = [];
        private readonly List<VCardRejectedCard> _rejected = [];
        private List<VCardProperty>? _properties;
        // Set as soon as something in the current card cannot be read. The remaining lines are still
        // consumed so that the card's own END marker is found and the file stays in step.
        private string? _failure;
        private int _position;

        internal void Append(ReadOnlySpan<char> characters)
        {
            foreach (char character in characters)
            {
                if (character is '\r' or '\n') FinishPhysicalLine();
                else _physical.Append(character);
            }
        }

        private void FinishPhysicalLine()
        {
            if (_physical.Length == 0) return;
            if (_physical[0] is ' ' or '\t')
            {
                if (_logical.Length == 0) throw new DomainValidationException("A vCard continuation line has no preceding content line.");
                _logical.Append(_physical.ToString(1, _physical.Length - 1));
            }
            else
            {
                FinishLogicalLine();
                _logical.Append(_physical);
            }
            _physical.Clear();
        }

        private void FinishLogicalLine()
        {
            if (_logical.Length == 0) return;
            string line = _logical.ToString();
            _logical.Clear();
            if (string.Equals(line, "BEGIN:VCARD", StringComparison.OrdinalIgnoreCase))
            {
                if (_properties is not null) throw new DomainValidationException("Nested vCards are not valid.");
                _properties = [];
                _failure = null;
                _position++;
            }
            else if (string.Equals(line, "END:VCARD", StringComparison.OrdinalIgnoreCase))
            {
                if (_properties is null) throw new DomainValidationException("A vCard end marker has no matching start marker.");
                FinishCard(_properties);
                // Counted together so a file of nothing but unusable cards still fails fast.
                if (_cards.Count + _rejected.Count > MaximumCards)
                    throw new DomainValidationException($"A vCard file cannot contain more than {MaximumCards} contacts.");
                _properties = null;
            }
            else
            {
                if (_properties is null) throw new DomainValidationException("Content outside a vCard is not supported.");
                if (_failure is not null) return;
                try
                {
                    _properties.Add(ParseProperty(line));
                }
                catch (DomainValidationException exception)
                {
                    _failure = exception.Message;
                    return;
                }

                if (_properties.Count > MaximumPropertiesPerCard)
                    _failure = $"A contact cannot contain more than {MaximumPropertiesPerCard} properties.";
            }
        }

        private void FinishCard(List<VCardProperty> properties)
        {
            if (_failure is null)
            {
                try
                {
                    _cards.Add(CreateCard(properties));
                    return;
                }
                catch (DomainValidationException exception)
                {
                    _failure = exception.Message;
                }
            }

            _rejected.Add(new(_position, Label(properties), _failure!));
            _failure = null;
        }

        /// <summary>
        /// Best effort at something the reader will recognise. A card rejected for having no FN has
        /// to be identifiable by whatever else it carried, or it cannot be found and fixed.
        /// </summary>
        private static string? Label(IReadOnlyList<VCardProperty> properties)
        {
            foreach (string name in new[] { "FN", "N", "ORG", "EMAIL", "TEL", "UID" })
            {
                VCardProperty? match = properties.FirstOrDefault(property =>
                    string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase));
                if (match is null) continue;
                string text = match.TextValue.Replace(';', ' ').ReplaceLineEndings(" ").Trim();
                if (text.Length == 0) continue;
                return text.Length <= 100 ? text : text[..100];
            }

            return null;
        }

        internal VCardParseResult Complete()
        {
            FinishPhysicalLine();
            FinishLogicalLine();
            if (_properties is not null) throw new DomainValidationException("A vCard is missing its end marker.");
            if (_cards.Count == 0 && _rejected.Count == 0) throw new DomainValidationException("No vCards were found.");
            return new(_cards, _rejected);
        }
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

        VCardProperty[] formattedNames = properties.Where(property => property.Name == "FN").ToArray();
        if (formattedNames.Length != 1 || string.IsNullOrWhiteSpace(formattedNames[0].TextValue))
        {
            throw new DomainValidationException("Each vCard must contain exactly one non-empty formatted name (FN).");
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
    public static IReadOnlyList<string> SplitList(string value)
    {
        List<string> values = [];
        StringBuilder current = new();
        bool escaped = false;
        foreach (char character in value)
        {
            if (!escaped && character == '\\')
            {
                escaped = true;
                current.Append(character);
            }
            else if (!escaped && character == ',')
            {
                values.Add(Decode(current.ToString()));
                current.Clear();
            }
            else
            {
                current.Append(character);
                escaped = false;
            }
        }

        values.Add(Decode(current.ToString()));
        return values;
    }

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
