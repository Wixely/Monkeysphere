using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Data.Tests;

/// <summary>
/// Two changes that answer decisions in the graph editing plan. The rendering boundary stops being
/// a constant, so that an operator who wants to see more of their own data can raise it and find
/// out how it behaves on their own hardware rather than be told no. And a relationship can be
/// edited in place, rather than deleted and remade: remaking gives a different relationship with a
/// new ID, and anything already holding the old one silently loses it.
/// </summary>
public sealed class GraphBoundsAndRelationshipEditTests
{
    [Fact]
    public async Task AGraphDeploymentShipsAtTheMeasuredBoundaryAndCanBeRaisedFromIt()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IGraphSettingsService settings = application.Services.GetRequiredService<IGraphSettingsService>();

        GraphConfiguration shipped = await settings.GetAsync();
        Assert.Equal(RelationshipGraphService.DefaultNodes, shipped.NodeLimit);
        Assert.Equal(RelationshipGraphService.DefaultEdges, shipped.EdgeLimit);
        Assert.False(shipped.IsRaised);

        GraphConfiguration raised = await settings.SaveAsync(new(WarnUnsavedChanges: false, NodeLimit: 900, EdgeLimit: 4_000));
        Assert.True(raised.IsRaised);

        // Read back through a fresh scope, because a setting that only lives in the instance that
        // saved it would look right here and be gone on the next request.
        GraphConfiguration reloaded = await application.Services.GetRequiredService<IGraphSettingsService>().GetAsync();
        Assert.Equal(900, reloaded.NodeLimit);
        Assert.Equal(4_000, reloaded.EdgeLimit);
        Assert.False(reloaded.WarnUnsavedChanges);
    }

    [Fact]
    public async Task ALimitOutsideWhatTheGraphWillDrawIsRefusedRatherThanQuietlyAdjusted()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IGraphSettingsService settings = application.Services.GetRequiredService<IGraphSettingsService>();

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            settings.SaveAsync(new(NodeLimit: RelationshipGraphService.MaximumNodes + 1)));
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            settings.SaveAsync(new(EdgeLimit: RelationshipGraphService.MaximumEdges + 1)));
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            settings.SaveAsync(new(NodeLimit: 0)));

        // Nothing was stored by any of those.
        GraphConfiguration unchanged = await settings.GetAsync();
        Assert.Equal(RelationshipGraphService.DefaultNodes, unchanged.NodeLimit);
    }

    [Fact]
    public async Task ARaisedLimitDrawsMoreOfTheGraphThanTheDefaultWould()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IRelationshipService relationships = application.Services.GetRequiredService<IRelationshipService>();
        IRelationshipGraphService graph = application.Services.GetRequiredService<IRelationshipGraphService>();

        RecordType type = await records.CreateRecordTypeAsync("Bounded person");
        RelationshipType connection = await relationships.CreateTypeAsync(new(
            "knows", RelationshipDirectionality.Symmetric));
        RecordDetails focus = await records.CreateRecordAsync(type.Id, "Focus", []);
        for (int index = 0; index < 20; index++)
        {
            RecordDetails neighbour = await records.CreateRecordAsync(type.Id, $"Neighbour {index}", []);
            _ = await relationships.CreateAsync(connection.Id, focus.Record.Id, neighbour.Record.Id);
        }

        RelationshipGraphResult tight = await graph.QueryAsync(new(FocusRecordId: focus.Record.Id, NodeLimit: 10));
        Assert.Equal(10, tight.Nodes.Count);
        Assert.True(tight.NodesTruncated);

        RelationshipGraphResult roomier = await graph.QueryAsync(new(FocusRecordId: focus.Record.Id, NodeLimit: 21));
        Assert.Equal(21, roomier.Nodes.Count);
        Assert.False(roomier.NodesTruncated);
    }

    [Fact]
    public async Task EditingARelationshipKeepsItsIdentityWhileChangingItsTypeAndNote()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IRelationshipService relationships = application.Services.GetRequiredService<IRelationshipService>();

        RecordType type = await records.CreateRecordTypeAsync("Edited person");
        RelationshipType colleague = await relationships.CreateTypeAsync(new(
            "colleague of", RelationshipDirectionality.Symmetric));
        RelationshipType mentor = await relationships.CreateTypeAsync(new(
            "mentors", RelationshipDirectionality.Directional, "is mentored by"));
        RecordDetails ada = await records.CreateRecordAsync(type.Id, "Ada", []);
        RecordDetails grace = await records.CreateRecordAsync(type.Id, "Grace", []);

        RelationshipView created = await relationships.CreateAsync(
            colleague.Id, ada.Record.Id, grace.Record.Id, "Met at work");

        RelationshipView edited = await relationships.UpdateAsync(
            created.Id, mentor.Id, "Took her on", ada.Record.Id, created.Revision);

        // The identity is the point of editing rather than remaking: anything holding this ID
        // still refers to the same relationship afterwards.
        Assert.Equal(created.Id, edited.Id);
        Assert.Equal(mentor.Id, edited.RelationshipTypeId);
        Assert.Equal("Took her on", edited.Note);
        Assert.NotEqual(created.Revision, edited.Revision);

        // And the change is what both ends now read, not just what the call returned.
        RelationshipView fromAda = Assert.Single(await relationships.ListForRecordAsync(ada.Record.Id));
        Assert.Equal("mentors", fromAda.Label);
        RelationshipView fromGrace = Assert.Single(await relationships.ListForRecordAsync(grace.Record.Id));
        Assert.Equal("is mentored by", fromGrace.Label);
    }

    [Fact]
    public async Task AnEditAgainstAStaleRevisionIsRefusedRatherThanOverwriting()
    {
        await using TestApplication application = await TestApplication.CreateAsync();
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IRelationshipService relationships = application.Services.GetRequiredService<IRelationshipService>();

        RecordType type = await records.CreateRecordTypeAsync("Contended person");
        RelationshipType knows = await relationships.CreateTypeAsync(new(
            "knows", RelationshipDirectionality.Symmetric));
        RecordDetails ada = await records.CreateRecordAsync(type.Id, "Ada", []);
        RecordDetails grace = await records.CreateRecordAsync(type.Id, "Grace", []);
        RelationshipView created = await relationships.CreateAsync(knows.Id, ada.Record.Id, grace.Record.Id);

        RelationshipView first = await relationships.UpdateAsync(
            created.Id, knows.Id, "First edit", ada.Record.Id, created.Revision);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => relationships.UpdateAsync(
            created.Id, knows.Id, "Second edit against what was read before", ada.Record.Id, created.Revision));

        // The losing edit changed nothing.
        RelationshipView current = Assert.Single(await relationships.ListForRecordAsync(ada.Record.Id));
        Assert.Equal("First edit", current.Note);
        Assert.Equal(first.Revision, current.Revision);
    }
}
