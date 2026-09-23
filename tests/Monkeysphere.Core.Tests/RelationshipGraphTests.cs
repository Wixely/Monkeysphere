using Monkeysphere.Core;

namespace Monkeysphere.Core.Tests;

public sealed class RelationshipGraphTests
{
    [Fact]
    public async Task GraphServiceEnforcesAcceptedScaleAndDepthBoundaries()
    {
        RelationshipGraphService service = new(new EmptyStore());

        // The ceiling, not the default. A deployment may raise the boundary from settings, so what
        // the service refuses is a request past the point where one setting could ask a browser to
        // draw the whole database.
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.QueryAsync(new(NodeLimit: RelationshipGraphService.MaximumNodes + 1)));
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            service.QueryAsync(new(EdgeLimit: RelationshipGraphService.MaximumEdges + 1)));

        // A raised limit between the default and the ceiling is accepted, which is the whole point
        // of the setting being there. The search comes along because the store double below pins
        // it, and this call has to reach the store to prove it was not rejected on the way.
        _ = await service.QueryAsync(new(
            Search: "  Ada  ",
            NodeLimit: RelationshipGraphService.DefaultNodes + 1,
            EdgeLimit: RelationshipGraphService.DefaultEdges + 1));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.QueryAsync(new(Depth: 4)));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.QueryAsync(new(Search: new string('x', 201))));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.QueryAsync(new(
            SelectedRecordIds: Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToArray())));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.QueryAsync(new(
            RecordTypeIds: Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToArray())));
        RelationshipGraphResult result = await service.QueryAsync(new(Search: "  Ada  "));
        Assert.Empty(result.Nodes);
    }

    private sealed class EmptyStore : IRelationshipGraphStore
    {
        public Task<RelationshipGraphResult> QueryAsync(
            RelationshipGraphQuery query,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal("Ada", query.Search);
            return Task.FromResult(new RelationshipGraphResult([], [], false, false));
        }
    }
}
