using System.Net;
using System.Net.Http.Headers;
using DnaX.RemoteAccess;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;
using Monkeysphere.Web.Remote;
using Monkeysphere.Web.Security;

namespace Monkeysphere.Web.Tests;

/// <summary>
/// The HTTP API is the surface with no way to establish backstage authority: `backstage` is an MCP
/// permission and the API offers only `records.read`. A hidden domain must therefore be invisible
/// there unconditionally, and must be invisible in the same way an unknown domain is.
/// </summary>
public sealed class ApiHiddenDomainTests
{
    private sealed class ApiFactory : RemoteEnabledApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // Backstage on deliberately: the point is that even with the deployment gate enabled,
            // the API has no route to backstage authority.
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Monkeysphere:Backstage:Available"] = "true",
                }));
        }
    }

    [Fact]
    public void TheApiSurfaceCannotBeGrantedBackstage()
    {
        // The gating rests on this: if `backstage` were selectable for the API, an API credential
        // could be rotated into seeing hidden domains and records.
        Assert.DoesNotContain(
            RemoteCredentialManager.AvailableScopes(DnaXRemoteSurface.Api),
            option => option.Scope == BackstageScopes.Backstage);
        Assert.Contains(
            RemoteCredentialManager.AvailableScopes(DnaXRemoteSurface.Mcp),
            option => option.Scope == BackstageScopes.Backstage);
    }

    [Fact]
    public async Task AHiddenDomainIsUnreachableOverTheHttpApi()
    {
        await using ApiFactory factory = new();
        using HttpClient client = factory.CreateClient();

        IDomainRegistry registry = factory.Services.GetRequiredService<IDomainRegistry>();
        MonkeysphereDomain visible = await registry.CreateAsync("Api visible sphere");
        MonkeysphereDomain hidden = await registry.CreateAsync("Api concealed sphere");
        await registry.SetHiddenAsync(hidden.Id, true);

        IDnaXRemoteAccessAdministration administration =
            factory.Services.GetRequiredService<IDnaXRemoteAccessAdministration>();
        DnaXRemoteAdministrationState state = await administration.GetStateAsync();
        DnaXGeneratedCredential credential = await administration.RotateCredentialAsync(
            DnaXRemoteSurface.Api, expectedVersion: state.Api.Version, scopes: ["records.read"]);
        DnaXRemoteEffectiveSurface route = await administration.SetActivationAsync(
            DnaXRemoteSurface.Api, active: true, allowAnonymous: false, expectedVersion: credential.Version);

        // The listing names the ordinary domain and not the concealed one.
        using HttpResponseMessage listing = await GetAsync(client, route.EndpointPath + "/domains", credential.Secret);
        string body = await listing.Content.ReadAsStringAsync();
        Assert.True(listing.IsSuccessStatusCode, body);
        Assert.Contains(visible.Id.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(hidden.Id.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Api concealed sphere", body, StringComparison.Ordinal);

        // Selecting it fails exactly as selecting a domain that never existed does. If these two
        // answered differently, the header would be an oracle for finding hidden domains.
        using HttpResponseMessage concealed = await GetAsync(
            client, route.EndpointPath + "/records", credential.Secret, hidden.Id);
        using HttpResponseMessage unknown = await GetAsync(
            client, route.EndpointPath + "/records", credential.Secret, Guid.CreateVersion7());
        Assert.Equal(HttpStatusCode.BadRequest, concealed.StatusCode);
        Assert.Equal(unknown.StatusCode, concealed.StatusCode);
        Assert.Equal(
            await unknown.Content.ReadAsStringAsync(),
            await concealed.Content.ReadAsStringAsync());

        // The ordinary domain still selects, so the refusal above is concealment rather than the
        // header being broken for everything.
        using HttpResponseMessage ordinary = await GetAsync(
            client, route.EndpointPath + "/records", credential.Secret, visible.Id);
        Assert.True(ordinary.IsSuccessStatusCode, await ordinary.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> GetAsync(
        HttpClient client, string path, string secret, Guid? domainId = null)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        if (domainId is Guid id)
        {
            request.Headers.Add("X-Monkeysphere-Domain", id.ToString("D"));
        }

        return await client.SendAsync(request);
    }
}
