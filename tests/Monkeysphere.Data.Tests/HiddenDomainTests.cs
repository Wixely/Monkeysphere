using DnaX.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monkeysphere.Core;

namespace Monkeysphere.Data.Tests;

/// <summary>
/// A hidden domain must be unobservable to an ordinary caller in exactly the way an unknown domain
/// is, and must stay reachable to the infrastructure that has to service every domain. The two
/// requirements pull in opposite directions, which is why the registry and the catalogue are
/// separate things rather than one list with a flag callers are trusted to check.
/// </summary>
public sealed class HiddenDomainTests
{
    private sealed record Harness(ServiceProvider Provider, string DataRoot) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(DataRoot)) Directory.Delete(DataRoot, recursive: true);
        }
    }

    private static async Task<Harness> CreateAsync(bool backstageAvailable = true, bool backstageActive = false)
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "Monkeysphere.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        ServiceCollection services = new();
        services.AddSingleton<IHostEnvironment>(new HiddenDomainHostEnvironment(dataRoot));
        services.AddDnaXHosting(options => options.WritableDataRoot = dataRoot);
        services.AddSingleton(new BackstageAvailability(backstageAvailable));
        services.AddScoped<IBackstageVisibility>(_ => new FixedVisibility(backstageAvailable && backstageActive));
        services.AddMonkeysphereData();
        ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);
        await provider.InitializeMonkeysphereDomainsAsync();
        return new Harness(provider, dataRoot);
    }

    private sealed class FixedVisibility(bool included) : IBackstageVisibility
    {
        public bool IncludeBackstageRecords { get; } = included;
    }

    private sealed class HiddenDomainHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Monkeysphere.Data.Tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    [Fact]
    public async Task AHiddenDomainIsWithheldFromAnOrdinaryCallerAndShownToBackstage()
    {
        await using Harness ordinary = await CreateAsync(backstageActive: false);
        IDomainRegistry registry = ordinary.Provider.GetRequiredService<IDomainRegistry>();
        MonkeysphereDomain secret = await registry.CreateAsync("Concealed sphere");
        await registry.SetHiddenAsync(secret.Id, true);

        await using (AsyncServiceScope scope = ordinary.Provider.CreateAsyncScope())
        {
            IDomainCatalog catalog = scope.ServiceProvider.GetRequiredService<IDomainCatalog>();
            Assert.DoesNotContain(catalog.Snapshot, domain => domain.Id == secret.Id);
            Assert.False(catalog.TryGet(secret.Id, out _));

            // The registry still holds it, which is what keeps migration, backup and the cleanup
            // sweeps working for a domain nobody can currently select.
            Assert.Contains(registry.All, domain => domain.Id == secret.Id);
            Assert.True(registry.TryGet(secret.Id, out _));
        }

        await using Harness backstage = await CreateAsync(backstageActive: true);
        IDomainRegistry backstageRegistry = backstage.Provider.GetRequiredService<IDomainRegistry>();
        MonkeysphereDomain other = await backstageRegistry.CreateAsync("Concealed sphere");
        await backstageRegistry.SetHiddenAsync(other.Id, true);
        await using (AsyncServiceScope scope = backstage.Provider.CreateAsyncScope())
        {
            IDomainCatalog catalog = scope.ServiceProvider.GetRequiredService<IDomainCatalog>();
            Assert.Contains(catalog.Snapshot, domain => domain.Id == other.Id);
            Assert.True(catalog.TryGet(other.Id, out _));
        }
    }

    [Fact]
    public async Task SelectingAHiddenDomainFailsExactlyAsSelectingAnUnknownOneDoes()
    {
        await using Harness harness = await CreateAsync(backstageActive: false);
        IDomainRegistry registry = harness.Provider.GetRequiredService<IDomainRegistry>();
        MonkeysphereDomain secret = await registry.CreateAsync("Concealed sphere");
        await registry.SetHiddenAsync(secret.Id, true);

        await using AsyncServiceScope scope = harness.Provider.CreateAsyncScope();
        ICurrentDomainScope selection = scope.ServiceProvider.GetRequiredService<ICurrentDomainScope>();

        // Indistinguishable on purpose. A caller that could tell "hidden" from "no such domain"
        // could enumerate hidden domains by reading the difference between the two refusals.
        DomainValidationException hidden = Assert.Throws<DomainValidationException>(() => selection.Use(secret.Id));
        DomainValidationException unknown = Assert.Throws<DomainValidationException>(() => selection.Use(Guid.CreateVersion7()));
        Assert.Equal(unknown.Message, hidden.Message);
    }

    [Fact]
    public async Task TheDefaultDomainCannotBeHidden()
    {
        await using Harness harness = await CreateAsync(backstageActive: true);
        IDomainRegistry registry = harness.Provider.GetRequiredService<IDomainRegistry>();

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            registry.SetHiddenAsync(MonkeysphereDomains.DefaultId, true));

        // Storage refuses it too, so a path that skipped the guard above still cannot do it.
        await using SqliteConnection connection = new($"Data Source={Path.Combine(harness.DataRoot, "domains.db")}");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE Domains SET IsHidden = 1 WHERE IsDefault = 1;";
        SqliteException failure = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        Assert.Contains("cannot be hidden", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HidingRequiresBackstage()
    {
        await using Harness harness = await CreateAsync(backstageActive: false);
        IDomainRegistry registry = harness.Provider.GetRequiredService<IDomainRegistry>();
        MonkeysphereDomain domain = await registry.CreateAsync("Ordinary sphere");

        await using AsyncServiceScope scope = harness.Provider.CreateAsyncScope();
        IDomainCatalog catalog = scope.ServiceProvider.GetRequiredService<IDomainCatalog>();
        await Assert.ThrowsAsync<DomainValidationException>(() => catalog.SetHiddenAsync(domain.Id, true));
    }

    [Fact]
    public async Task WithTheDeploymentGateOffAHiddenDomainIsUnreachableByEveryone()
    {
        // The gate is a kill switch, not merely a way of hiding the settings page: with it off
        // nothing observes a hidden domain, including a caller that would otherwise be backstage.
        await using Harness harness = await CreateAsync(backstageAvailable: false, backstageActive: true);
        IDomainRegistry registry = harness.Provider.GetRequiredService<IDomainRegistry>();
        MonkeysphereDomain secret = await registry.CreateAsync("Concealed sphere");
        await registry.SetHiddenAsync(secret.Id, true);

        await using AsyncServiceScope scope = harness.Provider.CreateAsyncScope();
        IDomainCatalog catalog = scope.ServiceProvider.GetRequiredService<IDomainCatalog>();
        Assert.DoesNotContain(catalog.Snapshot, domain => domain.Id == secret.Id);
        Assert.False(catalog.TryGet(secret.Id, out _));

        // Its data is untouched, which is what makes the gate reversible.
        Assert.Contains(registry.All, domain => domain.Id == secret.Id);
    }

    [Fact]
    public async Task ConcealmentChangesTheDomainRevision()
    {
        await using Harness harness = await CreateAsync(backstageActive: true);
        IDomainRegistry registry = harness.Provider.GetRequiredService<IDomainRegistry>();
        MonkeysphereDomain domain = await registry.CreateAsync("Concealed sphere");

        MonkeysphereDomain hidden = await registry.SetHiddenAsync(domain.Id, true, expectedRevision: domain.Revision);
        Assert.True(hidden.IsHidden);
        Assert.NotEqual(domain.Revision, hidden.Revision);

        // The stale revision must no longer work, or a concurrent caller could reveal a domain it
        // never saw hidden.
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            registry.SetHiddenAsync(domain.Id, false, expectedRevision: domain.Revision));

        MonkeysphereDomain revealed = await registry.SetHiddenAsync(domain.Id, false, expectedRevision: hidden.Revision);
        Assert.False(revealed.IsHidden);
    }
}
