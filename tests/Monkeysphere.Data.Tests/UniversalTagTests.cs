using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Data.Tests;

/// <summary>
/// Tags belong to the record rather than to its type. These pin the properties that makes them
/// "universal": present without anyone opting in, removable only by an administrator, and never
/// destroyed by that removal.
/// </summary>
public sealed class UniversalTagTests
{
    [Fact]
    public async Task EveryNewRecordTypeHasTagsWithoutAnyoneEnablingThem()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService service = application.Services.GetRequiredService<IMonkeysphereService>();

        RecordType created = await service.CreateRecordTypeAsync("Bare type");
        Assert.True(created.TagsEnabled);

        // A preset-installed type arrives the same way: nothing in the catalogue opts in.
        await application.Services.GetRequiredService<IPresetService>().InstallPresetAsync("monkeysphere.person");
        RecordType person = (await service.ListRecordTypesAsync()).Single(type => type.PresetKey == "monkeysphere.person");
        Assert.True(person.TagsEnabled);
    }

    [Fact]
    public async Task TagsAreTrimmedDeduplicatedCaseInsensitivelyAndKeepTheirOrder()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService service = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordType type = await service.CreateRecordTypeAsync("Tag fixture");

        RecordDetails record = await service.CreateRecordAsync(type.Id, "Tagged", [], null,
            ["  work  ", "Family", "family", "met in person"]);

        Assert.Equal(["work", "Family", "met in person"], record.Tags);
    }

    [Fact]
    public async Task NullTagsPreserveAndAnEmptyListClears()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService service = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordType type = await service.CreateRecordTypeAsync("Preserve fixture");
        RecordDetails record = await service.CreateRecordAsync(type.Id, "Tagged", [], null, ["keep"]);

        // A caller that says nothing about tags — a remote patch, a contact import — must not
        // erase them as a side effect of editing something else.
        RecordDetails renamed = await service.UpdateRecordAsync(record.Record.Id, "Renamed", []);
        Assert.Equal(["keep"], renamed.Tags);

        RecordDetails cleared = await service.UpdateRecordAsync(renamed.Record.Id, "Renamed", [], tags: []);
        Assert.Empty(cleared.Tags);
    }

    [Fact]
    public async Task EditingTagsChangesTheRecordRevision()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService service = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordType type = await service.CreateRecordTypeAsync("Revision fixture");
        RecordDetails record = await service.CreateRecordAsync(type.Id, "Tagged", [], null, ["before"]);

        // Only the tags change here. Without this the stale-edit check, contact-import preview
        // invalidation and twin synchronization would all miss a tag edit.
        RecordDetails retagged = await service.UpdateRecordAsync(
            record.Record.Id, "Tagged", [], expectedRevision: record.Revision, tags: ["after"]);

        Assert.NotEqual(record.Revision, retagged.Revision);
        Assert.Equal(["after"], retagged.Tags);
    }

    [Fact]
    public async Task SearchMatchesTags()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService service = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordType type = await service.CreateRecordTypeAsync("Search fixture");
        await service.CreateRecordAsync(type.Id, "Wholly unrelated name", [], null, ["sailing"]);
        await service.CreateRecordAsync(type.Id, "Another record", [], null, ["climbing"]);

        PagedResult<RecordSummary> found = await service.SearchRecordsAsync(new("sailing"));
        Assert.Equal("Wholly unrelated name", Assert.Single(found.Items).DisplayName);
    }

    [Fact]
    public async Task RemovingTagsFromATypeRetainsTheValuesItHidesAndRefusesNewOnes()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService service = application.Services.GetRequiredService<IMonkeysphereService>();
        IRecordTagStore tags = application.Services.GetRequiredService<IRecordTagStore>();
        RecordType type = await service.CreateRecordTypeAsync("Removable");
        RecordDetails record = await service.CreateRecordAsync(type.Id, "Tagged", [], null, ["retained"]);
        Assert.Equal(1, await tags.CountTaggedRecordsAsync(type.Id));

        await service.UpdateRecordTypeAsync(type.Id, "Removable", null, tagsEnabled: false);
        Assert.False((await service.GetRecordTypeAsync(type.Id))!.RecordType.TagsEnabled);

        // Refused rather than silently accepted: storing a tag that can never be displayed is a trap.
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.UpdateRecordAsync(record.Record.Id, "Tagged", [], tags: ["new"]));

        // The value is still there, and re-enabling restores it intact rather than resurrecting nothing.
        await service.UpdateRecordTypeAsync(type.Id, "Removable", null, tagsEnabled: true);
        Assert.Equal(["retained"], (await service.GetRecordAsync(record.Record.Id))!.Tags);
    }

    [Fact]
    public async Task TheTagsSettingSurvivesAnUnrelatedTypeEdit()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService service = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordType type = await service.CreateRecordTypeAsync("Renamed later");
        await service.UpdateRecordTypeAsync(type.Id, "Renamed later", null, tagsEnabled: false);

        // Null means "not speaking about tags", so renaming must not quietly switch them back on.
        await service.UpdateRecordTypeAsync(type.Id, "A new name", "🧭");
        RecordTypeDetails reloaded = (await service.GetRecordTypeAsync(type.Id))!;
        Assert.False(reloaded.RecordType.TagsEnabled);
        Assert.Equal("A new name", reloaded.RecordType.Name);
    }

    [Fact]
    public async Task TooManyOrOverLongTagsAreRefused()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService service = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordType type = await service.CreateRecordTypeAsync("Bounds");

        await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateRecordAsync(
            type.Id, "Too many", [], null,
            [.. Enumerable.Range(0, RecordTagRules.MaximumCount + 1).Select(index => $"tag-{index}")]));

        await Assert.ThrowsAsync<DomainValidationException>(() => service.CreateRecordAsync(
            type.Id, "Too long", [], null, [new string('x', RecordTagRules.MaximumLength + 1)]));
    }
}
