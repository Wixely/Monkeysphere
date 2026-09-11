using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Data.Tests;

/// <summary>
/// The point of retaining source material is that a decision made later can still act on it. These
/// import contacts with nothing enabled, then recover the fields afterwards without the file.
/// </summary>
public sealed class ContactBackfillTests
{
    private static string Card(int index) => $"""
        BEGIN:VCARD
        VERSION:3.0
        FN:Contact {index:D2}
        N:Surname{index:D2};Given{index:D2};;;
        ORG:Acme Ltd;Research
        CATEGORIES:myContacts,Imported
        NOTE:<HTCData><Facebook>id\:100{index:D2}</Facebook></HTCData>
        END:VCARD

        """;

    private static async Task<TestApplication> ImportedAsync(int count)
    {
        TestApplication application = await TestApplication.CreateAsync();
        await application.Services.GetRequiredService<IPresetService>().InstallPresetAsync("monkeysphere.person");
        IVCardService vcards = application.Services.GetRequiredService<IVCardService>();

        string file = string.Concat(Enumerable.Range(0, count).Select(Card));
        VCardImportPreview preview = await vcards.PreviewAsync(Encoding.UTF8.GetBytes(file));
        await vcards.ApplyAsync(preview,
            [.. preview.Contacts.Select(c => new VCardImportSelection(c.Index, VCardImportAction.CreateSeparately))]);
        return application;
    }

    [Fact]
    public async Task FieldsCanBeCreatedAndFilledLongAfterTheImportWithoutTheFile()
    {
        await using TestApplication application = await ImportedAsync(3);
        IContactEnrichmentBackfill backfill = application.Services.GetRequiredService<IContactEnrichmentBackfill>();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();

        ContactBackfillPreview preview = await backfill.PreviewAsync();
        Assert.Equal(3, preview.RecordsWithSource);
        ContactBackfillOffer organisation = preview.Offers.Single(offer => offer.Key == ContactEnrichments.Organisation);
        Assert.False(organisation.FieldExists);
        Assert.Equal(3, organisation.RecordCount);
        Assert.Equal(0, organisation.AlreadyFilledCount);

        // A photo has nothing to fill from retained material, so it is never offered here.
        Assert.DoesNotContain(preview.Offers, offer => offer.Key == ContactEnrichments.Photo);

        ContactBackfillResult result = await backfill.ApplyAsync(
            [ContactEnrichments.Organisation, ContactEnrichments.Categories, ContactEnrichments.FamilyName,
             ContactEnrichments.VendorFacebook]);

        Assert.Equal(4, result.FieldsCreated);
        Assert.Equal(3, result.RecordsUpdated);
        Assert.Equal(12, result.ValuesWritten);

        RecordSummary first = Assert.Single((await records.SearchRecordsAsync(new("Contact 00"))).Items);
        RecordDetails saved = (await records.GetRecordAsync(first.Id))!;
        Assert.Equal("Acme Ltd", saved.Values.Single(v => v.FieldName == "Organisation").TextValue);
        Assert.Equal("Surname00", saved.Values.Single(v => v.FieldName == "Family name").TextValue);
        Assert.Equal("10000", saved.Values.Single(v => v.FieldName == "Facebook ID").TextValue);
        Assert.Equal(["myContacts", "Imported"], saved.Values.Single(v => v.FieldName == "Categories").Tags);

        // Filled values are ordinary record data, so they are searchable like anything else.
        Assert.Equal(3, (await records.SearchRecordsAsync(new("Acme Ltd"))).TotalCount);
    }

    [Fact]
    public async Task RunningTheBackfillAgainChangesNothing()
    {
        await using TestApplication application = await ImportedAsync(2);
        IContactEnrichmentBackfill backfill = application.Services.GetRequiredService<IContactEnrichmentBackfill>();

        await backfill.ApplyAsync([ContactEnrichments.Organisation]);
        ContactBackfillResult again = await backfill.ApplyAsync([ContactEnrichments.Organisation]);

        Assert.Equal(0, again.FieldsCreated);
        Assert.Equal(0, again.RecordsUpdated);
        Assert.Equal(0, again.ValuesWritten);

        ContactBackfillPreview preview = await backfill.PreviewAsync();
        ContactBackfillOffer organisation = preview.Offers.Single(offer => offer.Key == ContactEnrichments.Organisation);
        Assert.True(organisation.FieldExists);
        Assert.Equal(2, organisation.AlreadyFilledCount);
    }

    // A value somebody has since typed is theirs. The backfill adds what was lost, never overwrites.
    [Fact]
    public async Task AValueEnteredByHandIsNotOverwritten()
    {
        await using TestApplication application = await ImportedAsync(1);
        IContactEnrichmentBackfill backfill = application.Services.GetRequiredService<IContactEnrichmentBackfill>();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();

        await backfill.ApplyAsync([ContactEnrichments.Organisation]);
        RecordSummary summary = Assert.Single((await records.SearchRecordsAsync(new("Contact 00"))).Items);
        RecordDetails saved = (await records.GetRecordAsync(summary.Id))!;
        Guid organisationField = saved.Values.Single(v => v.FieldName == "Organisation").FieldDefinitionId;

        await records.UpdateRecordAsync(summary.Id, saved.Record.DisplayName,
            [new FieldValueInput(organisationField, "Corrected By Hand")], saved.Aliases, saved.Revision);

        ContactBackfillResult again = await backfill.ApplyAsync([ContactEnrichments.Organisation]);
        Assert.Equal(0, again.ValuesWritten);

        RecordDetails after = (await records.GetRecordAsync(summary.Id))!;
        Assert.Equal("Corrected By Hand", after.Values.Single(v => v.FieldName == "Organisation").TextValue);
    }

    [Fact]
    public async Task ARecordTypedInByHandIsUntouchedBecauseItHasNoSourceMaterial()
    {
        await using TestApplication application = await ImportedAsync(1);
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordType person = (await records.ListRecordTypesAsync()).Single(t => t.PresetKey == "monkeysphere.person");
        RecordDetails manual = await records.CreateRecordAsync(person.Id, "Typed By Hand", []);

        IContactEnrichmentBackfill backfill = application.Services.GetRequiredService<IContactEnrichmentBackfill>();
        Assert.Equal(1, (await backfill.PreviewAsync()).RecordsWithSource);
        await backfill.ApplyAsync([ContactEnrichments.Organisation]);

        RecordDetails after = (await records.GetRecordAsync(manual.Record.Id))!;
        Assert.Empty(after.Values);
    }

    [Fact]
    public async Task AskingToFillSomethingThatIsNotAFieldIsRefused()
    {
        await using TestApplication application = await ImportedAsync(1);
        IContactEnrichmentBackfill backfill = application.Services.GetRequiredService<IContactEnrichmentBackfill>();

        await Assert.ThrowsAsync<DomainValidationException>(() => backfill.ApplyAsync([ContactEnrichments.Photo]));
        await Assert.ThrowsAsync<DomainValidationException>(() => backfill.ApplyAsync(["not-a-kind"]));
    }
}
