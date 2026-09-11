using System.Text;

namespace Monkeysphere.Core;

/// <summary>
/// What an enrichment produces. Most become an ordinary field on the record type; photos become a
/// record image instead, because an image is not a field value here.
/// </summary>
public enum ContactEnrichmentOutcome
{
    Field,
    RecordImage,
}

/// <summary>
/// One thing an import can make of a vCard property the application would otherwise only retain.
/// The catalogue is deliberately a short list of properties real exports actually carry rather than
/// a generic rule, because a field named REV or PRODID means nothing to the person reading it.
/// </summary>
public sealed record ContactEnrichmentKind(
    string Key,
    string Label,
    string Description,
    ContactEnrichmentOutcome Outcome,
    /// <summary>The vCard properties this reads. More than one may feed a single field.</summary>
    IReadOnlyList<string> PropertyNames,
    string? FieldName = null,
    string? FieldTypeId = null,
    string? CanonicalKey = null);

/// <summary>A value an enrichment extracted, ready to become a field value.</summary>
public sealed record ContactEnrichmentValue(string Kind, string? ScalarValue, IReadOnlyList<string>? Tags)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(ScalarValue) && (Tags is null || Tags.Count == 0);
}

/// <summary>A photo an import found, either carried in the card or referenced by URL.</summary>
public sealed record ContactPhotoCandidate(string? InlineBase64, string? RemoteUrl, string? DeclaredType)
{
    public bool IsRemote => RemoteUrl is not null;
}

public static class ContactEnrichments
{
    public const string Photo = "photo";
    public const string Categories = "categories";
    public const string Address = "address";
    public const string Organisation = "organisation";
    public const string JobTitle = "job-title";
    public const string FamilyName = "family-name";
    public const string GivenName = "given-name";
    public const string SocialHandles = "social-handles";
    public const string VendorFacebook = "vendor-facebook";

    /// <summary>Reserved so an enrichment field is recognised again on the next import and on export.</summary>
    private const string KeyPrefix = "monkeysphere.person.";

    public static readonly IReadOnlyList<ContactEnrichmentKind> All =
    [
        new(Photo, "Contact photos",
            "Store the photo a card carries as a record image. Photos held in the card itself are decoded locally; a card that only references a photo by web address needs a separate choice to fetch it.",
            ContactEnrichmentOutcome.RecordImage, ["PHOTO"]),
        new(Categories, "Categories",
            "The groups or labels the exporting application had this contact in.",
            ContactEnrichmentOutcome.Field, ["CATEGORIES"], "Categories", FieldTypes.Tags, KeyPrefix + "categories"),
        new(Address, "Address",
            "The postal address, written out as one line. No location lookup is performed and no coordinates are stored.",
            ContactEnrichmentOutcome.Field, ["ADR"], "Address", FieldTypes.MultilineText, KeyPrefix + "address"),
        new(Organisation, "Organisation",
            "The organisation the contact was recorded against.",
            ContactEnrichmentOutcome.Field, ["ORG"], "Organisation", FieldTypes.Text, KeyPrefix + "organisation"),
        new(JobTitle, "Job title",
            "The contact's title or role.",
            ContactEnrichmentOutcome.Field, ["TITLE"], "Job title", FieldTypes.Text, KeyPrefix + "job-title"),
        new(FamilyName, "Family name",
            "The family name from the card's structured name, which is separate from the display name.",
            ContactEnrichmentOutcome.Field, ["N"], "Family name", FieldTypes.Text, KeyPrefix + "family-name"),
        new(GivenName, "Given name",
            "The given name from the card's structured name.",
            ContactEnrichmentOutcome.Field, ["N"], "Given name", FieldTypes.Text, KeyPrefix + "given-name"),
        new(SocialHandles, "Social handles",
            "Messaging and social profile addresses the card carried.",
            ContactEnrichmentOutcome.Field, ["IMPP", "X-SOCIALPROFILE"], "Social handles", FieldTypes.Tags, KeyPrefix + "social-handles"),
        new(VendorFacebook, "Facebook ID (from HTC export)",
            "Some HTC phones write an HTCData block into the note. This takes the Facebook identifier out of it; the block itself is kept out of the note either way.",
            ContactEnrichmentOutcome.Field, ["NOTE"], "Facebook ID", FieldTypes.Text, KeyPrefix + "facebook-id"),
    ];

