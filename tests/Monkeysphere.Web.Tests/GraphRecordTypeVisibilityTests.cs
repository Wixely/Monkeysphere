using Monkeysphere.Web.Components;

namespace Monkeysphere.Web.Tests;

/// <summary>
/// A record type created after somebody had last touched the graph's type filter never appeared in
/// the graph. The filter stored the types that had been chosen, and a type that did not exist when
/// that list was written is named by it no more than one somebody had deliberately unticked — so the
/// new type was read as unwanted and quietly left out of every graph until its absence was noticed.
///
/// The cure is to store what has been hidden instead, and these pin the half of it that decides the
/// behaviour: what a stored preference says about a type it does not mention.
/// </summary>
public sealed class GraphRecordTypeVisibilityTests
{
    private static readonly Guid Person = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Pet = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid Project = Guid.Parse("33333333-3333-4333-8333-333333333333");

    [Fact]
    public void ATypeNobodyHasHiddenIsDrawn()
    {
        // The reported case: Project was created after the preference was written, so the stored list
        // has nothing to say about it. Under the old rule that silence meant "not chosen"; it now
        // means what silence should mean.
        IReadOnlyList<Guid> shown = GraphRecordTypeVisibility.Shown([Person, Pet, Project], [Pet.ToString("D")]);
        Assert.Equal([Person, Project], shown);
        Assert.DoesNotContain(Pet, shown);
    }

    [Fact]
    public void HidingStillPersistsAndHidingEverythingStillMeansEverything()
    {
        // The other half: a deliberate choice has to survive, or the fix would simply ignore the
        // preference and call the problem solved.
        Assert.Equal([Pet.ToString("D")], GraphRecordTypeVisibility.Hidden([Person, Pet], new HashSet<Guid> { Person }));

        // The graph draws nothing when nothing is chosen, and that is a legitimate state rather than
        // an empty preference: it round-trips.
        IReadOnlyList<string> allHidden = GraphRecordTypeVisibility.Hidden([Person, Pet], new HashSet<Guid>());
        Assert.Equal(2, allHidden.Count);
        Assert.Empty(GraphRecordTypeVisibility.Shown([Person, Pet], allHidden));

        // And showing everything stores nothing, so the common case leaves no list to go stale.
        Assert.Empty(GraphRecordTypeVisibility.Hidden([Person, Pet], new HashSet<Guid> { Person, Pet }));
    }

    [Fact]
    public void AStoredPreferenceDoesNotOutliveTheTypesItNames()
    {
        // Pet has been retired since. It is not drawn because it no longer exists, not because it was
        // hidden, and the difference shows on the next save: the stored list prunes itself rather
        // than carrying a name nobody can resolve.
        string[] stored = [Pet.ToString("D"), Project.ToString("D")];
        Assert.Equal([Person], GraphRecordTypeVisibility.Shown([Person], stored));
        Assert.Empty(GraphRecordTypeVisibility.Hidden([Person], new HashSet<Guid> { Person }));

        // Rubbish in the stored list names nothing rather than discarding the whole preference, so one
        // corrupt entry cannot silently un-hide everything else.
        Assert.Equal([Person], GraphRecordTypeVisibility.Shown([Person, Pet], ["not-a-guid", Pet.ToString("D")]));

        // No preference at all is every type, which is what a viewer who has never touched the filter
        // should see.
        Assert.Equal([Person, Pet], GraphRecordTypeVisibility.Shown([Person, Pet], null));
        Assert.Equal([Person, Pet], GraphRecordTypeVisibility.Shown([Person, Pet], []));
    }
}
