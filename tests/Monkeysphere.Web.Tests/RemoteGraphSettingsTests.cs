using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Monkeysphere.Core;
using Monkeysphere.Web.Remote;

namespace Monkeysphere.Web.Tests;

/// <summary>
/// How much of the graph a deployment draws stopped being a constant when the limits became
/// configurable, so a remote caller has no way to know this deployment's numbers without asking.
/// These pin that asking works, that it reports enough to tell a raised limit from the measured
/// one, and that setting it neither resets the values nobody mentioned nor accepts a limit the
/// graph cannot draw.
/// </summary>
public sealed partial class RemoteDiscoveryTests
{
    [Fact]
    public async Task McpReadsAndChangesTheGraphBoundsWithoutResettingWhatItWasNotAskedAbout()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(
            factory, ["records.read", "structure.write"]);
        Guid domainId = MonkeysphereDomains.DefaultId;

        using JsonDocument shipped = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_graph_settings",
            new { domainId });
        RemoteGraphSettings before = Structured(shipped).Deserialize<RemoteGraphSettings>(JsonOptions)!;
        Assert.Equal(RelationshipGraphService.DefaultNodes, before.NodeLimit);
        Assert.False(before.IsRaised);

        // The bounds and the defaults come back with the values, so a caller can tell a raised
        // limit from the measured one without knowing this build's numbers.
        Assert.Equal(RelationshipGraphService.MaximumNodes, before.MaximumNodeLimit);
        Assert.Equal(RelationshipGraphService.DefaultEdges, before.DefaultEdgeLimit);

        using JsonDocument raised = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "set_graph_settings",
            new { domainId, nodeLimit = RelationshipGraphService.DefaultNodes + 100 });
        RemoteGraphSettings after = Structured(raised).Deserialize<RemoteGraphSettings>(JsonOptions)!;
        Assert.Equal(RelationshipGraphService.DefaultNodes + 100, after.NodeLimit);
        Assert.True(after.IsRaised);

        // Raising one limit must not quietly reset the other three: a caller changing one setting
        // should not have to restate the rest to keep them.
        Assert.Equal(before.EdgeLimit, after.EdgeLimit);
        Assert.Equal(before.WarnUnsavedChanges, after.WarnUnsavedChanges);
        Assert.Equal(before.KeepRecordsApart, after.KeepRecordsApart);

        using JsonDocument spacing = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "set_graph_settings",
            new { domainId, keepRecordsApart = false });
        RemoteGraphSettings mixed = Structured(spacing).Deserialize<RemoteGraphSettings>(JsonOptions)!;
        Assert.False(mixed.KeepRecordsApart);
        Assert.Equal(after.NodeLimit, mixed.NodeLimit);

        // Refused rather than clamped. This value came from somebody who can be told, which is the
        // same reason the browser's settings page refuses it.
        using JsonDocument tooLarge = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "set_graph_settings",
            new { domainId, nodeLimit = RelationshipGraphService.MaximumNodes + 1 });
        AssertWriteError(tooLarge, "validation_failed");

        using JsonDocument unchanged = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_graph_settings",
            new { domainId });
        Assert.Equal(after.NodeLimit, Structured(unchanged).Deserialize<RemoteGraphSettings>(JsonOptions)!.NodeLimit);
    }

    /// <summary>
    /// The capability list is built from a hand-written array of tool types, so a tool registered
    /// for the server but forgotten there is fully callable and completely invisible: a caller
    /// planning its work from get_capabilities would conclude the capability does not exist. This
    /// catches that, which is exactly how it was found.
    /// </summary>
    [Fact]
    public async Task EveryImplementedToolIsReportedByCapabilities()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read"]);

        using JsonDocument response = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_capabilities");
        HashSet<string> reported = [.. Structured(response).Deserialize<RemoteCapabilities>(JsonOptions)!.Tools.Select(tool => tool.Name)];

        string[] implemented =
        [
            .. typeof(MonkeysphereGraphSettingsTools).Assembly.GetTypes()
                .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
                .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
                .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal),
        ];

        Assert.NotEmpty(implemented);
        string[] missing = [.. implemented.Where(name => !reported.Contains(name))];
        Assert.True(missing.Length == 0,
            $"Callable but absent from get_capabilities: {string.Join(", ", missing)}");
    }

    [Theory]
    [InlineData("records.read", true, false)]
    [InlineData("structure.write", true, true)]
    [InlineData("relationships.write", false, false)]
    public async Task McpGraphSettingsSeparateReadingTheBoundsFromChangingThem(string grant, bool canRead, bool canWrite)
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, [grant]);

        using JsonDocument capabilities = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_capabilities");
        RemoteCapabilities granted = Structured(capabilities).Deserialize<RemoteCapabilities>(JsonOptions)!;
        Assert.Equal(canRead, granted.Tools.Single(tool => tool.Name == "get_graph_settings").Allowed);
        Assert.Equal(canWrite, granted.Tools.Single(tool => tool.Name == "set_graph_settings").Allowed);

        // Changing how much of a domain is drawn belongs with reshaping the domain, not with
        // writing links in it, so the link grant reaches neither tool.
        using JsonDocument attempted = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "set_graph_settings",
            new { domainId = MonkeysphereDomains.DefaultId, nodeLimit = RelationshipGraphService.MinimumNodes });
        if (canWrite)
        {
            _ = Structured(attempted);
        }
        else
        {
            AssertWriteError(attempted, "permission_denied");
        }
    }
}
