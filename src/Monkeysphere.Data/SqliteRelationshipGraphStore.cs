using Dapper;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

public sealed class SqliteRelationshipGraphStore(MonkeysphereConnectionFactory connections) : IRelationshipGraphStore
{
    public async Task<RelationshipGraphResult> QueryAsync(
        RelationshipGraphQuery query,
        CancellationToken cancellationToken = default)
    {
        string? pattern = query.Search is null ? null : "%" + EscapeLike(query.Search) + "%";
        Guid[] selectedIds = query.FocusRecordId is Guid focusRecordId
            ? [focusRecordId]
            : query.SelectedRecordIds?.ToArray() ?? [];
        RelationshipGraphDisplayMode displayMode = query.FocusRecordId.HasValue
            ? RelationshipGraphDisplayMode.Connected
            : query.DisplayMode;
        string[] recordTypeIds = query.RecordTypeIds?.Select(id => id.ToString("D")).ToArray() ?? [];
        string? relationshipTypeId = query.RelationshipTypeId?.ToString("D");
        int nodeTake = query.NodeLimit + 1;
        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<GraphNodeRow> nodeRows = await connection.QueryAsync<GraphNodeRow>(new CommandDefinition("""
            WITH RECURSIVE connected(Id, Distance) AS (
                SELECT record.Id, 0
                FROM Records record
                WHERE (@HasRecordTypeFilter = 0 OR record.RecordTypeId IN @RecordTypeIds)
                  AND ((@Pattern IS NOT NULL
                        AND (record.DisplayName LIKE @Pattern ESCAPE '\' COLLATE NOCASE
                             OR EXISTS (SELECT 1 FROM RecordAliases alias
                                        WHERE alias.RecordId = record.Id
                                          AND alias.Value LIKE @Pattern ESCAPE '\' COLLATE NOCASE)))
                       OR (@Pattern IS NULL AND @DisplayMode = @AllMode)
                       OR (@Pattern IS NULL AND @DisplayMode <> @AllMode
                           AND @HasSelection = 1 AND record.Id IN @SelectedRecordIds))
                UNION
                SELECT CASE WHEN relationship.SourceRecordId = connected.Id
                            THEN relationship.TargetRecordId ELSE relationship.SourceRecordId END,
                       connected.Distance + 1
                FROM connected
                INNER JOIN Relationships relationship
                    ON relationship.SourceRecordId = connected.Id OR relationship.TargetRecordId = connected.Id
                WHERE @DisplayMode = @ConnectedMode
                  AND connected.Distance < @Depth
                  AND (@RelationshipTypeId IS NULL OR relationship.RelationshipTypeId = @RelationshipTypeId)
            ), selected AS (
                SELECT Id, min(Distance) AS Distance
                FROM connected
                GROUP BY Id
            )
            SELECT record.Id AS RecordId,
                   record.RecordTypeId,
                   type.Name AS RecordTypeName,
                   type.Symbol AS RecordTypeSymbol,
                   record.DisplayName,
                   selected.Distance,
                   (SELECT image.Id
                    FROM RecordImages image
                    WHERE image.RecordId = record.Id
                    ORDER BY image.IsCover DESC, image.Ordinal, image.Id
                    LIMIT 1) AS ImageId
            FROM selected
            INNER JOIN Records record ON record.Id = selected.Id
            INNER JOIN RecordTypes type ON type.Id = record.RecordTypeId
            WHERE (@HasRecordTypeFilter = 0 OR record.RecordTypeId IN @RecordTypeIds)
            ORDER BY CASE WHEN @HasSelection = 1 AND record.Id IN @SelectedRecordIds THEN 0 ELSE 1 END,
                     selected.Distance, record.DisplayName COLLATE NOCASE, record.Id
            LIMIT @NodeTake;
            """, new
        {
            Pattern = pattern,
            Depth = query.FocusRecordId.HasValue ? query.Depth : 1,
            DisplayMode = (int)displayMode,
            AllMode = (int)RelationshipGraphDisplayMode.All,
            ConnectedMode = (int)RelationshipGraphDisplayMode.Connected,
            HasSelection = selectedIds.Length > 0 ? 1 : 0,
            SelectedRecordIds = selectedIds.Select(id => id.ToString("D")).ToArray(),
            HasRecordTypeFilter = query.RecordTypeIds is null ? 0 : 1,
            RecordTypeIds = recordTypeIds,
            RelationshipTypeId = relationshipTypeId,
            NodeTake = nodeTake,
        }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        GraphNodeRow[] allNodes = nodeRows.ToArray();
        bool nodesTruncated = allNodes.Length > query.NodeLimit;
        GraphNodeRow[] includedRows = allNodes.Take(query.NodeLimit).ToArray();
        string[] nodeIds = includedRows.Select(row => row.RecordId).ToArray();
        RelationshipGraphEdge[] edges = [];
        bool edgesTruncated = false;
        if (nodeIds.Length > 0)
        {
            IEnumerable<GraphEdgeRow> edgeRows = await connection.QueryAsync<GraphEdgeRow>(new CommandDefinition("""
                SELECT relationship.Id AS RelationshipId,
                       relationship.RelationshipTypeId,
                       type.Name AS Label,
                       type.Directionality,
                       relationship.SourceRecordId,
                       relationship.TargetRecordId,
                       relationship.Note
                FROM Relationships relationship
                INNER JOIN RelationshipTypes type ON type.Id = relationship.RelationshipTypeId
                WHERE relationship.SourceRecordId IN @NodeIds
                  AND relationship.TargetRecordId IN @NodeIds
                  AND (@RelationshipTypeId IS NULL OR relationship.RelationshipTypeId = @RelationshipTypeId)
                ORDER BY type.Name COLLATE NOCASE, relationship.Id
                LIMIT @EdgeTake;
                """, new
            {
                NodeIds = nodeIds,
                RelationshipTypeId = relationshipTypeId,
                EdgeTake = query.EdgeLimit + 1,
            }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            GraphEdgeRow[] allEdges = edgeRows.ToArray();
            edgesTruncated = allEdges.Length > query.EdgeLimit;
            edges = allEdges.Take(query.EdgeLimit).Select(row => new RelationshipGraphEdge(
                Guid.ParseExact(row.RelationshipId, "D"),
                Guid.ParseExact(row.RelationshipTypeId, "D"),
                row.Label,
                (RelationshipDirectionality)row.Directionality,
                Guid.ParseExact(row.SourceRecordId, "D"),
                Guid.ParseExact(row.TargetRecordId, "D"),
                row.Note)).ToArray();
        }

        RelationshipGraphNode[] nodes = includedRows.Select(row => new RelationshipGraphNode(
            Guid.ParseExact(row.RecordId, "D"),
            Guid.ParseExact(row.RecordTypeId, "D"),
            row.RecordTypeName,
            row.DisplayName,
            row.Distance,
            row.ImageId is null ? null : Guid.ParseExact(row.ImageId, "D"),
            row.RecordTypeSymbol)).ToArray();
        return new(nodes, edges, nodesTruncated, edgesTruncated);
    }

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private sealed class GraphNodeRow
    {
        public required string RecordId { get; init; }
        public required string RecordTypeId { get; init; }
        public required string RecordTypeName { get; init; }
        public string? RecordTypeSymbol { get; init; }
        public required string DisplayName { get; init; }
        public int Distance { get; init; }
        public string? ImageId { get; init; }
    }

    private sealed class GraphEdgeRow
    {
        public required string RelationshipId { get; init; }
        public required string RelationshipTypeId { get; init; }
        public required string Label { get; init; }
        public int Directionality { get; init; }
        public required string SourceRecordId { get; init; }
        public required string TargetRecordId { get; init; }
        public string? Note { get; init; }
    }
}
