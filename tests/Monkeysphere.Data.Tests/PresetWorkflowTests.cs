using System.Text;
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
        IDashboardService dashboard = application.Services.GetRequiredService<IDashboardService>();

        Assert.False((await presets.GetSetupStatusAsync()).IsComplete);
        Assert.Equal(15, presets.RecordTypes.Count);
        Assert.All(presets.RecordTypes, preset => Assert.False(string.IsNullOrWhiteSpace(preset.Symbol)));
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
        Assert.Equal("🎮", videoGame.Symbol);
        RecordTypeDetails details = Assert.IsType<RecordTypeDetails>(await records.GetRecordTypeAsync(videoGame.Id));
        Assert.All(details.Fields, field =>
        {
            Assert.Equal("monkeysphere.video-game", field.Definition.PresetKey);
            Assert.NotNull(field.Definition.CanonicalKey);
        });

        RecordType person = Assert.Single(installed, item => item.PresetKey == "monkeysphere.person");
        RecordTypeDetails personDetails = Assert.IsType<RecordTypeDetails>(await records.GetRecordTypeAsync(person.Id));
        FieldDefinition birthday = Assert.Single(personDetails.Fields, field =>
            field.Definition.CanonicalKey == "monkeysphere.person.birthday").Definition;
        DashboardConfiguration dashboardDefaults = await dashboard.GetConfigurationAsync();
        Assert.Equal(person.Id, dashboardDefaults.RecordTypeId);
        Assert.Equal([birthday.Id], dashboardDefaults.RecurringFieldDefinitionIds);

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
        Assert.Equal("📚", book.Symbol);
        await records.RenameRecordTypeAsync(book.Id, "My Library");
        RecordType renamed = Assert.IsType<RecordTypeDetails>(await records.GetRecordTypeAsync(book.Id)).RecordType;
        Assert.Equal("My Library", renamed.Name);
        Assert.Equal("📚", renamed.Symbol);
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

    [Fact]
    public async Task VCardPreviewImportsIdempotentlyPreservesExtensionsExportsAndRollsBackBatches()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IPresetService presets = application.Services.GetRequiredService<IPresetService>();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IVCardService vcards = application.Services.GetRequiredService<IVCardService>();
        await presets.InstallPresetAsync("monkeysphere.person");

        byte[] source = Encoding.UTF8.GetBytes("""
            BEGIN:VCARD
            VERSION:3.0
            FN:Ada Lovelace
            N:Lovelace;Ada;;;
            NICKNAME:Enchantress of Numbers
            EMAIL;TYPE=home:ada@example.test
            item1.TEL;TYPE=cell:+44 1234 567890
            item1.X-ABLabel:iPhone
            TEL;TYPE=work:+44 9876 543210
            BDAY:18151210
            X-MONKEYSPHERE-FAVOURITE:Analytical Engine
            END:VCARD
            """);
        VCardImportPreview preview = await vcards.PreviewAsync(source);
        VCardContactPreview contact = Assert.Single(preview.Contacts);
        Assert.Equal("Ada Lovelace", contact.DisplayName);
        Assert.Equal(3, contact.FieldMappings.Count);
        Assert.True(contact.OpaquePropertyIndexes.Count >= 4);
        Assert.Empty(contact.DuplicateCandidates);

        VCardImportResult imported = await vcards.ApplyAsync(preview, [
            new(contact.Index, VCardImportAction.CreateSeparately),
        ]);
        Assert.Equal(1, imported.Created);
        RecordSummary ada = Assert.Single((await records.SearchRecordsAsync(new("Ada Lovelace", preview.RecordTypeId))).Items);
        RecordDetails details = Assert.IsType<RecordDetails>(await records.GetRecordAsync(ada.Id));
        Assert.Contains("Enchantress of Numbers", details.Aliases);
        Assert.Contains(details.Values, value => value.TextValue == "ada@example.test");
        Assert.Contains(details.Values, value => value.TextValue == "+44 1234 567890");
        Assert.Contains(details.Values, value => value.TemporalValue == "1815-12-10");

        VCardImportPreview duplicate = await vcards.PreviewAsync(source);
        VCardDuplicateCandidate exact = Assert.Single(Assert.Single(duplicate.Contacts).DuplicateCandidates, candidate => candidate.IsExactPriorImport);
        Assert.Equal(ada.Id, exact.RecordId);
        Assert.Equal(VCardImportAction.Skip, Assert.Single(duplicate.Contacts).RecommendedAction);

        VCardImportPreview mergePreview = await vcards.PreviewAsync(Encoding.UTF8.GetBytes("""
            BEGIN:VCARD
            VERSION:4.0
            FN:Ada Lovelace
            NICKNAME:Countess
            EMAIL:new-address@example.test
            URL:https://example.test/ada
            END:VCARD
            """));
        VCardContactPreview mergeContact = Assert.Single(mergePreview.Contacts);
        await vcards.ApplyAsync(mergePreview, [
            new(mergeContact.Index, VCardImportAction.MergeNonConflicting, ada.Id),
        ]);
        details = Assert.IsType<RecordDetails>(await records.GetRecordAsync(ada.Id));
        Assert.Equal("Ada Lovelace", details.Record.DisplayName);
        Assert.Contains("Countess", details.Aliases);
        Assert.Contains(details.Values, value => value.TextValue == "ada@example.test");
        Assert.DoesNotContain(details.Values, value => value.TextValue == "new-address@example.test");
        Assert.Contains(details.Values, value => value.TextValue == "https://example.test/ada");

        VCardImportPreview replacePreview = await vcards.PreviewAsync(Encoding.UTF8.GetBytes("""
            BEGIN:VCARD
            VERSION:4.0
            FN:Augusta King
            EMAIL:final@example.test
            TEL:+44 1234 567890
            END:VCARD
            """));
        VCardContactPreview replaceContact = Assert.Single(replacePreview.Contacts);
        Assert.Contains(replaceContact.DuplicateCandidates, candidate => candidate.RecordId == ada.Id);
        await vcards.ApplyAsync(replacePreview, [
            new(replaceContact.Index, VCardImportAction.ReplaceMappedValues, ada.Id),
        ]);
        details = Assert.IsType<RecordDetails>(await records.GetRecordAsync(ada.Id));
        Assert.Equal("Augusta King", details.Record.DisplayName);
        Assert.Empty(details.Aliases);
        Assert.Contains(details.Values, value => value.TextValue == "final@example.test");
        Assert.Contains(details.Values, value => value.TemporalValue == "1815-12-10");

        byte[] exported = await vcards.ExportAsync([ada.Id]);
        string exportedText = Encoding.UTF8.GetString(exported);
        Assert.Contains("VERSION:4.0", exportedText, StringComparison.Ordinal);
        Assert.Contains("FN:Augusta King", exportedText, StringComparison.Ordinal);
        Assert.Contains("X-MONKEYSPHERE-FAVOURITE:Analytical Engine", exportedText, StringComparison.Ordinal);
        Assert.Contains("ITEM1.X-ABLABEL:iPhone", exportedText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, Assert.Single(VCardParser.Parse(exported)).Named("TEL").Count);

        byte[] batchSource = Encoding.UTF8.GetBytes("""
            BEGIN:VCARD
            VERSION:4.0
            FN:New Contact
            END:VCARD
            BEGIN:VCARD
            VERSION:4.0
            FN:Augusta King
            END:VCARD
            """);
        VCardImportPreview batch = await vcards.PreviewAsync(batchSource);
        VCardContactPreview newContact = batch.Contacts[0];
        VCardContactPreview adaContact = batch.Contacts[1];
        VCardDuplicateCandidate adaCandidate = Assert.Single(adaContact.DuplicateCandidates);
        Assert.True(await records.DeleteRecordAsync(ada.Id));
        await Assert.ThrowsAsync<DomainValidationException>(() => vcards.ApplyAsync(batch, [
            new(newContact.Index, VCardImportAction.CreateSeparately),
            new(adaContact.Index, VCardImportAction.MergeNonConflicting, adaCandidate.RecordId),
        ]));
        Assert.Empty((await records.SearchRecordsAsync(new("New Contact", preview.RecordTypeId))).Items);
    }

    [Fact]
    public async Task VCardPreviewDetectsDuplicatesWithinTheImportAndNormalizesPhoneNumbers()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IPresetService presets = application.Services.GetRequiredService<IPresetService>();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IVCardService vcards = application.Services.GetRequiredService<IVCardService>();
        await presets.InstallPresetAsync("monkeysphere.person");

        VCardImportPreview preview = await vcards.PreviewAsync(Encoding.UTF8.GetBytes("""
            BEGIN:VCARD
            VERSION:4.0
            FN:Sam Example
            EMAIL:sam@example.test
            TEL:+44 1234 567890
            END:VCARD
            BEGIN:VCARD
            VERSION:4.0
            FN:Sam Example
            EMAIL:sam@example.test
            TEL:+44 1234 567890
            END:VCARD
            BEGIN:VCARD
            VERSION:4.0
            FN:Sam Example
            END:VCARD
            """));

        Assert.Empty(preview.Contacts[0].ImportDuplicateCandidates);
        VCardImportDuplicateCandidate exact = Assert.Single(preview.Contacts[1].ImportDuplicateCandidates);
        Assert.True(exact.IsExactCard);
        Assert.True(exact.IsStrongMatch);
        Assert.Equal(0, exact.ContactIndex);
        Assert.Equal(VCardImportAction.Skip, preview.Contacts[1].RecommendedAction);

        VCardImportDuplicateCandidate[] nameOnly = preview.Contacts[2].ImportDuplicateCandidates.ToArray();
        Assert.Equal(2, nameOnly.Length);
        Assert.All(nameOnly, candidate => Assert.False(candidate.IsStrongMatch));
        Assert.Equal(VCardImportAction.CreateSeparately, preview.Contacts[2].RecommendedAction);

        VCardImportResult result = await vcards.ApplyAsync(preview, [
            new(0, preview.Contacts[0].RecommendedAction),
            new(1, preview.Contacts[1].RecommendedAction),
            new(2, preview.Contacts[2].RecommendedAction),
        ]);
        Assert.Equal(2, result.Created);
        Assert.Equal(1, result.Skipped);

        VCardImportPreview normalizedPhone = await vcards.PreviewAsync(Encoding.UTF8.GetBytes("""
            BEGIN:VCARD
            VERSION:4.0
            FN:Different Display Name
            TEL:+44-1234-567890
            END:VCARD
            """));
        VCardContactPreview normalizedContact = Assert.Single(normalizedPhone.Contacts);
        VCardDuplicateCandidate phoneCandidate = Assert.Single(normalizedContact.DuplicateCandidates);
        Assert.Contains(phoneCandidate.Reasons, reason => reason.StartsWith("matching ", StringComparison.Ordinal));
        Assert.Equal(VCardImportAction.MergeNonConflicting, normalizedContact.RecommendedAction);
        Assert.Equal(2, (await records.SearchRecordsAsync(new("Sam Example", preview.RecordTypeId))).TotalCount);
    }
}
