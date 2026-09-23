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
    [Theory]
    [InlineData("records.read", false, false)]
    [InlineData("records.write", false, false)]
    [InlineData("records.delete", false, false)]
    [InlineData("instance.read", false, false)]
    [InlineData("relationships.write", true, false)]
    [InlineData("structure.write", false, true)]
    public async Task RelationshipPermissionsMatchCapabilitiesAndDoNotImplyOtherWrites(string grant, bool linksAllowed, bool typesAllowed)
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        IRelationshipService relationships = scope.ServiceProvider.GetRequiredService<IRelationshipService>();
        RecordType recordType = await records.CreateRecordTypeAsync("Permission people");
        RecordDetails first = await records.CreateRecordAsync(recordType.Id, "First", []);
        RecordDetails second = await records.CreateRecordAsync(recordType.Id, "Second", []);
        RelationshipType type = await relationships.CreateTypeAsync(new("knows", RelationshipDirectionality.Symmetric));
        var (administration, credential, surface) = await EnableRelationshipWritesAsync(factory, [grant]);
        Guid domainId = MonkeysphereDomains.DefaultId;
        using JsonDocument capabilitiesResponse = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_capabilities");
        RemoteCapabilities capabilities = Structured(capabilitiesResponse).Deserialize<RemoteCapabilities>(JsonOptions)!;
        Assert.Equal(linksAllowed, capabilities.Tools.Single(tool => tool.Name == "create_relationship").Allowed);
        Assert.Equal(linksAllowed, capabilities.Tools.Single(tool => tool.Name == "delete_relationship").Allowed);
        Assert.Equal(typesAllowed, capabilities.Tools.Single(tool => tool.Name == "create_relationship_type").Allowed);
        using JsonDocument definition = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_relationship_type",
            new { domainId, name = "new definition", directionality = "symmetric", idempotencyKey = Guid.CreateVersion7() });
        if (typesAllowed) _ = Structured(definition); else AssertWriteError(definition, "permission_denied");
        using JsonDocument created = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_relationship",
            RelationshipCreateInput(domainId, type, first, second));
        Guid linkId = linksAllowed ? Structured(created).Deserialize<RecordCommandReceipt>(JsonOptions)!.Items[0].Id : Guid.CreateVersion7();
        if (!linksAllowed) AssertWriteError(created, "permission_denied");
        string revision = linksAllowed ? Assert.Single(await relationships.ListForRecordAsync(first.Record.Id)).Revision : "missing";
        var delete = new { domainId, id = linkId, expectedRevision = revision, idempotencyKey = Guid.CreateVersion7() };
        using JsonDocument deleted = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "delete_relationship", delete);
        if (linksAllowed) _ = Structured(deleted); else AssertWriteError(deleted, "permission_denied");
        Assert.Empty(await relationships.ListForRecordAsync(first.Record.Id));
        Assert.Equal(typesAllowed ? 2 : 1, (await relationships.ListTypesAsync()).Count);
        await administration.RevokeCredentialAsync(DnaXRemoteSurface.Mcp, surface.Version);
        using HttpRequestMessage revoked = CreateRequest(surface.EndpointPath!, credential.Secret, "tools/call", "delete_relationship", delete);
        using HttpResponseMessage response = await client.SendAsync(revoked);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task McpRelationshipsCreateReplayDetectChangesAndDeleteWithoutRemovingRecords()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        IRelationshipService relationships = scope.ServiceProvider.GetRequiredService<IRelationshipService>();
        RecordType recordType = await records.CreateRecordTypeAsync("Linked people");
        RecordDetails first = await records.CreateRecordAsync(recordType.Id, "First", []);
        RecordDetails second = await records.CreateRecordAsync(recordType.Id, "Second", []);
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read", "relationships.write", "structure.write"]);
        Guid domainId = MonkeysphereDomains.DefaultId;
        var definition = new { domainId, name = "  knows  ", directionality = "symmetric", inverseName = "ignored", idempotencyKey = Guid.CreateVersion7() };
        using JsonDocument createdType = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_relationship_type", definition);
        RecordMutationOutcome typeReceipt = Structured(createdType).Deserialize<RecordCommandReceipt>(JsonOptions)!.Items[0];
        RelationshipType type = Assert.Single(await relationships.ListTypesAsync());
        Assert.Equal(type.Id, typeReceipt.Id);
        Assert.Equal(type.Revision, typeReceipt.Revision);
        Assert.Equal("knows", type.Name);
        Assert.Null(type.InverseName);
        using JsonDocument typeReplay = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_relationship_type", definition);
        Assert.Equal(Structured(createdType).GetRawText(), Structured(typeReplay).GetRawText());
        var create = RelationshipCreateInput(domainId, type, first, second);
        JsonDocument[] concurrent = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_relationship", create)));
        RecordMutationOutcome linkReceipt;
        try
        {
            Assert.Equal(Structured(concurrent[0]).GetRawText(), Structured(concurrent[1]).GetRawText());
            linkReceipt = Structured(concurrent[0]).Deserialize<RecordCommandReceipt>(JsonOptions)!.Items[0];
        }
        finally { foreach (JsonDocument document in concurrent) document.Dispose(); }
        RelationshipView link = Assert.Single(await relationships.ListForRecordAsync(first.Record.Id));
        Assert.Equal(linkReceipt.Id, link.Id);
        Assert.Equal(linkReceipt.Revision, link.Revision);
        Assert.Equal("Private relationship note", link.Note);
        using JsonDocument conflict = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_relationship",
            create with { note = "Different note" });
        AssertWriteError(conflict, "retry_conflict");
        using JsonDocument duplicate = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_relationship",
            RelationshipCreateInput(domainId, type, second, first));
        AssertWriteError(duplicate, "validation_failed");
        await relationships.RenameTypeAsync(type.Id, "is acquainted with", null, expectedRevision: type.Revision);
        using JsonDocument staleType = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_relationship",
            create with { idempotencyKey = Guid.CreateVersion7() });
        AssertWriteError(staleType, "stale_revision");
        var delete = new { domainId, id = link.Id, expectedRevision = link.Revision, idempotencyKey = Guid.CreateVersion7() };
        using JsonDocument staleLink = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "delete_relationship", delete);
        AssertWriteError(staleLink, "stale_revision");
        RelationshipView refreshed = Assert.Single(await relationships.ListForRecordAsync(first.Record.Id));
        var validDelete = delete with { expectedRevision = refreshed.Revision };
        using JsonDocument deleted = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "delete_relationship", validDelete);
        using JsonDocument deleteReplay = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "delete_relationship", validDelete);
        Assert.Equal(Structured(deleted).GetRawText(), Structured(deleteReplay).GetRawText());
        Assert.Equal("deleted", Structured(deleted).Deserialize<RecordCommandReceipt>(JsonOptions)!.Items[0].Outcome);
        using JsonDocument createReplay = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_relationship", create);
        Assert.Equal(linkReceipt, Structured(createReplay).Deserialize<RecordCommandReceipt>(JsonOptions)!.Items[0]);
        Assert.Empty(await relationships.ListForRecordAsync(first.Record.Id));
        Assert.Equal(2, (await records.SearchRecordsAsync(new())).TotalCount);
        await using var connection = await scope.ServiceProvider.GetRequiredService<MonkeysphereConnectionFactory>().OpenConnectionAsync();
        string audit = JsonSerializer.Serialize(await connection.QueryAsync("SELECT * FROM ApplicationCommandAudit;"));
        Assert.DoesNotContain(create.note, audit, StringComparison.Ordinal);
        Assert.DoesNotContain(credential.Secret, audit, StringComparison.Ordinal);
        Assert.Equal(3, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ApplicationCommandAudit WHERE Outcome = 'committed';"));
    }

    [Fact]
    public async Task McpRelationshipsRejectForeignReferencesStaleRecordsAndRotatedReceiptOwnership()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        IRelationshipService relationships = scope.ServiceProvider.GetRequiredService<IRelationshipService>();
        RecordType recordType = await records.CreateRecordTypeAsync("Isolated people");
        RecordDetails first = await records.CreateRecordAsync(recordType.Id, "First", []);
        RecordDetails second = await records.CreateRecordAsync(recordType.Id, "Second", []);
        RelationshipType type = await relationships.CreateTypeAsync(new("parent of", RelationshipDirectionality.Directional, "child of"));
        Guid otherDomain = (await scope.ServiceProvider.GetRequiredService<IDomainRegistry>().CreateAsync("Other links")).Id;
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["relationships.write", "structure.write"]);
        var create = RelationshipCreateInput(MonkeysphereDomains.DefaultId, type, first, second);
        using JsonDocument foreign = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_relationship", create with { domainId = otherDomain });
        AssertWriteError(foreign, "not_found");
        using JsonDocument emptyDomain = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_relationship", create with { domainId = Guid.Empty });
        AssertWriteError(emptyDomain, "validation_failed");
        using JsonDocument missingType = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_relationship",
            create with { typeId = Guid.CreateVersion7() });
        AssertWriteError(missingType, "not_found");
        RecordDetails foreignRecord;
        using (scope.ServiceProvider.GetRequiredService<ICurrentDomainScope>().Use(otherDomain))
        {
            RecordType foreignType = await records.CreateRecordTypeAsync("Foreign people");
            foreignRecord = await records.CreateRecordAsync(foreignType.Id, "Foreign", []);
        }
        using JsonDocument mixed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_relationship",
            create with { targetRecordId = foreignRecord.Record.Id, expectedTargetRevision = foreignRecord.Revision });
        AssertWriteError(mixed, "not_found");
        RecordDetails edited = await records.UpdateRecordAsync(second.Record.Id, "Updated second", [], expectedRevision: second.Revision);
        using JsonDocument stale = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_relationship", create);
        AssertWriteError(stale, "stale_revision");
        var valid = create with { expectedTargetRevision = edited.Revision };
        using JsonDocument created = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_relationship", valid);
        RecordMutationOutcome link = Structured(created).Deserialize<RecordCommandReceipt>(JsonOptions)!.Items[0];
        Assert.Equal("parent of", Assert.Single(await relationships.ListForRecordAsync(first.Record.Id)).Label);
        Assert.Equal("child of", Assert.Single(await relationships.ListForRecordAsync(second.Record.Id)).Label);
        var delete = new { domainId = otherDomain, id = link.Id, expectedRevision = link.Revision, idempotencyKey = Guid.CreateVersion7() };
        using JsonDocument foreignDelete = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "delete_relationship", delete);
        AssertWriteError(foreignDelete, "not_found");
        RemoteCredentialManager manager = factory.Services.GetRequiredService<RemoteCredentialManager>();
        DnaXGeneratedCredential rotated = await manager.RotateAsync(DnaXRemoteSurface.Mcp, surface.Version, ["relationships.write"]);
        using JsonDocument rotatedReplay = await SendAsync(client, surface.EndpointPath!, rotated.Secret, "tools/call", "create_relationship", valid);
        AssertWriteError(rotatedReplay, "validation_failed"); // New credential cannot replay the original receipt; duplicate link remains.
        Assert.Single(await relationships.ListForRecordAsync(first.Record.Id));
    }

    [Fact]
    public async Task McpRelationshipInputsUseSharedValidationAndLeaveNoPartialChanges()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        IRelationshipService relationships = scope.ServiceProvider.GetRequiredService<IRelationshipService>();
        RecordType recordType = await records.CreateRecordTypeAsync("Validation people");
        RecordDetails first = await records.CreateRecordAsync(recordType.Id, "First", []);
        RecordDetails second = await records.CreateRecordAsync(recordType.Id, "Second", []);
        RelationshipType type = await relationships.CreateTypeAsync(new("knows", RelationshipDirectionality.Symmetric));
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["relationships.write", "structure.write"]);
        Guid domainId = MonkeysphereDomains.DefaultId;
        foreach (var input in new[]
        {
            new { name = "", directionality = "symmetric", inverseName = (string?)null },
            new { name = new string('x', 201), directionality = "symmetric", inverseName = (string?)null },
            new { name = "directional", directionality = "directional", inverseName = (string?)null },
            new { name = "numeric", directionality = "0", inverseName = (string?)"inverse" },
            new { name = "unknown", directionality = "invalid", inverseName = (string?)null },
        })
        {
            using JsonDocument invalid = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_relationship_type",
                new { domainId, input.name, input.directionality, input.inverseName, idempotencyKey = Guid.CreateVersion7() });
            AssertWriteError(invalid, "validation_failed");
        }
        RelationshipInput create = RelationshipCreateInput(domainId, type, first, second);
        foreach (RelationshipInput input in new[]
        {
            create with { sourceRecordId = Guid.Empty }, create with { targetRecordId = first.Record.Id },
            create with { expectedTypeRevision = "" }, create with { expectedSourceRevision = "" },
            create with { note = new string('x', 2001) }, create with { idempotencyKey = Guid.Empty },
        })
        {
            using JsonDocument invalid = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_relationship", input);
            AssertWriteError(invalid, "validation_failed");
        }
        await relationships.RetireTypeAsync(type.Id, expectedRevision: type.Revision);
        using JsonDocument staleType = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_relationship", create);
        AssertWriteError(staleType, "stale_revision");
        using JsonDocument retiredType = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_relationship",
            create with { expectedTypeRevision = Assert.Single(await relationships.ListTypesAsync()).Revision });
        AssertWriteError(retiredType, "validation_failed");
        Assert.Single(await relationships.ListTypesAsync());
        Assert.Empty(await relationships.ListForRecordAsync(first.Record.Id));
        await using var connection = await scope.ServiceProvider.GetRequiredService<MonkeysphereConnectionFactory>().OpenConnectionAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RecordCommandReceipts;"));
    }

    /// <summary>
    /// A relationship that has ended is not one that never happened, so the surface that records
    /// them has to be able to say so — both when the ending is already known and, far more often,
    /// when it is learned afterwards. Until contract 1.23 a remote caller could only create a link
    /// or destroy it, and could not see that one had ended even after somebody else said so.
    /// </summary>
    [Fact]
    public async Task McpCanEndARelationshipAndChangeItWithoutReplacingIt()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        IRelationshipService relationships = scope.ServiceProvider.GetRequiredService<IRelationshipService>();
        RecordType recordType = await records.CreateRecordTypeAsync("Employed people");
        RecordDetails ada = await records.CreateRecordAsync(recordType.Id, "Ada", []);
        RecordDetails acme = await records.CreateRecordAsync(recordType.Id, "Acme", []);
        RelationshipType worksAt = await relationships.CreateTypeAsync(
            new("works at", RelationshipDirectionality.Directional, "employs"));
        RelationshipType consultsFor = await relationships.CreateTypeAsync(
            new("consults for", RelationshipDirectionality.Directional, "retains"));
        var (_, credential, surface) = await EnableRelationshipWritesAsync(
            factory, ["records.read", "relationships.write", "structure.write"]);
        Guid domainId = MonkeysphereDomains.DefaultId;

        // Created already over, which is how a job somebody left before this record existed arrives.
        DateTimeOffset ends = new(2025, 6, 30, 23, 59, 0, TimeSpan.Zero);
        using JsonDocument created = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_relationship",
            new
            {
                domainId,
                typeId = worksAt.Id,
                sourceRecordId = ada.Record.Id,
                targetRecordId = acme.Record.Id,
                expectedTypeRevision = worksAt.Revision,
                expectedSourceRevision = ada.Revision,
                expectedTargetRevision = acme.Revision,
                expiresAtUtc = ends,
                idempotencyKey = Guid.CreateVersion7(),
            });
        Guid linkId = Structured(created).Deserialize<RecordCommandReceipt>(JsonOptions)!.Items[0].Id;

        RelationshipView stored = Assert.Single(await relationships.ListForRecordAsync(ada.Record.Id));
        Assert.Equal(ends, stored.Expiry.ExpiresAtUtc);
        Assert.False(stored.Expiry.Expired);

        // And a reader sees it. Before this the ending existed in the database and on the graph,
        // and every remote caller was told the relationship simply ran on.
        using JsonDocument read = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_record_relationships",
            new { id = ada.Record.Id, domainId });
        RemoteRelationship reported = Assert.Single(
            Structured(read).Deserialize<IReadOnlyList<RemoteRelationship>>(JsonOptions)!);
        Assert.Equal(ends, reported.ExpiresAtUtc);
        Assert.False(reported.Expired);

        // Changed in place: a different type, a note, and said outright to be over. The ID is what
        // makes this an edit rather than a replacement, so anything holding it still resolves.
        var edit = new
        {
            domainId,
            id = linkId,
            typeId = consultsFor.Id,
            expectedRevision = stored.Revision,
            expectedTypeRevision = consultsFor.Revision,
            note = "Ended when the contract did",
            expired = true,
            idempotencyKey = Guid.CreateVersion7(),
        };
        using JsonDocument updated = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "update_relationship", edit);
        RecordMutationOutcome receipt = Structured(updated).Deserialize<RecordCommandReceipt>(JsonOptions)!.Items[0];
        Assert.Equal(linkId, receipt.Id);
        Assert.Equal("updated", receipt.Outcome);

        RelationshipView after = Assert.Single(await relationships.ListForRecordAsync(ada.Record.Id));
        Assert.Equal(linkId, after.Id);
        Assert.Equal("consults for", after.Label);
        Assert.Equal("Ended when the contract did", after.Note);
        Assert.True(after.Expiry.Expired);
        Assert.NotEqual(stored.Revision, after.Revision);

        // The same call again replays its receipt rather than writing twice.
        using JsonDocument replay = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "update_relationship", edit);
        Assert.Equal(Structured(updated).GetRawText(), Structured(replay).GetRawText());

        // The revision it was made against is spent, so the same edit under a new key is refused
        // rather than overwriting whatever happened in between.
        using JsonDocument stale = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "update_relationship",
            edit with { idempotencyKey = Guid.CreateVersion7() });
        AssertWriteError(stale, "stale_revision");

        // And the type it names has to be one the caller has actually read.
        using JsonDocument staleType = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "update_relationship",
            edit with { expectedRevision = after.Revision, expectedTypeRevision = worksAt.Revision, idempotencyKey = Guid.CreateVersion7() });
        AssertWriteError(staleType, "stale_revision");

        Assert.Equal(2, (await records.SearchRecordsAsync(new())).TotalCount);
    }

    /// <summary>
    /// Editing a relationship is a link write, not a structural one, and must not fall out of a
    /// grant that only reshapes types — nor be reachable from one that cannot write links at all.
    /// </summary>
    [Theory]
    [InlineData("relationships.write", true)]
    [InlineData("structure.write", false)]
    [InlineData("records.write", false)]
    public async Task McpRelationshipEditingNeedsTheLinkWriteGrant(string grant, bool allowed)
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        IRelationshipService relationships = scope.ServiceProvider.GetRequiredService<IRelationshipService>();
        RecordType recordType = await records.CreateRecordTypeAsync("Guarded people");
        RecordDetails first = await records.CreateRecordAsync(recordType.Id, "First", []);
        RecordDetails second = await records.CreateRecordAsync(recordType.Id, "Second", []);
        RelationshipType type = await relationships.CreateTypeAsync(new("knows", RelationshipDirectionality.Symmetric));
        RelationshipView link = await relationships.CreateAsync(type.Id, first.Record.Id, second.Record.Id);
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, [grant]);

        using JsonDocument capabilities = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_capabilities");
        Assert.Equal(allowed, Structured(capabilities).Deserialize<RemoteCapabilities>(JsonOptions)!
            .Tools.Single(tool => tool.Name == "update_relationship").Allowed);

        using JsonDocument attempted = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "update_relationship",
            new
            {
                domainId = MonkeysphereDomains.DefaultId,
                id = link.Id,
                typeId = type.Id,
                expectedRevision = link.Revision,
                expectedTypeRevision = type.Revision,
                expired = true,
                idempotencyKey = Guid.CreateVersion7(),
            });
        if (allowed)
        {
            _ = Structured(attempted);
        }
        else
        {
            AssertWriteError(attempted, "permission_denied");
        }

        Assert.Equal(allowed, Assert.Single(await relationships.ListForRecordAsync(first.Record.Id)).Expiry.Expired);
    }

    private sealed record RelationshipInput(Guid domainId, Guid typeId, Guid sourceRecordId, Guid targetRecordId,
        string expectedTypeRevision, string expectedSourceRevision, string expectedTargetRevision, string note, Guid idempotencyKey);

    private static RelationshipInput RelationshipCreateInput(Guid domainId, RelationshipType type, RecordDetails first, RecordDetails second) =>
        new(domainId, type.Id, first.Record.Id, second.Record.Id, type.Revision, first.Revision, second.Revision,
            "Private relationship note", Guid.CreateVersion7());

    private static async Task<(IDnaXRemoteAccessAdministration Administration, DnaXGeneratedCredential Credential, DnaXRemoteEffectiveSurface Surface)>
        EnableRelationshipWritesAsync(RemoteEnabledApplicationFactory factory, IReadOnlyList<string> grants)
    {
        IDnaXRemoteAccessAdministration administration = factory.Services.GetRequiredService<IDnaXRemoteAccessAdministration>();
        RemoteCredentialManager manager = factory.Services.GetRequiredService<RemoteCredentialManager>();
        DnaXGeneratedCredential credential = await manager.RotateAsync(DnaXRemoteSurface.Mcp, 0, grants.ToArray());
        DnaXRemoteEffectiveSurface surface = await administration.SetActivationAsync(DnaXRemoteSurface.Mcp, true, false, credential.Version);
        return (administration, credential, surface);
    }
}
