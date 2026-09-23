using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Data.Tests;

/// <summary>
/// Bulk tagging, which the graph needs and the ordinary record update could not safely provide:
/// that path takes a record's whole contents, so tagging forty records through it would mean
/// reading and rewriting every field value of all forty. These pin the parts that make a bulk
/// edit honest — it touches only tags, it says what happened to each record rather than claiming
/// one outcome for all of them, and a record that moved underneath the operator is reported
/// instead of overwritten.
/// </summary>
public sealed class RecordTagCommandTests
{
    [Fact]
    public async Task AddingATagReachesOnlyTheRecordsMissingItAndLeavesTheirOtherTagsAlone()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IRecordTagCommandService tagging = application.Services.GetRequiredService<IRecordTagCommandService>();

        RecordType type = await records.CreateRecordTypeAsync("Tagged person");
        RecordDetails ada = await records.CreateRecordAsync(type.Id, "Ada", [], null, ["mathematician"]);
        RecordDetails grace = await records.CreateRecordAsync(type.Id, "Grace", [], null, ["navy", "london"]);
        RecordDetails mira = await records.CreateRecordAsync(type.Id, "Mira", [], null, ["london"]);

        IReadOnlyList<RecordTagChange> applied = await tagging.ApplyAsync(new(
            [new(ada.Record.Id), new(grace.Record.Id), new(mira.Record.Id)],
            Add: ["london"]));

        Assert.Equal(RecordTagOutcome.Applied, applied.Single(change => change.RecordId == ada.Record.Id).Outcome);

        // Already carried it, so nothing was written and its revision still stands. Reporting this
        // as Applied would invalidate a revision the operator is still holding.
        RecordTagChange untouched = applied.Single(change => change.RecordId == grace.Record.Id);
        Assert.Equal(RecordTagOutcome.Unchanged, untouched.Outcome);
        Assert.Equal(grace.Revision, untouched.Revision);

