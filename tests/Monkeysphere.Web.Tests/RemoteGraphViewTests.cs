using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;
using Monkeysphere.Web.Remote;

namespace Monkeysphere.Web.Tests;

/// <summary>
/// A graph view is the one saved thing in this application that cannot be reconstructed from its
/// inputs: a layout somebody arranged by hand has no rule behind it, so a caller able to filter the
/// graph but not read the arrangement could never reopen the view that was saved. These pin the
/// arrangement surviving the round trip, the drawing staying inside the boundary the operator set,
/// and the same two separations the saved record views draw.
/// </summary>
public sealed partial class RemoteDiscoveryTests
{
    [Fact]
    public async Task McpSavesAndReopensAGraphArrangementAndDrawsWhatTheViewSelects()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        IRelationshipService relationships = scope.ServiceProvider.GetRequiredService<IRelationshipService>();
        RecordType type = await records.CreateRecordTypeAsync("Drawn person");
        RecordDetails ada = await records.CreateRecordAsync(type.Id, "Ada", []);
        RecordDetails charles = await records.CreateRecordAsync(type.Id, "Charles", []);
        RelationshipType knows = await relationships.CreateTypeAsync(new("knows", RelationshipDirectionality.Directional, "known by"));
        RelationshipView link = await relationships.CreateAsync(knows.Id, ada.Record.Id, charles.Record.Id);

        var (_, credential, surface) = await EnableRelationshipWritesAsync(
            factory, ["records.read", "views.manage"]);
        Guid domainId = MonkeysphereDomains.DefaultId;

