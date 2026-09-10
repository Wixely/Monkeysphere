using System.Text.Json;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Tests;

public sealed partial class RemoteDiscoveryTests
{
    // Every read tool must report an unresolvable domain the same way the write and export
    // tools do: isError with a structured error code a client can act on. Live verification on
    // 2026-09-08 found several returning an unhandled "An error occurred invoking '<tool>'"
    // instead, which fails closed but tells a client nothing. This pins the contract across
    // every domain-scoped read tool rather than the one that happened to be correct.
    [Fact]
    public async Task EveryDomainScopedReadToolReportsAnUnknownDomainAsAStructuredError()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read"]);
        Guid unknown = Guid.NewGuid();
        Guid someId = Guid.NewGuid();

        (string Tool, object Arguments)[] cases =
        [
            ("list_record_types", new { domainId = unknown }),
            ("get_record_type", new { id = someId, domainId = unknown }),
            ("search_records", new { domainId = unknown }),
            ("get_record", new { id = someId, domainId = unknown }),
            ("get_record_relationships", new { id = someId, domainId = unknown }),
            ("query_record_types", new { domainId = unknown }),
            ("list_field_definitions", new { domainId = unknown }),
            ("list_relationship_types", new { domainId = unknown }),
            ("query_record_relationships", new { id = someId, domainId = unknown }),
            ("get_setup_state", new { domainId = unknown }),
            ("list_installed_presets", new { domainId = unknown }),
            // list_relationship_presets is deliberately absent: it is a deployment-wide catalogue
            // that takes no domain selector at all.
            // query_records already satisfies the contract and guards against regression.
            ("query_records", new { domainId = unknown }),
        ];

        List<string> offenders = [];
        foreach ((string tool, object arguments) in cases)
        {
            using JsonDocument response = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", tool, arguments);
            JsonElement result = response.RootElement.GetProperty("result");
            if (!result.TryGetProperty("isError", out JsonElement isError) || !isError.GetBoolean())
            {
                offenders.Add($"{tool}: did not fail closed");
                continue;
            }

            if (!result.TryGetProperty("structuredContent", out JsonElement structured) ||
                !structured.TryGetProperty("error", out JsonElement error) ||
                !error.TryGetProperty("code", out JsonElement code))
            {
                offenders.Add($"{tool}: unstructured error");
                continue;
            }

            string value = code.GetString() ?? "";
            if (value is not ("validation_failed" or "not_found"))
            {
                offenders.Add($"{tool}: unexpected code '{value}'");
            }
        }

        Assert.True(offenders.Count == 0,
            "Read tools must return a structured error for an unknown domain:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    // A valid domain must still return its payload unchanged, so the error wrapping cannot
    // silently alter the success envelope these tools have always returned.
    [Fact]
    public async Task ReadToolsStillReturnTheirPayloadForAValidDomain()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read"]);
        Guid domainId = MonkeysphereDomains.DefaultId;

        using JsonDocument domains = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "list_domains");
        JsonElement listed = Structured(domains);
        Assert.Equal(JsonValueKind.Array, listed.ValueKind);
        Assert.Equal(domainId, listed[0].GetProperty("id").GetGuid());

        using JsonDocument types = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "list_record_types", new { domainId });
        Assert.Equal(JsonValueKind.Array, Structured(types).ValueKind);

        using JsonDocument fields = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "list_field_definitions", new { domainId });
        Assert.True(Structured(fields).TryGetProperty("totalCount", out _));

        using JsonDocument setup = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_setup_state", new { domainId });
        Assert.True(Structured(setup).TryGetProperty("installedPresetCount", out _));

        // A record that does not exist is "not found", not an error, and must stay an empty
        // content list. Cross-domain isolation relies on this shape, so it is pinned here too.
        using JsonDocument missing = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_record",
            new { id = Guid.NewGuid(), domainId });
        JsonElement result = missing.RootElement.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out JsonElement flag) && flag.GetBoolean());
        Assert.Equal(0, result.GetProperty("content").GetArrayLength());
    }
}
