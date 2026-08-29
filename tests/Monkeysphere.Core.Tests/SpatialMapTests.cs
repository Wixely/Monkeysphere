using Monkeysphere.Core;

namespace Monkeysphere.Core.Tests;

public sealed class SpatialMapTests
{
    [Fact]
    public async Task QueryValidationBoundsViewportAndPagination()
    {
        CapturingStore store = new();
        SpatialMapService service = new(store);
        SpatialMapQuery query = new(South: -10, West: 170, North: 10, East: -170, Page: 2, PageSize: 50);

        PagedResult<SpatialMapEntry> result = await service.QueryAsync(query);

        Assert.Same(store.Result, result);
        Assert.Equal(query, store.Query);
        await Assert.ThrowsAsync<DomainValidationException>(() => service.QueryAsync(new(South: 20, North: 10)));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.QueryAsync(new(West: -181)));
        await Assert.ThrowsAsync<DomainValidationException>(() => service.QueryAsync(new(PageSize: 501)));
    }

    private sealed class CapturingStore : ISpatialMapStore
    {
        public PagedResult<SpatialMapEntry> Result { get; } = new([], 1, 100, 0);
        public SpatialMapQuery? Query { get; private set; }

        public Task<PagedResult<SpatialMapEntry>> QueryAsync(
            SpatialMapQuery query,
            CancellationToken cancellationToken = default)
        {
            Query = query;
            return Task.FromResult(Result);
        }
    }
}
