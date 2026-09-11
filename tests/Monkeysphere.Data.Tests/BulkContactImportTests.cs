using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Data.Tests;

/// <summary>
/// A bulk export is only as good as its worst entry, and real address books contain entries no
/// parser can make sense of. One of those must cost the caller that contact and nothing else.
/// </summary>
public sealed class BulkContactImportTests
{
    private static string Good(int index) =>
        string.Create(CultureInfo.InvariantCulture, $"""
            BEGIN:VCARD
            VERSION:3.0
            FN:Contact {index:D3}
            EMAIL;TYPE=home:contact{index:D3}@example.test
            END:VCARD

            """);

    [Fact]
    public async Task OneUnusableCardDoesNotCostTheRestOfABulkImport()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        await application.Services.GetRequiredService<IPresetService>().InstallPresetAsync("monkeysphere.person");

        // 200 good contacts with three unusable ones scattered through them: no formatted name,
        // an unsupported version, and a property line with no value delimiter.
        StringBuilder file = new();
        for (int index = 0; index < 200; index++)
        {
            if (index == 40) file.Append("BEGIN:VCARD\r\nVERSION:4.0\r\nN:Nameless;;;;\r\nEND:VCARD\r\n");
            if (index == 90) file.Append("BEGIN:VCARD\r\nVERSION:2.1\r\nFN:Too Old\r\nEND:VCARD\r\n");
            if (index == 150) file.Append("BEGIN:VCARD\r\nVERSION:4.0\r\nFN:Broken Line\r\nOOPS-NO-COLON\r\nEND:VCARD\r\n");
            file.Append(Good(index));
        }

        IVCardService vcards = application.Services.GetRequiredService<IVCardService>();
        VCardImportPreview preview = await vcards.PreviewAsync(Encoding.UTF8.GetBytes(file.ToString()));

        Assert.Equal(200, preview.Contacts.Count);
        Assert.Equal(3, preview.Rejected.Count);

        // Each rejection has to be findable in the source file, which means a position and, where
        // the card carried anything identifying at all, a label.
        Assert.Equal([41, 92, 153], preview.Rejected.Select(rejected => rejected.Position));
        Assert.Equal(["Nameless", "Too Old", "Broken Line"], preview.Rejected.Select(rejected => rejected.Label));
        Assert.All(preview.Rejected, rejected => Assert.False(string.IsNullOrWhiteSpace(rejected.Reason)));

        VCardImportResult result = await vcards.ApplyAsync(preview,
            [.. preview.Contacts.Select(contact => new VCardImportSelection(contact.Index, VCardImportAction.CreateSeparately))]);

        Assert.Equal(200, result.Created);
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        Assert.Equal(200, (await records.SearchRecordsAsync(new())).TotalCount);

        // Nothing about a rejected card is retained: it is reported, not half-imported.
        Assert.Empty((await records.SearchRecordsAsync(new("Nameless"))).Items);
        Assert.Empty((await records.SearchRecordsAsync(new("Too Old"))).Items);
        Assert.Empty((await records.SearchRecordsAsync(new("Broken Line"))).Items);
    }

    [Fact]
    public async Task AFileWhoseCardsAreAllUnusableImportsNothingAndExplainsWhy()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        await application.Services.GetRequiredService<IPresetService>().InstallPresetAsync("monkeysphere.person");

        VCardImportPreview preview = await application.Services.GetRequiredService<IVCardService>()
            .PreviewAsync(Encoding.UTF8.GetBytes("BEGIN:VCARD\r\nVERSION:4.0\r\nORG:Only An Org\r\nEND:VCARD\r\n"));

        Assert.Empty(preview.Contacts);
        VCardRejectedCard rejected = Assert.Single(preview.Rejected);
        Assert.Equal("Only An Org", rejected.Label);
        Assert.Contains("formatted name", rejected.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // A file whose structure cannot be trusted is still refused outright: without matched markers
    // there is no way to know where one card ends, so "salvaging" it would invent contacts.
    [Fact]
    public async Task AStructurallyBrokenFileIsStillRefusedOutright()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        await application.Services.GetRequiredService<IPresetService>().InstallPresetAsync("monkeysphere.person");
        IVCardService vcards = application.Services.GetRequiredService<IVCardService>();

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            vcards.PreviewAsync(Encoding.UTF8.GetBytes("FN:No markers at all\r\n")));
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            vcards.PreviewAsync(Encoding.UTF8.GetBytes("BEGIN:VCARD\r\nVERSION:4.0\r\nFN:Unterminated\r\n")));
    }
}
