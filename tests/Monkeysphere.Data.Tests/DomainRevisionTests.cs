using DnaX.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monkeysphere.Core;

namespace Monkeysphere.Data.Tests;

public sealed partial class DomainIsolationTests
{
    [Fact]
    public async Task DomainRenameRevisionsRejectCompetingEditsAndPersistAfterRestart()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "Monkeysphere.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        ServiceProvider BuildProvider()
        {
            ServiceCollection services = new();
            services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(dataRoot));
            services.AddDnaXHosting(options => options.WritableDataRoot = dataRoot);
            services.AddMonkeysphereData();
            return services.BuildServiceProvider(validateScopes: true);
        }
        MonkeysphereDomain persisted;
        try
        {
            await using (ServiceProvider provider = BuildProvider())
            {
                await provider.InitializeMonkeysphereDomainsAsync();
                IDomainCatalog domains = provider.GetRequiredService<IDomainCatalog>();
                MonkeysphereDomain original = domains.DefaultDomain;
                Assert.Matches("^[0-9a-f]{32}$", original.Revision);
                Task<bool> RenameAsync(string name) => Task.Run(async () =>
                {
                    try
                    {
                        _ = await domains.RenameAsync(original.Id, name, expectedRevision: original.Revision);
                        return true;
                    }
                    catch (ConcurrencyConflictException) { return false; }
                });
                bool[] results = await Task.WhenAll(RenameAsync("First"), RenameAsync("Second"));
                Assert.Single(results, succeeded => succeeded);
                Assert.Single(results, succeeded => !succeeded);
                MonkeysphereDomain renamed = domains.DefaultDomain;
                Assert.NotEqual(original.Revision, renamed.Revision);
                persisted = await domains.RenameAsync(original.Id, original.Name, expectedRevision: renamed.Revision);
                Assert.NotEqual(original.Revision, persisted.Revision);
                Assert.True(persisted.IsDefault);
                MonkeysphereDomain other = await domains.CreateAsync("Reserved name");
                Assert.Matches("^[0-9a-f]{32}$", other.Revision);
                await Assert.ThrowsAsync<DomainValidationException>(() => domains.RenameAsync(original.Id, other.Name, expectedRevision: persisted.Revision));
                Assert.Equal(persisted, domains.DefaultDomain);
                using CancellationTokenSource cancelled = new();
                cancelled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => domains.RenameAsync(original.Id, "Cancelled", expectedRevision: persisted.Revision, cancellationToken: cancelled.Token));
                Assert.Equal(persisted, domains.DefaultDomain);
            }
            await using (ServiceProvider restarted = BuildProvider())
            {
                await restarted.InitializeMonkeysphereDomainsAsync();
                IDomainCatalog domains = restarted.GetRequiredService<IDomainCatalog>();
                Assert.Equal(persisted, domains.DefaultDomain);
                Assert.Equal(2, domains.Snapshot.Count);
                await Assert.ThrowsAsync<ConcurrencyConflictException>(() => domains.RenameAsync(persisted.Id, "Stale", expectedRevision: "old revision"));
                Assert.Equal(persisted, domains.DefaultDomain);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dataRoot, recursive: true);
        }
    }
}
