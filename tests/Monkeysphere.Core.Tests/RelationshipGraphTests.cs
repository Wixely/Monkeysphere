using Monkeysphere.Core;

namespace Monkeysphere.Core.Tests;

public sealed class RelationshipGraphTests
{
    [Fact]
    public async Task GraphServiceEnforcesAcceptedScaleAndDepthBoundaries()
    {
        RelationshipGraphService service = new(new EmptyStore());

        await Assert.ThrowsAsync<DomainValidationException>(() => service.QueryAsync(new(NodeLimit: 501)));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.QueryAsync(new(EdgeLimit: 2_001)));
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
