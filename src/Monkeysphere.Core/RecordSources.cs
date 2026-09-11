namespace Monkeysphere.Core;

/// <summary>
/// Where a retained payload came from. vCard contact import is the first producer; the set is open
/// so that a later importer keeps its own raw material in the same place rather than inventing a
/// second one.
/// </summary>
public static class RecordSourceKinds
{
    /// <summary>A vCard contact, as parsed from the imported file.</summary>
    public const string VCard = "vcard";

    public static readonly IReadOnlyList<string> All = [VCard];

    public static string Normalize(string? kind) =>
        kind is not null && All.Contains(kind, StringComparer.Ordinal)
            ? kind
            : throw new DomainValidationException($"Record source kind must be one of: {string.Join(", ", All)}.");
}

/// <summary>
/// How much of an imported value the application actually took. Anything other than
/// <see cref="Opaque"/> means the value is also visible as ordinary record data; opaque values
/// exist only here, which is the whole reason this material is retained.
/// </summary>
public enum RecordSourceMapping
{
    /// <summary>Kept verbatim and understood by nothing. Custom properties and embedded data land here.</summary>
    Opaque,
    DisplayName,
    Aliases,
    FieldValue,
}

/// <summary>One occasion on which source material was imported into a record.</summary>
public sealed record RecordSourceImport(
    Guid Id,
    Guid RecordId,
    string SourceKind,
    /// <summary>The producer's own format label, such as a vCard version. Null when it has none.</summary>
    string? SourceFormat,
    /// <summary>Content fingerprint of the imported item, used to recognise a re-import.</summary>
    string? Fingerprint,
    DateTimeOffset ImportedAtUtc);

public sealed record RecordSourceParameter(string Name, IReadOnlyList<string> Values);

/// <summary>
/// One retained line of source material. <see cref="RawValue"/> is deliberately absent: an embedded
/// photo or key can run to megabytes, so a listing carries its size and a bounded preview and the
/// bytes are read separately through <see cref="IRecordSourceService.ReadValueAsync"/>.
/// </summary>
public sealed record RecordSourceValue(
    int Ordinal,
    /// <summary>The import this came from, or null for material retained before imports were attributed.</summary>
    Guid? ImportId,
    /// <summary>The producer's own grouping label, such as a vCard group. Null when it has none.</summary>
    string? Grouping,
    string Name,
    IReadOnlyList<RecordSourceParameter> Parameters,
    RecordSourceMapping Mapping,
    Guid? FieldDefinitionId,
    string? FieldName,
    int ValueLength,
    string ValuePreview,
    bool IsPreviewTruncated);

/// <summary>Everything retained for one record, newest import first.</summary>
public sealed record RecordSourceSnapshot(
    Guid RecordId,
    IReadOnlyList<RecordSourceImport> Imports,
    IReadOnlyList<RecordSourceValue> Values);

/// <summary>A bounded range of one retained value, read the way exports and images are read.</summary>
public sealed record RecordSourceValueRange(
    int Ordinal,
    string Name,
    int TotalLength,
    int Offset,
    string Content,
    int? NextOffset,
    /// <summary>Uppercase SHA-256 of the complete value. A change between ranges means restart at 0.</summary>
    string ContentDigest);

public static class RecordSourceLimits
{
    /// <summary>Characters of a value shown inline before the reader has to ask for the bytes.</summary>
    public const int PreviewLength = 256;

    public const int MinimumRangeLength = 1;

    public const int MaximumRangeLength = 16_384;

    /// <summary>Retained lines returned in one listing. Bounded by the parser's per-card ceiling.</summary>
    public const int MaximumValues = VCardParser.MaximumPropertiesPerCard;
}

public interface IRecordSourceStore
{
    Task<RecordSourceSnapshot?> GetAsync(Guid recordId, CancellationToken cancellationToken = default);

    /// <summary>Reads one retained value in full. Null when the record or ordinal does not resolve.</summary>
    Task<string?> ReadValueAsync(Guid recordId, int ordinal, CancellationToken cancellationToken = default);
}

public interface IRecordSourceService
{
    /// <summary>
    /// Everything retained for a record, or null when the record does not resolve or holds nothing.
    /// A record withheld by backstage policy resolves for nobody who cannot already see it.
    /// </summary>
    Task<RecordSourceSnapshot?> GetAsync(Guid recordId, CancellationToken cancellationToken = default);

    Task<RecordSourceValueRange> ReadValueAsync(
        Guid recordId,
        int ordinal,
        int offset = 0,
        int count = RecordSourceLimits.MaximumRangeLength,
        CancellationToken cancellationToken = default);
}

public sealed class RecordSourceService(IRecordSourceStore store) : IRecordSourceService
{
    public Task<RecordSourceSnapshot?> GetAsync(Guid recordId, CancellationToken cancellationToken = default) =>
        store.GetAsync(recordId, cancellationToken);

    public async Task<RecordSourceValueRange> ReadValueAsync(
        Guid recordId,
        int ordinal,
        int offset = 0,
        int count = RecordSourceLimits.MaximumRangeLength,
        CancellationToken cancellationToken = default)
    {
        if (ordinal < 0) throw new DomainValidationException("Retained value ordinal cannot be negative.");
        if (offset < 0) throw new DomainValidationException("Offset cannot be negative.");
        if (count is < RecordSourceLimits.MinimumRangeLength or > RecordSourceLimits.MaximumRangeLength)
        {
            throw new DomainValidationException(
                $"Count must be between {RecordSourceLimits.MinimumRangeLength} and {RecordSourceLimits.MaximumRangeLength}.");
        }

        string value = await store.ReadValueAsync(recordId, ordinal, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainValidationException("The retained value was not found.");

        // An offset equal to the length returns nothing and ends the chain, matching the export and
        // image readers; anything beyond it is a caller error rather than a silent empty result.
        if (offset > value.Length) throw new DomainValidationException("Offset is beyond the end of the value.");
        int taken = Math.Min(count, value.Length - offset);
        string name = await NameAsync(recordId, ordinal, cancellationToken).ConfigureAwait(false);
        return new(
            ordinal,
            name,
            value.Length,
            offset,
            value.Substring(offset, taken),
            offset + taken >= value.Length ? null : offset + taken,
            RecordSourceDigest.Of(value));
    }

    private async Task<string> NameAsync(Guid recordId, int ordinal, CancellationToken cancellationToken)
    {
        RecordSourceSnapshot? snapshot = await store.GetAsync(recordId, cancellationToken).ConfigureAwait(false);
        return snapshot?.Values.FirstOrDefault(value => value.Ordinal == ordinal)?.Name ?? string.Empty;
    }
}

public static class RecordSourceDigest
{
    public static string Of(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    /// <summary>Bounded, single-line preview. Control characters would otherwise break a listing.</summary>
    public static (string Preview, bool Truncated) Preview(string value)
    {
        string collapsed = value.Length <= RecordSourceLimits.PreviewLength
            ? value
            : value[..RecordSourceLimits.PreviewLength];
        return (collapsed.ReplaceLineEndings(" "), value.Length > RecordSourceLimits.PreviewLength);
    }
}
