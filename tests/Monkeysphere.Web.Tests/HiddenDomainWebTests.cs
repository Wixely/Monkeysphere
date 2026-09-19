using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Tests;

/// <summary>
/// The hosted half of hidden domains: the request-scoped selection rule, and what a remote
/// credential can observe. The Data tests cover the registry/catalogue split itself.
/// </summary>
public sealed partial class RemoteDiscoveryTests
{
    private sealed class HiddenDomainFactory(bool enabled) : RemoteEnabledApplicationFactory
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

    private static async Task<Guid> SeedHiddenDomainAsync(RemoteEnabledApplicationFactory factory, string name)
    {
        IDomainRegistry registry = factory.Services.GetRequiredService<IDomainRegistry>();
        MonkeysphereDomain domain = await registry.CreateAsync(name);
        await registry.SetHiddenAsync(domain.Id, true);
        return domain.Id;
    }

    [Fact]
    public async Task MaintenanceSelectionReachesAHiddenDomainThatOrdinarySelectionRefuses()
    {
        await using HiddenDomainFactory factory = new(enabled: true);
        Guid hidden = await SeedHiddenDomainAsync(factory, "Concealed sweep target");

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        ICurrentDomainScope selection = scope.ServiceProvider.GetRequiredService<ICurrentDomainScope>();

        // No account and no grant in a background scope, so the caller-facing path refuses it...
        Assert.Throws<DomainValidationException>(() => selection.Use(hidden));

        // ...while the sweeps still reach it. Without this, a hidden domain's expired previews and
        // queued media cleanup would accumulate with nothing ever draining them.
        using IDisposable maintenance = selection.UseForMaintenance(hidden);
        Assert.Equal(hidden, selection.Id);
    }

    [Fact]
    public async Task AHiddenDomainIsAbsentFromMcpDomainListingWithoutTheBackstageGrant()
    {
        await using HiddenDomainFactory factory = new(enabled: true);
        Guid hidden = await SeedHiddenDomainAsync(factory, "Concealed remote sphere");
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read"]);

        using JsonDocument listed = await SendAsync(
            client, surface.EndpointPath!, credential.Secret, "tools/call", "list_domains");
        Assert.DoesNotContain(hidden.ToString("D"), listed.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ABackstageGrantSeesAHiddenDomainAndTheDeploymentGateOverridesIt()
    {
        await using (HiddenDomainFactory granted = new(enabled: true))
        {
            Guid hidden = await SeedHiddenDomainAsync(granted, "Concealed remote sphere");
            using HttpClient client = granted.CreateClient();
            var (_, credential, surface) = await EnableRelationshipWritesAsync(
                granted, ["records.read", "backstage"]);
            using JsonDocument listed = await SendAsync(
                client, surface.EndpointPath!, credential.Secret, "tools/call", "list_domains");
            Assert.Contains(hidden.ToString("D"), listed.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }

        // With the gate off the same grant observes nothing, which is what makes it a kill switch
        // rather than merely a way of hiding the settings page.
        await using HiddenDomainFactory off = new(enabled: false);
        Guid concealed = await SeedHiddenDomainAsync(off, "Concealed remote sphere");
        using HttpClient offClient = off.CreateClient();
        var (_, offCredential, offSurface) = await EnableRelationshipWritesAsync(
            off, ["records.read", "backstage"]);
        using JsonDocument offListed = await SendAsync(
            offClient, offSurface.EndpointPath!, offCredential.Secret, "tools/call", "list_domains");
        Assert.DoesNotContain(concealed.ToString("D"), offListed.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }
}
