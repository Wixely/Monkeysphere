using System.Text.Json;
using System.IO.Compression;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;
using Monkeysphere.Data;
using Monkeysphere.Web.Remote;

namespace Monkeysphere.Web.Tests;

public sealed partial class RemoteDiscoveryTests
{
    [Fact]
    public async Task BackupOfPendingCreationRestoresAndCompletesTheSameDomain()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "Monkeysphere.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        RecordCommandIdentity identity = new(Guid.NewGuid(), "mcp", new string('A', 64), "domains.create", Guid.NewGuid(), new string('B', 64));
        try
        {
            string packagePath;
            await using (PersistentApplicationFactory factory = new(dataRoot))
            {
                IDomainCommands commands = factory.Services.GetRequiredService<IDomainCommands>();
                await using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataRoot, "domains.db") }.ConnectionString);
                await connection.OpenAsync();
                await connection.ExecuteAsync("CREATE TRIGGER TestRejectCreationAudit BEFORE INSERT ON DomainCommandAudit BEGIN SELECT RAISE(ABORT, 'Test failure'); END;");
                await Assert.ThrowsAsync<SqliteException>(() => commands.CreateAsync(identity, "Pending at backup"));
                await connection.ExecuteAsync("DROP TRIGGER TestRejectCreationAudit;");
                using IServiceScope scope = factory.Services.CreateScope();
                IBackupService backups = scope.ServiceProvider.GetRequiredService<IBackupService>();
                BackupInfo backup = await backups.CreateAsync();
                _ = await backups.ValidateAsync(backup.Id);
                packagePath = Path.Combine(dataRoot, "backups", backup.FileName);
                using (ZipArchive archive = ZipFile.OpenRead(packagePath))
                    Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains(identity.DomainId.ToString("N"), StringComparison.Ordinal));
                _ = await commands.CreateAsync(identity, "Pending at backup");
                _ = await factory.Services.GetRequiredService<IDomainCatalog>().RenameAsync(identity.DomainId, "After backup");
            }
            SqliteConnection.ClearAllPools();
            _ = await OfflineBackupRestore.RestoreAsync(packagePath, dataRoot);
            Assert.False(Directory.Exists(Path.Combine(dataRoot, "domains", identity.DomainId.ToString("N"))));
            await using (PersistentApplicationFactory factory = new(dataRoot))
            {
                IDomainCatalog domains = factory.Services.GetRequiredService<IDomainCatalog>();
                Assert.Equal(2, domains.Snapshot.Count);
                Assert.True(domains.TryGet(identity.DomainId, out MonkeysphereDomain? created));
                Assert.Equal("Pending at backup", created!.Name);
                RecordCommandReceipt receipt = await factory.Services.GetRequiredService<IDomainCommands>().CreateAsync(identity, "Pending at backup");
                Assert.Equal(created.Revision, receipt.Items[0].Revision);
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(dataRoot, recursive: true); }
    }

    [Theory]
    [InlineData("records.read", false)]
    [InlineData("structure.write", false)]
    [InlineData("domains.manage", true)]
    public async Task DomainCreationRequiresManageGrant(string grant, bool allowed)
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, [grant]);
        using JsonDocument capabilitiesResult = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_capabilities");
        RemoteCapabilities capabilities = Structured(capabilitiesResult).Deserialize<RemoteCapabilities>(JsonOptions)!;
        Assert.Equal(allowed, capabilities.Tools.Single(tool => tool.Name == "create_domain").Allowed);
        Assert.Equal(16, capabilities.DomainWriteLimits!.MaximumPendingCreations);
        Guid domainId = Guid.NewGuid();
        using JsonDocument result = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_domain",
            new { domainId, name = "Created through MCP", idempotencyKey = Guid.NewGuid() });
        IDomainCatalog domains = factory.Services.GetRequiredService<IDomainCatalog>();
        if (allowed)
        {
            RecordCommandReceipt receipt = Structured(result).Deserialize<RecordCommandReceipt>(JsonOptions)!;
            Assert.Equal(domainId, receipt.Items[0].Id);
            Assert.Equal("created", receipt.Items[0].Outcome);
            Assert.True(domains.TryGet(domainId, out MonkeysphereDomain? created));
            Assert.False(created!.IsDefault);
        }
        else
        {
            AssertWriteError(result, "permission_denied");
            Assert.False(domains.TryGet(domainId, out _));
        }
    }

    [Fact]
    public async Task McpDomainCreationReplaysConcurrentRequestsAndCanProceedToSetup()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["domains.manage", "records.read", "structure.write"]);
        var request = new { domainId = Guid.NewGuid(), name = "Remote domain", idempotencyKey = Guid.NewGuid() };
        JsonDocument[] concurrent = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_domain", request)));
        string receipt;
        try
        {
            receipt = Structured(concurrent[0]).GetRawText();
            Assert.Equal(receipt, Structured(concurrent[1]).GetRawText());
        }
        finally { foreach (JsonDocument document in concurrent) document.Dispose(); }
        using JsonDocument stateResult = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_setup_state", new { domainId = request.domainId });
        RemoteSetupState state = Structured(stateResult).Deserialize<RemoteSetupState>(JsonOptions)!;
        Assert.False(state.Setup.IsComplete);
        using JsonDocument setup = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "complete_setup",
            new
            {
                domainId = request.domainId,
                starterPackKey = "blank",
                selectedPresetKeys = Array.Empty<string>(),
                expectedRevision = state.Revision,
                expectedCatalogRevision = state.CatalogRevision,
                acknowledgeBlank = true,
                idempotencyKey = Guid.NewGuid()
            });
        Assert.False(setup.RootElement.GetProperty("result").TryGetProperty("isError", out JsonElement isError) && isError.GetBoolean());
        using JsonDocument replay = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_domain", request);
        Assert.Equal(receipt, Structured(replay).GetRawText());
        using JsonDocument conflict = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_domain", request with { name = "Changed" });
        AssertWriteError(conflict, "retry_conflict");
        using JsonDocument targetConflict = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_domain", request with { domainId = Guid.NewGuid() });
        AssertWriteError(targetConflict, "retry_conflict");
        using JsonDocument duplicate = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_domain", request with { idempotencyKey = Guid.NewGuid() });
        AssertWriteError(duplicate, "validation_failed");
        using JsonDocument invalidDefault = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_domain", request with { domainId = MonkeysphereDomains.DefaultId, idempotencyKey = Guid.NewGuid() });
        AssertWriteError(invalidDefault, "validation_failed");
        Assert.Equal(2, factory.Services.GetRequiredService<IDomainCatalog>().Snapshot.Count);
    }
}
