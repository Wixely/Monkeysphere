using System.Text.Json;
using Dapper;
using DnaX.RemoteAccess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Monkeysphere.Core;
using Monkeysphere.Data;
using Monkeysphere.Web.Remote;

namespace Monkeysphere.Web.Tests;

public sealed partial class RemoteDiscoveryTests
{
    [Fact]
    public async Task PreviewCleanupSweepsEachDomainAndPreservesLivePreviews()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        IDomainCatalog domains = scope.ServiceProvider.GetRequiredService<IDomainCatalog>();
        _ = await domains.CreateAsync("Cleanup fixture");
        foreach (MonkeysphereDomain domain in domains.Snapshot)
        {
            using IDisposable selection = scope.ServiceProvider.GetRequiredService<ICurrentDomainScope>().Use(domain.Id);
            RecordType type = await scope.ServiceProvider.GetRequiredService<IMonkeysphereService>().CreateRecordTypeAsync("Cleanup fixture");
            PreparedRecord prepared = await scope.ServiceProvider.GetRequiredService<RecordCommandService>().PrepareCreateAsync(type.Id, "Pending", []);
            Guid id = Guid.CreateVersion7();
            PreparedRecordMutation[] mutations = [new(RecordMutationKind.Create, id, prepared)];
            RecordBatchPreviewItem[] items = [new(0, "create", id, type.Id, "Pending", 0, 0)];
            IRecordBatchStore store = scope.ServiceProvider.GetRequiredService<IRecordBatchStore>();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            // Store the live preview first so saving it does not opportunistically remove the expired fixture.
            await store.SavePreviewAsync(new(domain.Id, "mcp", new string('A', 64), "records.batch", Guid.CreateVersion7(), new string('B', 64)), mutations, items, now);
            await store.SavePreviewAsync(new(domain.Id, "mcp", new string('A', 64), "records.batch", Guid.CreateVersion7(), new string('B', 64)), mutations, items, now.AddMinutes(-16));
        }
        RecordPreviewCleanupWorker worker = Assert.Single(factory.Services.GetServices<IHostedService>().OfType<RecordPreviewCleanupWorker>());
        await worker.SweepAsync();
        foreach (MonkeysphereDomain domain in domains.Snapshot)
        {
            using IDisposable selection = scope.ServiceProvider.GetRequiredService<ICurrentDomainScope>().Use(domain.Id);
            await using var connection = await scope.ServiceProvider.GetRequiredService<MonkeysphereConnectionFactory>().OpenConnectionAsync();
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RecordBatchPreviews;"));
        }
    }

    [Fact]
    public async Task McpBatchPreviewApplyIsAtomicRetryableAndCredentialBound()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        RecordType type = await records.CreateRecordTypeAsync("Batch fixture");
        FieldDefinition field = await records.CreateAndAttachFieldAsync(type.Id, new("Value", FieldTypes.Text, false));
        RecordDetails original = await records.CreateRecordAsync(type.Id, "Original", [new(field.Id, "Private unchanged field")], ["Private unchanged alias"]);
        Guid domainId = MonkeysphereDomains.DefaultId;
        var (administration, credential, surface) = await EnableWritesAsync(factory);
        var request = new
        {
            domainId,
            idempotencyKey = Guid.CreateVersion7(),
            operations = new RemoteRecordBatchInput[] {
            new("create", type.Id, "New fixture", [new(field.Id, "New field")]),
            new("patch", Id: original.Record.Id, ExpectedRevision: original.Revision, Changes: [new("set_name", Value: "Updated fixture")]),
        }
        };
        using JsonDocument previewResponse = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "preview_record_batch", request);
        RecordBatchPreview preview = Structured(previewResponse).Deserialize<RecordBatchPreview>(JsonOptions)!;
        Assert.Equal(2, preview.Items.Count);
        Assert.Equal(TimeSpan.FromMinutes(15), preview.ExpiresAtUtc - preview.CreatedAtUtc);
        Assert.False(preview.Applied);
        Assert.DoesNotContain("Private unchanged", previewResponse.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(1, (await records.SearchRecordsAsync(new())).TotalCount);
        Assert.Equal(original.Revision, (await records.GetRecordAsync(original.Record.Id))!.Revision);
        using JsonDocument previewRetry = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "preview_record_batch", request);
        Assert.Equal(Structured(previewResponse).GetRawText(), Structured(previewRetry).GetRawText());
        using JsonDocument inspected = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_record_batch_preview", new { domainId, previewId = preview.PreviewId });
        Assert.Equal(Structured(previewResponse).GetRawText(), Structured(inspected).GetRawText());
        using JsonDocument changedRequest = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "preview_record_batch",
            request with { operations = [new("create", type.Id, "Changed", [])] });
        AssertWriteError(changedRequest, "retry_conflict");

        var apply = new { domainId, previewId = preview.PreviewId, idempotencyKey = Guid.CreateVersion7() };
        JsonDocument[] competing = await Task.WhenAll(
            SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "apply_record_batch", apply),
            SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "apply_record_batch", apply));
        using (competing[0])
        using (competing[1])
        {
            Assert.Equal(Structured(competing[0]).GetRawText(), Structured(competing[1]).GetRawText());
            RecordCommandReceipt receipt = Structured(competing[0]).Deserialize<RecordCommandReceipt>(JsonOptions)!;
            Assert.Equal(preview.Items.Select(item => item.Id), receipt.Items.Select(item => item.Id));
            Assert.Equal(["created", "updated"], receipt.Items.Select(item => item.Outcome));
            Assert.Equal(2, (await records.SearchRecordsAsync(new())).TotalCount);
            RecordDetails updated = (await records.GetRecordAsync(original.Record.Id))!;
            Assert.Equal("Updated fixture", updated.Record.DisplayName);
            Assert.Equal("Private unchanged field", Assert.Single(updated.Values).TextValue);
            Assert.Equal(["Private unchanged alias"], updated.Aliases);
            using JsonDocument consumed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "apply_record_batch", apply with { idempotencyKey = Guid.CreateVersion7() });
            AssertWriteError(consumed, "preview_consumed");
            Assert.True(await records.DeleteRecordAsync(original.Record.Id));
            using JsonDocument replay = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "apply_record_batch", apply);
            Assert.Equal(Structured(competing[0]).GetRawText(), Structured(replay).GetRawText());
        }
        DnaXGeneratedCredential rotated = await administration.RotateCredentialAsync(DnaXRemoteSurface.Mcp, surface.Version, ["records.write"]);
        using JsonDocument wrongOwner = await SendAsync(client, surface.EndpointPath!, rotated.Secret, "tools/call", "get_record_batch_preview", new { domainId, previewId = preview.PreviewId });
        AssertWriteError(wrongOwner, "not_found");
        await using var connection = await scope.ServiceProvider.GetRequiredService<MonkeysphereConnectionFactory>().OpenConnectionAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ApplicationCommandAudit WHERE Action = 'records.batch' AND Outcome = 'committed';"));
        Assert.Equal("[]", await connection.ExecuteScalarAsync<string>("SELECT MutationsJson FROM RecordBatchPreviews;"));
    }

    [Fact]
    public async Task McpBatchRejectsStalePreviewWithoutCommittingEarlierItems()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        RecordType type = await records.CreateRecordTypeAsync("Stale batch fixture");
        RecordDetails original = await records.CreateRecordAsync(type.Id, "Original", []);
        var (_, credential, surface) = await EnableWritesAsync(factory);
        Guid domainId = MonkeysphereDomains.DefaultId;
        using JsonDocument response = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "preview_record_batch",
            new
            {
                domainId,
                idempotencyKey = Guid.CreateVersion7(),
                operations = new RemoteRecordBatchInput[] {
                new("create", type.Id, "Must roll back", []),
                new("patch", Id: original.Record.Id, ExpectedRevision: original.Revision, Changes: [new("set_name", Value: "Stale edit")]),
            }
            });
        RecordBatchPreview preview = Structured(response).Deserialize<RecordBatchPreview>(JsonOptions)!;
        await records.UpdateRecordAsync(original.Record.Id, "Browser edit", [], expectedRevision: original.Revision);
        using JsonDocument stale = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "apply_record_batch",
            new { domainId, previewId = preview.PreviewId, idempotencyKey = Guid.CreateVersion7() });
        AssertWriteError(stale, "stale_preview");
        Assert.Equal(1, (await records.SearchRecordsAsync(new())).TotalCount);
        Assert.Null(await records.GetRecordAsync(preview.Items[0].Id));
        Assert.Equal("Browser edit", (await records.GetRecordAsync(original.Record.Id))!.Record.DisplayName);
        await using var connection = await scope.ServiceProvider.GetRequiredService<MonkeysphereConnectionFactory>().OpenConnectionAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RecordCommandReceipts;"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT Applied FROM RecordBatchPreviews;"));
    }

    [Fact]
    public async Task McpBatchToolsEnforceScopesDomainsAndInputBounds()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        RecordType type = await records.CreateRecordTypeAsync("Bounds fixture");
        Guid domainId = MonkeysphereDomains.DefaultId;
        IDnaXRemoteAccessAdministration administration = factory.Services.GetRequiredService<IDnaXRemoteAccessAdministration>();
        DnaXGeneratedCredential reader = await administration.RotateCredentialAsync(DnaXRemoteSurface.Mcp, 0, ["records.read"]);
        DnaXRemoteEffectiveSurface surface = await administration.SetActivationAsync(DnaXRemoteSurface.Mcp, true, false, reader.Version);
        var request = new { domainId, idempotencyKey = Guid.CreateVersion7(), operations = new[] { new RemoteRecordBatchInput("create", type.Id, "Fixture", []) } };
        using JsonDocument denied = await SendAsync(client, surface.EndpointPath!, reader.Secret, "tools/call", "preview_record_batch", request);
        AssertWriteError(denied, "permission_denied");
        using JsonDocument deniedApply = await SendAsync(client, surface.EndpointPath!, reader.Secret, "tools/call", "apply_record_batch",
            new { domainId, previewId = Guid.CreateVersion7(), idempotencyKey = Guid.CreateVersion7() });
        AssertWriteError(deniedApply, "permission_denied");
        DnaXGeneratedCredential writer = await administration.RotateCredentialAsync(DnaXRemoteSurface.Mcp, surface.Version, ["records.write"]);
        using JsonDocument empty = await SendAsync(client, surface.EndpointPath!, writer.Secret, "tools/call", "preview_record_batch", request with { operations = [] });
        AssertWriteError(empty, "validation_failed");
        using JsonDocument excessive = await SendAsync(client, surface.EndpointPath!, writer.Secret, "tools/call", "preview_record_batch",
            request with { operations = Enumerable.Repeat(request.operations[0], 101).ToArray() });
        AssertWriteError(excessive, "validation_failed");
        using JsonDocument unsupported = await SendAsync(client, surface.EndpointPath!, writer.Secret, "tools/call", "preview_record_batch",
            request with { operations = [new("delete", Id: Guid.CreateVersion7())] });
        AssertWriteError(unsupported, "validation_failed");
        using JsonDocument response = await SendAsync(client, surface.EndpointPath!, writer.Secret, "tools/call", "preview_record_batch", request);
        RecordBatchPreview preview = Structured(response).Deserialize<RecordBatchPreview>(JsonOptions)!;
        Guid otherDomain = (await scope.ServiceProvider.GetRequiredService<IDomainCatalog>().CreateAsync("Other batch fixture")).Id;
        using JsonDocument wrongDomain = await SendAsync(client, surface.EndpointPath!, writer.Secret, "tools/call", "apply_record_batch",
            new { domainId = otherDomain, previewId = preview.PreviewId, idempotencyKey = Guid.CreateVersion7() });
        AssertWriteError(wrongDomain, "not_found");
        Assert.Equal(0, (await records.SearchRecordsAsync(new())).TotalCount);
    }
}