        // The tag was added, not substituted for what was already there.
        RecordDetails reloaded = (await records.GetRecordAsync(ada.Record.Id))!;
        Assert.Equal(["mathematician", "london"], reloaded.Tags);
        Assert.Equal(["navy", "london"], (await records.GetRecordAsync(grace.Record.Id))!.Tags);
    }

    [Fact]
    public async Task RemovingATagLeavesEveryOtherTagInPlace()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IRecordTagCommandService tagging = application.Services.GetRequiredService<IRecordTagCommandService>();

        RecordType type = await records.CreateRecordTypeAsync("Untagged person");
        RecordDetails ada = await records.CreateRecordAsync(type.Id, "Ada", [], null, ["work", "london", "mentor"]);

        _ = await tagging.ApplyAsync(new([new(ada.Record.Id)], Remove: ["london"]));

        Assert.Equal(["work", "mentor"], (await records.GetRecordAsync(ada.Record.Id))!.Tags);
    }

    [Fact]
    public async Task ATagEditTouchesNothingButTags()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IRecordTagCommandService tagging = application.Services.GetRequiredService<IRecordTagCommandService>();

        RecordType type = await records.CreateRecordTypeAsync("Whole person");
        FieldDefinition note = await records.CreateAndAttachFieldAsync(type.Id, new("Note", FieldTypes.Text, false));
        RecordDetails ada = await records.CreateRecordAsync(
            type.Id, "Ada Lovelace", [new(note.Id, "first programmer")], ["Ada King"], ["work"]);

        _ = await tagging.ApplyAsync(new([new(ada.Record.Id)], Add: ["mathematician"]));

        // The whole reason this exists rather than routing through the record update: nothing but
        // the tags may move.
        RecordDetails reloaded = (await records.GetRecordAsync(ada.Record.Id))!;
        Assert.Equal("Ada Lovelace", reloaded.Record.DisplayName);
        Assert.Equal(["Ada King"], reloaded.Aliases);
        Assert.Equal("first programmer", reloaded.Values.Single(value => value.FieldDefinitionId == note.Id).TextValue);
        Assert.Equal(["work", "mathematician"], reloaded.Tags);
    }

    [Fact]
    public async Task ARecordThatMovedUnderneathTheOperatorIsReportedWhileTheRestApply()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IRecordTagCommandService tagging = application.Services.GetRequiredService<IRecordTagCommandService>();

        RecordType type = await records.CreateRecordTypeAsync("Contended person");
        RecordDetails ada = await records.CreateRecordAsync(type.Id, "Ada", []);
        RecordDetails grace = await records.CreateRecordAsync(type.Id, "Grace", []);

        // Somebody else renames Ada after the operator read her.
        _ = await records.UpdateRecordAsync(ada.Record.Id, "Ada Lovelace", [], null, ada.Revision);

        IReadOnlyList<RecordTagChange> applied = await tagging.ApplyAsync(new(
            [new(ada.Record.Id, ada.Revision), new(grace.Record.Id, grace.Revision)],
            Add: ["london"]));

        Assert.Equal(RecordTagOutcome.Stale, applied.Single(change => change.RecordId == ada.Record.Id).Outcome);
        Assert.Equal(RecordTagOutcome.Applied, applied.Single(change => change.RecordId == grace.Record.Id).Outcome);

        // The stale one was left exactly as it was; the other went through.
        Assert.Empty((await records.GetRecordAsync(ada.Record.Id))!.Tags);
        Assert.Equal(["london"], (await records.GetRecordAsync(grace.Record.Id))!.Tags);
    }

    [Fact]
    public async Task ARecordTypeWithTagsTurnedOffIsReportedRatherThanSilentlySkipped()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IRecordTagCommandService tagging = application.Services.GetRequiredService<IRecordTagCommandService>();

        RecordType tagless = await records.CreateRecordTypeAsync("Tagless thing");
        await records.UpdateRecordTypeAsync(tagless.Id, tagless.Name, tagless.Symbol, tagsEnabled: false);
        RecordDetails thing = await records.CreateRecordAsync(tagless.Id, "A thing", []);

        RecordTagChange change = Assert.Single(await tagging.ApplyAsync(new([new(thing.Record.Id)], Add: ["london"])));

        Assert.Equal(RecordTagOutcome.TagsDisabled, change.Outcome);
        Assert.Equal("A thing", change.DisplayName);
    }

    [Fact]
    public async Task ATagTypedAnyWhichWayIsStoredWithTheCataloguesOwnSpelling()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IRecordTagCommandService tagging = application.Services.GetRequiredService<IRecordTagCommandService>();

        RecordType type = await records.CreateRecordTypeAsync("Spelled person");
        RecordDetails ada = await records.CreateRecordAsync(type.Id, "Ada", [], null, ["Work"]);
        RecordDetails grace = await records.CreateRecordAsync(type.Id, "Grace", []);

        // Different casing and spacing for a tag the catalogue already knows as "Work".
        _ = await tagging.ApplyAsync(new([new(grace.Record.Id)], Add: ["  work  "]));

        // The catalogue's spelling wins, so the two records agree and searching finds both.
        Assert.Equal(["Work"], (await records.GetRecordAsync(grace.Record.Id))!.Tags);
        Assert.Equal(["Work"], (await records.GetRecordAsync(ada.Record.Id))!.Tags);
    }

    [Fact]
    public async Task ReadingSeveralRecordsTagsIsWhatLetsTheMenuShowWhatTheyShare()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IRecordTagStore tags = application.Services.GetRequiredService<IRecordTagStore>();

        RecordType type = await records.CreateRecordTypeAsync("Shared person");
        RecordDetails ada = await records.CreateRecordAsync(type.Id, "Ada", [], null, ["work", "london"]);
        RecordDetails grace = await records.CreateRecordAsync(type.Id, "Grace", [], null, ["work", "navy"]);
        RecordDetails bare = await records.CreateRecordAsync(type.Id, "Bare", []);

        IReadOnlyDictionary<Guid, IReadOnlyList<string>> held = await tags.ListForRecordsAsync(
            [ada.Record.Id, grace.Record.Id, bare.Record.Id]);

        // Order preserved per record, because that is the order the record itself shows.
        Assert.Equal(["work", "london"], held[ada.Record.Id]);
        Assert.Equal(["work", "navy"], held[grace.Record.Id]);

        // A record with no tags is simply absent rather than present and empty, so the caller has
        // one thing to handle instead of two.
        Assert.False(held.ContainsKey(bare.Record.Id));

        // What the menu calls "on all selected" is the intersection, which here is one tag.
        string[] shared = [.. held[ada.Record.Id].Intersect(held[grace.Record.Id], StringComparer.OrdinalIgnoreCase)];
        Assert.Equal(["work"], shared);
    }

    [Fact]
    public async Task AnEditThatCannotMeanOneThingIsRefusedBeforeAnythingIsWritten()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IRecordTagCommandService tagging = application.Services.GetRequiredService<IRecordTagCommandService>();

        RecordType type = await records.CreateRecordTypeAsync("Refused person");
        RecordDetails ada = await records.CreateRecordAsync(type.Id, "Ada", []);

        // Nothing to do.
        await Assert.ThrowsAsync<DomainValidationException>(() => tagging.ApplyAsync(new([new(ada.Record.Id)])));

        // Nobody to do it to.
        await Assert.ThrowsAsync<DomainValidationException>(() => tagging.ApplyAsync(new([], Add: ["london"])));

        // Both add and remove the same tag: whichever won would depend on an order the caller
        // cannot see.
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            tagging.ApplyAsync(new([new(ada.Record.Id)], Add: ["london"], Remove: ["LONDON"])));

        // One record named twice would carry two expected revisions, the second necessarily stale.
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            tagging.ApplyAsync(new([new(ada.Record.Id), new(ada.Record.Id)], Add: ["london"])));

        // More records than a graph selection can hold.
        await Assert.ThrowsAsync<DomainValidationException>(() => tagging.ApplyAsync(new(
            [.. Enumerable.Range(0, RecordTagCommandService.MaximumRecords + 1)
                .Select(_ => new RecordTagSelection(Guid.CreateVersion7()))],
            Add: ["london"])));

        Assert.Empty((await records.GetRecordAsync(ada.Record.Id))!.Tags);
    }
}
