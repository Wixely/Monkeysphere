namespace Monkeysphere.Web.Components;

/// <summary>
/// Which record types the graph draws for a viewer who has expressed a preference.
///
/// The rule lives here rather than inline in the page because getting it wrong is invisible: the
/// graph simply omits some records and looks like a graph. It was wrong. The stored preference was
/// the set of types the viewer had <em>chosen</em>, and a record type created after that was written
/// is named by no stored list at all — so it was read as unchosen and never drawn, until somebody
/// noticed the gap and went to tick it. Storing what has been <em>hidden</em> answers the same
/// question for an existing type and the opposite one for a new type, which is the behaviour anyone
/// would expect: a type is drawn unless you said otherwise.
/// </summary>
public static class GraphRecordTypeVisibility
{
    /// <summary>
    /// The types to draw: everything that exists now, less whatever this viewer has hidden. Ids in
    /// the stored list that no longer exist are simply absent from the result, so a retired type
    /// leaves no trace behind, and unparseable entries are treated as naming nothing rather than
    /// discarding the whole preference.
    /// </summary>
    public static IReadOnlyList<Guid> Shown(IEnumerable<Guid> existing, IEnumerable<string>? hidden)
    {
        ArgumentNullException.ThrowIfNull(existing);
        HashSet<Guid> excluded = (hidden ?? [])
            .Select(value => Guid.TryParse(value, out Guid id) ? id : Guid.Empty)
            .ToHashSet();
        return [.. existing.Where(id => !excluded.Contains(id))];
    }

    /// <summary>
    /// What to store for a viewer currently showing <paramref name="shown"/>. Computed against the
    /// types that exist now, so the stored list prunes itself as types are retired rather than
    /// accumulating names nobody can resolve.
    /// </summary>
    public static IReadOnlyList<string> Hidden(IEnumerable<Guid> existing, IReadOnlySet<Guid> shown)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(shown);
        return [.. existing.Where(id => !shown.Contains(id)).Select(id => id.ToString("D"))];
    }
}