    public static ContactEnrichmentKind Require(string key) =>
        All.FirstOrDefault(kind => string.Equals(kind.Key, key, StringComparison.Ordinal))
            ?? throw new DomainValidationException($"Unknown contact enrichment '{key}'.");

    /// <summary>Every enrichment this card could actually produce a value for.</summary>
    public static IReadOnlyList<string> Detect(VCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        List<string> detected = [];
        foreach (ContactEnrichmentKind kind in All)
        {
            bool present = kind.Outcome == ContactEnrichmentOutcome.RecordImage
                ? Photos(card).Count > 0
                : Extract(kind.Key, card) is { IsEmpty: false };
            if (present) detected.Add(kind.Key);
        }

        return detected;
    }

    /// <summary>The value this enrichment takes from the card, or null when it has nothing to take.</summary>
    public static ContactEnrichmentValue? Extract(string key, VCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        ContactEnrichmentKind kind = Require(key);
        ContactEnrichmentValue? value = key switch
        {
            Categories => Tags(kind, Values(card, "CATEGORIES").SelectMany(VCardText.SplitList)),
            SocialHandles => Tags(kind, Values(card, "IMPP").Concat(Values(card, "X-SOCIALPROFILE"))),
            Organisation => Scalar(kind, FirstComponent(card, "ORG")),
            JobTitle => Scalar(kind, Values(card, "TITLE").FirstOrDefault()),
            Address => Scalar(kind, FormatAddress(card)),
            FamilyName => Scalar(kind, NameComponent(card, 0)),
            GivenName => Scalar(kind, NameComponent(card, 1)),
            VendorFacebook => Scalar(kind, FacebookIdentifier(card)),
            _ => null,
        };
        return value is null || value.IsEmpty ? null : value;
    }

    /// <summary>
    /// The note text with any vendor block removed. HTC phones write an HTCData block into the
    /// note, and importing it verbatim fills the notes field with markup nobody wrote or wants.
    /// </summary>
    public static string CleanNote(string note)
    {
        if (string.IsNullOrEmpty(note)) return note;
        string cleaned = RemoveBlock(note, "<HTCData>", "</HTCData>");
        return cleaned.Trim();
    }

    /// <summary>True when the note carries a vendor block, so a caller can say the note was tidied.</summary>
    public static bool NoteCarriesVendorBlock(string note) =>
        !string.IsNullOrEmpty(note) && note.Contains("<HTCData>", StringComparison.OrdinalIgnoreCase);

    /// <summary>Photos the card carries, in card order. Inline data and web addresses are both reported.</summary>
    public static IReadOnlyList<ContactPhotoCandidate> Photos(VCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        List<ContactPhotoCandidate> photos = [];
        foreach (VCardProperty property in card.Named("PHOTO"))
        {
            VCardParameter? typeParameter = property.Parameters
                .FirstOrDefault(parameter => string.Equals(parameter.Name, "TYPE", StringComparison.OrdinalIgnoreCase));
            string declaredType = typeParameter is { Values.Count: > 0 } ? typeParameter.Values[0] : string.Empty;
            bool inline = property.Parameters.Any(parameter =>
                string.Equals(parameter.Name, "ENCODING", StringComparison.OrdinalIgnoreCase) &&
                parameter.Values.Any(value => value is "b" or "B" or "BASE64" or "base64"));
            string raw = property.Value.Trim();
            if (raw.Length == 0) continue;

            // vCard 4.0 writes inline data as a data: URI instead of an ENCODING parameter.
            if (!inline && raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = raw.IndexOf(',', StringComparison.Ordinal);
                if (comma > 0)
                {
                    photos.Add(new(raw[(comma + 1)..], null, MediaTypeOfDataUri(raw[..comma])));
                    continue;
                }
            }

            if (inline)
            {
                photos.Add(new(raw, null, declaredType));
            }
            else if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                photos.Add(new(null, VCardText.Decode(raw), declaredType));
            }
        }

