using System.Net;
using System.Text.Json;
using Dapper;
using DnaX.RemoteAccess;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;
using Monkeysphere.Data;
using Monkeysphere.Web.Remote;

namespace Monkeysphere.Web.Tests;

public sealed partial class RemoteDiscoveryTests
{
    [Fact]
    public async Task McpDirectDeletionRequiresNoDependentData()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        RecordType type = await records.CreateRecordTypeAsync("Direct deletion fixture");
        RecordDetails empty = await records.CreateRecordAsync(type.Id, "Empty", []);
        RecordDetails dependent = await records.CreateRecordAsync(type.Id, "With alias", [], ["Alias"]);
        IDnaXRemoteAccessAdministration administration = factory.Services.GetRequiredService<IDnaXRemoteAccessAdministration>();
        DnaXGeneratedCredential credential = await administration.RotateCredentialAsync(DnaXRemoteSurface.Mcp, 0, ["records.delete"]);
        DnaXRemoteEffectiveSurface surface = await administration.SetActivationAsync(DnaXRemoteSurface.Mcp, true, false, credential.Version);
        var request = new { domainId = MonkeysphereDomains.DefaultId, id = empty.Record.Id, expectedRevision = empty.Revision, idempotencyKey = Guid.CreateVersion7() };
        using JsonDocument deleted = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "delete_record", request);
        Assert.Equal("deleted", Assert.Single(Structured(deleted).Deserialize<RecordDeletionStatus>(JsonOptions)!.Receipt.Items).Outcome);
        Assert.Null(await records.GetRecordAsync(empty.Record.Id));
        using JsonDocument replay = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "delete_record", request);
        Assert.Equal(Structured(deleted).GetRawText(), Structured(replay).GetRawText());
        using JsonDocument requiresPreview = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "delete_record",
            request with { id = dependent.Record.Id, expectedRevision = dependent.Revision, idempotencyKey = Guid.CreateVersion7() });
        AssertWriteError(requiresPreview, "validation_failed");
        Assert.NotNull(await records.GetRecordAsync(dependent.Record.Id));
    }

    [Fact]
    public async Task McpDeletionRequiresSeparateGrantAndRejectsNewDependenciesBeforeRetryableDelete()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        RecordType type = await records.CreateRecordTypeAsync("Delete fixture");
        RecordDetails record = await records.CreateRecordAsync(type.Id, "Private original name", [], ["Private alias"]);
        RecordDetails other = await records.CreateRecordAsync(type.Id, "Keep", []);
        var (administration, writer, surface) = await EnableWritesAsync(factory);
        Guid domainId = MonkeysphereDomains.DefaultId;
        var request = new { domainId, id = record.Record.Id, expectedRevision = record.Revision, idempotencyKey = Guid.CreateVersion7() };
        using JsonDocument denied = await SendAsync(client, surface.EndpointPath!, writer.Secret, "tools/call", "preview_record_deletion", request);
        AssertWriteError(denied, "permission_denied");
        using JsonDocument deniedApply = await SendAsync(client, surface.EndpointPath!, writer.Secret, "tools/call", "delete_record",
            new { domainId, id = record.Record.Id, expectedRevision = record.Revision, idempotencyKey = Guid.CreateVersion7(), previewId = Guid.CreateVersion7() });
        AssertWriteError(deniedApply, "permission_denied");
        RemoteCredentialManager manager = factory.Services.GetRequiredService<RemoteCredentialManager>();
        DnaXGeneratedCredential credential = await manager.RotateAsync(DnaXRemoteSurface.Mcp, surface.Version, ["records.delete"]);
        using JsonDocument discovery = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_capabilities");
        RemoteCapabilities capabilities = Structured(discovery).Deserialize<RemoteCapabilities>(JsonOptions)!;
        Assert.True(capabilities.Tools.Single(tool => tool.Name == "delete_record").Allowed);
        Assert.False(capabilities.Tools.Single(tool => tool.Name == "create_record").Allowed);
        Assert.False(capabilities.Tools.Single(tool => tool.Name == "get_record").Allowed);
        Assert.Equal(1000, capabilities.RecordWriteLimits.MaximumPendingMediaCleanupPerDomain);
        using JsonDocument response = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "preview_record_deletion", request);
        RecordDeletionPreview preview = Structured(response).Deserialize<RecordDeletionPreview>(JsonOptions)!;
        Assert.Equal(1, preview.Impact.AliasCount);
        Assert.Equal(0, preview.Impact.RelationshipCount);
        Assert.DoesNotContain("Private", response.RootElement.GetRawText(), StringComparison.Ordinal);
        using JsonDocument replayPreview = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "preview_record_deletion", request);
        Assert.Equal(Structured(response).GetRawText(), Structured(replayPreview).GetRawText());
        using JsonDocument inspected = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_record_deletion_preview", new { domainId, previewId = preview.PreviewId });
        Assert.Equal(Structured(response).GetRawText(), Structured(inspected).GetRawText());
        IRelationshipService relationships = scope.ServiceProvider.GetRequiredService<IRelationshipService>();
        RelationshipType link = await relationships.CreateTypeAsync(new("Related", RelationshipDirectionality.Symmetric));
        await relationships.CreateAsync(link.Id, record.Record.Id, other.Record.Id);
        var apply = new { domainId, id = record.Record.Id, expectedRevision = record.Revision, previewId = preview.PreviewId, idempotencyKey = Guid.CreateVersion7() };
        using JsonDocument stale = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "delete_record", apply);
        AssertWriteError(stale, "stale_preview");
        Assert.NotNull(await records.GetRecordAsync(record.Record.Id));
        using JsonDocument freshResponse = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "preview_record_deletion", request with { idempotencyKey = Guid.CreateVersion7() });
        RecordDeletionPreview fresh = Structured(freshResponse).Deserialize<RecordDeletionPreview>(JsonOptions)!;
        Assert.Equal(1, fresh.Impact.RelationshipCount);
        apply = apply with { previewId = fresh.PreviewId };
        JsonDocument[] results = await Task.WhenAll(
            SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "delete_record", apply),
            SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "delete_record", apply));
        using (results[0])
        using (results[1])
        {
            Assert.Equal(Structured(results[0]).GetProperty("receipt").GetRawText(), Structured(results[1]).GetProperty("receipt").GetRawText());
            RecordDeletionStatus status = Structured(results[0]).Deserialize<RecordDeletionStatus>(JsonOptions)!;
            Assert.Equal("deleted", Assert.Single(status.Receipt.Items).Outcome);
            Assert.False(status.MediaCleanupPending);
            using JsonDocument queried = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_record_deletion_status", new { domainId, apply.idempotencyKey });
            Assert.Equal(Structured(results[0]).GetRawText(), Structured(queried).GetRawText());
        }
        Assert.Null(await records.GetRecordAsync(record.Record.Id));
        Assert.NotNull(await records.GetRecordAsync(other.Record.Id));
        Assert.Empty(await relationships.ListForRecordAsync(other.Record.Id));
        using JsonDocument changedRetry = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "delete_record", apply with { id = other.Record.Id });
        AssertWriteError(changedRetry, "retry_conflict");
        using JsonDocument consumed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "delete_record", apply with { idempotencyKey = Guid.CreateVersion7() });
        AssertWriteError(consumed, "preview_consumed");
        DnaXGeneratedCredential rotated = await manager.RotateAsync(DnaXRemoteSurface.Mcp, credential.Version, ["records.delete"]);
        using JsonDocument wrongOwner = await SendAsync(client, surface.EndpointPath!, rotated.Secret, "tools/call", "get_record_deletion_status", new { domainId, apply.idempotencyKey });
        AssertWriteError(wrongOwner, "not_found");
        using HttpRequestMessage revoked = CreateRequest(surface.EndpointPath!, credential.Secret, "tools/call", "delete_record", apply);
        using HttpResponseMessage revokedResponse = await client.SendAsync(revoked);
        Assert.Equal(HttpStatusCode.Unauthorized, revokedResponse.StatusCode);
        await using var connection = await scope.ServiceProvider.GetRequiredService<MonkeysphereConnectionFactory>().OpenConnectionAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ApplicationCommandAudit WHERE Action = 'records.delete' AND Outcome = 'committed';"));
    }

    [Fact]
    public async Task McpDeletionRejectsUnknownExpiredAndWrongDomainPreviews()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        RecordType type = await records.CreateRecordTypeAsync("Invalid deletion fixture");
        RecordDetails record = await records.CreateRecordAsync(type.Id, "Keep", []);
        Guid domainId = MonkeysphereDomains.DefaultId;
        IDnaXRemoteAccessAdministration administration = factory.Services.GetRequiredService<IDnaXRemoteAccessAdministration>();
        DnaXGeneratedCredential credential = await administration.RotateCredentialAsync(DnaXRemoteSurface.Mcp, 0, ["records.delete"]);
        DnaXRemoteEffectiveSurface surface = await administration.SetActivationAsync(DnaXRemoteSurface.Mcp, true, false, credential.Version);
        var request = new { domainId, id = record.Record.Id, expectedRevision = record.Revision, idempotencyKey = Guid.CreateVersion7() };
        using JsonDocument wrongRevision = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "preview_record_deletion", request with { expectedRevision = "stale" });
        AssertWriteError(wrongRevision, "stale_revision");
        using JsonDocument response = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "preview_record_deletion", request);
        RecordDeletionPreview preview = Structured(response).Deserialize<RecordDeletionPreview>(JsonOptions)!;
        var apply = new { domainId, id = record.Record.Id, expectedRevision = record.Revision, previewId = preview.PreviewId, idempotencyKey = Guid.CreateVersion7() };
        using JsonDocument missing = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "delete_record", apply with { previewId = Guid.CreateVersion7() });
        AssertWriteError(missing, "not_found");
        Guid otherDomain = (await scope.ServiceProvider.GetRequiredService<IDomainCatalog>().CreateAsync("Other deletion fixture")).Id;
        using JsonDocument wrongDomain = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "delete_record", apply with { domainId = otherDomain });
        AssertWriteError(wrongDomain, "not_found");
        await using var connection = await scope.ServiceProvider.GetRequiredService<MonkeysphereConnectionFactory>().OpenConnectionAsync();
        await connection.ExecuteAsync("UPDATE RecordDeletionPreviews SET ExpiresAtUtc = '2000-01-01T00:00:00.0000000+00:00';");
        using JsonDocument expired = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "delete_record", apply);
        AssertWriteError(expired, "preview_expired");
        Assert.NotNull(await records.GetRecordAsync(record.Record.Id));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RecordCommandReceipts;"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RecordMediaCleanup;"));
    }
}
