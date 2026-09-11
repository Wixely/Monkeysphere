using System.Text;
using Monkeysphere.Core;

namespace Monkeysphere.Core.Tests;

/// <summary>
/// The shapes here are taken from what real exports actually write: Google references photos by
/// web address, HTC phones bury a markup block in the note, and structured values arrive with
/// their separators escaped. Each is a way an import silently loses or corrupts information.
/// </summary>
public sealed class ContactEnrichmentTests
{
    private static VCard Parse(string body) =>
        VCardParser.Parse(Encoding.UTF8.GetBytes("BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Test Person\r\n" + body + "END:VCARD\r\n")).Cards[0];

    [Fact]
    public void AnHtcNoteBlockIsTakenOutOfTheNoteAndItsIdentifierRecovered()
    {
        // The padding and escaped colons are exactly how an HTC export writes this.
        const string note = @"<HTCData><Facebook>id\:9900112233/friendof\:4455667788<<<<<<<<<<</Facebook></HTCData>";
        VCard card = Parse($"NOTE:{note}\r\n");
        string raw = card.Named("NOTE")[0].TextValue;

        Assert.True(ContactEnrichments.NoteCarriesVendorBlock(raw));
        Assert.Equal(string.Empty, ContactEnrichments.CleanNote(raw));
        Assert.Equal("9900112233", ContactEnrichments.Extract(ContactEnrichments.VendorFacebook, card)!.ScalarValue);
    }

    [Fact]
    public void ANoteThatAlsoCarriesRealTextKeepsOnlyTheRealText()
    {
        VCard card = Parse("NOTE:Met at the conference.<HTCData><Facebook>id\\:42</Facebook></HTCData>\r\n");
        string raw = card.Named("NOTE")[0].TextValue;

        Assert.Equal("Met at the conference.", ContactEnrichments.CleanNote(raw));
        Assert.Equal("42", ContactEnrichments.Extract(ContactEnrichments.VendorFacebook, card)!.ScalarValue);
    }

    [Fact]
    public void AnOrdinaryNoteIsLeftExactlyAsWritten()
    {
        VCard card = Parse("NOTE:Likes cycling. Allergic to shellfish.\r\n");
        string raw = card.Named("NOTE")[0].TextValue;

        Assert.False(ContactEnrichments.NoteCarriesVendorBlock(raw));
        Assert.Equal("Likes cycling. Allergic to shellfish.", ContactEnrichments.CleanNote(raw));
        Assert.Null(ContactEnrichments.Extract(ContactEnrichments.VendorFacebook, card));
    }

    [Fact]
    public void APhotoReferencedByWebAddressIsReportedAsRemoteAndNotAsData()
    {
        VCard card = Parse("PHOTO:https://photos.example.test/contacts/abc123\r\n");
        ContactPhotoCandidate photo = Assert.Single(ContactEnrichments.Photos(card));

        Assert.True(photo.IsRemote);
        Assert.Equal("https://photos.example.test/contacts/abc123", photo.RemoteUrl);
        Assert.Null(photo.InlineBase64);
    }

    [Theory]
    [InlineData("PHOTO;ENCODING=b;TYPE=PNG:aVZCT1J3", "image/png-ish")]
    [InlineData("PHOTO;ENCODING=BASE64:aVZCT1J3", "declared or not")]
    public void APhotoCarriedInTheCardIsReportedAsDataNeedingNoNetwork(string line, string _)
    {
        ContactPhotoCandidate photo = Assert.Single(ContactEnrichments.Photos(Parse(line + "\r\n")));

        Assert.False(photo.IsRemote);
        Assert.Equal("aVZCT1J3", photo.InlineBase64);
    }

    // vCard 4.0 writes inline data as a data: URI rather than with an ENCODING parameter.
    [Fact]
    public void AVersionFourDataUriPhotoIsTreatedAsDataNotAsAWebAddress()
    {
        ContactPhotoCandidate photo = Assert.Single(
            ContactEnrichments.Photos(Parse("PHOTO:data:image/jpeg;base64,aVZCT1J3\r\n")));

        Assert.False(photo.IsRemote);
        Assert.Equal("aVZCT1J3", photo.InlineBase64);
        Assert.Equal("image/jpeg", photo.DeclaredType);
    }

