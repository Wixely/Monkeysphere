using DnaX.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Monkeysphere.Core;
using Monkeysphere.Data;

namespace Monkeysphere.Data.Tests;

public sealed class DomainIsolationTests
{
    [Fact]
    public async Task ExistingDataRemainsInRenameableDefaultDomainAndNewDomainsAreIsolated()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "Monkeysphere.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        ServiceCollection services = new();
        TestCurrentDomain currentDomain = new();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(dataRoot));
        services.AddSingleton<ICurrentDomainScope>(currentDomain);
        services.AddSingleton<ICurrentDomain>(currentDomain);
        services.AddDnaXHosting(options => options.WritableDataRoot = dataRoot);
        services.AddMonkeysphereData();

        await using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);
        try
        {
            await provider.InitializeMonkeysphereDomainsAsync();
            IDomainCatalog domains = provider.GetRequiredService<IDomainCatalog>();
            Assert.Equal("Default", domains.DefaultDomain.Name);

            Guid defaultRecordId;
            await using (AsyncServiceScope scope = provider.CreateAsyncScope())
            {
                IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
                IPresetService presets = scope.ServiceProvider.GetRequiredService<IPresetService>();
                RecordType type = await records.CreateRecordTypeAsync("Person");
                RecordDetails record = await records.CreateRecordAsync(type.Id, "Same name", []);
                defaultRecordId = record.Record.Id;
                Assert.True((await presets.GetSetupStatusAsync()).IsComplete);
            }

            MonkeysphereDomain second = await domains.CreateAsync("Online friends");
            Assert.NotEqual(MonkeysphereDomains.DefaultId, second.Id);
            using (currentDomain.Use(second.Id))
            await using (AsyncServiceScope scope = provider.CreateAsyncScope())
            {
                IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
                IPresetService presets = scope.ServiceProvider.GetRequiredService<IPresetService>();
                Assert.Empty(await records.ListRecordTypesAsync());
                Assert.Null(await records.GetRecordAsync(defaultRecordId));
                Assert.False((await presets.GetSetupStatusAsync()).IsComplete);

                RecordType type = await records.CreateRecordTypeAsync("Person");
                _ = await records.CreateRecordAsync(type.Id, "Same name", []);
                Assert.Equal(1, (await records.SearchRecordsAsync(new("Same name"))).TotalCount);
            }

            await using (AsyncServiceScope scope = provider.CreateAsyncScope())
            {
                IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
                Assert.Single(await records.ListRecordTypesAsync());
                Assert.NotNull(await records.GetRecordAsync(defaultRecordId));
                Assert.Equal(1, (await records.SearchRecordsAsync(new("Same name"))).TotalCount);
            }

            MonkeysphereDomain renamed = await domains.RenameAsync(MonkeysphereDomains.DefaultId, "Personal friends");
            Assert.True(renamed.IsDefault);
            Assert.Equal("Personal friends", renamed.Name);
            Assert.True(File.Exists(Path.Combine(dataRoot, "monkeysphere.db")));
            Assert.True(File.Exists(Path.Combine(dataRoot, "domains", second.Id.ToString("N"), "monkeysphere.db")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DomainNamesAreUniqueAndInvalidSelectionsFailClosed()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "Monkeysphere.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        ServiceCollection services = new();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(dataRoot));
        services.AddDnaXHosting(options => options.WritableDataRoot = dataRoot);
        services.AddMonkeysphereData();

        await using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);
        try
        {
            await provider.InitializeMonkeysphereDomainsAsync();
            IDomainCatalog domains = provider.GetRequiredService<IDomainCatalog>();
            _ = await domains.CreateAsync("Fictional characters");
            await Assert.ThrowsAsync<DomainValidationException>(() => domains.CreateAsync(" fictional CHARACTERS "));
            await Assert.ThrowsAsync<DomainValidationException>(() => domains.RenameAsync(Guid.CreateVersion7(), "Missing"));

            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            ICurrentDomainScope selection = scope.ServiceProvider.GetRequiredService<ICurrentDomainScope>();
            Assert.Throws<DomainValidationException>(() => selection.Use(Guid.CreateVersion7()));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    private sealed class TestCurrentDomain : ICurrentDomainScope
    {
        private readonly AsyncLocal<Guid?> _current = new();

        public Guid Id => _current.Value ?? MonkeysphereDomains.DefaultId;

        public IDisposable Use(Guid domainId)
        {
            Guid? previous = _current.Value;
            _current.Value = domainId;
            return new Scope(_current, previous);
        }

        private sealed class Scope(AsyncLocal<Guid?> current, Guid? previous) : IDisposable
        {
            public void Dispose() => current.Value = previous;
        }
    }

    private sealed class TestHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Monkeysphere.Data.Tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
