using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;
using Monkeysphere.Web.Remote;

namespace Monkeysphere.Web.Tests;

/// <summary>
/// A relationship definition could be created remotely and then only relabelled or retired in the
/// browser. These pin the labelling rule holding wherever it is asked for — an inverse label belongs to
/// a directional type and nowhere else, and directionality itself never changes because every existing
/// relationship was recorded under it — and that retiring a type keeps what it already links.
/// </summary>
public sealed partial class RemoteDiscoveryTests
{
    [Fact]
    public async Task McpRelabelsARelationshipTypeUnderItsOwnDirectionalityRule()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        Guid domainId = MonkeysphereDomains.DefaultId;

        Guid directionalId;
        Guid symmetricId;
        string directionalRevision;
        string symmetricRevision;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IRelationshipService relationships = scope.ServiceProvider.GetRequiredService<IRelationshipService>();
            RelationshipType directional = await relationships.CreateTypeAsync(
                new("Employs", RelationshipDirectionality.Directional, "Works for"));
            RelationshipType symmetric = await relationships.CreateTypeAsync(
                new("Sibling of", RelationshipDirectionality.Symmetric));
            directionalId = directional.Id;
            symmetricId = symmetric.Id;
            directionalRevision = directional.Revision;
            symmetricRevision = symmetric.Revision;
        }

        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read", "structure.write"]);
        Guid key = Guid.CreateVersion7();

        using JsonDocument renamed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "rename_relationship_type",
            new { domainId, typeId = directionalId, expectedRevision = directionalRevision, name = "  Employer of  ", inverseName = "  Employed by  ", idempotencyKey = key });
        RecordMutationOutcome outcome = Assert.Single(Structured(renamed).Deserialize<RecordCommandReceipt>(JsonOptions)!.Items);
        Assert.Equal("updated", outcome.Outcome);

        // The identical retry replays rather than relabelling again, stale revision and all.
        using JsonDocument replayed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "rename_relationship_type",
            new { domainId, typeId = directionalId, expectedRevision = directionalRevision, name = "  Employer of  ", inverseName = "  Employed by  ", idempotencyKey = key });
        Assert.Equal(outcome.Revision, Assert.Single(Structured(replayed).Deserialize<RecordCommandReceipt>(JsonOptions)!.Items).Revision);

        using JsonDocument stale = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "rename_relationship_type",
            new { domainId, typeId = directionalId, expectedRevision = directionalRevision, name = "Something else", inverseName = "Or other", idempotencyKey = Guid.CreateVersion7() });
        AssertWriteError(stale, "stale_revision");

        // A directional type with no inverse label leaves half its relationships unreadable, so it is
        // refused rather than stored.
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            RelationshipType current = Assert.Single(
                await scope.ServiceProvider.GetRequiredService<IRelationshipService>().ListTypesAsync(),
                type => type.Id == directionalId);
            Assert.Equal("Employer of", current.Name);
            Assert.Equal("Employed by", current.InverseName);

            using JsonDocument missing = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "rename_relationship_type",
                new { domainId, typeId = directionalId, expectedRevision = current.Revision, name = "Employer of", idempotencyKey = Guid.CreateVersion7() });
            AssertWriteError(missing, "validation_failed");
        }

        // A symmetric type has no inverse, and one offered for it is dropped rather than refused, which
        // is what create_relationship_type has always done: the label would never be read, and refusing
        // a request that asks for exactly what a symmetric type already is would be pedantry.
        using JsonDocument spurious = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "rename_relationship_type",
            new { domainId, typeId = symmetricId, expectedRevision = symmetricRevision, name = "  Sibling  ", inverseName = "Also sibling", idempotencyKey = Guid.CreateVersion7() });
        _ = Structured(spurious);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            RelationshipType symmetric = Assert.Single(
                await scope.ServiceProvider.GetRequiredService<IRelationshipService>().ListTypesAsync(),
                type => type.Id == symmetricId);
            Assert.Equal("Sibling", symmetric.Name);
            Assert.Null(symmetric.InverseName);
            Assert.Equal(RelationshipDirectionality.Symmetric, symmetric.Directionality);
        }
    }

    [Fact]
    public async Task McpRetiresARelationshipTypeAndKeepsWhatItAlreadyLinks()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        Guid domainId = MonkeysphereDomains.DefaultId;

        Guid typeId;
        Guid adaId;
        Guid graceId;
        string revision;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            IRelationshipService relationships = scope.ServiceProvider.GetRequiredService<IRelationshipService>();
            RecordType person = await records.CreateRecordTypeAsync("Linked person");
            adaId = (await records.CreateRecordAsync(person.Id, "Ada", [])).Record.Id;
            graceId = (await records.CreateRecordAsync(person.Id, "Grace", [])).Record.Id;
            RelationshipType type = await relationships.CreateTypeAsync(
                new("Corresponds with", RelationshipDirectionality.Symmetric));
            typeId = type.Id;
            _ = await relationships.CreateAsync(type.Id, adaId, graceId, "Letters");
            revision = Assert.Single(await relationships.ListTypesAsync(), listed => listed.Id == typeId).Revision;
        }

        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read", "structure.write", "relationships.write"]);

        using JsonDocument retired = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "retire_relationship_type",
            new { domainId, typeId, expectedRevision = revision, idempotencyKey = Guid.CreateVersion7() });
        Assert.Equal("retired", Assert.Single(Structured(retired).Deserialize<RecordCommandReceipt>(JsonOptions)!.Items).Outcome);

        string retiredRevision;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IRelationshipService relationships = scope.ServiceProvider.GetRequiredService<IRelationshipService>();
            RelationshipType type = Assert.Single(await relationships.ListTypesAsync(), listed => listed.Id == typeId);
            Assert.Equal(RelationshipLifecycle.Retired, type.Lifecycle);
            retiredRevision = type.Revision;

            // The relationship it already recorded is still there and still labelled, which is the whole
            // reason retirement is not a deletion.
            RelationshipView view = Assert.Single((await relationships.QueryForRecordAsync(adaId, 1, 25)).Items);
            Assert.Equal(graceId, view.RelatedRecordId);
            Assert.Equal("Corresponds with", view.Label);
            Assert.Equal("Letters", view.Note);
        }

        // Nothing new can be recorded under it, from here as much as from the page.
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            RecordDetails ada = (await scope.ServiceProvider.GetRequiredService<IMonkeysphereService>().GetRecordAsync(adaId))!;
            RecordDetails grace = (await scope.ServiceProvider.GetRequiredService<IMonkeysphereService>().GetRecordAsync(graceId))!;
            using JsonDocument refused = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_relationship",
                new
                {
                    domainId,
                    typeId,
                    sourceRecordId = graceId,
                    targetRecordId = adaId,
                    expectedTypeRevision = retiredRevision,
                    expectedSourceRevision = grace.Revision,
                    expectedTargetRevision = ada.Revision,
                    idempotencyKey = Guid.CreateVersion7(),
                });
            AssertWriteError(refused, "validation_failed");
        }

        // Retiring it twice is refused rather than reported as done.
        using JsonDocument twice = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "retire_relationship_type",
            new { domainId, typeId, expectedRevision = retiredRevision, idempotencyKey = Guid.CreateVersion7() });
        AssertWriteError(twice, "validation_failed");

        // But it can still be relabelled, because its relationships are still shown somewhere.
        using JsonDocument relabelled = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "rename_relationship_type",
            new { domainId, typeId, expectedRevision = retiredRevision, name = "Used to correspond with", idempotencyKey = Guid.CreateVersion7() });
        _ = Structured(relabelled);
    }

    [Theory]
    [InlineData("structure.write", true)]
    [InlineData("relationships.write", false)]
    public async Task RelationshipTypeLifecycleNeedsStructureWriteRatherThanRelationshipWrite(string grant, bool allowed)
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        Guid domainId = MonkeysphereDomains.DefaultId;

        Guid typeId;
        string revision;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            RelationshipType type = await scope.ServiceProvider.GetRequiredService<IRelationshipService>()
                .CreateTypeAsync(new("Guarded link", RelationshipDirectionality.Symmetric));
            typeId = type.Id;
            revision = type.Revision;
        }

        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read", grant]);

        // Recording a relationship and changing what a relationship means are different powers, so the
        // grant that lets a client link two records does not let it relabel or retire the definition.
        using JsonDocument renamed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "rename_relationship_type",
            new { domainId, typeId, expectedRevision = revision, name = "Renamed by a grant", idempotencyKey = Guid.CreateVersion7() });
        if (allowed) _ = Structured(renamed); else AssertWriteError(renamed, "permission_denied");

        // Read again, because a successful rename moved it: sending the pre-rename revision here would
        // fail as staleness and say nothing about the grant.
        string current = Assert.Single(
            await factory.Services.CreateScope().ServiceProvider.GetRequiredService<IRelationshipService>().ListTypesAsync(),
            listed => listed.Id == typeId).Revision;
        using JsonDocument retired = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "retire_relationship_type",
            new { domainId, typeId, expectedRevision = current, idempotencyKey = Guid.CreateVersion7() });
        if (allowed) _ = Structured(retired); else AssertWriteError(retired, "permission_denied");

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            RelationshipType type = Assert.Single(
                await scope.ServiceProvider.GetRequiredService<IRelationshipService>().ListTypesAsync(),
                listed => listed.Id == typeId);
            Assert.Equal(allowed ? "Renamed by a grant" : "Guarded link", type.Name);
            Assert.Equal(allowed ? RelationshipLifecycle.Retired : RelationshipLifecycle.Active, type.Lifecycle);
        }
    }
}