    [Fact]
    public void CategoriesBecomeTagsAndDropDuplicatesAndBlanks()
    {
        VCard card = Parse("CATEGORIES:Imported 07/04/2012 1,myContacts,,myContacts\r\n");
        ContactEnrichmentValue value = ContactEnrichments.Extract(ContactEnrichments.Categories, card)!;

        Assert.Equal(["Imported 07/04/2012 1", "myContacts"], value.Tags);
    }

    [Fact]
    public void StructuredNameAndOrganisationTakeTheRightComponents()
    {
        VCard card = Parse("N:Beaumont;Camille;Marie;Dr;PhD\r\nORG:Acme Ltd;Research;Optics\r\nTITLE:Lead Engineer\r\n");

        Assert.Equal("Beaumont", ContactEnrichments.Extract(ContactEnrichments.FamilyName, card)!.ScalarValue);
        Assert.Equal("Camille", ContactEnrichments.Extract(ContactEnrichments.GivenName, card)!.ScalarValue);
        Assert.Equal("Acme Ltd", ContactEnrichments.Extract(ContactEnrichments.Organisation, card)!.ScalarValue);
        Assert.Equal("Lead Engineer", ContactEnrichments.Extract(ContactEnrichments.JobTitle, card)!.ScalarValue);
    }

    [Fact]
    public void AnAddressIsWrittenOutWithoutInventingComponentsItDoesNotHave()
    {
        VCard card = Parse("ADR;TYPE=home:;;12 Rue de la Paix;Paris;;75002;France\r\n");
        string address = ContactEnrichments.Extract(ContactEnrichments.Address, card)!.ScalarValue!;

        Assert.Equal(["12 Rue de la Paix", "Paris 75002", "France"], address.Split(Environment.NewLine));
    }

    [Fact]
    public void SocialHandlesCombineBothPropertiesRealExportsUse()
    {
        VCard card = Parse("IMPP:xmpp:ada@example.test\r\nX-SOCIALPROFILE;TYPE=mastodon:https://example.test/@ada\r\n");
        ContactEnrichmentValue value = ContactEnrichments.Extract(ContactEnrichments.SocialHandles, card)!;

        Assert.Equal(["xmpp:ada@example.test", "https://example.test/@ada"], value.Tags);
    }

    [Fact]
    public void DetectionReportsOnlyWhatTheCardCanActuallyProduce()
    {
        VCard sparse = Parse("NOTE:Just a note.\r\n");
        Assert.Empty(ContactEnrichments.Detect(sparse));

        VCard rich = Parse("N:Beaumont;Camille;;;\r\nCATEGORIES:myContacts\r\nPHOTO:https://example.test/a.jpg\r\n");
        Assert.Equal(
            [ContactEnrichments.Photo, ContactEnrichments.Categories, ContactEnrichments.FamilyName, ContactEnrichments.GivenName],
            ContactEnrichments.Detect(rich));
    }

    [Fact]
    public void EveryFieldProducingEnrichmentDeclaresWhatItWouldCreate()
    {
        foreach (ContactEnrichmentKind kind in ContactEnrichments.All.Where(k => k.Outcome == ContactEnrichmentOutcome.Field))
        {
            Assert.False(string.IsNullOrWhiteSpace(kind.FieldName), kind.Key);
            Assert.False(string.IsNullOrWhiteSpace(kind.FieldTypeId), kind.Key);
            Assert.False(string.IsNullOrWhiteSpace(kind.CanonicalKey), kind.Key);
        }

        // Two enrichments may read one property, but each must own a distinct field.
        Assert.Equal(
            ContactEnrichments.All.Count(k => k.Outcome == ContactEnrichmentOutcome.Field),
            ContactEnrichments.All.Where(k => k.Outcome == ContactEnrichmentOutcome.Field)
                .Select(k => k.CanonicalKey).Distinct(StringComparer.Ordinal).Count());
    }
}
