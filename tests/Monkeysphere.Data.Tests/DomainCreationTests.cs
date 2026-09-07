using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Data.Tests;

public sealed partial class DomainIsolationTests
{
    [Fact]
    public async Task PendingCreationQuotaRejectsNewRequestsButAllowsAnOwnedRetry()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "Monkeysphere.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            await using ServiceProvider provider = RegistryProvider(dataRoot);
            await provider.InitializeMonkeysphereDomainsAsync();
            IDomainCommands commands = provider.GetRequiredService<IDomainCommands>();
            RecordCommandIdentity identity = new(Guid.NewGuid(), "mcp", new string('A', 64), "domains.create", Guid.NewGuid(), new string('B', 64));
            await using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataRoot, "domains.db") }.ConnectionString);
            await connection.OpenAsync();
            for (int index = 0; index < DomainCommandLimits.MaximumPendingCreations; index++)
            {
                await connection.ExecuteAsync("""
                    INSERT INTO DomainCreations (DomainId, Name, Surface, CredentialFingerprint, Action, IdempotencyKey, RequestHash, CreatedAtUtc)
                    VALUES (@DomainId, @Name, 'mcp', @Fingerprint, 'domains.create', @Key, @Hash, @Now);
                    """, new
                {
                    DomainId = (index == 0 ? identity.DomainId : Guid.NewGuid()).ToString("D"),
                    Name = $"Reserved {index}",
                    Fingerprint = identity.CredentialFingerprint,
                    Key = (index == 0 ? identity.IdempotencyKey : Guid.NewGuid()).ToString("D"),
                    Hash = identity.RequestHash,
                    Now = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                });
            }
            await Assert.ThrowsAsync<DomainValidationException>(() => commands.CreateAsync(identity with { DomainId = Guid.NewGuid(), IdempotencyKey = Guid.NewGuid() }, "Over quota"));
            RecordCommandReceipt receipt = await commands.CreateAsync(identity, "Reserved 0");
            Assert.Equal(identity.DomainId, receipt.Items[0].Id);
            Assert.Equal(DomainCommandLimits.MaximumPendingCreations - 1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM DomainCreations;"));
            Guid unavailableId = Guid.NewGuid();
            string directory = Directory.CreateDirectory(Path.Combine(dataRoot, "domains", unavailableId.ToString("N"))).FullName;
            string marker = Path.Combine(directory, "keep.txt");
            await File.WriteAllTextAsync(marker, "Existing storage");
            await Assert.ThrowsAsync<DomainValidationException>(() => commands.CreateAsync(identity with { DomainId = unavailableId, IdempotencyKey = Guid.NewGuid() }, "Unavailable ID"));
            Assert.Equal("Existing storage", await File.ReadAllTextAsync(marker));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(dataRoot, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreationReservationRecoversWithOrWithoutInitializedStorage(bool removePendingStorage)
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "Monkeysphere.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        RecordCommandIdentity identity = new(Guid.NewGuid(), "mcp", new string('A', 64), "domains.create", Guid.NewGuid(), new string('B', 64));
        try
        {
            await using (ServiceProvider provider = RegistryProvider(dataRoot))
            {
                await provider.InitializeMonkeysphereDomainsAsync();
                IDomainCatalog domains = provider.GetRequiredService<IDomainCatalog>();
                IDomainCommands commands = provider.GetRequiredService<IDomainCommands>();
                _ = await domains.RenameAsync(domains.DefaultDomain.Id, "Renamed default");
                await using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataRoot, "domains.db") }.ConnectionString);
                await connection.OpenAsync();
                await connection.ExecuteAsync("CREATE TRIGGER TestRejectCreationAudit BEFORE INSERT ON DomainCommandAudit BEGIN SELECT RAISE(ABORT, 'Test failure'); END;");
                await Assert.ThrowsAsync<SqliteException>(() => commands.CreateAsync(identity, "Default"));
                Assert.Single(domains.Snapshot);
                Assert.False(domains.TryGet(identity.DomainId, out _));
                Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM DomainCreations;"));
                Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM DomainCommandReceipts;"));
                Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM DomainCommandAudit;"));
                await Assert.ThrowsAsync<DomainValidationException>(() => domains.CreateAsync("default"));
                await Assert.ThrowsAsync<DomainValidationException>(() => domains.RenameAsync(domains.DefaultDomain.Id, "Default"));
                CommandReplayException conflict = await Assert.ThrowsAsync<CommandReplayException>(() => commands.CreateAsync(identity with { RequestHash = new string('C', 64) }, "Changed"));
                Assert.Equal("retry_conflict", conflict.Code);
                await Assert.ThrowsAsync<DomainValidationException>(() => commands.CreateAsync(identity with { CredentialFingerprint = new string('D', 64) }, "Default"));
                await connection.ExecuteAsync("DROP TRIGGER TestRejectCreationAudit;");
            }
            SqliteConnection.ClearAllPools();
            if (removePendingStorage) Directory.Delete(Path.Combine(dataRoot, "domains", identity.DomainId.ToString("N")), recursive: true);
            await using (ServiceProvider provider = RegistryProvider(dataRoot))
            {
                await provider.InitializeMonkeysphereDomainsAsync();
                IDomainCatalog domains = provider.GetRequiredService<IDomainCatalog>();
                IDomainCommands commands = provider.GetRequiredService<IDomainCommands>();
                Assert.Equal(2, domains.Snapshot.Count);
                Assert.True(domains.TryGet(identity.DomainId, out MonkeysphereDomain? created));
                Assert.False(created!.IsDefault);
                Assert.Equal("Default", created.Name);
                RecordCommandReceipt replay = await commands.CreateAsync(identity, "Default");
                Assert.Equal(created.Revision, replay.Items[0].Revision);
                _ = await domains.RenameAsync(identity.DomainId, "Later name");
                RecordCommandReceipt laterReplay = await commands.CreateAsync(identity, "Default");
                Assert.Equal(replay.Items.ToArray(), laterReplay.Items.ToArray());
                Assert.Equal(replay.CompletedAtUtc, laterReplay.CompletedAtUtc);
                Assert.Equal("Later name", domains.Snapshot.Single(domain => domain.Id == identity.DomainId).Name);
                await using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataRoot, "domains.db") }.ConnectionString);
                await connection.OpenAsync();
                Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM DomainCreations;"));
                Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM DomainCommandReceipts;"));
                Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM DomainCommandAudit WHERE Outcome = 'committed';"));
                await using SqliteConnection database = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataRoot, "domains", identity.DomainId.ToString("N"), "monkeysphere.db") }.ConnectionString);
                await database.OpenAsync();
                Assert.Equal(0, await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Records;"));
                Assert.Equal(0, await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM SetupState;"));
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(dataRoot, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrowserCreationResumesAfterPublicationFailureAndCancelledCommandsDoNotReserve(bool retryBeforeRestart)
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "Monkeysphere.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            await using (ServiceProvider provider = RegistryProvider(dataRoot))
            {
                await provider.InitializeMonkeysphereDomainsAsync();
                IDomainCatalog domains = provider.GetRequiredService<IDomainCatalog>();
                IDomainCommands commands = provider.GetRequiredService<IDomainCommands>();
                await using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataRoot, "domains.db") }.ConnectionString);
                await connection.OpenAsync();
                using CancellationTokenSource cancelled = new();
                cancelled.Cancel();
                RecordCommandIdentity identity = new(Guid.NewGuid(), "mcp", new string('A', 64), "domains.create", Guid.NewGuid(), new string('B', 64));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commands.CreateAsync(identity, "Cancelled", cancelled.Token));
                Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM DomainCreations;"));
                await connection.ExecuteAsync("CREATE TRIGGER TestRejectPublication BEFORE INSERT ON Domains BEGIN SELECT RAISE(ABORT, 'Test failure'); END;");
                await Assert.ThrowsAsync<SqliteException>(() => domains.CreateAsync("Browser pending"));
                Assert.Single(domains.Snapshot);
                Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM DomainCreations WHERE Surface = 'browser';"));
                await connection.ExecuteAsync("DROP TRIGGER TestRejectPublication;");
                if (retryBeforeRestart)
                {
                    Guid reservedId = Guid.Parse((await connection.ExecuteScalarAsync<string>("SELECT DomainId FROM DomainCreations;"))!);
                    MonkeysphereDomain recovered = await domains.CreateAsync("Browser pending");
                    Assert.Equal(reservedId, recovered.Id);
                    Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM DomainCreations;"));
                }
            }
            await using (ServiceProvider provider = RegistryProvider(dataRoot))
            {
                await provider.InitializeMonkeysphereDomainsAsync();
                IDomainCatalog domains = provider.GetRequiredService<IDomainCatalog>();
                Assert.Equal(2, domains.Snapshot.Count);
                Assert.Contains(domains.Snapshot, domain => domain.Name == "Browser pending");
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(dataRoot, recursive: true); }
    }
}
