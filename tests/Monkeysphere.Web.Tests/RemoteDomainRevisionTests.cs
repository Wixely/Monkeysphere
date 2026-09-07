using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;
using Monkeysphere.Web.Remote;

namespace Monkeysphere.Web.Tests;

public sealed partial class RemoteDiscoveryTests
{
    [Fact]
    public async Task DomainDiscoveryReturnsTheCurrentPersistedRenameRevision()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read"]);
        IDomainCatalog domains = factory.Services.GetRequiredService<IDomainCatalog>();
        MonkeysphereDomain original = domains.DefaultDomain;
        using JsonDocument listed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "list_domains");
        using JsonDocument items = JsonDocument.Parse(listed.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
        RemoteDomain before = Assert.Single(items.RootElement.Deserialize<RemoteDomain[]>(JsonOptions)!);
        Assert.Equal(original.Revision, before.Revision);
        MonkeysphereDomain renamed = await domains.RenameAsync(original.Id, "Renamed by browser service", expectedRevision: before.Revision);
        using JsonDocument queried = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "query_domains");
        RemoteDomain after = Assert.Single(Structured(queried).Deserialize<PagedResult<RemoteDomain>>(JsonOptions)!.Items);
        Assert.Equal(renamed.Name, after.Name);
        Assert.Equal(renamed.Revision, after.Revision);
        Assert.NotEqual(before.Revision, after.Revision);
    }
}
