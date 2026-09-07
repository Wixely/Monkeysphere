using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;
using Monkeysphere.Web.Remote;

namespace Monkeysphere.Web.Tests;

public sealed partial class RemoteDiscoveryTests
{
    [Fact]
    public async Task StructuredSearchCombinesFiltersSortsAndPreservesEmptyPageTotals()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        RecordType type = await records.CreateRecordTypeAsync("Search fixture");
        FieldDefinition number = await records.CreateAndAttachFieldAsync(type.Id, new("Score", FieldTypes.Number, false));
        FieldDefinition tags = await records.CreateAndAttachFieldAsync(type.Id, new("Tags", FieldTypes.Tags, false));
        FieldDefinition date = await records.CreateAndAttachFieldAsync(type.Id, new("Day", FieldTypes.ExactDate, false));
        RecordDetails alpha = await records.CreateRecordAsync(type.Id, "Alpha", [new(number.Id, "2"), new(tags.Id, Tags: ["group", "100%_literal"]), new(date.Id, "2020-01-01")], ["Find alias"]);
        RecordDetails beta = await records.CreateRecordAsync(type.Id, "Beta", [new(number.Id, "10"), new(tags.Id, Tags: ["group"]), new(date.Id, "2021-01-01")]);
        _ = await records.CreateRecordAsync(type.Id, "Gamma", [new(number.Id, "30"), new(tags.Id, Tags: ["other"]), new(date.Id, "2019-01-01")]);
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read"]);
        async Task<RemotePage<RemoteRecordSummary>> QueryAsync(object request)
        {
            using JsonDocument result = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "query_records", request);
            return Structured(result).Deserialize<RemotePage<RemoteRecordSummary>>(JsonOptions)!;
        }
        var request = new
        {
            recordTypeId = type.Id,
            filters = new[] { new RemoteRecordFilter(tags.Id, "equals", "group"), new RemoteRecordFilter(number.Id, "greater_than", "1") },
            sort = new RemoteRecordSort(number.Id, true),
            page = 1,
            pageSize = 1
        };
        RemotePage<RemoteRecordSummary> first = await QueryAsync(request);
        Assert.Equal(2, first.TotalCount);
        Assert.Equal(beta.Record.Id, Assert.Single(first.Items).Id);
        RemotePage<RemoteRecordSummary> second = await QueryAsync(request with { page = 2 });
        Assert.Equal(alpha.Record.Id, Assert.Single(second.Items).Id);
        RemotePage<RemoteRecordSummary> empty = await QueryAsync(request with { page = 3 });
        Assert.Empty(empty.Items);
        Assert.Equal(2, empty.TotalCount);
        Assert.Equal(alpha.Record.Id, Assert.Single((await QueryAsync(new { query = "Find alias" })).Items).Id);
        Assert.Equal(alpha.Record.Id, Assert.Single((await QueryAsync(new { filters = new[] { new RemoteRecordFilter(tags.Id, "contains", "%_") } })).Items).Id);
        Assert.Empty((await QueryAsync(new { query = "' OR 1=1 --" })).Items);
        Assert.Empty((await QueryAsync(new { query = "Alpha", filters = new[] { new RemoteRecordFilter(date.Id, "before", "2020-01-01") } })).Items);
        Assert.Empty((await QueryAsync(new { query = "Alpha", filters = new[] { new RemoteRecordFilter(date.Id, "after", "2020-01-01") } })).Items);
        Assert.Equal(alpha.Record.Id, Assert.Single((await QueryAsync(new { filters = new[] { new RemoteRecordFilter(date.Id, "after", "2019-12-31"), new RemoteRecordFilter(date.Id, "before", "2020-01-02") } })).Items).Id);
        Assert.Equal(["Gamma", "Beta", "Alpha"], (await QueryAsync(new { sort = new RemoteRecordSort(Descending: true) })).Items.Select(item => item.DisplayName));
        using JsonDocument legacy = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "search_records", new { query = "Find alias", page = 0, pageSize = 0 });
        Assert.Equal(alpha.Record.Id, Assert.Single(Structured(legacy).Deserialize<RemotePage<RemoteRecordSummary>>(JsonOptions)!.Items).Id);
    }

    [Fact]
    public async Task StructuredSearchRejectsInvalidBoundsOperatorsValuesAndForeignReferences()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        RecordType type = await records.CreateRecordTypeAsync("Default type");
        FieldDefinition number = await records.CreateAndAttachFieldAsync(type.Id, new("Score", FieldTypes.Number, false));
        MonkeysphereDomain other = await factory.Services.GetRequiredService<IDomainCatalog>().CreateAsync("Other search domain");
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read"]);
        object[] invalid = [new { page = 0 }, new { page = 10001 }, new { pageSize = 101 }, new { query = new string('q', 501) },
            new { filters = new RemoteRecordFilter?[] { null } },
            new { filters = Enumerable.Repeat(new RemoteRecordFilter(number.Id, "equals", "1"), 11).ToArray() },
            new { filters = new[] { new RemoteRecordFilter(number.Id, "bogus", "1") } },
            new { filters = new[] { new RemoteRecordFilter(number.Id, "equals", " ") } },
            new { filters = new[] { new RemoteRecordFilter(number.Id, "equals", new string('x', 2001)) } },
            new { filters = new[] { new RemoteRecordFilter(number.Id, "greater_than", "NaN") } },
            new { filters = new[] { new RemoteRecordFilter(number.Id, "less_than", "Infinity") } },
            new { filters = new[] { new RemoteRecordFilter(number.Id, "before", "not a date") } },
            new { filters = new[] { new { fieldDefinitionId = number.Id, @operator = "equals", value = "1", typo = true } } },
            new { sort = new { descending = true, typo = true } },
            new { domainId = other.Id, recordTypeId = type.Id },
            new { domainId = other.Id, sort = new RemoteRecordSort(number.Id) },
            new { domainId = other.Id, filters = new[] { new RemoteRecordFilter(number.Id, "equals", "1") } },
            new { domainId = Guid.NewGuid() }];
        foreach (object request in invalid)
        {
            using JsonDocument result = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "query_records", request);
            Assert.True(result.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        }
        using JsonDocument isolated = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "query_records", new { domainId = other.Id });
        Assert.Equal(0, Structured(isolated).GetProperty("totalCount").GetInt32());
    }

    [Theory]
    [InlineData("instance.read")]
    [InlineData("records.write")]
    [InlineData("domains.manage")]
    public async Task StructuredSearchRequiresRecordReads(string grant)
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, [grant]);
        using JsonDocument capabilities = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_capabilities");
        Assert.False(Structured(capabilities).Deserialize<RemoteCapabilities>(JsonOptions)!.Tools.Single(tool => tool.Name == "query_records").Allowed);
        using JsonDocument result = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "query_records");
        AssertWriteError(result, "permission_denied");
    }
}