        using JsonDocument createdResult = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_graph_view", new
        {
            domainId,
            name = "  Ada and Charles  ",
            displayMode = "connected",
            selectedRecordIds = new[] { ada.Record.Id },
            recordTypeIds = new[] { type.Id },
            nodePositions = new[]
            {
                new RemoteGraphNodePosition(ada.Record.Id, -120.5, 40),
                new RemoteGraphNodePosition(charles.Record.Id, 260, -18.25),
            },
            viewport = new RemoteGraphViewport(12, -34, 1.5),
        });
        RemoteGraphView created = Structured(createdResult).Deserialize<RemoteGraphView>(JsonOptions)!;
        Assert.Equal("Ada and Charles", created.Name);
        Assert.Equal("connected", created.DisplayMode);

        using JsonDocument listed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "list_graph_views", new { domainId });
        RemoteGraphView summary = Assert.Single(Structured(listed).Deserialize<RemoteGraphView[]>(JsonOptions)!);
        Assert.Equal(created.Id, summary.Id);

        // The list omits the arrangement, which is the bulk of a view and of no use to a caller that
        // is listing rather than reopening.
        Assert.Empty(summary.NodePositions);
        Assert.Null(summary.Viewport);

        using JsonDocument fetched = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_graph_view",
            new { domainId, id = created.Id });
        RemoteGraphView full = Structured(fetched).Deserialize<RemoteGraphView>(JsonOptions)!;

        // The coordinates come back exactly as they went in, negatives and fractions included. A
        // layout rounded on the way through is a layout nobody arranged.
        Assert.Equal(2, full.NodePositions.Count);
        RemoteGraphNodePosition adaPosition = full.NodePositions.Single(position => position.RecordId == ada.Record.Id);
        Assert.Equal(-120.5, adaPosition.X);
        Assert.Equal(40, adaPosition.Y);
        Assert.Equal(new RemoteGraphViewport(12, -34, 1.5), full.Viewport);

        using JsonDocument drawnResult = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "query_graph",
            new { domainId, graphViewId = created.Id });
        RemoteGraph drawn = Structured(drawnResult).Deserialize<RemoteGraph>(JsonOptions)!;

        // The view's own selection drew the graph: Ada, and Charles because he is connected to her.
        Assert.Equal([ada.Record.Id, charles.Record.Id], drawn.Nodes.Select(node => node.RecordId).Order());
        RemoteGraphEdge edge = Assert.Single(drawn.Edges);
        Assert.Equal(link.Id, edge.RelationshipId);
        Assert.Equal("directional", edge.Directionality);
        Assert.False(edge.IsExpired);
        Assert.False(drawn.NodesTruncated);
        Assert.False(drawn.EdgesTruncated);

        // An ended relationship is drawn and said to be over rather than omitted, because the graph
        // is a record of what was as well as what is.
        await relationships.UpdateAsync(link.Id, knows.Id, null, ada.Record.Id, link.Revision,
            new RelationshipExpiry(ExpiresAtUtc: DateTimeOffset.UtcNow.AddDays(-1)));
        using JsonDocument expiredResult = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "query_graph",
            new { domainId, graphViewId = created.Id });
        Assert.True(Assert.Single(Structured(expiredResult).Deserialize<RemoteGraph>(JsonOptions)!.Edges).IsExpired);

        using JsonDocument updatedResult = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "update_graph_view",
            new { domainId, id = created.Id, name = "Everyone", recordTypeIds = new[] { type.Id } });
        RemoteGraphView updated = Structured(updatedResult).Deserialize<RemoteGraphView>(JsonOptions)!;

        // A replacement rather than a merge: an arrangement left out is forgotten, which is the only
        // way a caller can reset one.
        Assert.Equal("Everyone", updated.Name);
        Assert.Equal("all", updated.DisplayMode);
        Assert.Empty(updated.NodePositions);
        Assert.Null(updated.Viewport);
        Assert.Empty(updated.RecordIds);

        using JsonDocument deleted = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "delete_graph_view",
            new { domainId, id = created.Id });
        Assert.Equal(created.Id, Structured(deleted).Deserialize<RemoteGraphViewDeletion>(JsonOptions)!.Id);
        using JsonDocument again = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "delete_graph_view",
            new { domainId, id = created.Id });
        AssertWriteError(again, "not_found");

        // Deleting the arrangement destroyed no records and no links.
        Assert.Equal(2, (await records.SearchRecordsAsync(new RecordSearch(RecordTypeId: type.Id))).TotalCount);
        Assert.Single(await relationships.ListForRecordAsync(ada.Record.Id));
    }

    [Fact]
    public async Task AGraphQueryStaysInsideTheBoundaryTheOperatorSetAndSaysWhenItStoppedEarly()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        IGraphSettingsService settings = scope.ServiceProvider.GetRequiredService<IGraphSettingsService>();
        _ = await settings.SaveAsync(new(NodeLimit: RelationshipGraphService.MinimumNodes, EdgeLimit: RelationshipGraphService.MinimumEdges));
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        RecordType type = await records.CreateRecordTypeAsync("Crowded type");
        for (int index = 0; index < RelationshipGraphService.MinimumNodes + 5; index++)
        {
            _ = await records.CreateRecordAsync(type.Id, $"Record {index}", []);
        }

        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read"]);
        Guid domainId = MonkeysphereDomains.DefaultId;

        using JsonDocument defaulted = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "query_graph", new { domainId });
        RemoteGraph graph = Structured(defaulted).Deserialize<RemoteGraph>(JsonOptions)!;

        // The domain's configured limit, not this build's ceiling, and the graph says it stopped.
        Assert.Equal(RelationshipGraphService.MinimumNodes, graph.AppliedNodeLimit);
        Assert.Equal(RelationshipGraphService.MinimumNodes, graph.Nodes.Count);
        Assert.True(graph.NodesTruncated);

        // Asking for more than the operator allowed does not get more. Raising what a query may draw
        // means raising the setting, which is a different grant — otherwise the settings page would
        // be advice rather than a boundary.
        using JsonDocument greedy = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "query_graph",
            new { domainId, nodeLimit = RelationshipGraphService.MaximumNodes });
        RemoteGraph capped = Structured(greedy).Deserialize<RemoteGraph>(JsonOptions)!;
        Assert.Equal(RelationshipGraphService.MinimumNodes, capped.AppliedNodeLimit);
        Assert.True(capped.NodesTruncated);

        // Asking for less than the limit is honoured, because that narrows rather than widens.
        using JsonDocument modest = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "query_graph",
            new { domainId, nodeLimit = 2 });
        RemoteGraph small = Structured(modest).Deserialize<RemoteGraph>(JsonOptions)!;
        Assert.Equal(2, small.AppliedNodeLimit);
        Assert.Equal(2, small.Nodes.Count);

        // Once the operator raises the setting the same query draws more, which is the other half of
        // the boundary being real.
        _ = await settings.SaveAsync(new(NodeLimit: RelationshipGraphService.MinimumNodes + 20, EdgeLimit: RelationshipGraphService.MinimumEdges));
        using JsonDocument raised = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "query_graph", new { domainId });
        RemoteGraph wider = Structured(raised).Deserialize<RemoteGraph>(JsonOptions)!;
        Assert.Equal(RelationshipGraphService.MinimumNodes + 5, wider.Nodes.Count);
        Assert.False(wider.NodesTruncated);
    }

    [Theory]
    [InlineData("records.read", true, false, true)]
    [InlineData("views.manage", true, true, false)]
    [InlineData("structure.write", false, false, false)]
    public async Task McpSeparatesArrangingAGraphFromDrawingIt(string grant, bool canRead, bool canWrite, bool canDraw)
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            _ = await records.CreateRecordTypeAsync("Guarded graph type");
        }

        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, [grant]);
        Guid domainId = MonkeysphereDomains.DefaultId;

        using JsonDocument capabilities = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_capabilities");
        RemoteCapabilities granted = Structured(capabilities).Deserialize<RemoteCapabilities>(JsonOptions)!;
        Assert.Equal(canRead, granted.Tools.Single(tool => tool.Name == "get_graph_view").Allowed);
        Assert.Equal(canWrite, granted.Tools.Single(tool => tool.Name == "create_graph_view").Allowed);
        Assert.Equal(canDraw, granted.Tools.Single(tool => tool.Name == "query_graph").Allowed);

        using JsonDocument listed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "list_graph_views", new { domainId });
        if (canRead) _ = Structured(listed); else AssertWriteError(listed, "permission_denied");

        using JsonDocument written = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_graph_view",
            new { domainId, name = "Attempted" });
        if (canWrite) _ = Structured(written); else AssertWriteError(written, "permission_denied");

        // Drawing a graph returns record names and the links between them, so it takes the read
        // grant: a credential that may arrange a graph may not thereby see what is in it.
        using JsonDocument drawn = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "query_graph", new { domainId });
        if (canDraw) _ = Structured(drawn); else AssertWriteError(drawn, "permission_denied");
    }

    [Fact]
    public async Task McpRefusesGraphViewsAndQueriesThatBreachTheirBounds()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using IServiceScope scope = factory.Services.CreateScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        RecordType type = await records.CreateRecordTypeAsync("Bounded graph type");
        RecordDetails only = await records.CreateRecordAsync(type.Id, "Only", []);
        RecordType unlisted = await records.CreateRecordTypeAsync("Unlisted type");
        RecordDetails stranger = await records.CreateRecordAsync(unlisted.Id, "Stranger", []);

        var (_, credential, surface) = await EnableRelationshipWritesAsync(
            factory, ["records.read", "views.manage"]);
        Guid domainId = MonkeysphereDomains.DefaultId;

        object[] refused =
        [
            new { domainId, name = "Unnamed mode", displayMode = "sideways" },
            // Connected and isolated are relative to something, so they need a record to be relative to.
            new { domainId, name = "Connected to nothing", displayMode = "connected", recordTypeIds = new[] { type.Id } },
            // A remembered node of a type the view does not draw is a node the view would never show.
            new { domainId, name = "Positioned stranger", recordTypeIds = new[] { type.Id },
                nodePositions = new[] { new RemoteGraphNodePosition(stranger.Record.Id, 0, 0) } },
            new { domainId, name = "Selected stranger", displayMode = "connected",
                selectedRecordIds = new[] { stranger.Record.Id }, recordTypeIds = new[] { type.Id } },
            new { domainId, name = "Absent record", displayMode = "connected",
                selectedRecordIds = new[] { Guid.CreateVersion7() }, recordTypeIds = new[] { type.Id } },
            new { domainId, name = "Two of the same node", recordTypeIds = new[] { type.Id },
                nodePositions = new[] { new RemoteGraphNodePosition(only.Record.Id, 1, 1), new RemoteGraphNodePosition(only.Record.Id, 2, 2) } },
            new { domainId, name = "Off the canvas", recordTypeIds = new[] { type.Id },
                nodePositions = new[] { new RemoteGraphNodePosition(only.Record.Id, GraphViewService.MaximumCoordinateMagnitude + 1, 0) } },
            new { domainId, name = "Zoomed too far in", recordTypeIds = new[] { type.Id },
                viewport = new RemoteGraphViewport(0, 0, GraphViewService.MaximumZoom + 0.01) },
            new { domainId, name = "Zoomed too far out", recordTypeIds = new[] { type.Id },
                viewport = new RemoteGraphViewport(0, 0, GraphViewService.MinimumZoom - 0.01) },
            new { domainId, name = new string('n', GraphViewService.MaximumNameLength + 1) },
            new { domainId, name = "Too many records", displayMode = "connected", recordTypeIds = new[] { type.Id },
                selectedRecordIds = Enumerable.Range(0, RelationshipGraphService.MaximumSelectedRecords + 1).Select(_ => Guid.CreateVersion7()).ToArray() },
        ];
        foreach (object request in refused)
        {
            using JsonDocument response = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_graph_view", request);
            AssertWriteError(response, "validation_failed");
        }

        using JsonDocument nothingStored = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "list_graph_views", new { domainId });
        Assert.Empty(Structured(nothingStored).Deserialize<RemoteGraphView[]>(JsonOptions)!);

        Guid viewId = Structured(await CreateAsync()).Deserialize<RemoteGraphView>(JsonOptions)!.Id;
        async Task<JsonDocument> CreateAsync() => await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_graph_view",
            new { domainId, name = "Drawable", recordTypeIds = new[] { type.Id } });

        object[] badQueries =
        [
            // Two selections would silently disagree, so sending both is refused rather than one of
            // them quietly winning.
            new { domainId, graphViewId = viewId, displayMode = "all" },
            new { domainId, graphViewId = viewId, selectedRecordIds = new[] { only.Record.Id } },
            new { domainId, graphViewId = viewId, recordTypeIds = new[] { type.Id } },
            new { domainId, depth = RelationshipGraphService.MaximumDepth + 1 },
            new { domainId, depth = -1 },
            new { domainId, search = new string('s', RelationshipGraphService.MaximumSearchLength + 1) },
            new { domainId, displayMode = "diagonal" },
            // Connected and isolated are relative to something, so a caller that named neither a
            // selection nor a focus is told rather than handed an empty graph it would read as
            // "nothing matches". This is the rule a saved graph view is already held to.
            new { domainId, displayMode = "isolated" },
            new { domainId, displayMode = "connected" },
            // Likewise an explicitly empty record-type list, which is almost always a selection that
            // came out empty rather than a deliberate request to draw nothing.
            new { domainId, recordTypeIds = Array.Empty<Guid>() },
            new { domainId, nodeLimit = 0 },
        ];
        foreach (object request in badQueries)
        {
            using JsonDocument response = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "query_graph", request);
            AssertWriteError(response, "validation_failed");
        }

        // Omitting the filter is not the same as emptying it: the graph opens on every active type,
        // which is how the page opens and therefore the only default that will not surprise.
        using JsonDocument everyType = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "query_graph", new { domainId });
        RemoteGraph all = Structured(everyType).Deserialize<RemoteGraph>(JsonOptions)!;
        Assert.Equal([only.Record.Id, stranger.Record.Id], all.Nodes.Select(node => node.RecordId).Order());

        // A view that remembers no record types is reproduced as it was saved rather than widened,
        // because an empty selection there is what the operator chose.
        using JsonDocument narrow = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "query_graph",
            new { domainId, graphViewId = viewId });
        Assert.Equal(only.Record.Id, Assert.Single(Structured(narrow).Deserialize<RemoteGraph>(JsonOptions)!.Nodes).RecordId);

        using JsonDocument missing = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "query_graph",
            new { domainId, graphViewId = Guid.CreateVersion7() });
        AssertWriteError(missing, "not_found");
        using JsonDocument missingUpdate = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "update_graph_view",
            new { domainId, id = Guid.CreateVersion7(), name = "Ghost" });
        AssertWriteError(missingUpdate, "not_found");
    }

    [Fact]
    public async Task AGraphViewBelongsToItsDomainAndCapabilitiesReportItsBounds()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        MonkeysphereDomain other = await factory.Services.GetRequiredService<IDomainRegistry>().CreateAsync("Other graph domain");
        Guid viewId;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            RecordType type = await records.CreateRecordTypeAsync("Domestic graph type");
            viewId = (await scope.ServiceProvider.GetRequiredService<IGraphViewService>()
                .CreateAsync(new("Local", RelationshipGraphDisplayMode.All, [], [type.Id]))).Id;
        }

        var (_, credential, surface) = await EnableRelationshipWritesAsync(
            factory, ["records.read", "views.manage"]);

        using JsonDocument elsewhere = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_graph_view",
            new { domainId = other.Id, id = viewId });
        Assert.Empty(elsewhere.RootElement.GetProperty("result").GetProperty("content").EnumerateArray());

        using JsonDocument theirDelete = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "delete_graph_view",
            new { domainId = other.Id, id = viewId });
        AssertWriteError(theirDelete, "not_found");

        using JsonDocument ours = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_graph_view",
            new { domainId = MonkeysphereDomains.DefaultId, id = viewId });
        Assert.Equal(viewId, Structured(ours).Deserialize<RemoteGraphView>(JsonOptions)!.Id);

        using JsonDocument capabilities = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_capabilities");
        RemoteGraphViewLimits limits = Structured(capabilities).Deserialize<RemoteCapabilities>(JsonOptions)!.GraphViewLimits!;
        Assert.Equal(GraphViewService.MaximumNameLength, limits.MaximumNameLength);
        Assert.Equal(RelationshipGraphService.MaximumSelectedRecords, limits.MaximumSelectedRecords);
        Assert.Equal(RelationshipGraphService.MaximumRecordTypes, limits.MaximumRecordTypes);
        Assert.Equal(RelationshipGraphService.MaximumNodes, limits.MaximumNodePositions);
        Assert.Equal(GraphViewService.MaximumCoordinateMagnitude, limits.MaximumCoordinateMagnitude);
        Assert.Equal(GraphViewService.MinimumZoom, limits.MinimumZoom);
        Assert.Equal(GraphViewService.MaximumZoom, limits.MaximumZoom);
        Assert.Equal(RelationshipGraphService.MaximumDepth, limits.MaximumDepth);
        Assert.Equal(RelationshipGraphService.MaximumSearchLength, limits.MaximumSearchLength);
    }
}
