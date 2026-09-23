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

/// <summary>What became of one record in a bulk tag edit.</summary>
public enum RecordTagOutcome
{
    /// <summary>Its tags changed and were saved.</summary>
    Applied,

    /// <summary>It already carried exactly these tags, so nothing was written and its revision still stands.</summary>
    Unchanged,

    /// <summary>It moved underneath the caller since they read it, and was left alone.</summary>
    Stale,

    /// <summary>No such record, or none this caller may see.</summary>
    NotFound,

    /// <summary>Its record type has tags turned off, so it has nowhere to put them.</summary>
    TagsDisabled,
}

/// <summary>One record in a bulk tag edit, with the revision it was read at when there is one.</summary>
public sealed record RecordTagSelection(Guid RecordId, string? ExpectedRevision = null);

/// <summary>
/// Tags to add to and remove from an explicit set of records, and nothing else. Deliberately not
/// expressed as a whole-record update: doing this through the ordinary record update would mean
/// reading every field value of every selected record and writing them all back, which is a great
/// many chances to overwrite something the caller never intended to touch.
/// </summary>
public sealed record RecordTagEdit(
    IReadOnlyList<RecordTagSelection> Records,
    IReadOnlyList<string>? Add = null,
    IReadOnlyList<string>? Remove = null);

/// <summary>
/// One record's outcome. Reported per record rather than as a single success, because a selection
/// of forty is very likely to contain one that has moved or one whose type has no tags, and
/// "done" would be a untrue answer for those.
/// </summary>
public sealed record RecordTagChange(
    Guid RecordId,
    RecordTagOutcome Outcome,
    string DisplayName,
    IReadOnlyList<string> Tags,
    string Revision);

/// <summary>
/// Applies a bulk tag edit. Each record is its own transaction: a selection is not all-or-nothing,
/// because one stale record should not discard the other thirty-nine.
/// </summary>
public interface IRecordTagCommandStore
{
    Task<IReadOnlyList<RecordTagChange>> ApplyAsync(
        IReadOnlyList<RecordTagSelection> records,
        IReadOnlyList<ResolvedTag> add,
        IReadOnlyList<string> remove,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

public interface IRecordTagCommandService
{
    Task<IReadOnlyList<RecordTagChange>> ApplyAsync(RecordTagEdit edit, CancellationToken cancellationToken = default);
}

public sealed class RecordTagCommandService(
    IRecordTagCommandStore store,
    ITagCatalogue catalogue,
    ICurrentDomain currentDomain,
    TimeProvider timeProvider) : IRecordTagCommandService
{
    /// <summary>
    /// The same bound as a graph filter, because the graph selection is what this exists to serve
    /// and the two should not disagree about how much can be selected at once.
    /// </summary>
    public const int MaximumRecords = 100;

    public async Task<IReadOnlyList<RecordTagChange>> ApplyAsync(
        RecordTagEdit edit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        Guid[] recordIds = edit.Records.Select(record => record.RecordId).Distinct().ToArray();
        if (recordIds.Length == 0)
        {
            throw new DomainValidationException("Choose at least one record to tag.");
        }

        if (recordIds.Length != edit.Records.Count)
        {
            // Two entries for one record would carry two expected revisions, and the second could
            // only ever be stale against the first's write.
            throw new DomainValidationException("A record can appear only once in a tag edit.");
        }

        if (recordIds.Length > MaximumRecords)
        {
            throw new DomainValidationException($"A tag edit cannot cover more than {MaximumRecords} records.");
        }

        IReadOnlyList<string> add = RecordTagRules.Normalize(edit.Add);
        IReadOnlyList<string> remove = RecordTagRules.Normalize(edit.Remove);
        if (add.Count == 0 && remove.Count == 0)
        {
            throw new DomainValidationException("Choose at least one tag to add or remove.");
        }

        // Removing a tag this edit also adds cannot be honoured both ways, and silently preferring
        // one would make the result depend on an order the caller cannot see.
        string[] contradictions = add.Intersect(remove, StringComparer.OrdinalIgnoreCase).ToArray();
        if (contradictions.Length > 0)
        {
            throw new DomainValidationException(
                $"A tag edit cannot both add and remove the same tag: {string.Join(", ", contradictions)}.");
        }

        // Added names go through the catalogue, so a name already known in another domain becomes
        // that tag and gains this one rather than minting a duplicate, and the catalogue's own
        // spelling is what gets stored. Removals are matched against stored text instead, because
        // a name being removed need not exist in the catalogue at all.
        List<ResolvedTag> resolved = [];
        foreach (string name in add)
        {
            TagDefinition definition = await catalogue.EnsureAsync(name, currentDomain.Id, cancellationToken).ConfigureAwait(false);
            if (!resolved.Any(existing => string.Equals(existing.Name, definition.Name, StringComparison.OrdinalIgnoreCase)))
            {
                resolved.Add(new(definition.Id, definition.Name));
            }
        }

        return await store.ApplyAsync(edit.Records, resolved, remove, timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
    }
}
