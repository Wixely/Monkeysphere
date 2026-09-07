using System.Text.Json;
using Dapper;
using DnaX.Hosting;
using DnaX.RemoteAccess;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;
using Monkeysphere.Web.Remote;

namespace Monkeysphere.Web.Tests;

public sealed partial class RemoteDiscoveryTests
{
    [Theory]
    [InlineData("records.read", false)]
    [InlineData("records.write", false)]
    [InlineData("structure.write", false)]
    [InlineData("domains.manage", true)]
    public async Task DomainRenameRequiresItsOwnGrantAndAdvertisesRegistryLimits(string grant, bool allowed)
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, [grant]);
        IDomainCatalog domains = factory.Services.GetRequiredService<IDomainCatalog>();
        MonkeysphereDomain original = domains.DefaultDomain;
        using JsonDocument capabilitiesResult = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_capabilities");
        RemoteCapabilities capabilities = Structured(capabilitiesResult).Deserialize<RemoteCapabilities>(JsonOptions)!;
        Assert.Equal(allowed, capabilities.Tools.Single(tool => tool.Name == "rename_domain").Allowed);
        Assert.NotNull(capabilities.DomainWriteLimits);
        Assert.Equal(1000, capabilities.DomainWriteLimits.MaximumRetainedCommands);
        Assert.Equal(24, capabilities.DomainWriteLimits.RetryWindowHours);
        using JsonDocument info = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_instance_info");
        Assert.Equal(4, Structured(info).GetProperty("domainRegistrySchemaVersion").GetInt32());
        using JsonDocument renamed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "rename_domain",
            new { domainId = original.Id, name = "Scoped rename", expectedRevision = original.Revision, idempotencyKey = Guid.CreateVersion7() });
        if (allowed)
        {
            Assert.Equal("renamed", Structured(renamed).Deserialize<RecordCommandReceipt>(JsonOptions)!.Items[0].Outcome);
            Assert.Equal("Scoped rename", domains.DefaultDomain.Name);
            using JsonDocument deniedRead = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "list_domains");
            Assert.True(deniedRead.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        }
        else
        {
            AssertWriteError(renamed, "permission_denied");
            Assert.Equal(original, domains.DefaultDomain);
        }
    }

    [Fact]
    public async Task McpDomainRenameReplaysWithoutOverwritingLaterEditsAndRejectsOwnershipChanges()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["domains.manage", "records.read"]);
        IDomainCatalog domains = factory.Services.GetRequiredService<IDomainCatalog>();
        MonkeysphereDomain original = domains.DefaultDomain;
        MonkeysphereDomain other = await domains.CreateAsync("Other domain");
        var request = new { domainId = original.Id, name = "Remote private name", expectedRevision = original.Revision, idempotencyKey = Guid.CreateVersion7() };
        JsonDocument[] concurrent = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "rename_domain", request)));
        string receipt;
        try
        {
            receipt = Structured(concurrent[0]).GetRawText();
            Assert.Equal(receipt, Structured(concurrent[1]).GetRawText());
            Assert.Equal(domains.DefaultDomain.Revision, Structured(concurrent[0]).Deserialize<RecordCommandReceipt>(JsonOptions)!.Items[0].Revision);
        }
        finally { foreach (JsonDocument document in concurrent) document.Dispose(); }
        Assert.True(domains.DefaultDomain.IsDefault);
        _ = await domains.RenameAsync(original.Id, "Later edit", expectedRevision: domains.DefaultDomain.Revision);
        using JsonDocument replay = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "rename_domain", request);
        Assert.Equal(receipt, Structured(replay).GetRawText());
        Assert.Equal("Later edit", domains.DefaultDomain.Name);
        using JsonDocument changed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "rename_domain", request with { name = "Changed payload" });
        AssertWriteError(changed, "retry_conflict");
        using JsonDocument crossTarget = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "rename_domain", request with { domainId = other.Id });
        AssertWriteError(crossTarget, "retry_conflict");
        using JsonDocument missing = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "rename_domain",
            request with { domainId = Guid.NewGuid(), idempotencyKey = Guid.CreateVersion7() });
        AssertWriteError(missing, "not_found");
        using JsonDocument collision = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "rename_domain",
            request with { name = other.Name, expectedRevision = domains.DefaultDomain.Revision, idempotencyKey = Guid.CreateVersion7() });
        AssertWriteError(collision, "validation_failed");
        RemoteCredentialManager manager = factory.Services.GetRequiredService<RemoteCredentialManager>();
        DnaXGeneratedCredential rotated = await manager.RotateAsync(DnaXRemoteSurface.Mcp, surface.Version, ["domains.manage"]);
        using JsonDocument notOwned = await SendAsync(client, surface.EndpointPath!, rotated.Secret, "tools/call", "rename_domain", request);
        AssertWriteError(notOwned, "stale_revision");
        Assert.Equal("Later edit", domains.DefaultDomain.Name);
        IDnaXPaths paths = factory.Services.GetRequiredService<IDnaXPaths>();
        await using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = paths.ResolveWritable("domains.db") }.ConnectionString);
        await connection.OpenAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM DomainCommandReceipts;"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM DomainCommandAudit WHERE Outcome = 'committed';"));
        string audit = JsonSerializer.Serialize(await connection.QueryAsync("SELECT * FROM DomainCommandAudit;"));
        Assert.DoesNotContain(request.name, audit, StringComparison.Ordinal);
        Assert.DoesNotContain(credential.Secret, audit, StringComparison.Ordinal);
    }
}
