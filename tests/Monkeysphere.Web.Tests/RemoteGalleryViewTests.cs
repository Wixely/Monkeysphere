using System.Text.Json;
using DnaX.RemoteAccess;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;
using Monkeysphere.Data;
using Monkeysphere.Web.Remote;

namespace Monkeysphere.Web.Tests;

/// <summary>
/// The gallery view type over MCP. What matters here beyond round-tripping the kind is that running a
/// gallery returns what a gallery is made of — the pictures and the connections — so a client can draw
/// the same page in one call, and that it still returns no image bytes, because reading pictures out of
/// the deployment is a different permission from asking a view a question.
/// </summary>
public sealed partial class RemoteDiscoveryTests
{
    private const string Pixel =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    [Fact]
    public async Task McpSavesAGalleryViewAndRunsItWithItsPicturesAndConnectionsButNoBytes()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        IRecordImageService images = scope.ServiceProvider.GetRequiredService<IRecordImageService>();
        IRelationshipService relationships = scope.ServiceProvider.GetRequiredService<IRelationshipService>();

        RecordType locomotive = await records.CreateRecordTypeAsync("Locomotive", "🚆");
        RecordType depot = await records.CreateRecordTypeAsync("Depot", "🏭");
        FieldDefinition number = await records.CreateAndAttachFieldAsync(locomotive.Id, new("Running number", FieldTypes.Text, false));
        RecordDetails engine = await records.CreateRecordAsync(locomotive.Id, "Flying Scotsman", [new(number.Id, "60103")]);
        RecordDetails works = await records.CreateRecordAsync(depot.Id, "Doncaster Works", []);

        using MemoryStream first = new(Convert.FromBase64String(Pixel));
        RecordImage engineImage = await images.AddAsync(engine.Record.Id, first, "scotsman.png");
        using MemoryStream second = new(Convert.FromBase64String(Pixel));
        RecordImage depotImage = await images.AddAsync(works.Record.Id, second, "doncaster.png");

        RelationshipType allocatedTo = await relationships.CreateTypeAsync(
            new("allocated to", RelationshipDirectionality.Directional, "hosts"));
        await relationships.CreateAsync(allocatedTo.Id, engine.Record.Id, works.Record.Id);

        IDnaXRemoteAccessAdministration administration = factory.Services.GetRequiredService<IDnaXRemoteAccessAdministration>();
        DnaXGeneratedCredential credential = await administration.RotateCredentialAsync(
            DnaXRemoteSurface.Mcp, 0, ["records.read", "views.manage"]);
        DnaXRemoteEffectiveSurface surface = await administration.SetActivationAsync(DnaXRemoteSurface.Mcp, true, false, credential.Version);
        Guid domainId = MonkeysphereDomains.DefaultId;

