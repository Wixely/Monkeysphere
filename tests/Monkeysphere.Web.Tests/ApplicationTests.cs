using System.Net;
using System.Net.Http.Headers;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using DnaX.RemoteAccess;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;
using Monkeysphere.Data;
using Monkeysphere.Web.Security;

namespace Monkeysphere.Web.Tests;

public sealed class ApplicationTests : IClassFixture<MonkeysphereApplicationFactory>
{
    private const string AdministratorPassword = "test-only-LongPassword-2048!";
    private readonly MonkeysphereApplicationFactory _factory;

    public ApplicationTests(MonkeysphereApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task HealthEndpointsAreAnonymous()
    {
        using HttpClient client = CreateClient();

        HttpResponseMessage live = await client.GetAsync("/health/live");
        HttpResponseMessage ready = await client.GetAsync("/health/ready");
        HttpResponseMessage mapLibrary = await client.GetAsync("/vendor/openlayers/10.10.0/ol.js");
        HttpResponseMessage graphLibrary = await client.GetAsync("/vendor/cytoscape/3.34.0/cytoscape.min.js");
        HttpResponseMessage graphBehavior = await client.GetAsync("/relationship-graph.js");
        HttpResponseMessage themeBehavior = await client.GetAsync("/theme.js");
        HttpResponseMessage comboboxBehavior = await client.GetAsync("/combobox.js");
        HttpResponseMessage missing = await client.GetAsync("/missing-browser-asset.js");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("nosniff", Assert.Single(live.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-referrer", Assert.Single(live.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("DENY", Assert.Single(live.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("camera=(), microphone=(), geolocation=()", Assert.Single(live.Headers.GetValues("Permissions-Policy")));
        Assert.Equal(
            "base-uri 'self'; frame-ancestors 'none'; object-src 'none'",
            Assert.Single(live.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal(HttpStatusCode.OK, missing.StatusCode);
        Assert.Equal("no-referrer", Assert.Single(missing.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("DENY", Assert.Single(missing.Headers.GetValues("X-Frame-Options")));
        Assert.Equal(
            "base-uri 'self'; frame-ancestors 'none'; object-src 'none'",
            Assert.Single(missing.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal(HttpStatusCode.OK, mapLibrary.StatusCode);
        Assert.True((await mapLibrary.Content.ReadAsByteArrayAsync()).Length > 1_000_000);
        Assert.Equal(HttpStatusCode.OK, graphLibrary.StatusCode);
        Assert.True((await graphLibrary.Content.ReadAsByteArrayAsync()).Length > 400_000);
        Assert.Equal(HttpStatusCode.OK, graphBehavior.StatusCode);
        string graphScript = await graphBehavior.Content.ReadAsStringAsync();
        Assert.Contains("/thumbnail", graphScript, StringComparison.Ordinal);
        Assert.Contains("'background-image': 'data(imageUrl)'", graphScript, StringComparison.Ordinal);
        Assert.Contains("badgeFor", graphScript, StringComparison.Ordinal);
        Assert.Contains("positionBadge", graphScript, StringComparison.Ordinal);
        Assert.Contains("'text-margin-y': 0", graphScript, StringComparison.Ordinal);
        Assert.Contains("'active-bg-opacity': 0", graphScript, StringComparison.Ordinal);
        Assert.Contains("monkeysphere:themechanged", graphScript, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, themeBehavior.StatusCode);
        string themeScript = await themeBehavior.Content.ReadAsStringAsync();
        Assert.Contains("monkeysphere.theme", themeScript, StringComparison.Ordinal);
        Assert.Contains("localStorage.setItem", themeScript, StringComparison.Ordinal);
        Assert.Contains("Blazor?.addEventListener('enhancedload'", themeScript, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, comboboxBehavior.StatusCode);
        Assert.Contains("combobox-input", await comboboxBehavior.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordImagesRequireAuthenticationAndServeOnlyNormalizedContent()
    {
        await using MonkeysphereApplicationFactory factory = new();
        Guid recordId;
        Guid imageId;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            IRecordImageService images = scope.ServiceProvider.GetRequiredService<IRecordImageService>();
            RecordType type = await records.CreateRecordTypeAsync("Image type " + Guid.NewGuid().ToString("N"));
            RecordDetails record = await records.CreateRecordAsync(type.Id, "Image record", []);
            byte[] png = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
            RecordImage image = await images.AddAsync(record.Record.Id, new MemoryStream(png), "portrait.png");
            recordId = record.Record.Id;
            imageId = image.Id;
        }

        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
        });
        string path = $"/records/{recordId}/images/{imageId}/thumbnail";
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync(path)).StatusCode);
        string loginHtml = await client.GetStringAsync("/login");
        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = ExtractAntiforgeryToken(loginHtml),
            ["username"] = "admin",
            ["password"] = AdministratorPassword,
            ["returnUrl"] = path,
        });
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/auth/login", form)).StatusCode);

        HttpResponseMessage response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.True((await response.Content.ReadAsByteArrayAsync()).Length > 0);
        HttpResponseMessage original = await client.GetAsync($"/records/{recordId}/images/{imageId}/original");
        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
        Assert.Equal("image/png", original.Content.Headers.ContentType?.MediaType);
        Assert.Equal("portrait.png", original.Content.Headers.ContentDisposition?.FileName);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/structures")]
    [InlineData("/structures/relationship-types")]
    [InlineData("/structures/saved-views")]
    [InlineData("/settings")]
    [InlineData("/settings/backups")]
    [InlineData("/settings/remote-access")]
    [InlineData("/saved-views")]
    [InlineData("/calendar")]
    [InlineData("/map")]
    [InlineData("/graph")]
    [InlineData("/calendar/export.ics?from=2026-09-01&to=2026-09-30")]
    [InlineData("/vcard")]
    [InlineData("/backups")]
    [InlineData("/vcard/export.vcf?ids=0198f100-0000-7000-8000-000000000001")]
    public async Task SensitivePagesRequireAdministratorAuthentication(string path)
    {
        using HttpClient client = CreateClient(allowRedirect: false);

        HttpResponseMessage response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location?.AbsolutePath);
    }

    [Fact]
    public async Task AuthenticatedSavedViewPagesRenderPersistedViews()
    {
        string suffix = Guid.NewGuid().ToString("N");
        Guid typeId;
        Guid recordId;
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            ISavedViewService views = scope.ServiceProvider.GetRequiredService<ISavedViewService>();
            IRelationshipService relationships = scope.ServiceProvider.GetRequiredService<IRelationshipService>();
            RecordType type = await records.CreateRecordTypeAsync("View type " + suffix);
            typeId = type.Id;
            FieldDefinition name = await records.CreateAndAttachFieldAsync(
                type.Id,
                new CreateFieldRequest("View field " + suffix, FieldTypes.Text, false));
            FieldDefinition location = await records.CreateAndAttachFieldAsync(
                type.Id,
                new CreateFieldRequest("Map location " + suffix, FieldTypes.Location, false));
            FieldDefinition occasion = await records.CreateAndAttachFieldAsync(
                type.Id,
                new CreateFieldRequest("Occasion " + suffix, FieldTypes.ExactDate, false));
            recordId = (await records.CreateRecordAsync(
                type.Id,
                "View record " + suffix,
                [
                    new(occasion.Id, "2026-09-15"),
                    new(location.Id, Location: new LocationValueInput("Test location", "51.5", "-0.1")),
                ])).Record.Id;
            RecordDetails related = await records.CreateRecordAsync(type.Id, "Related record " + suffix, []);
            RelationshipType connection = await relationships.CreateTypeAsync(new(
                "Graph connection " + suffix,
                RelationshipDirectionality.Symmetric));
            _ = await relationships.CreateAsync(connection.Id, recordId, related.Record.Id);
            _ = await views.CreateAsync(new SaveViewRequest(
                "Grid view " + suffix,
                type.Id,
                null,
                [name.Id],
                []));
        }

        using HttpClient client = CreateClient(allowRedirect: false);
        string loginHtml = await client.GetStringAsync("/login");
        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = ExtractAntiforgeryToken(loginHtml),
            ["username"] = "admin",
            ["password"] = AdministratorPassword,
            ["returnUrl"] = "/structures/saved-views",
        });
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/auth/login", form)).StatusCode);

        string viewsHtml = await client.GetStringAsync("/structures/saved-views");
        string structuresHtml = await client.GetStringAsync("/structures");
        string recordsHtml = await client.GetStringAsync("/records");
        string calendarHtml = await client.GetStringAsync("/calendar");
        string mapHtml = await client.GetStringAsync("/map");
        string graphHtml = await client.GetStringAsync("/graph");
        HttpResponseMessage calendarExport = await client.GetAsync("/calendar/export.ics?from=2026-09-01&to=2026-09-30");
        string typeHtml = await client.GetStringAsync($"/structures/record-types/{typeId}");
        string editorHtml = await client.GetStringAsync($"/records/new?typeId={typeId}");
        string recordHtml = await client.GetStringAsync($"/records/{recordId}");
        Assert.Contains("Grid view " + suffix, viewsHtml, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Structure sections\"", structuresHtml, StringComparison.Ordinal);
        Assert.Contains("href=\"/structures/relationship-types\"", structuresHtml, StringComparison.Ordinal);
        Assert.Contains(">Structures</a>", structuresHtml, StringComparison.Ordinal);
        string decodedStructuresHtml = WebUtility.HtmlDecode(structuresHtml);
        Assert.Matches(@"\d+ fields · v\d+", decodedStructuresHtml);
        Assert.DoesNotContain("@preset", decodedStructuresHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("@type", decodedStructuresHtml, StringComparison.Ordinal);
        Assert.Contains("Grid view " + suffix, recordsHtml, StringComparison.Ordinal);
        Assert.Contains("role=\"combobox\"", recordsHtml, StringComparison.Ordinal);
        Assert.Contains("aria-autocomplete=\"list\"", recordsHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<select", recordsHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Upcoming exact dates", calendarHtml, StringComparison.Ordinal);
        Assert.Contains("View type " + suffix, calendarHtml, StringComparison.Ordinal);
        Assert.Contains("Remind me", calendarHtml, StringComparison.Ordinal);
        Assert.Contains("Private spatial view", mapHtml, StringComparison.Ordinal);
        Assert.Contains("1 location", mapHtml, StringComparison.Ordinal);
        Assert.Contains("Map location " + suffix, mapHtml, StringComparison.Ordinal);
        Assert.Contains("Browse locations as a list", mapHtml, StringComparison.Ordinal);
        Assert.Contains("Bounded private view", graphHtml, StringComparison.Ordinal);
        Assert.Contains("Graph connection " + suffix, graphHtml, StringComparison.Ordinal);
        Assert.Contains("Select a displayed record", graphHtml, StringComparison.Ordinal);
        Assert.Contains("Skip to main content", graphHtml, StringComparison.Ordinal);
        Assert.Contains("data-theme-toggle", graphHtml, StringComparison.Ordinal);
        Assert.Contains("theme.js", graphHtml, StringComparison.Ordinal);
        Assert.Equal("text/calendar", calendarExport.Content.Headers.ContentType?.MediaType);
        Assert.Equal("monkeysphere-calendar.ics", calendarExport.Content.Headers.ContentDisposition?.FileName);
        Assert.Contains("View record " + suffix, await calendarExport.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("Evolve field definitions", typeHtml, StringComparison.Ordinal);
        Assert.Contains("Type lifecycle", typeHtml, StringComparison.Ordinal);
        Assert.Contains("Preview retirement", typeHtml, StringComparison.Ordinal);
        Assert.Contains("Preview type merge", typeHtml, StringComparison.Ordinal);
        Assert.Contains("Preview merge", typeHtml, StringComparison.Ordinal);
        Assert.Contains("Preview conversion", typeHtml, StringComparison.Ordinal);
        Assert.Contains("role=\"combobox\"", typeHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<datalist", typeHtml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Aliases and nicknames", editorHtml, StringComparison.Ordinal);
        Assert.Contains("Coordinate accuracy (metres)", editorHtml, StringComparison.Ordinal);
        Assert.Contains("Click the map to set coordinates", editorHtml, StringComparison.Ordinal);
        Assert.Contains("Images", recordHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackupPackagesContainVerifiedDatabasesAndOriginalsOnly()
    {
        await using MonkeysphereApplicationFactory factory = new();
        IBackupService backups;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            IRecordImageService images = scope.ServiceProvider.GetRequiredService<IRecordImageService>();
            backups = scope.ServiceProvider.GetRequiredService<IBackupService>();
            RecordType type = await records.CreateRecordTypeAsync("Backup type " + Guid.NewGuid().ToString("N"));
            RecordDetails record = await records.CreateRecordAsync(type.Id, "Backed-up record", []);
            byte[] png = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
            _ = await images.AddAsync(record.Record.Id, new MemoryStream(png), "portrait.png");
        }

        BackupInfo backup = await backups.CreateAsync();
        BackupValidation validation = await backups.ValidateAsync(backup.Id);
        Assert.Equal(1, validation.FormatVersion);
        Assert.Equal(Monkeysphere.Data.MonkeysphereSchema.Manifest.CurrentVersion, validation.ApplicationSchemaVersion);
        Assert.Equal(1, validation.OriginalImageCount);

        await using Stream content = Assert.IsAssignableFrom<Stream>(await backups.OpenAsync(backup.Id));
        using ZipArchive archive = new(content, ZipArchiveMode.Read);
        string[] paths = archive.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Contains("manifest.json", paths);
        Assert.Contains("databases/monkeysphere.db", paths);
        Assert.Contains("databases/remote-access.db", paths);
        Assert.Single(paths, path => path.EndsWith(".original.png", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, path => path.Contains("preview", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(paths, path => path.StartsWith("keys/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OfflineRestoreRollsBackLiveDataAndRegeneratesImageDerivatives()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "Monkeysphere.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            Guid recordId;
            Guid imageId;
            string packagePath;
            await using (PersistentApplicationFactory factory = new(dataRoot))
            {
                _ = factory.Services;
                using IServiceScope scope = factory.Services.CreateScope();
                IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
                IRecordImageService images = scope.ServiceProvider.GetRequiredService<IRecordImageService>();
                IBackupService backups = scope.ServiceProvider.GetRequiredService<IBackupService>();
                RecordType type = await records.CreateRecordTypeAsync("Restore type");
                RecordDetails record = await records.CreateRecordAsync(type.Id, "Before backup", []);
                byte[] png = Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
                RecordImage image = await images.AddAsync(record.Record.Id, new MemoryStream(png), "portrait.png");
                BackupInfo backup = await backups.CreateAsync();
                packagePath = Path.Combine(dataRoot, "backups", backup.FileName);
                _ = await records.UpdateRecordAsync(record.Record.Id, "After backup", []);
                recordId = record.Record.Id;
                imageId = image.Id;
            }

            SqliteConnection.ClearAllPools();
            string rollback = await OfflineBackupRestore.RestoreAsync(packagePath, dataRoot);
            Assert.True(Directory.Exists(rollback));

            await using (PersistentApplicationFactory restoredFactory = new(dataRoot))
            {
                _ = restoredFactory.Services;
                using IServiceScope scope = restoredFactory.Services.CreateScope();
                IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
                IRecordImageService images = scope.ServiceProvider.GetRequiredService<IRecordImageService>();
                Assert.Equal("Before backup", (await records.GetRecordAsync(recordId))?.Record.DisplayName);
                RecordImageFile preview = Assert.IsType<RecordImageFile>(
                    await images.OpenAsync(recordId, imageId, RecordImageVariant.Preview));
                await preview.Content.DisposeAsync();
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AdministratorCanSignInWithAntiforgeryProtection()
    {
        using HttpClient client = CreateClient(allowRedirect: false);
        string loginHtml = await client.GetStringAsync("/login");
        string token = ExtractAntiforgeryToken(loginHtml);

        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["username"] = "admin",
            ["password"] = AdministratorPassword,
            ["returnUrl"] = "/",
        });
        HttpResponseMessage login = await client.PostAsync("/auth/login", form);

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/", login.Headers.Location?.OriginalString);

        HttpResponseMessage home = await client.GetAsync("/setup");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        string homeHtml = await home.Content.ReadAsStringAsync();
        using FormUrlEncodedContent logoutForm = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = ExtractAntiforgeryToken(homeHtml),
        });
        HttpResponseMessage logout = await client.PostAsync("/auth/logout", logoutForm);
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal("/login", logout.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task AuthenticatedVCardPageImportsAndExportsOnlySelectedPeople()
    {
        await using MonkeysphereApplicationFactory factory = new();
        Guid recordId;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IPresetService presets = scope.ServiceProvider.GetRequiredService<IPresetService>();
            IVCardService vcards = scope.ServiceProvider.GetRequiredService<IVCardService>();
            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            await presets.CompleteSetupAsync("people", ["monkeysphere.person"]);
            VCardImportPreview preview = await vcards.PreviewAsync(Encoding.UTF8.GetBytes("""
                BEGIN:VCARD
                VERSION:4.0
                FN:Web Export Person
                EMAIL:web@example.test
                END:VCARD
                """));
            await vcards.ApplyAsync(preview, [new(0, VCardImportAction.CreateSeparately)]);
            recordId = Assert.Single((await records.SearchRecordsAsync(new("Web Export Person", preview.RecordTypeId))).Items).Id;
        }

        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
        });
        string loginHtml = await client.GetStringAsync("/login");
        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = ExtractAntiforgeryToken(loginHtml),
            ["username"] = "admin",
            ["password"] = AdministratorPassword,
            ["returnUrl"] = "/settings",
        });
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/auth/login", form)).StatusCode);

        string page = await client.GetStringAsync("/settings");
        Assert.Contains("Preview a contact file", page, StringComparison.Ordinal);
        Assert.Contains("Web Export Person", page, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Settings sections\"", page, StringComparison.Ordinal);
        Assert.Contains("href=\"/settings/backups\"", page, StringComparison.Ordinal);
        Assert.Contains(">Settings</a>", page, StringComparison.Ordinal);
        HttpResponseMessage export = await client.GetAsync($"/vcard/export.vcf?ids={recordId:D}");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("text/vcard", export.Content.Headers.ContentType?.MediaType);
        Assert.Equal("monkeysphere-contacts.vcf", export.Content.Headers.ContentDisposition?.FileName);
        string exported = await export.Content.ReadAsStringAsync();
        Assert.Contains("FN:Web Export Person", exported, StringComparison.Ordinal);
        Assert.Contains("EMAIL:web@example.test", exported, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoginWithoutAntiforgeryTokenIsRejected()
    {
        using HttpClient client = CreateClient(allowRedirect: false);
        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["username"] = "admin",
            ["password"] = AdministratorPassword,
        });

        HttpResponseMessage response = await client.PostAsync("/auth/login", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("http://localhost", false)]
    [InlineData("https://localhost", true)]
    public async Task SessionCookieWorksOnSupportedTransportAndIsSecureOnHttps(string baseAddress, bool expectSecure)
    {
        using HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri(baseAddress),
            HandleCookies = true,
        });
        string loginHtml = await client.GetStringAsync("/login");
        string token = ExtractAntiforgeryToken(loginHtml);
        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["username"] = "admin",
            ["password"] = AdministratorPassword,
            ["returnUrl"] = "/",
        });

        HttpResponseMessage login = await client.PostAsync("/auth/login", form);
        string cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), value =>
            value.StartsWith("Monkeysphere.Session=", StringComparison.Ordinal));

