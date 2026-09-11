using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using DnaX.RemoteAccess;
using Monkeysphere.Core;
using Monkeysphere.Web.Remote;

namespace Monkeysphere.Web.Tests;

/// <summary>
/// The browser decides backstage with a session that expires; a remote credential decides it with
/// a grant that does not. These pin both halves of that, and that the deployment gate overrules
/// the grant, because "the operator turned backstage off" has to mean nothing can see the records.
/// </summary>
public sealed partial class RemoteDiscoveryTests
{
    private sealed class BackstageEnabledFactory(bool enabled) : RemoteEnabledApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Monkeysphere:Backstage:Available"] = enabled ? "true" : "false",
                }));
        }
    }

    private static async Task<Guid> SeedHiddenRecordAsync(RemoteEnabledApplicationFactory factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        RecordType type = await records.CreateRecordTypeAsync("Backstage subject");
        RecordDetails record = await records.CreateRecordAsync(type.Id, "Concealed Cornelius", []);
        await scope.ServiceProvider.GetRequiredService<IBackstageRecordStore>()
            .SetStateAsync(record.Record.Id, BackstageStates.Hidden);
        return record.Record.Id;
    }

    private static async Task<int> SearchCountAsync(HttpClient client, string path, string secret)
    {
        using JsonDocument response = await SendAsync(client, path, secret, "tools/call", "search_records",
            new { domainId = MonkeysphereDomains.DefaultId, query = "Concealed" });
        return Structured(response).GetProperty("totalCount").GetInt32();
    }

    // The grant is offered for MCP only, and holding it authorizes no read on its own.
    [Fact]
    public async Task TheBackstageGrantIsSelectableForMcpAloneAndAuthorizesNothingByItself()
    {
        Assert.Contains(RemoteCredentialManager.AvailableScopes(DnaXRemoteSurface.Mcp), option => option.Scope == "backstage");
        Assert.DoesNotContain(RemoteCredentialManager.AvailableScopes(DnaXRemoteSurface.Api), option => option.Scope == "backstage");

        await using BackstageEnabledFactory factory = new(enabled: true);
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["backstage"]);
        _ = await SeedHiddenRecordAsync(factory);

        using JsonDocument denied = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "search_records", new { domainId = MonkeysphereDomains.DefaultId });
        Assert.True(denied.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task AnMcpCredentialWithoutTheBackstageGrantCannotSeeAHiddenRecord()
    {
        await using BackstageEnabledFactory factory = new(enabled: true);
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read"]);
        Guid hidden = await SeedHiddenRecordAsync(factory);

        Assert.Equal(0, await SearchCountAsync(client, surface.EndpointPath!, credential.Secret));

        using JsonDocument direct = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "get_record", new { id = hidden, domainId = MonkeysphereDomains.DefaultId });
        Assert.Empty(direct.RootElement.GetProperty("result").GetProperty("content").EnumerateArray());
    }

    [Fact]
    public async Task AnMcpCredentialWithTheBackstageGrantSeesAHiddenRecord()
    {
        await using BackstageEnabledFactory factory = new(enabled: true);
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read", "backstage"]);
        Guid hidden = await SeedHiddenRecordAsync(factory);

        Assert.Equal(1, await SearchCountAsync(client, surface.EndpointPath!, credential.Secret));

        using JsonDocument direct = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "get_record", new { id = hidden, domainId = MonkeysphereDomains.DefaultId });
        Assert.Equal(hidden, Structured(direct).GetProperty("record").GetProperty("id").GetGuid());
    }

    // The gate is a kill switch, not merely a way to hide the settings section.
    [Fact]
    public async Task TheBackstageGrantSeesNothingWhileBackstageIsDisabledForTheDeployment()
    {
        await using BackstageEnabledFactory factory = new(enabled: false);
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read", "backstage"]);
        _ = await SeedHiddenRecordAsync(factory);

        Assert.Equal(0, await SearchCountAsync(client, surface.EndpointPath!, credential.Secret));
    }
}