        return photos;
    }

    private static ContactEnrichmentValue? Scalar(ContactEnrichmentKind kind, string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : new(kind.Key, value.Trim(), null);

    private static ContactEnrichmentValue? Tags(ContactEnrichmentKind kind, IEnumerable<string> values)
    {
        string[] tags = values
            .Select(value => VCardText.Decode(value).Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToArray();
        return tags.Length == 0 ? null : new(kind.Key, null, tags);
    }

    private static IEnumerable<string> Values(VCard card, string name) =>
        card.Named(name).Select(property => property.Value).Where(value => value.Length > 0);

    /// <summary>ORG is structured as organisation;unit;unit. Only the organisation itself is taken.</summary>
    private static string? FirstComponent(VCard card, string name) =>
        card.Named(name)
            .Select(property => VCardText.SplitStructured(property.Value))
            .Select(parts => parts.Count > 0 ? parts[0] : null)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    /// <summary>N is family;given;additional;prefix;suffix.</summary>
    private static string? NameComponent(VCard card, int index)
    {
        IReadOnlyList<VCardProperty> names = card.Named("N");
        VCardProperty? name = names.Count > 0 ? names[0] : null;
        if (name is null) return null;
        IReadOnlyList<string> parts = VCardText.SplitStructured(name.Value);
        return index < parts.Count ? parts[index] : null;
    }

    /// <summary>
    /// ADR is post-office-box;extended;street;locality;region;postcode;country. It is written out as
    /// lines rather than parsed into separate fields: addresses differ too much between countries
    /// for a fixed set of columns to be honest about what it holds.
    /// </summary>
    private static string? FormatAddress(VCard card)
    {
        IReadOnlyList<VCardProperty> addresses = card.Named("ADR");
        VCardProperty? address = addresses.Count > 0 ? addresses[0] : null;
        if (address is null) return null;
        IReadOnlyList<string> parts = VCardText.SplitStructured(address.Value);
        string Part(int index) => index < parts.Count ? parts[index].Trim() : string.Empty;
        string[] lines =
        [
            Part(0), Part(1), Part(2),
            string.Join(" ", new[] { Part(3), Part(5) }.Where(value => value.Length > 0)),
            Part(4), Part(6),
        ];
        string[] written = lines.Where(line => line.Length > 0).ToArray();
        return written.Length == 0 ? null : string.Join(Environment.NewLine, written);
    }

    /// <summary>
    /// Takes the Facebook identifier out of an HTC note block. The block is not general XML and is
    /// not treated as such: only this one known shape is read, and anything else is left alone.
    /// </summary>
    private static string? FacebookIdentifier(VCard card)
    {
        foreach (VCardProperty note in card.Named("NOTE"))
        {
            string text = note.TextValue;
            if (!NoteCarriesVendorBlock(text)) continue;
            string inner = Between(text, "<Facebook>", "</Facebook>");
            if (inner.Length == 0) continue;
            // Values look like "id:9900112233/friendof:4455667788"; the padding some exports add is junk.
            string identifier = Between(inner + "/", "id:", "/").Trim().Trim('<');
            if (identifier.Length > 0 && identifier.All(char.IsAsciiDigit)) return identifier;
        }

        return null;
    }

    private static string Between(string value, string start, string end)
    {
        int from = value.IndexOf(start, StringComparison.OrdinalIgnoreCase);
        if (from < 0) return string.Empty;
        from += start.Length;
        int to = value.IndexOf(end, from, StringComparison.OrdinalIgnoreCase);
        return to < 0 ? string.Empty : value[from..to];
    }

    private static string RemoveBlock(string value, string start, string end)
    {
        StringBuilder result = new(value.Length);
        int position = 0;
        while (position < value.Length)
        {
            int from = value.IndexOf(start, position, StringComparison.OrdinalIgnoreCase);
            if (from < 0)
            {
                result.Append(value, position, value.Length - position);
                break;
            }

            result.Append(value, position, from - position);
            int to = value.IndexOf(end, from, StringComparison.OrdinalIgnoreCase);
            if (to < 0) break;
            position = to + end.Length;
        }

        return result.ToString();
    }

    private static string? MediaTypeOfDataUri(string prefix)
    {
        string declared = prefix["data:".Length..];
        int semicolon = declared.IndexOf(';', StringComparison.Ordinal);
        if (semicolon >= 0) declared = declared[..semicolon];
        return declared.Length == 0 ? null : declared;
    }
}
