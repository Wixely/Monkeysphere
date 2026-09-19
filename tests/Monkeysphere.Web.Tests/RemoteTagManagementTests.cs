using System.Text.Json;
using DnaX.RemoteAccess;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;
using Monkeysphere.Web.Remote;

namespace Monkeysphere.Web.Tests;

/// <summary>
/// Tag catalogue management over MCP. The grant is deliberately its own: these tools write to the
/// deployment registry rather than to one domain, and two of them delete record content in every
/// domain that holds the tag.
/// </summary>
public sealed partial class RemoteDiscoveryTests
{
    private static readonly Guid[] NoDomains = [];

    private static async Task<(Guid TagId, Guid RecordId)> SeedCatalogueAsync(RemoteEnabledApplicationFactory factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        RecordType type = await records.CreateRecordTypeAsync("Tagged subject");
        RecordDetails record = await records.CreateRecordAsync(type.Id, "Tagged Tabitha", [], null, ["london"]);
        TagDefinition tag = (await scope.ServiceProvider.GetRequiredService<ITagCatalogue>().ListAsync())
            .Single(entry => entry.Name == "london");
        return (tag.Id, record.Record.Id);
    }

    [Fact]
    public void ManagingTagsIsItsOwnGrantOnMcpAlone()
    {
        // If this folded into structure.write, a credential that may create record types would
        // silently also be able to delete record content in every domain.
        Assert.Contains(RemoteCredentialManager.AvailableScopes(DnaXRemoteSurface.Mcp),
            option => option.Scope == "tags.manage");
        Assert.DoesNotContain(RemoteCredentialManager.AvailableScopes(DnaXRemoteSurface.Api),
            option => option.Scope == "tags.manage");
    }

    [Fact]
    public async Task StructureWriteDoesNotAuthoriseTagManagement()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        (Guid tagId, _) = await SeedCatalogueAsync(factory);
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["structure.write"]);

        using JsonDocument denied = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "delete_tag", new { tagId, expectedRevision = "whatever" });
        Assert.Contains("scope", denied.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListTagsReturnsAppearanceAndMembership()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        _ = await SeedCatalogueAsync(factory);
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read"]);

        using JsonDocument listed = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "list_tags");
        JsonElement tag = Structured(listed).EnumerateArray().Single();
        Assert.Equal("london", tag.GetProperty("name").GetString());
        Assert.Matches("^#[0-9a-f]{6}$", tag.GetProperty("colour").GetString()!);
        Assert.Equal("#", tag.GetProperty("icon").GetString());
        Assert.Equal(
            MonkeysphereDomains.DefaultId,
            tag.GetProperty("domainIds").EnumerateArray().Single().GetGuid());
    }

    [Fact]
    public async Task CreateTagJoinsAnExistingLabelRatherThanDuplicatingIt()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        (Guid tagId, _) = await SeedCatalogueAsync(factory);
        Guid second;
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            second = (await scope.ServiceProvider.GetRequiredService<IDomainRegistry>()
                .CreateAsync("Second sphere")).Id;
        }

        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["tags.manage"]);
        using JsonDocument created = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "create_tag", new { domainId = second, name = "LONDON" });

        // The same tag, gaining a domain, with the catalogue's spelling rather than the one sent.
        Assert.Equal(tagId, Structured(created).GetProperty("id").GetGuid());
        Assert.Equal("london", Structured(created).GetProperty("name").GetString());
        Assert.Equal(2, Structured(created).GetProperty("domainIds").GetArrayLength());
    }

    [Fact]
    public async Task AppearanceAndRenameAreRevisionCheckedAndRenameReachesRecords()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        (Guid tagId, Guid recordId) = await SeedCatalogueAsync(factory);
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["tags.manage", "records.read"]);

        string revision = await TagRevisionAsync(client, surface.EndpointPath!, credential.Secret);
        using JsonDocument recoloured = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "set_tag_appearance",
            new { tagId, colour = "#4F7FD0", icon = "🏙", expectedRevision = revision });
        Assert.Equal("#4f7fd0", Structured(recoloured).GetProperty("colour").GetString());
        Assert.Equal("🏙", Structured(recoloured).GetProperty("icon").GetString());

        // The stale revision must now be refused.
        using JsonDocument stale = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "set_tag_appearance",
            new { tagId, colour = "#000000", icon = "#", expectedRevision = revision });
        Assert.Contains("stale_revision", stale.RootElement.GetRawText(), StringComparison.Ordinal);

        revision = await TagRevisionAsync(client, surface.EndpointPath!, credential.Secret);
        using JsonDocument renamed = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "rename_tag", new { tagId, name = "greater london", expectedRevision = revision });
        Assert.Equal("greater london", Structured(renamed).GetProperty("name").GetString());

        // The catalogue is authoritative at once; the record follows when the queue drains.
        await factory.Services.GetRequiredService<ITagMaintenance>().DrainRenamesAsync();
        using JsonDocument record = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "get_record", new { domainId = MonkeysphereDomains.DefaultId, id = recordId });
        Assert.Equal(
            "greater london",
            Structured(record).GetProperty("tags").EnumerateArray().Single().GetString());
    }

    [Fact]
    public async Task UsageIsCountableAndDroppingADomainStripsItsRecords()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        (Guid tagId, Guid recordId) = await SeedCatalogueAsync(factory);
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["tags.manage", "records.read"]);

        using JsonDocument usage = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "count_tag_usage", new { tagId });
        JsonElement entry = Structured(usage).EnumerateArray().Single();
        Assert.Equal(MonkeysphereDomains.DefaultId, entry.GetProperty("domainId").GetGuid());
        Assert.Equal(1, entry.GetProperty("recordCount").GetInt32());

        string revision = await TagRevisionAsync(client, surface.EndpointPath!, credential.Secret);
        using JsonDocument dropped = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "set_tag_domains", new { tagId, domainIds = NoDomains, expectedRevision = revision });
        Assert.Equal(0, Structured(dropped).GetProperty("domainIds").GetArrayLength());

        using JsonDocument record = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "get_record", new { domainId = MonkeysphereDomains.DefaultId, id = recordId });
        Assert.Empty(Structured(record).GetProperty("tags").EnumerateArray());
    }

    [Fact]
    public async Task DeletingRemovesTheTagFromRecordsAndRepeatingItReportsNotFound()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        (Guid tagId, Guid recordId) = await SeedCatalogueAsync(factory);
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["tags.manage", "records.read"]);

        string revision = await TagRevisionAsync(client, surface.EndpointPath!, credential.Secret);
        using JsonDocument deleted = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "delete_tag", new { tagId, expectedRevision = revision });
        Assert.Contains("deleted", deleted.RootElement.GetRawText(), StringComparison.Ordinal);

        using JsonDocument record = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "get_record", new { domainId = MonkeysphereDomains.DefaultId, id = recordId });
        Assert.Empty(Structured(record).GetProperty("tags").EnumerateArray());

        // There is no receipt replay here, deliberately: a repeat fails closed rather than
        // reporting a second success for work that already happened.
        using JsonDocument again = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "delete_tag", new { tagId, expectedRevision = revision });
        Assert.Contains("not", again.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> TagRevisionAsync(HttpClient client, string path, string secret)
    {
        using JsonDocument listed = await SendAsync(client, path, secret, "tools/call", "list_tags");
        return Structured(listed).EnumerateArray().First().GetProperty("revision").GetString()!;
    }
}
