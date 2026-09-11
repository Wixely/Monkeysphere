using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Data.Tests;

/// <summary>
/// The shape of a real Android/Google export: a structured name, a photo referenced by web
/// address, categories, and an HTC note block. Before enrichment every one of these was either
/// dropped or, in the note's case, imported as markup into the notes field.
/// </summary>
public sealed class ContactEnrichmentImportTests
{
    private const string Card = """
        BEGIN:VCARD
        VERSION:3.0
        FN:Test Contact
        N:Surname;Given;;;
        TEL;TYPE=CELL:+33600000000
        NOTE:<HTCData><Facebook>id\:9900112233/friendof\:4455667788</Facebook></HTCData>
        PHOTO:https://photos.example.test/contacts/abc123
        CATEGORIES:Imported 07/04/2012 1,myContacts
        ORG:Acme Ltd;Research
        TITLE:Lead Engineer
        ADR;TYPE=home:;;12 Rue de la Paix;Paris;;75002;France
        END:VCARD

        """;

    private static async Task<TestApplication> StartAsync()
    {
        TestApplication application = await TestApplication.CreateAsync();
        await application.Services.GetRequiredService<IPresetService>().InstallPresetAsync("monkeysphere.person");
        return application;
    }

    [Fact]
    public async Task AnUnenrichedImportOffersEveryFieldTheCardCarriesWithoutCreatingAny()
    {
        await using TestApplication application = await StartAsync();
        IVCardService vcards = application.Services.GetRequiredService<IVCardService>();
        VCardImportPreview preview = await vcards.PreviewAsync(Encoding.UTF8.GetBytes(Card));

        string[] offered = preview.EnrichmentOffers.Select(offer => offer.Key).ToArray();
        Assert.Contains(ContactEnrichments.Photo, offered);
        Assert.Contains(ContactEnrichments.Categories, offered);
        Assert.Contains(ContactEnrichments.Address, offered);
        Assert.Contains(ContactEnrichments.Organisation, offered);
        Assert.Contains(ContactEnrichments.JobTitle, offered);
        Assert.Contains(ContactEnrichments.FamilyName, offered);
        Assert.Contains(ContactEnrichments.VendorFacebook, offered);

        // Nothing is created merely by previewing, so every field-backed offer is still pending.
        Assert.All(preview.EnrichmentOffers.Where(offer => !offer.CreatesRecordImage),
            offer => Assert.False(offer.FieldExists, offer.Key));
        Assert.All(preview.EnrichmentOffers, offer => Assert.Equal(1, offer.ContactCount));

        // The photo offer reports that acting on it would need a network request.
        VCardEnrichmentOffer photo = preview.EnrichmentOffers.Single(offer => offer.Key == ContactEnrichments.Photo);
        Assert.True(photo.CreatesRecordImage);
        Assert.Equal(1, photo.RemotePhotoCount);
    }

    // The note is tidied whether or not anything is enabled: importing markup nobody wrote is a
    // defect on its own, and the block is still retained as source material.
    [Fact]
    public async Task AVendorBlockNeverReachesTheNotesFieldEvenWithNothingEnabled()
    {
        await using TestApplication application = await StartAsync();
        IVCardService vcards = application.Services.GetRequiredService<IVCardService>();
        VCardImportPreview preview = await vcards.PreviewAsync(Encoding.UTF8.GetBytes(Card));
        VCardContactPreview contact = Assert.Single(preview.Contacts);

        Assert.True(contact.NoteWasTidied);
        Assert.DoesNotContain(contact.FieldMappings, mapping => mapping.FieldName == "Notes");

        await vcards.ApplyAsync(preview, [new(contact.Index, VCardImportAction.CreateSeparately)]);
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordSummary summary = Assert.Single((await records.SearchRecordsAsync(new("Test Contact"))).Items);
        RecordDetails saved = (await records.GetRecordAsync(summary.Id))!;
        Assert.DoesNotContain(saved.Values, value => value.TextValue is string text && text.Contains("HTCData", StringComparison.Ordinal));

        // Retained verbatim, so nothing is actually lost by keeping it out of the note.
        RecordSourceSnapshot source = (await application.Services.GetRequiredService<IRecordSourceService>()
            .GetAsync(summary.Id))!;
        RecordSourceValue note = Assert.Single(source.Values, value => value.Name == "NOTE");
        Assert.Contains("HTCData", note.ValuePreview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnablingEnrichmentsCreatesTheFieldsAndTheNextPreviewFillsThem()
    {
        await using TestApplication application = await StartAsync();
        IVCardService vcards = application.Services.GetRequiredService<IVCardService>();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();

        string[] enabled =
        [
            ContactEnrichments.Categories, ContactEnrichments.Address, ContactEnrichments.Organisation,
            ContactEnrichments.JobTitle, ContactEnrichments.FamilyName, ContactEnrichments.GivenName,
            ContactEnrichments.VendorFacebook,
        ];
        IReadOnlyList<FieldDefinition> created = await vcards.EnableEnrichmentsAsync(enabled);
        Assert.Equal(enabled.Length, created.Count);
        Assert.All(created, field => Assert.StartsWith("monkeysphere.person.", field.CanonicalKey!, StringComparison.Ordinal));

        // Enabling is idempotent: a second import must not produce a second Categories field.
        Assert.Empty(await vcards.EnableEnrichmentsAsync(enabled));

        VCardImportPreview preview = await vcards.PreviewAsync(Encoding.UTF8.GetBytes(Card));
        Assert.All(preview.EnrichmentOffers.Where(offer => !offer.CreatesRecordImage),
            offer => Assert.True(offer.FieldExists, offer.Key));

        VCardContactPreview contact = Assert.Single(preview.Contacts);
        await vcards.ApplyAsync(preview, [new(contact.Index, VCardImportAction.CreateSeparately)]);

        RecordSummary summary = Assert.Single((await records.SearchRecordsAsync(new("Test Contact"))).Items);
        RecordDetails saved = (await records.GetRecordAsync(summary.Id))!;
        string? Value(string fieldName) => saved.Values
            .FirstOrDefault(value => value.FieldName == fieldName)?.TextValue;

        Assert.Equal("Surname", Value("Family name"));
        Assert.Equal("Given", Value("Given name"));
        Assert.Equal("Acme Ltd", Value("Organisation"));
        Assert.Equal("Lead Engineer", Value("Job title"));
        Assert.Equal("9900112233", Value("Facebook ID"));
        Assert.Contains("12 Rue de la Paix", Value("Address")!, StringComparison.Ordinal);

        RecordValue categories = Assert.Single(saved.Values, value => value.FieldName == "Categories");
        Assert.Equal(["Imported 07/04/2012 1", "myContacts"], categories.Tags);

        // The enriched values are searchable, which is the point of making them fields.
        Assert.Single((await records.SearchRecordsAsync(new("Acme Ltd"))).Items);
    }

    [Fact]
    public async Task EnablingAnUnknownEnrichmentIsRefused()
    {
        await using TestApplication application = await StartAsync();
        IVCardService vcards = application.Services.GetRequiredService<IVCardService>();
        await Assert.ThrowsAsync<DomainValidationException>(() => vcards.EnableEnrichmentsAsync(["not-a-kind"]));

        // The canonical key belongs to the catalogue, so a caller cannot bind a field to any key.
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordType person = (await records.ListRecordTypesAsync()).Single(type => type.PresetKey == "monkeysphere.person");
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            records.CreateEnrichmentFieldAsync(person.Id, "Sneaky", FieldTypes.Text, "monkeysphere.person.email"));
    }
}
