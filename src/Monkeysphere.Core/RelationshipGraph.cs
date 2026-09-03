namespace Monkeysphere.Core;

public enum RelationshipGraphDisplayMode
{
    All,
    Connected,
    Isolated,
}

public sealed record RelationshipGraphQuery(
    string? Search = null,
    Guid? RelationshipTypeId = null,
    Guid? FocusRecordId = null,
    int Depth = 1,
    int NodeLimit = RelationshipGraphService.MaximumNodes,
    int EdgeLimit = RelationshipGraphService.MaximumEdges,
    RelationshipGraphDisplayMode DisplayMode = RelationshipGraphDisplayMode.All,
    IReadOnlyList<Guid>? SelectedRecordIds = null,
    IReadOnlyList<Guid>? RecordTypeIds = null);

public sealed record RelationshipGraphNode(
    Guid RecordId,
    Guid RecordTypeId,
    string RecordTypeName,
    string DisplayName,
    int Distance,
    Guid? ImageId = null,
    string? RecordTypeSymbol = null);

public sealed record RelationshipGraphEdge(
    Guid RelationshipId,
    Guid RelationshipTypeId,
    string Label,
    RelationshipDirectionality Directionality,
    Guid SourceRecordId,
    Guid TargetRecordId,
    string? Note);

public sealed record RelationshipGraphResult(
    IReadOnlyList<RelationshipGraphNode> Nodes,
    IReadOnlyList<RelationshipGraphEdge> Edges,
    bool NodesTruncated,
    bool EdgesTruncated);

public interface IRelationshipGraphStore
{
    Task<RelationshipGraphResult> QueryAsync(
        RelationshipGraphQuery query,
        CancellationToken cancellationToken = default);
}

public interface IRelationshipGraphService
{
    Task<RelationshipGraphResult> QueryAsync(
        RelationshipGraphQuery query,
        CancellationToken cancellationToken = default);
}

public sealed class RelationshipGraphService(IRelationshipGraphStore store) : IRelationshipGraphService
{
    public const int MaximumNodes = 500;
    public const int MaximumEdges = 2_000;
    public const int MaximumSelectedRecords = 100;
    public const int MaximumRecordTypes = 100;

    public Task<RelationshipGraphResult> QueryAsync(
        RelationshipGraphQuery query,
        CancellationToken cancellationToken = default)
    {
        string? search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        if (search?.Length > 200)
        {
            throw new DomainValidationException("Graph search cannot exceed 200 characters.");
        }

        if (query.Depth is < 0 or > 3)
        {
            throw new DomainValidationException("Graph neighbour depth must be between 0 and 3.");
        }

        if (query.NodeLimit is < 1 or > MaximumNodes)
        {
            throw new DomainValidationException("Graph node limit must be between 1 and 500.");
        }

        if (query.EdgeLimit is < 1 or > MaximumEdges)
        {
            throw new DomainValidationException("Graph edge limit must be between 1 and 2,000.");
        }

        if (!Enum.IsDefined(query.DisplayMode))
        {
            throw new DomainValidationException("Graph display mode is invalid.");
        }

        Guid[] selectedRecordIds = query.SelectedRecordIds?.Distinct().ToArray() ?? [];
        if (selectedRecordIds.Length > MaximumSelectedRecords)
        {
            throw new DomainValidationException($"A graph filter cannot contain more than {MaximumSelectedRecords} records.");
        }

        Guid[]? recordTypeIds = query.RecordTypeIds?.Distinct().ToArray();
        if (recordTypeIds?.Length > MaximumRecordTypes)
        {
            throw new DomainValidationException($"A graph filter cannot contain more than {MaximumRecordTypes} record types.");
        }

        return store.QueryAsync(query with
        {
            Search = search,
            SelectedRecordIds = selectedRecordIds,
            RecordTypeIds = recordTypeIds,
        }, cancellationToken);
    }
}
