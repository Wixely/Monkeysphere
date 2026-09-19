using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Tests;

/// <summary>
/// Universal tags over MCP. They need no new grant: tags are ordinary record content, so they sit
/// under the existing records.read and records.write authority and inherit backstage with the
/// record. What needs pinning is that a patch which says nothing about tags preserves them, and
/// that a create differing only by its tags is not mistaken for a replay of another command.
/// </summary>
public sealed partial class RemoteDiscoveryTests
{
    private static readonly string[] SeedTags = ["work", "met in person"];
    private static readonly string[] WorkOnly = ["work"];
    private static readonly string[] WorkAndMet = ["work", "met in person"];
    private static readonly string[] WorkUpper = ["WORK"];
    private static readonly string[] MessyTags = ["  remote  ", "Remote", "colleague"];
    private static readonly string[] OtherTags = ["something-else"];
    private static readonly string[] ReplacedTags = ["replaced"];
    private static readonly string[] RefusedTags = ["nope"];

    private static async Task<(Guid TypeId, Guid RecordId)> SeedTaggedAsync(RemoteEnabledApplicationFactory factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        RecordType type = await records.CreateRecordTypeAsync("Tagged subject");
        RecordDetails record = await records.CreateRecordAsync(
            type.Id, "Tagged Tabitha", [], null, SeedTags);
        return (type.Id, record.Record.Id);
    }

    [Fact]
    public async Task GetRecordReturnsUniversalTags()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        (_, Guid recordId) = await SeedTaggedAsync(factory);
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read"]);

