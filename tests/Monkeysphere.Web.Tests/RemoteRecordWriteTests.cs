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
    public async Task RecordToolsValidateCreatePatchAndReplayWithoutLosingUnmentionedData()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        RecordType type = await records.CreateRecordTypeAsync("Remote fixture");
        FieldDefinition required = await records.CreateAndAttachFieldAsync(type.Id, new("Category", FieldTypes.Text, true));
        FieldDefinition temporal = await records.CreateAndAttachFieldAsync(type.Id, new("Meeting", FieldTypes.Temporal, false));
        FieldDefinition location = await records.CreateAndAttachFieldAsync(type.Id, new("Place", FieldTypes.Location, false));
        FieldDefinition custom = await records.CreateAndAttachFieldAsync(type.Id, new("Extension", "fixture.custom", false));
        FieldDefinition tags = await records.CreateAndAttachFieldAsync(type.Id, new("Tags", FieldTypes.Tags, false));
        RemoteFieldInput[] values = [new(required.Id, "Friend"), new(temporal.Id, Temporal: new("2010s", "decade", true, "Estimated")),
            new(location.Id, Location: new("Fictional park", "51.5", "-0.1", "20", "0.5")),
            new(custom.Id, "opaque extension"), new(tags.Id, Tags: ["First", "Second"])];
        var (administration, credential, surface) = await EnableWritesAsync(factory);
        Guid domainId = MonkeysphereDomains.DefaultId;
        string[] aliases = ["Alias"];
        using JsonDocument validation = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "validate_record",
            new { domainId, recordTypeId = type.Id, displayName = "  Fictional person  ", values, aliases });
        Assert.True(Structured(validation).GetProperty("isValid").GetBoolean());
        Assert.Equal("Fictional person", Structured(validation).GetProperty("displayName").GetString());
        Assert.Equal(0, (await records.SearchRecordsAsync(new())).TotalCount);

        Guid createKey = Guid.CreateVersion7();
        var create = new { domainId, recordTypeId = type.Id, displayName = "Fictional person", values, aliases, idempotencyKey = createKey };
        using JsonDocument created = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_record", create);
        RecordCommandReceipt receipt = Structured(created).Deserialize<RecordCommandReceipt>(JsonOptions)!;
        RecordMutationOutcome item = Assert.Single(receipt.Items);
        Assert.Equal("created", item.Outcome);
        Assert.Equal(TimeSpan.FromHours(24), receipt.RetryUntilUtc - receipt.CompletedAtUtc);
        var patch = new
        {
            domainId,
            id = item.Id,
            expectedRevision = item.Revision,
            idempotencyKey = Guid.CreateVersion7(),
            changes = new[] { new RemoteRecordPatchChange("set_name", Value: "Renamed fixture") }
        };
        using JsonDocument patched = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "patch_record", patch);
        RecordMutationOutcome updated = Assert.Single(Structured(patched).Deserialize<RecordCommandReceipt>(JsonOptions)!.Items);
        Assert.NotEqual(item.Revision, updated.Revision);
        RecordDetails current = (await records.GetRecordAsync(item.Id))!;
        Assert.Equal("Renamed fixture", current.Record.DisplayName);
        Assert.Equal(["Alias"], current.Aliases);
        Assert.Equal(5, current.Values.Count);
        Assert.Equal("opaque extension", current.Values.Single(value => value.FieldDefinitionId == custom.Id).TextValue);
        RecordValue meeting = current.Values.Single(value => value.FieldDefinitionId == temporal.Id);
        Assert.Equal(TemporalPrecision.Decade, meeting.TemporalPrecision);
        Assert.True(meeting.IsApproximate);
        Assert.Equal("Estimated", meeting.ApproximationNote);
        Assert.Equal(20, current.Values.Single(value => value.FieldDefinitionId == location.Id).Location!.AccuracyMetres);
        Assert.Equal(["First", "Second"], current.Values.Single(value => value.FieldDefinitionId == tags.Id).Tags);

        using JsonDocument replay = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_record", create);
        Assert.Equal(Structured(created).GetRawText(), Structured(replay).GetRawText());
        using JsonDocument conflict = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_record",
            create with { displayName = "Different request" });
        AssertWriteError(conflict, "retry_conflict");
        using JsonDocument stale = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "patch_record",
            patch with { idempotencyKey = Guid.CreateVersion7() });
        AssertWriteError(stale, "stale_revision");

        using JsonDocument changedValues = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "patch_record",
            new
            {
                domainId,
                id = item.Id,
                expectedRevision = updated.Revision,
                idempotencyKey = Guid.CreateVersion7(),
                changes = new RemoteRecordPatchChange[] { new("set_field", required.Id, ScalarValue: "Colleague"),
                    new("clear_field", tags.Id), new("replace_aliases", Values: []) }
            });
        _ = Structured(changedValues);
        RecordDetails changed = (await records.GetRecordAsync(item.Id))!;
        Assert.Empty(changed.Aliases);
        Assert.DoesNotContain(changed.Values, value => value.FieldDefinitionId == tags.Id);
        Assert.Equal("Colleague", changed.Values.Single(value => value.FieldDefinitionId == required.Id).TextValue);
        Assert.Equal("opaque extension", changed.Values.Single(value => value.FieldDefinitionId == custom.Id).TextValue);

        Assert.True(await records.DeleteRecordAsync(item.Id));
        using JsonDocument replayDeleted = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "patch_record", patch);
        Assert.Equal(Structured(patched).GetRawText(), Structured(replayDeleted).GetRawText());
        Assert.Null(await records.GetRecordAsync(item.Id));
        await administration.RevokeCredentialAsync(DnaXRemoteSurface.Mcp, surface.Version);
        using HttpRequestMessage revoked = CreateRequest(surface.EndpointPath!, credential.Secret, "tools/call", "create_record", create);
        using HttpResponseMessage denied = await client.SendAsync(revoked);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    [Fact]
    public async Task WriteGrantIsExplicitAndRotationDoesNotTransferReceiptOwnership()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        RecordType type = await records.CreateRecordTypeAsync("Permission fixture");
        IDnaXRemoteAccessAdministration administration = factory.Services.GetRequiredService<IDnaXRemoteAccessAdministration>();
        DnaXGeneratedCredential reader = await administration.RotateCredentialAsync(DnaXRemoteSurface.Mcp, 0, ["records.read"]);
        DnaXRemoteEffectiveSurface surface = await administration.SetActivationAsync(DnaXRemoteSurface.Mcp, true, false, reader.Version);
        var create = new
        {
            domainId = MonkeysphereDomains.DefaultId,
            recordTypeId = type.Id,
            displayName = "Credential fixture",
            idempotencyKey = Guid.CreateVersion7(),
            values = Array.Empty<RemoteFieldInput>()
        };
        foreach (string tool in new[] { "create_record", "validate_record" })
        {
            using JsonDocument denied = await SendAsync(client, surface.EndpointPath!, reader.Secret, "tools/call", tool, create);
            AssertWriteError(denied, "permission_denied");
        }
        Assert.Equal(0, (await records.SearchRecordsAsync(new())).TotalCount);
        RemoteCredentialManager manager = factory.Services.GetRequiredService<RemoteCredentialManager>();
        DnaXGeneratedCredential writer = await manager.RotateAsync(DnaXRemoteSurface.Mcp, surface.Version, ["records.write"]);
        using JsonDocument capabilities = await SendAsync(client, surface.EndpointPath!, writer.Secret, "tools/call", "get_capabilities");
        RemoteCapabilities permissions = Structured(capabilities).Deserialize<RemoteCapabilities>(JsonOptions)!;
        Assert.True(permissions.Tools.Single(tool => tool.Name == "create_record").Allowed);
        Assert.False(permissions.Tools.Single(tool => tool.Name == "get_record").Allowed);
        using JsonDocument created = await SendAsync(client, surface.EndpointPath!, writer.Secret, "tools/call", "create_record", create);
        Guid firstId = Assert.Single(Structured(created).Deserialize<RecordCommandReceipt>(JsonOptions)!.Items).Id;
        DnaXGeneratedCredential next = await manager.RotateAsync(DnaXRemoteSurface.Mcp, writer.Version, ["records.write"]);
        using JsonDocument newOwner = await SendAsync(client, surface.EndpointPath!, next.Secret, "tools/call", "create_record", create);
        Guid nextId = Assert.Single(Structured(newOwner).Deserialize<RecordCommandReceipt>(JsonOptions)!.Items).Id;
        Assert.NotEqual(firstId, nextId);
        Assert.Equal(2, (await records.SearchRecordsAsync(new())).TotalCount);
    }

    [Fact]
    public async Task InvalidPatchesAreAtomicAndFailureAuditContainsOnlyMetadata()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        RecordType type = await records.CreateRecordTypeAsync("Validation fixture");
        FieldDefinition field = await records.CreateAndAttachFieldAsync(type.Id, new("Required", FieldTypes.Text, true));
        RecordDetails original = await records.CreateRecordAsync(type.Id, "Original", [new(field.Id, "Keep")]);
        var (_, credential, surface) = await EnableWritesAsync(factory);
        RemoteRecordPatchChange[][] invalidChanges = [
            [new("set_name", Value: "Should not persist"), new("clear_field", field.Id)],
            [new("set_field", field.Id, ScalarValue: "")],
            [new("set_field", field.Id, Tags: ["Wrong shape"])],
            [new("set_name", Value: "First"), new("set_name", Value: "Second")],
            [new("set_name", Value: "Ambiguous", Values: ["Extra shape"])],
            [new("replace_aliases", Values: [null!])],
        ];
        foreach (RemoteRecordPatchChange[] changes in invalidChanges)
        {
            using JsonDocument invalid = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "patch_record",
                new
                {
                    domainId = MonkeysphereDomains.DefaultId,
                    id = original.Record.Id,
                    expectedRevision = original.Revision,
                    idempotencyKey = Guid.CreateVersion7(),
                    changes
                });
            AssertWriteError(invalid, "validation_failed");
        }
        RecordDetails unchanged = (await records.GetRecordAsync(original.Record.Id))!;
        Assert.Equal(original.Revision, unchanged.Revision);
        Assert.Equal("Original", unchanged.Record.DisplayName);
        Assert.Equal("Keep", Assert.Single(unchanged.Values).TextValue);
        await using var connection = await scope.ServiceProvider.GetRequiredService<MonkeysphereConnectionFactory>().OpenConnectionAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RecordCommandReceipts;"));
        var audit = (await connection.QueryAsync("SELECT * FROM ApplicationCommandAudit;")).ToArray();
        Assert.Equal(invalidChanges.Length, audit.Length);
        string json = JsonSerializer.Serialize(audit);
        Assert.Contains("validation_failed", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Should not persist", json, StringComparison.Ordinal);
        Assert.DoesNotContain(credential.Secret, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteToolsRejectWrongDomainsAndUnknownNestedInputMembers()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        RecordType type = await records.CreateRecordTypeAsync("Isolation fixture");
        FieldDefinition field = await records.CreateAndAttachFieldAsync(type.Id, new("Text", FieldTypes.Text, false));
        Guid otherDomain = (await scope.ServiceProvider.GetRequiredService<IDomainCatalog>().CreateAsync("Other fixture")).Id;
        var (_, credential, surface) = await EnableWritesAsync(factory);
        using JsonDocument wrong = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_record",
            new { domainId = otherDomain, recordTypeId = type.Id, displayName = "Wrong domain", idempotencyKey = Guid.CreateVersion7(), values = Array.Empty<RemoteFieldInput>() });
        AssertWriteError(wrong, "not_found");
        using JsonDocument unknownMember = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_record",
            new
            {
                domainId = MonkeysphereDomains.DefaultId,
                recordTypeId = type.Id,
                displayName = "Invalid shape",
                idempotencyKey = Guid.CreateVersion7(),
                values = new[] { new { fieldDefinitionId = field.Id, scalarValue = "Text", unexpectedMember = "Must reject" } }
            });
        Assert.True(unknownMember.RootElement.TryGetProperty("error", out _) ||
            unknownMember.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal(0, (await records.SearchRecordsAsync(new())).TotalCount);
    }

    [Fact]
    public async Task ConcurrentMcpRetriesCommitOnceAndCompetingEditsRejectStaleRevision()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        RecordType type = await records.CreateRecordTypeAsync("Concurrent fixture");
        var (_, credential, surface) = await EnableWritesAsync(factory);
        Guid domainId = MonkeysphereDomains.DefaultId;
        var create = new
        {
            domainId,
            recordTypeId = type.Id,
            displayName = "Concurrent fixture",
            idempotencyKey = Guid.CreateVersion7(),
            values = Array.Empty<RemoteFieldInput>()
        };
        JsonDocument[] duplicates = await Task.WhenAll(
            SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_record", create),
            SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_record", create));
        RecordMutationOutcome item;
        using (duplicates[0])
        using (duplicates[1])
        {
            Assert.Equal(Structured(duplicates[0]).GetRawText(), Structured(duplicates[1]).GetRawText());
            item = Assert.Single(Structured(duplicates[0]).Deserialize<RecordCommandReceipt>(JsonOptions)!.Items);
        }
        Assert.Equal(1, (await records.SearchRecordsAsync(new())).TotalCount);
        var patch = new
        {
            domainId,
            id = item.Id,
            expectedRevision = item.Revision,
            idempotencyKey = Guid.CreateVersion7(),
            changes = new[] { new RemoteRecordPatchChange("set_name", Value: "First edit") }
        };
        JsonDocument[] competing = await Task.WhenAll(
            SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "patch_record", patch),
            SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "patch_record",
                patch with { idempotencyKey = Guid.CreateVersion7(), changes = [new("set_name", Value: "Second edit")] }));
        using (competing[0])
        using (competing[1])
        {
            JsonDocument failure = Assert.Single(competing, document =>
                document.RootElement.GetProperty("result").TryGetProperty("isError", out JsonElement error) && error.GetBoolean());
            AssertWriteError(failure, "stale_revision");
            JsonDocument success = Assert.Single(competing, document => document != failure);
            RecordMutationOutcome winner = Assert.Single(Structured(success).Deserialize<RecordCommandReceipt>(JsonOptions)!.Items);
            Assert.Equal(winner.Revision, (await records.GetRecordAsync(item.Id))!.Revision);
        }
        await using var connection = await scope.ServiceProvider.GetRequiredService<MonkeysphereConnectionFactory>().OpenConnectionAsync();
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ApplicationCommandAudit WHERE Outcome = 'committed';"));
    }

    private static JsonElement Structured(JsonDocument response)
    {
        JsonElement result = response.RootElement.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out JsonElement error) && error.GetBoolean(), response.RootElement.GetRawText());
        return result.GetProperty("structuredContent");
    }

    private static void AssertWriteError(JsonDocument response, string code)
    {
        JsonElement result = response.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        JsonElement error = result.GetProperty("structuredContent").GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(error.GetProperty("correlationId").GetString()));
    }

    private static async Task<(IDnaXRemoteAccessAdministration Administration, DnaXGeneratedCredential Credential, DnaXRemoteEffectiveSurface Surface)> EnableWritesAsync(RemoteEnabledApplicationFactory factory)
    {
        IDnaXRemoteAccessAdministration administration = factory.Services.GetRequiredService<IDnaXRemoteAccessAdministration>();
        DnaXGeneratedCredential credential = await administration.RotateCredentialAsync(DnaXRemoteSurface.Mcp, 0, ["records.read", "records.write"]);
        DnaXRemoteEffectiveSurface surface = await administration.SetActivationAsync(DnaXRemoteSurface.Mcp, true, false, credential.Version);
        return (administration, credential, surface);
    }
}
