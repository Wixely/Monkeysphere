using Dapper;
using DnaX.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monkeysphere.Core;

namespace Monkeysphere.Data.Tests;

public sealed partial class DomainIsolationTests
{
    private static ServiceProvider RegistryProvider(string dataRoot)
    {
        ServiceCollection services = new();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(dataRoot));
        services.AddDnaXHosting(options => options.WritableDataRoot = dataRoot);
        services.AddMonkeysphereData();
        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public async Task RegistryRenameReceiptReplaysAfterRestartWithoutUndoingLaterRename()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "Monkeysphere.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        RecordCommandIdentity identity = new(MonkeysphereDomains.DefaultId, "mcp", new string('A', 64), "domains.rename", Guid.NewGuid(), new string('B', 64));
        RecordCommandReceipt receipt;
        string originalRevision;
        try
        {
            await using (ServiceProvider provider = RegistryProvider(dataRoot))
            {
                await provider.InitializeMonkeysphereDomainsAsync();
                IDomainCatalog domains = provider.GetRequiredService<IDomainCatalog>();
                IDomainCommands commands = provider.GetRequiredService<IDomainCommands>();
                originalRevision = domains.DefaultDomain.Revision;
                receipt = await commands.RenameAsync(identity, "First remote name", originalRevision);
                Assert.Equal(receipt.Items[0].Revision, domains.DefaultDomain.Revision);
                _ = await domains.RenameAsync(identity.DomainId, "Later browser name", expectedRevision: receipt.Items[0].Revision);
            }
            await using (ServiceProvider provider = RegistryProvider(dataRoot))
            {
                await provider.InitializeMonkeysphereDomainsAsync();
                IDomainCatalog domains = provider.GetRequiredService<IDomainCatalog>();
                IDomainCommands commands = provider.GetRequiredService<IDomainCommands>();
                RecordCommandReceipt replay = await commands.RenameAsync(identity, "First remote name", originalRevision);
                Assert.Equal(receipt.Items.ToArray(), replay.Items.ToArray());
                Assert.Equal(receipt.CompletedAtUtc, replay.CompletedAtUtc);
                Assert.Equal("Later browser name", domains.DefaultDomain.Name);
                await Assert.ThrowsAsync<CommandReplayException>(() => commands.RenameAsync(identity with { RequestHash = new string('C', 64) }, "Changed request", originalRevision));
                await Assert.ThrowsAsync<CommandReplayException>(() => commands.RenameAsync(identity with { DomainId = Guid.NewGuid() }, "Different domain", originalRevision));
                await Assert.ThrowsAsync<ConcurrencyConflictException>(() => commands.RenameAsync(identity with { CredentialFingerprint = new string('D', 64) }, "First remote name", originalRevision));
                await using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataRoot, "domains.db") }.ConnectionString);
                await connection.OpenAsync();
                Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM DomainCommandReceipts;"));
                Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM DomainCommandAudit;"));
                await connection.ExecuteAsync("UPDATE DomainCommandReceipts SET RetryUntilUtc = '2000-01-01T00:00:00.0000000+00:00';");
                CommandReplayException expired = await Assert.ThrowsAsync<CommandReplayException>(() => commands.RenameAsync(identity, "First remote name", originalRevision));
                Assert.Equal("retry_expired", expired.Code);
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(dataRoot, recursive: true); }
    }

    [Fact]
    public async Task RegistryAuditFailureAndQuotaLeaveDomainAndCacheUnchanged()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "Monkeysphere.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            await using ServiceProvider provider = RegistryProvider(dataRoot);
            await provider.InitializeMonkeysphereDomainsAsync();
            IDomainCatalog domains = provider.GetRequiredService<IDomainCatalog>();
            IDomainCommands commands = provider.GetRequiredService<IDomainCommands>();
            MonkeysphereDomain original = domains.DefaultDomain;
            RecordCommandIdentity identity = new(original.Id, "mcp", new string('A', 64), "domains.rename", Guid.NewGuid(), new string('B', 64));
            await using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataRoot, "domains.db") }.ConnectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync("CREATE TRIGGER TestRejectDomainAudit BEFORE INSERT ON DomainCommandAudit BEGIN SELECT RAISE(ABORT, 'Test failure'); END;");
            await Assert.ThrowsAsync<SqliteException>(() => commands.RenameAsync(identity, "Failed rename", original.Revision));
            Assert.Equal(original, domains.DefaultDomain);
            Assert.Equal(original.Name, await connection.ExecuteScalarAsync<string>("SELECT Name FROM Domains WHERE IsDefault = 1;"));
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM DomainCommandReceipts;"));
            await connection.ExecuteAsync("DROP TRIGGER TestRejectDomainAudit;");
            await connection.ExecuteAsync("""
                WITH RECURSIVE entries(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM entries WHERE n < @Count)
                INSERT INTO DomainCommandReceipts (Surface, CredentialFingerprint, Action, IdempotencyKey, DomainId, RequestHash, ReceiptJson, RetryUntilUtc, ForgetAfterUtc)
                SELECT 'mcp', @Fingerprint, 'domains.rename', 'quota-' || n, @DomainId, @Hash, NULL, @Future, @Future FROM entries;
                """, new
            {
                Count = DomainCommandLimits.MaximumRetainedCommands,
                Fingerprint = new string('C', 64),
                DomainId = original.Id.ToString("D"),
                Hash = new string('D', 64),
                Future = DateTimeOffset.UtcNow.AddDays(10).ToString("O", System.Globalization.CultureInfo.InvariantCulture)
            });
            await Assert.ThrowsAsync<DomainValidationException>(() => commands.RenameAsync(identity, "Full history", original.Revision));
            Assert.Equal(original, domains.DefaultDomain);
            await connection.ExecuteAsync("UPDATE DomainCommandReceipts SET ForgetAfterUtc = '2000-01-01T00:00:00.0000000+00:00';");
            _ = await commands.RenameAsync(identity, "Successful rename", original.Revision);
            Assert.Equal("Successful rename", domains.DefaultDomain.Name);
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM DomainCommandReceipts;"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(dataRoot, recursive: true); }
    }
}
