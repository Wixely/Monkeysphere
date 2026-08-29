using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using DnaX.RemoteAccess;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;
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

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task HomeRequiresAdministratorAuthentication()
    {
        using HttpClient client = CreateClient(allowRedirect: false);

        HttpResponseMessage response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location?.AbsolutePath);
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

        HttpResponseMessage home = await client.GetAsync("/");
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
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
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
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("password")]
    [InlineData("too-short")]
    public void InvalidOrPlaceholderPasswordsFailClosed(string? password)
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
    public async Task RecordsSurviveACompleteApplicationRestart()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "Monkeysphere.Tests", Guid.NewGuid().ToString("N"));
        Guid recordId;
        try
        {
            await using (PersistentApplicationFactory first = new(dataRoot))
            {
                using HttpClient client = first.CreateClient();
                _ = await client.GetAsync("/health/ready");
                using IServiceScope scope = first.Services.CreateScope();
                IMonkeysphereService service = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
                RecordType type = await service.CreateRecordTypeAsync("Restart person");
                FieldDefinition name = await service.CreateAndAttachFieldAsync(
                    type.Id,
                    new CreateFieldRequest("Name", FieldTypes.Text, true));
                RecordDetails record = await service.CreateRecordAsync(type.Id, "Grace Hopper", [new(name.Id, "Grace")]);
                recordId = record.Record.Id;
            }

            await using (PersistentApplicationFactory second = new(dataRoot))
            {
                using HttpClient client = second.CreateClient();
                _ = await client.GetAsync("/health/ready");
                using IServiceScope scope = second.Services.CreateScope();
                IMonkeysphereService service = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
                RecordDetails persisted = Assert.IsType<RecordDetails>(await service.GetRecordAsync(recordId));
                Assert.Equal("Grace Hopper", persisted.Record.DisplayName);
                Assert.Equal("Grace", Assert.Single(persisted.Values).TextValue);
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
            IRelationshipService relationships = scope.ServiceProvider.GetRequiredService<IRelationshipService>();
            RecordType type = await service.CreateRecordTypeAsync("Person " + Guid.NewGuid().ToString("N"));
            FieldDefinition nickname = await service.CreateAndAttachFieldAsync(
                type.Id,
                new CreateFieldRequest("Nickname", FieldTypes.Text, true));
            RecordDetails ada = await service.CreateRecordAsync(type.Id, "Ada Lovelace", [new(nickname.Id, "Ada")]);
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
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search_records","arguments":{"query":"Ada"},"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}}}""",
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
