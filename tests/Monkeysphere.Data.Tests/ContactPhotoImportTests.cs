using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Data.Tests;

/// <summary>
/// Photos are the one enrichment that can reach outside the deployment, so the rule that matters
/// most here is the negative one: nothing is fetched unless the import was told to.
/// </summary>
public sealed class ContactPhotoImportTests
{
    private const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    /// <summary>Records every request it is asked to make, so a test can prove none was.</summary>
    private sealed class RecordingFetcher(byte[]? content) : IContactPhotoFetcher
    {
        public List<string> Requested { get; } = [];

        public Task<ContactPhotoBytes?> FetchAsync(string url, CancellationToken cancellationToken = default)
        {
            Requested.Add(url);
            return Task.FromResult(content is null ? null : new ContactPhotoBytes(content, "fetched.png"));
        }
    }

    private static byte[] Card(string name, string photoLine) => Encoding.UTF8.GetBytes(
        $"BEGIN:VCARD\r\nVERSION:3.0\r\nFN:{name}\r\n{photoLine}\r\nEND:VCARD\r\n");

    private static async Task<(TestApplication App, IVCardService VCards, ContactPhotoImporter Photos)> StartAsync()
    {
        TestApplication application = await TestApplication.CreateAsync();
        await application.Services.GetRequiredService<IPresetService>().InstallPresetAsync("monkeysphere.person");
        return (application,
            application.Services.GetRequiredService<IVCardService>(),
            application.Services.GetRequiredService<ContactPhotoImporter>());
    }

    private static async Task<RecordDetails> SavedAsync(TestApplication app, string name)
    {
        IMonkeysphereService records = app.Services.GetRequiredService<IMonkeysphereService>();
        RecordSummary summary = Assert.Single((await records.SearchRecordsAsync(new(name))).Items);
        return (await records.GetRecordAsync(summary.Id))!;
    }

    private static async Task<VCardImportResult> ImportAsync(IVCardService vcards, VCardImportPreview preview) =>
        await vcards.ApplyAsync(preview,
            [.. preview.Contacts.Select(c => new VCardImportSelection(c.Index, VCardImportAction.CreateSeparately))]);

    [Fact]
    public async Task APhotoCarriedInTheCardIsStoredWithoutAnyRequestBeingMade()
    {
        var (app, vcards, photos) = await StartAsync();
        await using TestApplication _ = app;
        RecordingFetcher fetcher = new([1, 2, 3]);

        VCardImportPreview preview = await vcards.PreviewAsync(Card("Inline Photo", "PHOTO;ENCODING=b;TYPE=PNG:" + OnePixelPng));
        VCardImportResult imported = await ImportAsync(vcards, preview);
        ContactPhotoImportResult result = await photos.AttachAsync(preview, fetcher);

        Assert.Equal(1, result.Attached);
        Assert.Empty(fetcher.Requested);

        RecordDetails saved = await SavedAsync(app, "Inline Photo");
        Assert.Single(saved.Images);
    }

    [Fact]
    public async Task APhotoHeldElsewhereIsLeftAloneWhenFetchingWasNotEnabled()
    {
        var (app, vcards, photos) = await StartAsync();
        await using TestApplication _ = app;

        VCardImportPreview preview = await vcards.PreviewAsync(Card("Remote Photo", "PHOTO:https://photos.example.test/a.jpg"));
        VCardImportResult imported = await ImportAsync(vcards, preview);
        ContactPhotoImportResult result = await photos.AttachAsync(preview, fetcher: null);

        Assert.Equal(0, result.Attached);
        Assert.Equal(1, result.Skipped);
        ContactPhotoImportOutcome outcome = Assert.Single(result.Outcomes);
        Assert.True(outcome.WasRemote);
        Assert.Contains("not enabled", outcome.Failure!, StringComparison.Ordinal);

        RecordDetails saved = await SavedAsync(app, "Remote Photo");
        Assert.Empty(saved.Images);
    }

    [Fact]
    public async Task APhotoHeldElsewhereIsFetchedOnceWhenTheImportAsksForIt()
    {
        var (app, vcards, photos) = await StartAsync();
        await using TestApplication _ = app;
        RecordingFetcher fetcher = new(Convert.FromBase64String(OnePixelPng));

        VCardImportPreview preview = await vcards.PreviewAsync(Card("Remote Photo", "PHOTO:https://photos.example.test/a.jpg"));
        VCardImportResult imported = await ImportAsync(vcards, preview);
        ContactPhotoImportResult result = await photos.AttachAsync(preview, fetcher);

        Assert.Equal(1, result.Attached);
        Assert.Equal("https://photos.example.test/a.jpg", Assert.Single(fetcher.Requested));

        RecordDetails saved = await SavedAsync(app, "Remote Photo");
        Assert.Single(saved.Images);
    }

    [Fact]
    public async Task AnUnreachablePhotoCostsThatContactAPictureAndNothingElse()
    {
        var (app, vcards, photos) = await StartAsync();
        await using TestApplication _ = app;
        RecordingFetcher fetcher = new(content: null);

        byte[] file = Encoding.UTF8.GetBytes(
            "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Has Photo\r\nPHOTO:https://photos.example.test/gone.jpg\r\nEND:VCARD\r\n" +
            "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Inline Photo\r\nPHOTO;ENCODING=b:" + OnePixelPng + "\r\nEND:VCARD\r\n");
        VCardImportPreview preview = await vcards.PreviewAsync(file);
        VCardImportResult imported = await ImportAsync(vcards, preview);
        ContactPhotoImportResult result = await photos.AttachAsync(preview, fetcher);

        Assert.Equal(1, result.Attached);
        Assert.Equal(1, result.Failed);
        Assert.Equal(2, imported.Created);
    }

    // Re-importing the same export must not give everyone a second copy of the same face.
    [Fact]
    public async Task AContactThatAlreadyHasAnImageKeepsIt()
    {
        var (app, vcards, photos) = await StartAsync();
        await using TestApplication _ = app;
        RecordingFetcher fetcher = new(Convert.FromBase64String(OnePixelPng));

        byte[] card = Card("Inline Photo", "PHOTO;ENCODING=b:" + OnePixelPng);
        VCardImportPreview first = await vcards.PreviewAsync(card);
        VCardImportResult imported = await ImportAsync(vcards, first);
        Assert.Equal(1, (await photos.AttachAsync(first, fetcher)).Attached);

        // The same record, offered the same photo again.
        ContactPhotoImportResult again = await photos.AttachAsync(first, fetcher);
        Assert.Equal(0, again.Attached);
        Assert.Equal(1, again.Skipped);

        RecordDetails saved = await SavedAsync(app, "Inline Photo");
        Assert.Single(saved.Images);
    }

    [Fact]
    public async Task DataThatIsNotAnImageIsRefusedByThePipelineAndReportedAgainstItsContact()
    {
        var (app, vcards, photos) = await StartAsync();
        await using TestApplication _ = app;

        string notAnImage = Convert.ToBase64String(Encoding.UTF8.GetBytes("this is not a picture at all"));
        VCardImportPreview preview = await vcards.PreviewAsync(Card("Bad Photo", "PHOTO;ENCODING=b:" + notAnImage));
        VCardImportResult imported = await ImportAsync(vcards, preview);
        ContactPhotoImportResult result = await photos.AttachAsync(preview);

        Assert.Equal(0, result.Attached);
        Assert.Equal(1, result.Failed);
        Assert.Equal(1, imported.Created);
    }
}