        using JsonDocument response = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "get_record", new { domainId = MonkeysphereDomains.DefaultId, id = recordId });
        JsonElement tags = Structured(response).GetProperty("tags");
        Assert.Equal(["work", "met in person"], tags.EnumerateArray().Select(tag => tag.GetString()!).ToArray());
    }

    [Fact]
    public async Task QueryRecordsFiltersByTagAndRequiresEveryTagListed()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        (Guid typeId, _) = await SeedTaggedAsync(factory);
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            _ = await records.CreateRecordAsync(typeId, "Only Work Wendy", [], null, WorkOnly);
        }

        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read"]);

        // One tag matches both records; adding the second narrows rather than widens.
        using JsonDocument broad = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "query_records", new { domainId = MonkeysphereDomains.DefaultId, tags = WorkOnly });
        Assert.Equal(2, Structured(broad).GetProperty("totalCount").GetInt32());

        using JsonDocument narrow = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "query_records",
            new { domainId = MonkeysphereDomains.DefaultId, tags = WorkAndMet });
        Assert.Equal(1, Structured(narrow).GetProperty("totalCount").GetInt32());

        // Matching is case-insensitive, as tag storage is.
        using JsonDocument cased = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "query_records", new { domainId = MonkeysphereDomains.DefaultId, tags = WorkUpper });
        Assert.Equal(2, Structured(cased).GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task CreateAcceptsTagsAndTwoCreatesDifferingOnlyByTagsAreDistinctCommands()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        (Guid typeId, _) = await SeedTaggedAsync(factory);
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read", "records.write"]);

        Guid key = Guid.CreateVersion7();
        using JsonDocument created = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "create_record",
            new
            {
                domainId = MonkeysphereDomains.DefaultId,
                recordTypeId = typeId,
                displayName = "Remote Rosalind",
                idempotencyKey = key,
                values = Array.Empty<object>(),
                tags = MessyTags,
            });
        RecordCommandReceipt receipt = Structured(created).Deserialize<RecordCommandReceipt>(JsonOptions)!;
        Assert.Equal("created", receipt.Items[0].Outcome);

        // Trimmed, case-insensitively de-duplicated, original order kept.
        using JsonDocument read = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "get_record",
            new { domainId = MonkeysphereDomains.DefaultId, id = receipt.Items[0].Id });
        Assert.Equal(
            ["remote", "colleague"],
            Structured(read).GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()!).ToArray());

        // Same idempotency key, different tags: the request hash covers tags, so this is a changed
        // payload rather than a replay, and must be refused instead of returning the old receipt.
        using JsonDocument conflict = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "create_record",
            new
            {
                domainId = MonkeysphereDomains.DefaultId,
                recordTypeId = typeId,
                displayName = "Remote Rosalind",
                idempotencyKey = key,
                values = Array.Empty<object>(),
                tags = OtherTags,
            });
        Assert.Contains("retry_conflict", conflict.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PatchReplacesTagsAndLeavesThemAloneWhenUnmentioned()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        (_, Guid recordId) = await SeedTaggedAsync(factory);
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read", "records.write"]);

        string revision = await ReadRevisionAsync(client, surface.EndpointPath!, credential.Secret, recordId);

        // A patch about something else must not disturb tags. This is the property that lets an
        // existing client keep patching records without silently destroying data it never knew about.
        using JsonDocument renamed = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "patch_record",
            new
            {
                domainId = MonkeysphereDomains.DefaultId,
                id = recordId,
                expectedRevision = revision,
                idempotencyKey = Guid.CreateVersion7(),
                changes = new[] { new { operation = "set_name", value = "Renamed Tabitha" } },
            });
        Assert.Contains("updated", renamed.RootElement.GetRawText(), StringComparison.Ordinal);
        using (JsonDocument afterRename = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "get_record", new { domainId = MonkeysphereDomains.DefaultId, id = recordId }))
        {
            Assert.Equal(
                ["work", "met in person"],
                Structured(afterRename).GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()!).ToArray());
        }

        revision = await ReadRevisionAsync(client, surface.EndpointPath!, credential.Secret, recordId);
        using JsonDocument retagged = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "patch_record",
            new
            {
                domainId = MonkeysphereDomains.DefaultId,
                id = recordId,
                expectedRevision = revision,
                idempotencyKey = Guid.CreateVersion7(),
                changes = new[] { new { operation = "replace_tags", values = ReplacedTags } },
            });
        Assert.Contains("updated", retagged.RootElement.GetRawText(), StringComparison.Ordinal);
        using (JsonDocument afterRetag = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "get_record", new { domainId = MonkeysphereDomains.DefaultId, id = recordId }))
        {
            Assert.Equal(
                ["replaced"],
                Structured(afterRetag).GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()!).ToArray());
        }

        // An empty list is the explicit way to clear them.
        revision = await ReadRevisionAsync(client, surface.EndpointPath!, credential.Secret, recordId);
        using JsonDocument cleared = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "patch_record",
            new
            {
                domainId = MonkeysphereDomains.DefaultId,
                id = recordId,
                expectedRevision = revision,
                idempotencyKey = Guid.CreateVersion7(),
                changes = new[] { new { operation = "replace_tags", values = Array.Empty<string>() } },
            });
        Assert.Contains("updated", cleared.RootElement.GetRawText(), StringComparison.Ordinal);
        using JsonDocument afterClear = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "get_record", new { domainId = MonkeysphereDomains.DefaultId, id = recordId });
        Assert.Empty(Structured(afterClear).GetProperty("tags").EnumerateArray());
    }

    [Fact]
    public async Task TagsOnATypeThatHasThemRemovedAreRefused()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        (Guid typeId, _) = await SeedTaggedAsync(factory);
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMonkeysphereService>()
                .UpdateRecordTypeAsync(typeId, "Tagged subject", null, tagsEnabled: false);
        }

        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read", "records.write"]);
        using JsonDocument refused = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "create_record",
            new
            {
                domainId = MonkeysphereDomains.DefaultId,
                recordTypeId = typeId,
                displayName = "Should not store tags",
                idempotencyKey = Guid.CreateVersion7(),
                values = Array.Empty<object>(),
                tags = RefusedTags,
            });
        Assert.Contains("do not have tags", refused.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ReadRevisionAsync(HttpClient client, string path, string secret, Guid recordId)
    {
        using JsonDocument record = await SendAsync(client, path, secret,
            "tools/call", "get_record", new { domainId = MonkeysphereDomains.DefaultId, id = recordId });
        return Structured(record).GetProperty("revision").GetString()!;
    }
}
