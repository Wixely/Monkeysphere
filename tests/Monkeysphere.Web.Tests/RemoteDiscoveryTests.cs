using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using DnaX.RemoteAccess;
using DnaX.RemoteAccess.Mcp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Monkeysphere.Core;
using Monkeysphere.Web.Remote;

namespace Monkeysphere.Web.Tests;

public sealed partial class RemoteDiscoveryTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("records.read", true)]
    [InlineData("instance.read", false)]
    public async Task DiscoveryMatchesRegisteredToolsAndEffectivePermissions(string scope, bool canReadRecords)
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        IDnaXRemoteAccessAdministration administration = factory.Services.GetRequiredService<IDnaXRemoteAccessAdministration>();
        DnaXGeneratedCredential credential = await administration.RotateCredentialAsync(DnaXRemoteSurface.Mcp, 0, [scope]);
        DnaXRemoteEffectiveSurface surface = await administration.SetActivationAsync(DnaXRemoteSurface.Mcp, true, false, credential.Version);
        using JsonDocument discovery = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/list");
        string[] registered = discovery.RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!).Order(StringComparer.Ordinal).ToArray();
        Assert.Contains("list_domains", registered);
        Assert.Contains("get_instance_info", registered);
        Assert.Contains("get_capabilities", registered);
        foreach (JsonElement tool in discovery.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray())
        {
            if (tool.GetProperty("name").GetString() == "search_records")
            {
                Assert.True(tool.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("domainId", out _));
            }
        }

        using JsonDocument response = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_capabilities");
        RemoteCapabilities capabilities = response.RootElement.GetProperty("result").GetProperty("structuredContent")
            .Deserialize<RemoteCapabilities>(JsonOptions)!;
        Assert.Equal(registered, capabilities.Tools.Select(tool => tool.Name));
        Assert.Equal([scope], capabilities.GrantedScopes);
        Assert.True(capabilities.SupportsDomainSelection);
        Assert.True(capabilities.SupportsWrites);
        Assert.False(capabilities.SupportsFileTransfer);
        Assert.Equal(1_048_576, capabilities.RequestLimits.MaximumRequestBodyBytes);
        Assert.Equal(8, capabilities.RequestLimits.MaximumConcurrentRequests);
        Assert.Equal(30, capabilities.RequestLimits.RequestTimeoutSeconds);
        Assert.Equal(24, capabilities.RecordWriteLimits.RetryWindowHours);
        Assert.Equal(7, capabilities.RecordWriteLimits.TombstoneRetentionDays);
        Assert.Equal(1000, capabilities.RecordWriteLimits.MaximumPatchChanges);
        Assert.Equal(10_000, capabilities.RecordWriteLimits.MaximumRetainedCommandsPerDomain);
        Assert.Equal(100, capabilities.RecordWriteLimits.MaximumBatchRecords);
        Assert.Equal(15, capabilities.RecordWriteLimits.PreviewLifetimeMinutes);
        Assert.Equal(100, capabilities.RecordWriteLimits.MaximumRetainedPreviewsPerDomain);
        Assert.Equal(1_048_576, capabilities.RecordWriteLimits.MaximumPreviewBytes);
        Assert.False(capabilities.Tools.Single(tool => tool.Name == "create_record").Allowed);
        Assert.Equal(canReadRecords, capabilities.Tools.Single(tool => tool.Name == "get_record").Allowed);
        Assert.True(capabilities.Tools.Single(tool => tool.Name == "get_instance_info").Allowed);

        using JsonDocument fieldTypes = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "list_field_types");
        if (canReadRecords)
        {
            RemoteFieldTypeCatalog catalog = fieldTypes.RootElement.GetProperty("result").GetProperty("structuredContent")
                .Deserialize<RemoteFieldTypeCatalog>(JsonOptions)!;
            Assert.Equal(FieldTypes.Recognized, catalog.Types.Select(type => type.TypeId));
            Assert.True(catalog.AllowsCustomTypes);
        }
        else
        {
            Assert.True(fieldTypes.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        }

        using JsonDocument infoResponse = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_instance_info");
        RemoteInstanceInfo info = infoResponse.RootElement.GetProperty("result").GetProperty("structuredContent")
            .Deserialize<RemoteInstanceInfo>(JsonOptions)!;
        Assert.Equal("Monkeysphere", info.Application);
        Assert.Equal(capabilities.ContractVersion, info.ContractVersion);
        Assert.DoesNotContain('+', info.Version);
        Assert.True(info.DatabaseSchemaVersion > 0);

        if (!canReadRecords)
        {
            using JsonDocument denied = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "list_domains");
            Assert.True(denied.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        }

        await administration.RevokeCredentialAsync(DnaXRemoteSurface.Mcp, surface.Version);
        using HttpRequestMessage revoked = CreateRequest(surface.EndpointPath!, credential.Secret, "tools/call", "get_capabilities");
        using HttpResponseMessage revokedResponse = await client.SendAsync(revoked);
        // With its only credential revoked, DnaX hides the unavailable surface.
        Assert.Equal(HttpStatusCode.NotFound, revokedResponse.StatusCode);
    }

    [Fact]
    public async Task RecordDiscoveryPreservesChoiceConfigurationAndStructuredTemporalValues()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        Guid typeId;
        Guid recordId;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IMonkeysphereService service = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            RecordType type = await service.CreateRecordTypeAsync("Discovery fixture");
            typeId = type.Id;
            FieldDefinition choice = await service.CreateAndAttachFieldAsync(type.Id,
                new CreateFieldRequest("Category", FieldTypes.Choice, true, ["Friend", "Colleague"]));
            FieldDefinition temporal = await service.CreateAndAttachFieldAsync(type.Id,
                new CreateFieldRequest("First meeting", FieldTypes.Temporal, false));
            RecordDetails record = await service.CreateRecordAsync(type.Id, "Fictional person",
                [new(choice.Id, "Friend"), new(temporal.Id, Temporal: new("2010s", TemporalPrecision.Decade, true, "Estimated"))]);
            recordId = record.Record.Id;
        }

        IDnaXRemoteAccessAdministration administration = factory.Services.GetRequiredService<IDnaXRemoteAccessAdministration>();
        DnaXGeneratedCredential credential = await administration.RotateCredentialAsync(DnaXRemoteSurface.Mcp, 0, ["records.read"]);
        DnaXRemoteEffectiveSurface surface = await administration.SetActivationAsync(DnaXRemoteSurface.Mcp, true, false, credential.Version);
        using JsonDocument typeResponse = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "get_record_type", new { id = typeId });
        RemoteRecordType typeResult = typeResponse.RootElement.GetProperty("result").GetProperty("structuredContent")
            .Deserialize<RemoteRecordType>(JsonOptions)!;
        RemoteFieldDefinition choiceResult = typeResult.Fields.Single(field => field.TypeId == FieldTypes.Choice);
        Assert.Equal(["Friend", "Colleague"], choiceResult.ChoiceOptions);
        Assert.Contains("Colleague", choiceResult.ConfigurationJson, StringComparison.Ordinal);
        Assert.True(choiceResult.IsRequired);
        Assert.Equal("active", choiceResult.Lifecycle);

        using JsonDocument recordResponse = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "get_record", new { id = recordId });
        RemoteRecord recordResult = recordResponse.RootElement.GetProperty("result").GetProperty("structuredContent")
            .Deserialize<RemoteRecord>(JsonOptions)!;
        Assert.Equal("Friend", recordResult.Values.Single(value => value.TypeId == FieldTypes.Choice).ScalarValue);
        RemoteRecordValue date = recordResult.Values.Single(value => value.TypeId == FieldTypes.Temporal);
        Assert.NotNull(date.Value); // Legacy display value stays available.
        Assert.Null(date.ScalarValue);
        Assert.Equal(new RemoteTemporalValue("2010", "decade", true, "Estimated"), date.Temporal);
    }

    [Fact]
    public async Task SchemaDiscoveryPagesPastLegacyCapsAndRejectsCrossDomainSelectors()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        Guid secondDomainId;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IMonkeysphereService service = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            for (int index = 0; index < 105; index++)
            {
                await service.CreateRecordTypeAsync($"Type {index:D3}");
            }
            secondDomainId = (await scope.ServiceProvider.GetRequiredService<IDomainCatalog>().CreateAsync("Isolated schema")).Id;
            using (scope.ServiceProvider.GetRequiredService<ICurrentDomainScope>().Use(secondDomainId))
            {
                RecordType type = await service.CreateRecordTypeAsync("Other domain type");
                await service.CreateAndAttachFieldAsync(type.Id, new("Custom value", "custom.value", false));
            }
        }

        IDnaXRemoteAccessAdministration administration = factory.Services.GetRequiredService<IDnaXRemoteAccessAdministration>();
        DnaXGeneratedCredential credential = await administration.RotateCredentialAsync(DnaXRemoteSurface.Mcp, 0, ["records.read"]);
        DnaXRemoteEffectiveSurface surface = await administration.SetActivationAsync(DnaXRemoteSurface.Mcp, true, false, credential.Version);
        using JsonDocument page = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "query_record_types", new { page = 2, pageSize = 100 });
        PagedResult<RemoteTypeSummary> types = page.RootElement.GetProperty("result").GetProperty("structuredContent")
            .Deserialize<PagedResult<RemoteTypeSummary>>(JsonOptions)!;
        Assert.Equal(105, types.TotalCount);
        Assert.Equal(5, types.Items.Count);
        Assert.Equal("Type 100", types.Items[0].Name);

        using JsonDocument fields = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "list_field_definitions", new { domainId = secondDomainId });
        PagedResult<RemoteReusableField> fieldPage = fields.RootElement.GetProperty("result").GetProperty("structuredContent")
            .Deserialize<PagedResult<RemoteReusableField>>(JsonOptions)!;
        Assert.Equal("custom.value", Assert.Single(fieldPage.Items).TypeId);
        using JsonDocument defaultFields = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "list_field_definitions");
        Assert.Equal(0, defaultFields.RootElement.GetProperty("result").GetProperty("structuredContent")
            .Deserialize<PagedResult<RemoteReusableField>>(JsonOptions)!.TotalCount);

        foreach (object invalid in new object[] { new { page = 0 }, new { pageSize = 101 }, new { domainId = Guid.NewGuid() }, new { domainId = "invalid" } })
        {
            using JsonDocument denied = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "query_record_types", invalid);
            Assert.True(denied.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        }
    }

    [Fact]
    public async Task RelationshipDiscoveryPagesBeyondFiveHundredWithCorrectPerspective()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        Guid focusId;
        Guid otherDomainId;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IMonkeysphereService service = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            IRelationshipService relationships = scope.ServiceProvider.GetRequiredService<IRelationshipService>();
            RecordType type = await service.CreateRecordTypeAsync("People");
            focusId = (await service.CreateRecordAsync(type.Id, "Focus", [])).Record.Id;
            RelationshipType relationship = await relationships.CreateTypeAsync(new("knows", RelationshipDirectionality.Directional, "known by"));
            for (int index = 0; index < 505; index++)
            {
                Guid peerId = (await service.CreateRecordAsync(type.Id, $"Peer {index:D3}", [])).Record.Id;
                await relationships.CreateAsync(relationship.Id, peerId, focusId);
            }
            otherDomainId = (await scope.ServiceProvider.GetRequiredService<IDomainCatalog>().CreateAsync("Other links")).Id;
        }

        IDnaXRemoteAccessAdministration administration = factory.Services.GetRequiredService<IDnaXRemoteAccessAdministration>();
        DnaXGeneratedCredential credential = await administration.RotateCredentialAsync(DnaXRemoteSurface.Mcp, 0, ["records.read"]);
        DnaXRemoteEffectiveSurface surface = await administration.SetActivationAsync(DnaXRemoteSurface.Mcp, true, false, credential.Version);
        using JsonDocument page = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "query_record_relationships", new { id = focusId, page = 6, pageSize = 100 });
        PagedResult<RelationshipView> links = page.RootElement.GetProperty("result").GetProperty("structuredContent")
            .Deserialize<PagedResult<RelationshipView>>(JsonOptions)!;
        Assert.Equal(505, links.TotalCount);
        Assert.Equal(5, links.Items.Count);
        Assert.Equal("Peer 500", links.Items[0].RelatedDisplayName);
        Assert.All(links.Items, link => { Assert.Equal("known by", link.Label); Assert.False(link.IsOutgoing); });
        using JsonDocument isolated = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "query_record_relationships", new { id = focusId, domainId = otherDomainId });
        Assert.Equal(0, isolated.RootElement.GetProperty("result").GetProperty("structuredContent")
            .Deserialize<PagedResult<RelationshipView>>(JsonOptions)!.TotalCount);
    }

    [Theory]
    [InlineData("temporal", "temporal", true)]
    [InlineData("tags", "tags", true)]
    [InlineData("location", "location", true)]
    [InlineData("custom.value", "scalarValue", false)]
    public async Task FieldInputSchemasPreserveStructuredAndUnknownTypes(string typeId, string property, bool recognized)
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        IDnaXRemoteAccessAdministration administration = factory.Services.GetRequiredService<IDnaXRemoteAccessAdministration>();
        DnaXGeneratedCredential credential = await administration.RotateCredentialAsync(DnaXRemoteSurface.Mcp, 0, ["records.read"]);
        DnaXRemoteEffectiveSurface surface = await administration.SetActivationAsync(DnaXRemoteSurface.Mcp, true, false, credential.Version);
        using JsonDocument response = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "get_field_type_schema", new { typeId });
        RemoteFieldTypeSchema schema = response.RootElement.GetProperty("result").GetProperty("structuredContent")
            .Deserialize<RemoteFieldTypeSchema>(JsonOptions)!;
        Assert.Equal(property, schema.ValueProperty);
        Assert.Equal(recognized, schema.Recognized);
        if (typeId == "temporal")
        {
            JsonElement properties = schema.ValueSchema.GetProperty("properties");
            Assert.Contains("decade", properties.GetProperty("precision").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
            Assert.True(properties.TryGetProperty("isApproximate", out _));
            Assert.True(properties.TryGetProperty("approximationNote", out _));
        }
        if (!recognized) Assert.Equal("string", schema.ValueSchema.GetProperty("type").GetString());
    }

    [Fact]
    public async Task CredentialPermissionSelectionPreservesNarrowGrantsAndRejectsUnsupportedElevation()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        IDnaXRemoteAccessAdministration administration = scope.ServiceProvider.GetRequiredService<IDnaXRemoteAccessAdministration>();
        RemoteCredentialManager manager = scope.ServiceProvider.GetRequiredService<RemoteCredentialManager>();
        DnaXGeneratedCredential first = await administration.RotateCredentialAsync(DnaXRemoteSurface.Mcp, 0, ["instance.read"]);
        DnaXRemoteEffectiveSurface surface = await administration.SetActivationAsync(DnaXRemoteSurface.Mcp, true, false, first.Version);
        Assert.Equal(["instance.read"], RemoteCredentialManager.InitialScopes(surface));
        await Assert.ThrowsAsync<DomainValidationException>(() => manager.RotateAsync(DnaXRemoteSurface.Mcp, surface.Version, ["unsupported.write"]));
        DnaXGeneratedCredential second = await manager.RotateAsync(DnaXRemoteSurface.Mcp, surface.Version,
            RemoteCredentialManager.InitialScopes(surface).ToArray());
        using JsonDocument capabilities = await SendAsync(client, surface.EndpointPath!, second.Secret, "tools/call", "get_capabilities");
        Assert.Equal(["instance.read"], capabilities.RootElement.GetProperty("result").GetProperty("structuredContent")
            .Deserialize<RemoteCapabilities>(JsonOptions)!.GrantedScopes);
        using JsonDocument denied = await SendAsync(client, surface.EndpointPath!, second.Secret, "tools/call", "list_domains");
        Assert.True(denied.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        using HttpRequestMessage oldCredential = CreateRequest(surface.EndpointPath!, first.Secret, "tools/call", "get_capabilities");
        using HttpResponseMessage oldResponse = await client.SendAsync(oldCredential);
        Assert.Equal(HttpStatusCode.Unauthorized, oldResponse.StatusCode);
        await Assert.ThrowsAsync<DnaXRemoteConcurrencyException>(() => manager.RotateAsync(DnaXRemoteSurface.Mcp, surface.Version, ["records.read"]));
    }

    [Fact]
    public async Task ChunkTransferPrototypeFitsPinnedTransportAndOversizeIsRejected()
    {
        await using TransferPrototypeFactory factory = new();
        using HttpClient client = factory.CreateClient();
        IDnaXRemoteAccessAdministration administration = factory.Services.GetRequiredService<IDnaXRemoteAccessAdministration>();
        DnaXGeneratedCredential credential = await administration.RotateCredentialAsync(DnaXRemoteSurface.Mcp, 0, ["records.read"]);
        DnaXRemoteEffectiveSurface surface = await administration.SetActivationAsync(DnaXRemoteSurface.Mcp, true, false, credential.Version);
        byte[] bytes = new byte[256 * 1024];
        RandomNumberGenerator.Fill(bytes);
        using JsonDocument response = await SendAsync(client, surface.EndpointPath!, credential.Secret,
            "tools/call", "probe_binary_chunk", new { contentBase64 = Convert.ToBase64String(bytes) });
        JsonElement result = response.RootElement.GetProperty("result").GetProperty("structuredContent");
        Assert.Equal(bytes.Length, result.GetProperty("length").GetInt32());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), result.GetProperty("sha256").GetString());

        using HttpRequestMessage oversized = CreateRequest(surface.EndpointPath!, credential.Secret, "tools/call", "probe_binary_chunk",
            new { contentBase64 = new string('A', 1_048_576) });
        using HttpResponseMessage rejected = await client.SendAsync(oversized);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode);
    }

    [Fact]
    public async Task UnrelatedScopeCannotInvokeDiscovery()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        IDnaXRemoteAccessAdministration administration = factory.Services.GetRequiredService<IDnaXRemoteAccessAdministration>();
        DnaXGeneratedCredential credential = await administration.RotateCredentialAsync(DnaXRemoteSurface.Mcp, 0, ["unrelated.read"]);
        DnaXRemoteEffectiveSurface surface = await administration.SetActivationAsync(DnaXRemoteSurface.Mcp, true, false, credential.Version);
        foreach (string tool in new[] { "get_instance_info", "get_capabilities" })
        {
            using JsonDocument response = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", tool);
            Assert.True(response.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        }
    }

    private static async Task<JsonDocument> SendAsync(HttpClient client, string path, string secret, string method, string? tool = null, object? arguments = null)
    {
        using HttpRequestMessage request = CreateRequest(path, secret, method, tool, arguments);
        using HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        string payload = body.StartsWith("event:", StringComparison.Ordinal) || body.StartsWith("data:", StringComparison.Ordinal)
            ? body.Split('\n').First(line => line.StartsWith("data:", StringComparison.Ordinal))[5..].Trim()
            : body;
        return JsonDocument.Parse(payload);
    }

    private static HttpRequestMessage CreateRequest(string path, string secret, string method, string? tool, object? arguments = null)
    {
        HttpRequestMessage request = new(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        request.Headers.Add("Mcp-Method", method);
        if (tool is not null) request.Headers.Add("Mcp-Name", tool);
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method,
            @params = new
            {
                name = tool,
                arguments = arguments ?? new { },
                _meta = new Dictionary<string, object?>
                {
                    ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                    ["io.modelcontextprotocol/clientCapabilities"] = new { },
                },
            },
        }, JsonOptions), Encoding.UTF8, "application/json");
        return request;
    }

    private sealed class TransferPrototypeFactory : RemoteEnabledApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.AddDnaXRemoteMcp().WithTools<TransferProbeTools>());
        }
    }

    // Registered only in the disposable test host; never in Monkeysphere's production tools.
    [McpServerToolType]
    public sealed class TransferProbeTools
    {
        [McpServerTool(Name = "probe_binary_chunk", UseStructuredContent = true)]
        public static TransferProbeResult Probe(IHttpContextAccessor accessor, string contentBase64)
        {
            if (accessor.HttpContext?.User.HasDnaXRemoteScope("records.read") != true)
            {
                throw new UnauthorizedAccessException();
            }
            byte[] bytes = Convert.FromBase64String(contentBase64);
            return new(bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
        }
    }

    public sealed record TransferProbeResult(int Length, string Sha256);
}