        using JsonDocument created = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_saved_view",
            new
            {
                domainId,
                name = "Locomotives",
                recordTypeId = locomotive.Id,
                columnFieldDefinitionIds = new[] { number.Id },
                kind = "Gallery",
            });
        RemoteSavedView view = Structured(created).Deserialize<RemoteSavedView>(JsonOptions)!;
        Assert.Equal(nameof(SavedViewKind.Gallery), view.Kind);

        // Read back and listed the same way it was saved, because a client that cannot tell which
        // kind a view is cannot draw it.
        using JsonDocument fetched = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_saved_view",
            new { domainId, id = view.Id });
        Assert.Equal(nameof(SavedViewKind.Gallery), Structured(fetched).Deserialize<RemoteSavedView>(JsonOptions)!.Kind);

        using JsonDocument listed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "list_saved_views",
            new { domainId });
        Assert.Equal(nameof(SavedViewKind.Gallery), Assert.Single(
            Structured(listed).Deserialize<RemoteSavedView[]>(JsonOptions)!).Kind);

        using JsonDocument run = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "run_saved_view",
            new { domainId, id = view.Id });
        RemotePage<RemoteSavedViewRow> page = Structured(run).Deserialize<RemotePage<RemoteSavedViewRow>>(JsonOptions)!;
        RemoteSavedViewRow row = Assert.Single(page.Items);

        // The record's own pictures, named but not sent: a gallery needs to know which images to ask
        // for, and asking for them is read_record_image under the media grant this credential lacks.
        Assert.Equal(engineImage.Id, Assert.Single(row.Images).Id);
        Assert.Equal(1, row.TotalImageCount);
        Assert.DoesNotContain(Pixel[..24], run.RootElement.GetRawText(), StringComparison.Ordinal);

        // And what the record is connected to, carrying that record's own picture, so one call draws
        // one page rather than one call plus a relationship query per row.
        RemoteGalleryRelation relation = Assert.Single(row.Related);
        Assert.Equal(works.Record.Id, relation.RecordId);
        Assert.Equal("Doncaster Works", relation.DisplayName);
        Assert.Equal("allocated to", relation.Label);
        Assert.Equal(depotImage.Id, relation.ImageId);
        Assert.False(relation.IsExpired);
        Assert.Equal(1, row.TotalRelatedCount);

        // The values are still the shape every other remote read returns, rather than a rendered line.
        Assert.Equal("60103", Assert.Single(row.Values).Value);

        // The bounds a client plans a collage against are published rather than discovered by refusal.
        using JsonDocument capabilities = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_capabilities");
        RemoteCapabilities discovery = Structured(capabilities).Deserialize<RemoteCapabilities>(JsonOptions)!;
        Assert.Equal("1.39", discovery.ContractVersion);
        Assert.Equal(new RemoteSavedViewLimits(), discovery.SavedViewLimits);
    }

    [Fact]
    public async Task AGalleryViewKeepsItsKindThroughACopyAndLosesItToAnUpdateThatOmitsIt()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        RecordType locomotive = await records.CreateRecordTypeAsync("Locomotive", "🚆");

        IDnaXRemoteAccessAdministration administration = factory.Services.GetRequiredService<IDnaXRemoteAccessAdministration>();
        DnaXGeneratedCredential credential = await administration.RotateCredentialAsync(
            DnaXRemoteSurface.Mcp, 0, ["records.read", "views.manage"]);
        DnaXRemoteEffectiveSurface surface = await administration.SetActivationAsync(DnaXRemoteSurface.Mcp, true, false, credential.Version);
        Guid domainId = MonkeysphereDomains.DefaultId;

        using JsonDocument created = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_saved_view",
            new { domainId, name = "Locomotives", recordTypeId = locomotive.Id, kind = "gallery" });
        RemoteSavedView view = Structured(created).Deserialize<RemoteSavedView>(JsonOptions)!;

        // Spelled without regard to case, because a caller typing the name of a thing should not have
        // to guess its capitalisation.
        Assert.Equal(nameof(SavedViewKind.Gallery), view.Kind);

        using JsonDocument copied = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "duplicate_saved_view",
            new { domainId, id = view.Id, name = "Locomotives copy" });
        Assert.Equal(nameof(SavedViewKind.Gallery), Structured(copied).Deserialize<RemoteSavedView>(JsonOptions)!.Kind);

        // An update replaces rather than merges, which the tool says plainly: a kind left out reverts
        // the view to a grid, the same way a filter list left out is stored empty.
        using JsonDocument updated = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "update_saved_view",
            new { domainId, id = view.Id, name = "Locomotives", recordTypeId = locomotive.Id });
        Assert.Equal(nameof(SavedViewKind.Grid), Structured(updated).Deserialize<RemoteSavedView>(JsonOptions)!.Kind);

        // A kind that is not one of the named ones is refused as the caller's mistake rather than
        // stored as an integer nothing can draw.
        using JsonDocument invented = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_saved_view",
            new { domainId, name = "Invented", recordTypeId = locomotive.Id, kind = "mosaic" });
        AssertWriteError(invented, "validation_failed");
    }
}
