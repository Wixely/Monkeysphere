using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Data.Tests;

/// <summary>
/// The tag catalogue is the one thing that deliberately spans domains. These pin the behaviour
/// that makes that worth doing — one label is one tag everywhere — and the boundaries that keep it
/// from becoming a way to read across domains.
/// </summary>
public sealed class TagCatalogueTests
{
    [Fact]
    public async Task ANewTagGetsALegibleColourAndTheDefaultIcon()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        ITagCatalogue catalogue = application.Services.GetRequiredService<ITagCatalogue>();

        TagDefinition tag = await catalogue.EnsureAsync("sailing", MonkeysphereDomains.DefaultId);

        Assert.Equal(TagAppearance.DefaultIcon, tag.Icon);
        Assert.Matches("^#[0-9a-f]{6}$", tag.Colour);
        Assert.Equal([MonkeysphereDomains.DefaultId], tag.DomainIds);
    }

    [Fact]
    public async Task TypingAKnownLabelInAnotherDomainEnablesTheSameTagRatherThanCreatingASecond()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IDomainRegistry registry = application.Services.GetRequiredService<IDomainRegistry>();
        ITagCatalogue catalogue = application.Services.GetRequiredService<ITagCatalogue>();
        MonkeysphereDomain second = await registry.CreateAsync("Second sphere");

        TagDefinition first = await catalogue.EnsureAsync("london", MonkeysphereDomains.DefaultId);
        // Different casing on purpose: one label is one tag whatever was typed.
        TagDefinition again = await catalogue.EnsureAsync("LONDON", second.Id);

        Assert.Equal(first.Id, again.Id);
        Assert.Equal("london", again.Name);
        Assert.Equal([first.Id], (await catalogue.ListAsync()).Select(tag => tag.Id).ToArray());
        Assert.Contains(MonkeysphereDomains.DefaultId, again.DomainIds);
        Assert.Contains(second.Id, again.DomainIds);
    }

    [Fact]
    public async Task RecordsStoreTheCataloguesSpellingRatherThanWhatWasTyped()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        ITagCatalogue catalogue = application.Services.GetRequiredService<ITagCatalogue>();
        await catalogue.EnsureAsync("book club", MonkeysphereDomains.DefaultId);

        RecordType type = await records.CreateRecordTypeAsync("Person");
        RecordDetails record = await records.CreateRecordAsync(type.Id, "Mira", [], null, ["Book Club"]);

        Assert.Equal(["book club"], record.Tags);
    }

    [Fact]
    public async Task AnotherDomainsTagIsNotOfferedAsASuggestion()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IDomainRegistry registry = application.Services.GetRequiredService<IDomainRegistry>();
        ITagCatalogue catalogue = application.Services.GetRequiredService<ITagCatalogue>();
        MonkeysphereDomain second = await registry.CreateAsync("Second sphere");
        await catalogue.EnsureAsync("only-over-there", second.Id);
        await catalogue.EnsureAsync("here", MonkeysphereDomains.DefaultId);

        // Suggestions are the surface that would otherwise disclose what another sphere contains.
        IReadOnlyList<string> offered = await application.Services
            .GetRequiredService<IRecordTagStore>().ListVocabularyAsync();

        Assert.Equal(["here"], offered);
    }

    [Fact]
    public async Task RenamingReachesTheRecordsOfEveryDomainThatUsesTheTag()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        ITagCatalogue catalogue = application.Services.GetRequiredService<ITagCatalogue>();
        RecordType type = await records.CreateRecordTypeAsync("Person");
        RecordDetails record = await records.CreateRecordAsync(type.Id, "Ada", [], null, ["londres"]);
        TagDefinition tag = (await catalogue.ListAsync()).Single();

        await catalogue.RenameAsync(tag.Id, "london", tag.Revision);

        // The catalogue changes at once; the record follows when the queue is drained, which is
        // what "eventually consistent, never atomic across domains" means in practice.
        Assert.Equal(["londres"], (await records.GetRecordAsync(record.Record.Id))!.Tags);
        await application.Services.GetRequiredService<ITagMaintenance>().DrainRenamesAsync();
        Assert.Equal(["london"], (await records.GetRecordAsync(record.Record.Id))!.Tags);
    }

    [Fact]
    public async Task RenamingOntoAnExistingTagIsRefusedRatherThanMerging()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        ITagCatalogue catalogue = application.Services.GetRequiredService<ITagCatalogue>();
        TagDefinition first = await catalogue.EnsureAsync("work", MonkeysphereDomains.DefaultId);
        _ = await catalogue.EnsureAsync("navy", MonkeysphereDomains.DefaultId);

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            catalogue.RenameAsync(first.Id, "navy", first.Revision));
    }

    [Fact]
    public async Task RemovingADomainStripsTheTagFromItsRecords()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        ITagCatalogue catalogue = application.Services.GetRequiredService<ITagCatalogue>();
        RecordType type = await records.CreateRecordTypeAsync("Person");
        RecordDetails record = await records.CreateRecordAsync(type.Id, "Grace", [], null, ["navy", "mentor"]);
        TagDefinition navy = (await catalogue.ListAsync()).Single(tag => tag.Name == "navy");

        // The count is what the page shows before doing this, because it cannot be undone.
        Assert.Equal(1, (await catalogue.CountUsageAsync(navy.Id))[MonkeysphereDomains.DefaultId]);

        await catalogue.SetDomainsAsync(navy.Id, [], navy.Revision);

        Assert.Equal(["mentor"], (await records.GetRecordAsync(record.Record.Id))!.Tags);
    }

    [Fact]
    public async Task DeletingATagRemovesItFromRecordsEverywhere()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        ITagCatalogue catalogue = application.Services.GetRequiredService<ITagCatalogue>();
        RecordType type = await records.CreateRecordTypeAsync("Person");
        RecordDetails record = await records.CreateRecordAsync(type.Id, "Charles", [], null, ["inventor"]);
        TagDefinition tag = (await catalogue.ListAsync()).Single();

        await catalogue.DeleteAsync(tag.Id, tag.Revision);

        Assert.Empty((await records.GetRecordAsync(record.Record.Id))!.Tags);
        Assert.Empty(await catalogue.ListAsync());
    }

    [Fact]
    public async Task AppearanceIsValidatedAndRevisionChecked()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        ITagCatalogue catalogue = application.Services.GetRequiredService<ITagCatalogue>();
        TagDefinition tag = await catalogue.EnsureAsync("weekly", MonkeysphereDomains.DefaultId);

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            catalogue.SetAppearanceAsync(tag.Id, "not-a-colour", "#", tag.Revision));

        TagDefinition updated = await catalogue.SetAppearanceAsync(tag.Id, "#4F7FD0", "⚽", tag.Revision);
        Assert.Equal("#4f7fd0", updated.Colour);
        Assert.Equal("⚽", updated.Icon);

        // The stale revision must no longer work.
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            catalogue.SetAppearanceAsync(tag.Id, "#000000", "#", tag.Revision));
    }

    [Fact]
    public async Task ExistingTagsAreAdoptedIntoTheCatalogueOnStartup()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        ITagCatalogue catalogue = application.Services.GetRequiredService<ITagCatalogue>();
        RecordType type = await records.CreateRecordTypeAsync("Person");
        _ = await records.CreateRecordAsync(type.Id, "Tomás", [], null, ["neighbour"]);

        // Simulates a domain whose rows predate the catalogue: the identity is cleared, leaving
        // only the text, which is exactly what a database migrated from before 33 looks like.
        await using (Microsoft.Data.Sqlite.SqliteConnection connection =
            await application.Services.GetRequiredService<MonkeysphereConnectionFactory>().OpenConnectionAsync())
        {
            await using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE RecordTags SET TagId = NULL;";
            await command.ExecuteNonQueryAsync();
        }

        Assert.NotEqual(0, await application.Services.GetRequiredService<ITagMaintenance>().AdoptExistingTagsAsync());
        Assert.Contains(await catalogue.ListAsync(), tag => tag.Name == "neighbour");
    }

    [Fact]
    public async Task ARenameThatOnlyChangesCaseStillReachesTheRecords()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        ITagCatalogue catalogue = application.Services.GetRequiredService<ITagCatalogue>();
        RecordType type = await records.CreateRecordTypeAsync("Person");
        RecordDetails record = await records.CreateRecordAsync(type.Id, "Ada", [], null, ["london"]);
        TagDefinition tag = (await catalogue.ListAsync()).Single();

        // The stored column is NOCASE, so a guard comparing under that collation finds nothing to
        // do here and would drop the queued work as though it had been applied.
        await catalogue.RenameAsync(tag.Id, "London", tag.Revision);
        await application.Services.GetRequiredService<ITagMaintenance>().DrainRenamesAsync();

        Assert.Equal(["London"], (await records.GetRecordAsync(record.Record.Id))!.Tags);
    }

    [Fact]
    public async Task ATagCannotBeAttachedToADomainThatDoesNotExist()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        ITagCatalogue catalogue = application.Services.GetRequiredService<ITagCatalogue>();

        // An unchecked identifier would leave a membership row pointing at nothing, which no
        // cascade removes because the registry holds no foreign key to Domains here.
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            catalogue.EnsureAsync("orphan", Guid.CreateVersion7()));
        Assert.Empty(await catalogue.ListAsync());
    }
}