        Assert.Equal(expectSecure, cookie.Contains("; secure", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/setup")).StatusCode);
    }

    [Fact]
    public async Task RemoteSurfacesAreUnavailableByDefault()
    {
        using HttpClient client = CreateClient(allowRedirect: false);

        HttpResponseMessage response = await client.GetAsync("/api/v1/records");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private HttpClient CreateClient(bool allowRedirect = true) => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = allowRedirect,
        BaseAddress = new Uri("https://localhost"),
        HandleCookies = true,
    });

    private static string ExtractAntiforgeryToken(string html)
    {
        Match match = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, "The login form did not contain an antiforgery token.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
}

public sealed class AdministratorCredentialTests
{
    [Fact]
    public void MissingConfigurationUsesDefaultAdministratorCredential()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        AdministratorCredential credential = AdministratorCredential.Load(configuration);

        Assert.True(credential.Verify("admin", "admin"));
        Assert.False(credential.Verify("admin", "incorrect"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ExplicitBlankPasswordIsRejected(string password)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MONKEYSPHERE_ADMIN_PASSWORD"] = password,
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() => AdministratorCredential.Load(configuration));
    }

    [Fact]
    public void ValidCredentialVerifiesOnlyTheConfiguredPair()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MONKEYSPHERE_ADMIN_USERNAME"] = "owner",
                ["MONKEYSPHERE_ADMIN_PASSWORD"] = "test-only-LongPassword-4096!",
            })
            .Build();
        AdministratorCredential credential = AdministratorCredential.Load(configuration);

        Assert.True(credential.Verify("owner", "test-only-LongPassword-4096!"));
        Assert.False(credential.Verify("owner", "incorrect-test-password"));
        Assert.False(credential.Verify("someone-else", "test-only-LongPassword-4096!"));
    }
}

