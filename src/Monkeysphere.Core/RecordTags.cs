namespace Monkeysphere.Core;

/// <summary>
/// Universal tags: the freeform, cross-cutting labels every record carries. They are deliberately
/// not a field. A tags-typed field still means one specific thing — a Person's "Likes" is not a
/// Book's "Genres" — whereas these are the labels that deserve no field of their own.
/// </summary>
public static class RecordTagRules
{
    /// <summary>Shared with the tags field type so that the two cannot drift apart.</summary>
    public const int MaximumCount = 100;

    public const int MaximumLength = 200;

    /// <summary>
    /// Trims, rejects empty and over-long values, and removes case-insensitive duplicates while
    /// preserving the order the caller gave. The result is what storage holds, ordinal by ordinal.
    /// </summary>
    public static IReadOnlyList<string> Normalize(IReadOnlyList<string>? tags)
    {
        if (tags is null || tags.Count == 0)
        {
            return [];
        }

        string[] trimmed = tags.Select(tag => (tag ?? string.Empty).Trim()).ToArray();

        if (trimmed.Any(tag => tag.Length == 0))
        {
            throw new DomainValidationException("A tag cannot be empty.");
        }

        if (trimmed.Any(tag => tag.Length > MaximumLength))
        {
            throw new DomainValidationException($"A tag cannot be longer than {MaximumLength} characters.");
        }

        string[] distinct = trimmed.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        // Counted after de-duplication, so repeating one tag cannot exhaust the allowance.
        if (distinct.Length > MaximumCount)
        {
            throw new DomainValidationException($"A record cannot carry more than {MaximumCount} tags.");
        }

        return distinct;
    }
}

/// <summary>
/// What one domain knows about its universal tags. Writing them is deliberately not here: a tag
/// has to be resolved through ITagCatalogue first, so that a name already known elsewhere becomes
/// that tag rather than a second one, and a second write path would quietly skip that.
/// </summary>
public interface IRecordTagStore
{
    /// <summary>
    /// Every tag in use in this domain, for suggestions and filtering. Withholds the tags that
    /// only hidden records carry: an ordinary reader must not learn of a hidden record through a
    /// suggestion list it can see but a record it cannot.
    /// </summary>
    Task<IReadOnlyList<string>> ListVocabularyAsync(CancellationToken cancellationToken = default);

    /// <summary>Counts records carrying any tag, so removing tags from a type can say what it hides.</summary>
    Task<int> CountTaggedRecordsAsync(Guid recordTypeId, CancellationToken cancellationToken = default);
}
