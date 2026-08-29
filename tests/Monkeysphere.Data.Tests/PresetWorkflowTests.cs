using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Data.Tests;

public sealed class PresetWorkflowTests
{
    [Fact]
    public async Task StarterPackInstallsSelectedConcretePresetsAndRelationshipsTransactionally()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IPresetService presets = application.Services.GetRequiredService<IPresetService>();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IRelationshipService relationships = application.Services.GetRequiredService<IRelationshipService>();

        Assert.False((await presets.GetSetupStatusAsync()).IsComplete);
        Assert.Equal(15, presets.RecordTypes.Count);
        Assert.DoesNotContain(presets.RecordTypes, item => item.Name == "Thing");
        RecordTypePreset homePreset = Assert.Single(presets.RecordTypes, item => item.Key == "monkeysphere.home");
        Assert.Equal(2, homePreset.Version);
        Assert.Equal(FieldTypes.Location, Assert.Single(homePreset.Fields, field => field.CanonicalKey == "monkeysphere.home.location").TypeId);
        Assert.DoesNotContain(homePreset.Fields, field => field.CanonicalKey == "monkeysphere.home.approximation-radius-km");
        StarterPack everyday = Assert.Single(presets.StarterPacks, item => item.Key == "everyday");
        Assert.Contains("the family car", everyday.ExampleItems);
        Assert.Contains("a favourite video game", everyday.ExampleItems);

        await presets.CompleteSetupAsync("everyday", [
            "monkeysphere.person",
            "monkeysphere.vehicle",
            "monkeysphere.video-game",
        ]);

        SetupStatus status = await presets.GetSetupStatusAsync();
        Assert.True(status.IsComplete);
        Assert.Equal("everyday", status.StarterPackKey);
        IReadOnlyList<RecordType> installed = await records.ListRecordTypesAsync();
        Assert.Equal(["Person", "Vehicle", "Video Game"], installed.Select(item => item.Name).Order());
        RecordType videoGame = Assert.Single(installed, item => item.PresetKey == "monkeysphere.video-game");
        Assert.Equal(1, videoGame.PresetVersion);
        RecordTypeDetails details = Assert.IsType<RecordTypeDetails>(await records.GetRecordTypeAsync(videoGame.Id));
        Assert.All(details.Fields, field =>
        {
            Assert.Equal("monkeysphere.video-game", field.Definition.PresetKey);
            Assert.NotNull(field.Definition.CanonicalKey);
        });

        IReadOnlyList<RelationshipType> relationshipTypes = await relationships.ListTypesAsync();
        Assert.Contains(relationshipTypes, item => item.PresetKey == "monkeysphere.relationship.owns");
        Assert.Contains(relationshipTypes, item => item.PresetKey == "monkeysphere.relationship.played");
        Assert.Contains(relationshipTypes, item => item.PresetKey == "monkeysphere.relationship.completed");
        Assert.DoesNotContain(relationshipTypes, item => item.PresetKey == "monkeysphere.relationship.read");
        await Assert.ThrowsAsync<DomainValidationException>(() => presets.CompleteSetupAsync("blank", []));
    }

    [Fact]
    public async Task BlankSlateIsPersistedAsACompletedChoice()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IPresetService presets = application.Services.GetRequiredService<IPresetService>();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();

        await presets.CompleteSetupAsync("blank", []);

        SetupStatus status = await presets.GetSetupStatusAsync();
        Assert.True(status.IsComplete);
        Assert.Equal("blank", status.StarterPackKey);
        Assert.Empty(await records.ListRecordTypesAsync());
    }

    [Fact]
    public async Task CatalogueInstallationCreatesAnEditableVersionedLocalType()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IPresetService presets = application.Services.GetRequiredService<IPresetService>();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();

        await presets.InstallPresetAsync("monkeysphere.book");

        RecordType book = Assert.Single(await records.ListRecordTypesAsync());
        Assert.Equal("Book", book.Name);
        Assert.Equal("monkeysphere.book", book.PresetKey);
        await records.RenameRecordTypeAsync(book.Id, "My Library");
        Assert.Equal("My Library", (await records.GetRecordTypeAsync(book.Id))?.RecordType.Name);
        await Assert.ThrowsAsync<DomainValidationException>(() => presets.InstallPresetAsync("monkeysphere.book"));
    }

    [Fact]
    public async Task StructuredPlacePresetInstallsAsVersionTwoWithoutChangingItsIdentity()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IPresetService presets = application.Services.GetRequiredService<IPresetService>();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();

        await presets.InstallPresetAsync("monkeysphere.home");

        RecordType home = Assert.Single(await records.ListRecordTypesAsync());
        Assert.Equal("monkeysphere.home", home.PresetKey);
        Assert.Equal(2, home.PresetVersion);
        RecordTypeDetails details = Assert.IsType<RecordTypeDetails>(await records.GetRecordTypeAsync(home.Id));
        RecordTypeField location = Assert.Single(details.Fields, field =>
            field.Definition.CanonicalKey == "monkeysphere.home.location");
        Assert.Equal(FieldTypes.Location, location.Definition.TypeId);
        Assert.Equal(2, location.Definition.PresetVersion);
        Assert.DoesNotContain(details.Fields, field =>
            field.Definition.CanonicalKey == "monkeysphere.home.approximation-radius-km");
    }
}