public class MonkeysphereApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dataRoot;
    private readonly bool _deleteDataRoot;

    public MonkeysphereApplicationFactory()
        : this(dataRoot: null, deleteDataRoot: true)
    {
    }

    protected MonkeysphereApplicationFactory(string? dataRoot, bool deleteDataRoot)
    {
        _dataRoot = dataRoot ?? Path.Combine(
            Path.GetTempPath(),
            "Monkeysphere.Tests",
            Guid.NewGuid().ToString("N"));
        _deleteDataRoot = deleteDataRoot;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Program resolves the data root during host construction, before the
        // later application-configuration callback is applied by the test host.
        builder.UseSetting("MONKEYSPHERE_DATA_ROOT", _dataRoot);
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MONKEYSPHERE_DATA_ROOT"] = _dataRoot,
                ["MONKEYSPHERE_ADMIN_USERNAME"] = "admin",
                ["MONKEYSPHERE_ADMIN_PASSWORD"] = AdministratorPasswordForFactory,
                ["DnaX:RemoteAccess:Enabled"] = "false",
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing || !_deleteDataRoot)
        {
            return;
        }

        SqliteConnection.ClearAllPools();
        string allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Monkeysphere.Tests"));
        string ownedRoot = Path.GetFullPath(_dataRoot);
        if (ownedRoot.StartsWith(Path.TrimEndingDirectorySeparator(allowedRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(ownedRoot))
        {
            Directory.Delete(ownedRoot, recursive: true);
        }
    }

    private const string AdministratorPasswordForFactory = "test-only-LongPassword-2048!";
}

