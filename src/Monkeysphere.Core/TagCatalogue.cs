using System.Globalization;

namespace Monkeysphere.Core;

/// <summary>
/// A tag as the deployment knows it. Tags are deliberately the one thing that spans domains: the
/// same label typed in two spheres means the same tag, which is what lets an administrator curate
/// them in one place. Membership is curation rather than access control — it decides where the tag
/// is offered, not who may read anything.
/// </summary>
public sealed record TagDefinition(
    Guid Id,
    string Name,
    string Colour,
    string Icon,
    IReadOnlyList<Guid> DomainIds,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string Revision = "")
{
    /// <summary>Records in that domain carrying this tag. Only populated where a page needs it.</summary>
    public int UsageCount { get; init; }
}

/// <summary>A tag as it is about to be written to a record: catalogue spelling, catalogue identity.</summary>
public sealed record ResolvedTag(Guid Id, string Name);

public static class TagAppearance
{
    public const string DefaultIcon = "#";

    /// <summary>An icon is one or two characters, so an emoji or a symbol both fit.</summary>
    public const int MaximumIconLength = 8;

    /// <summary>
    /// A colour for a brand-new tag. Deliberately not uniform random: a random RGB triple is
    /// frequently unreadable against one of the two themes. Hue is free, while saturation and
    /// lightness are held in a band that stays legible on both the light and the dark ground.
    /// </summary>
    public static string RandomColour() => FromHue(Random.Shared.Next(0, 360));

    /// <summary>Deterministic variant, so a test can assert a colour without pinning randomness.</summary>
    public static string FromHue(int hue) => HslToHex(((hue % 360) + 360) % 360, 0.55, 0.52);

    public static string NormalizeColour(string? colour)
    {
        string value = (colour ?? string.Empty).Trim();
        if (value.Length != 7 || value[0] != '#' ||
            !value[1..].All(character => Uri.IsHexDigit(character)))
        {
            throw new DomainValidationException("A tag colour must be a hex value such as #4f7fd0.");
        }

        return "#" + value[1..].ToLowerInvariant();
    }

    public static string NormalizeIcon(string? icon)
    {
        string value = (icon ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return DefaultIcon;
        }

        // Counted in text elements rather than chars, so one emoji built from several code units
        // is one icon rather than several characters.
        if (new System.Globalization.StringInfo(value).LengthInTextElements > 2 || value.Length > MaximumIconLength)
        {
            throw new DomainValidationException("A tag icon must be one or two characters.");
        }

        return value;
    }

    private static string HslToHex(int hue, double saturation, double lightness)
    {
        double chroma = (1 - Math.Abs((2 * lightness) - 1)) * saturation;
        double secondary = chroma * (1 - Math.Abs(((hue / 60.0) % 2) - 1));
        double match = lightness - (chroma / 2);
        (double red, double green, double blue) = hue switch
        {
            < 60 => (chroma, secondary, 0d),
            < 120 => (secondary, chroma, 0d),
            < 180 => (0d, chroma, secondary),
            < 240 => (0d, secondary, chroma),
            < 300 => (secondary, 0d, chroma),
            _ => (chroma, 0d, secondary),
        };
        return string.Create(CultureInfo.InvariantCulture,
            $"#{Channel(red + match):x2}{Channel(green + match):x2}{Channel(blue + match):x2}");
    }

    private static int Channel(double value) => (int)Math.Round(Math.Clamp(value, 0, 1) * 255);
}

/// <summary>
/// The deployment's tag catalogue. Reads are filtered to what the caller may observe: a hidden
/// domain's membership is withheld without backstage, so the tags page cannot be used to learn
/// that a concealed sphere exists.
/// </summary>
public interface ITagCatalogue
{
    /// <summary>Every tag the caller may see, optionally narrowed to one domain.</summary>
    Task<IReadOnlyList<TagDefinition>> ListAsync(Guid? domainId = null, CancellationToken cancellationToken = default);

    Task<TagDefinition?> FindAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the catalogue entry for a name, creating it when it is new and adding the domain
    /// when the tag already exists elsewhere. This is what makes typing an existing label in a
    /// second sphere enable that tag there rather than mint a duplicate, and why the caller must
    /// store the returned <see cref="TagDefinition.Name"/> rather than what was typed: the
    /// catalogue's spelling is the canonical one.
    /// </summary>
    Task<TagDefinition> EnsureAsync(string name, Guid domainId, CancellationToken cancellationToken = default);

    Task<TagDefinition> SetAppearanceAsync(Guid id, string colour, string icon, string? expectedRevision = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renames a tag. The catalogue changes at once; every domain database holding the old text is
    /// brought across by a durable queue, so the rename is eventually consistent and never atomic
    /// across domains.
    /// </summary>
    Task<TagDefinition> RenameAsync(Guid id, string name, string? expectedRevision = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the tag's domain membership. Removing a domain also removes the tag from every
    /// record in it, so callers must confirm against <see cref="CountUsageAsync"/> first.
    /// </summary>
    Task<TagDefinition> SetDomainsAsync(Guid id, IReadOnlyList<Guid> domainIds, string? expectedRevision = null,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, string? expectedRevision = null, CancellationToken cancellationToken = default);

    /// <summary>Records carrying the tag, per domain, so a destructive edit can say what it costs.</summary>
    Task<IReadOnlyDictionary<Guid, int>> CountUsageAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>
/// Deployment upkeep for tags. Both steps reconcile across databases, which no migration can do,
/// so they run at startup and on a timer rather than inside the schema.
/// </summary>
public interface ITagMaintenance
{
    /// <summary>Applies queued catalogue renames to the domain databases still holding the old text.</summary>
    Task<int> DrainRenamesAsync(CancellationToken cancellationToken = default);

    /// <summary>Adopts tag text that predates the catalogue, and converges spelling across domains.</summary>
    Task<int> AdoptExistingTagsAsync(CancellationToken cancellationToken = default);
}