public sealed class RestartPersistenceTests
{
    [Fact]
    public async Task RecordsAndSavedViewsSurviveACompleteApplicationRestart()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "Monkeysphere.Tests", Guid.NewGuid().ToString("N"));
        Guid recordId;
        Guid imageId;
        Guid savedViewId;
        try
        {
            await using (PersistentApplicationFactory first = new(dataRoot))
            {
                using HttpClient client = first.CreateClient();
                _ = await client.GetAsync("/health/ready");
                using IServiceScope scope = first.Services.CreateScope();
                IMonkeysphereService service = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
                IRecordImageService images = scope.ServiceProvider.GetRequiredService<IRecordImageService>();
                ISavedViewService views = scope.ServiceProvider.GetRequiredService<ISavedViewService>();
                RecordType type = await service.CreateRecordTypeAsync("Restart person");
                FieldDefinition name = await service.CreateAndAttachFieldAsync(
                    type.Id,
                    new CreateFieldRequest("Name", FieldTypes.Text, true));
                RecordDetails record = await service.CreateRecordAsync(type.Id, "Grace Hopper", [new(name.Id, "Grace")]);
                recordId = record.Record.Id;
                byte[] png = Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
                imageId = (await images.AddAsync(recordId, new MemoryStream(png), "grace.png")).Id;
                SavedViewDetails view = await views.CreateAsync(new SaveViewRequest(
                    "Restart view",
                    type.Id,
                    "Grace",
                    [name.Id],
                    []));
                savedViewId = view.View.Id;
            }

            await using (PersistentApplicationFactory second = new(dataRoot))
            {
                using HttpClient client = second.CreateClient();
                _ = await client.GetAsync("/health/ready");
                using IServiceScope scope = second.Services.CreateScope();
                IMonkeysphereService service = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
                IRecordImageService images = scope.ServiceProvider.GetRequiredService<IRecordImageService>();
                ISavedViewService views = scope.ServiceProvider.GetRequiredService<ISavedViewService>();
                RecordDetails persisted = Assert.IsType<RecordDetails>(await service.GetRecordAsync(recordId));
                Assert.Equal("Grace Hopper", persisted.Record.DisplayName);
                Assert.Equal("Grace", Assert.Single(persisted.Values).TextValue);
                Assert.Equal(imageId, Assert.Single(persisted.Images).Id);
                RecordImageFile persistedImage = Assert.IsType<RecordImageFile>(
                    await images.OpenAsync(recordId, imageId, RecordImageVariant.Thumbnail));
                await persistedImage.Content.DisposeAsync();
                SavedViewDetails persistedView = Assert.IsType<SavedViewDetails>(await views.GetAsync(savedViewId));
                Assert.Equal("Restart view", persistedView.View.Name);
                Assert.Equal(recordId, Assert.Single((await service.SearchRecordsAsync(views.ToSearch(persistedView))).Items).Id);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            string allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Monkeysphere.Tests"));
            string ownedRoot = Path.GetFullPath(dataRoot);
            if (ownedRoot.StartsWith(Path.TrimEndingDirectorySeparator(allowedRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(ownedRoot))
            {
                Directory.Delete(ownedRoot, recursive: true);
            }
        }
    }
}

public sealed class PersistentApplicationFactory(string dataRoot)
    : MonkeysphereApplicationFactory(dataRoot, deleteDataRoot: false);

public sealed class RemoteAccessApplicationTests
{
    [Fact]
    public async Task ReadOnlyApiUsesScopesAndImmediatelyRotatesRoutesAndCredentials()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });
        Guid adaId;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IMonkeysphereService service = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            IRecordImageService images = scope.ServiceProvider.GetRequiredService<IRecordImageService>();
            IRelationshipService relationships = scope.ServiceProvider.GetRequiredService<IRelationshipService>();
            RecordType type = await service.CreateRecordTypeAsync("Person " + Guid.NewGuid().ToString("N"));
            FieldDefinition nickname = await service.CreateAndAttachFieldAsync(
                type.Id,
                new CreateFieldRequest("Nickname", FieldTypes.Text, true));
            FieldDefinition location = await service.CreateAndAttachFieldAsync(
                type.Id,
                new CreateFieldRequest("Location", FieldTypes.Location, false));
            RecordDetails ada = await service.CreateRecordAsync(
                type.Id,
                "Ada Lovelace",
                [
                    new(nickname.Id, "Ada"),
                    new(location.Id, Location: new LocationValueInput(
                        "Analytical Engine room",
                        "51.501",
                        "-0.141",
                        ApproximationRadiusKilometres: "1")),
                ],
                ["Enchantress of Numbers"]);
            byte[] png = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
            _ = await images.AddAsync(ada.Record.Id, new MemoryStream(png), "ada-portrait.png");
            RecordDetails charles = await service.CreateRecordAsync(type.Id, "Charles Babbage", [new(nickname.Id, "Charles")]);
            RelationshipType collaborator = await relationships.CreateTypeAsync(new(
                "collaborated with",
                RelationshipDirectionality.Symmetric));
            await relationships.CreateAsync(collaborator.Id, ada.Record.Id, charles.Record.Id);
            adaId = ada.Record.Id;
        }

        IDnaXRemoteAccessAdministration administration =
            factory.Services.GetRequiredService<IDnaXRemoteAccessAdministration>();
        DnaXRemoteAdministrationState initialState = await administration.GetStateAsync();
        Assert.Equal(0, initialState.Api.Version);
        DnaXGeneratedCredential firstCredential = await administration.RotateCredentialAsync(
            DnaXRemoteSurface.Api,
            expectedVersion: initialState.Api.Version,
            scopes: ["records.read"]);
        DnaXRemoteEffectiveSurface firstRoute = await administration.SetActivationAsync(
            DnaXRemoteSurface.Api,
            active: true,
            allowAnonymous: false,
            expectedVersion: firstCredential.Version);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(firstRoute.EndpointPath + "/records")).StatusCode);
        HttpResponseMessage firstResponse = await SendAsync(client, firstRoute.EndpointPath + "/records", firstCredential.Secret);
        string firstBody = await firstResponse.Content.ReadAsStringAsync();
        Assert.True(firstResponse.StatusCode == HttpStatusCode.OK, $"Expected 200 but received {(int)firstResponse.StatusCode}: {firstBody}");
        Assert.Contains("Ada Lovelace", firstBody, StringComparison.Ordinal);
        HttpResponseMessage typesResponse = await SendAsync(
            client,
            firstRoute.EndpointPath + "/record-types",
            firstCredential.Secret);
        string typesBody = await typesResponse.Content.ReadAsStringAsync();
        Assert.True(typesResponse.IsSuccessStatusCode, typesBody);
        Assert.Contains("\"lifecycle\":\"active\"", typesBody, StringComparison.Ordinal);
        HttpResponseMessage recordResponse = await SendAsync(
            client,
            firstRoute.EndpointPath + $"/records/{adaId}",
            firstCredential.Secret);
        string recordBody = await recordResponse.Content.ReadAsStringAsync();
        Assert.True(recordResponse.IsSuccessStatusCode, recordBody);
        Assert.Contains("Enchantress of Numbers", recordBody, StringComparison.Ordinal);
        Assert.Contains("Analytical Engine room", recordBody, StringComparison.Ordinal);
        Assert.Contains("\"latitude\":51.501", recordBody, StringComparison.Ordinal);
        Assert.Contains("\"approximationRadiusKilometres\":1", recordBody, StringComparison.Ordinal);
        Assert.Contains("ada-portrait.png", recordBody, StringComparison.Ordinal);
        HttpResponseMessage relationshipResponse = await SendAsync(
            client,
            firstRoute.EndpointPath + $"/records/{adaId}/relationships",
            firstCredential.Secret);
        string relationshipBody = await relationshipResponse.Content.ReadAsStringAsync();
        Assert.True(relationshipResponse.IsSuccessStatusCode, relationshipBody);
        Assert.Contains("Charles Babbage", relationshipBody, StringComparison.Ordinal);

        DnaXRemoteEffectiveSurface secondRoute = await administration.RotateEndpointAsync(
            DnaXRemoteSurface.Api,
            firstRoute.Version);
        Assert.NotEqual(firstRoute.EndpointPath, secondRoute.EndpointPath);
        Assert.Equal(HttpStatusCode.NotFound,
            (await SendAsync(client, firstRoute.EndpointPath + "/records", firstCredential.Secret)).StatusCode);

        DnaXGeneratedCredential secondCredential = await administration.RotateCredentialAsync(
            DnaXRemoteSurface.Api,
            secondRoute.Version,
            ["records.read"]);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await SendAsync(client, secondRoute.EndpointPath + "/records", firstCredential.Secret)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await SendAsync(client, secondRoute.EndpointPath + "/records", secondCredential.Secret)).StatusCode);

        DnaXGeneratedCredential wrongScope = await administration.RotateCredentialAsync(
            DnaXRemoteSurface.Api,
            secondCredential.Version,
            ["record-types.read"]);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAsync(client, secondRoute.EndpointPath + "/records", wrongScope.Secret)).StatusCode);

        DnaXGeneratedCredential mcpCredential = await administration.RotateCredentialAsync(
            DnaXRemoteSurface.Mcp,
            expectedVersion: 0,
            scopes: ["records.read"]);
        DnaXRemoteEffectiveSurface mcpRoute = await administration.SetActivationAsync(
            DnaXRemoteSurface.Mcp,
            active: true,
            allowAnonymous: false,
            expectedVersion: mcpCredential.Version);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await SendMcpAsync(client, mcpRoute.EndpointPath!, secondCredential.Secret)).StatusCode);
        HttpResponseMessage mcpResponse = await SendMcpAsync(client, mcpRoute.EndpointPath!, mcpCredential.Secret);
        string mcpBody = await mcpResponse.Content.ReadAsStringAsync();
        Assert.True(mcpResponse.IsSuccessStatusCode, mcpBody);
        Assert.Contains("Ada Lovelace", mcpBody, StringComparison.Ordinal);

        IReadOnlyList<DnaXRemoteAuditRecord> audit = await administration.GetRecentActivityAsync(20);
        Assert.Contains(audit, item => item.Event.Action == "records.search" && item.Event.Result == DnaXRemoteAuditResult.Allowed);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string path, string secret)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendMcpAsync(HttpClient client, string path, string secret)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        request.Headers.Add("Mcp-Method", "tools/call");
        request.Headers.Add("Mcp-Name", "search_records");
        request.Content = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search_records","arguments":{"query":"Enchantress"},"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}}}""",
            Encoding.UTF8,
            "application/json");
        return await client.SendAsync(request);
    }
}

public sealed class RemoteEnabledApplicationFactory : MonkeysphereApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DnaX:RemoteAccess:Enabled"] = "true",
                ["DnaX:RemoteAccess:DeploymentId"] = "monkeysphere-web-tests",
                ["DnaX:RemoteAccess:Network:RequireHttps"] = "false",
                ["DnaX:RemoteAccess:Api:Available"] = "true",
                ["DnaX:RemoteAccess:Api:UseRandomizedEndpoint"] = "true",
                ["DnaX:RemoteAccess:Api:AllowRuntimeActivation"] = "true",
                ["DnaX:RemoteAccess:Api:AllowCredentialRotation"] = "true",
                ["DnaX:RemoteAccess:Api:AllowEndpointRotation"] = "true",
                ["DnaX:RemoteAccess:Mcp:Available"] = "true",
                ["DnaX:RemoteAccess:Mcp:UseRandomizedEndpoint"] = "true",
                ["DnaX:RemoteAccess:Mcp:AllowRuntimeActivation"] = "true",
                ["DnaX:RemoteAccess:Mcp:AllowCredentialRotation"] = "true",
                ["DnaX:RemoteAccess:Mcp:AllowEndpointRotation"] = "true",
            });
        });
    }
}
