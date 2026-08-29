using Dapper;
using Microsoft.Data.Sqlite;
using Monkeysphere.Core;

namespace Monkeysphere.Data;

public sealed class SqliteSpatialMapStore(MonkeysphereConnectionFactory connections) : ISpatialMapStore
{
    public async Task<PagedResult<SpatialMapEntry>> QueryAsync(
        SpatialMapQuery query,
        CancellationToken cancellationToken = default)
    {
        int offset = checked((query.Page - 1) * query.PageSize);
        object parameters = new
        {
            query.South,
            query.West,
            query.North,
            query.East,
            RecordTypeId = query.RecordTypeId?.ToString("D"),
            FieldDefinitionId = query.FieldDefinitionId?.ToString("D"),
            query.PageSize,
            Offset = offset,
        };

        const string filters = """
            spatial.MaxLatitude >= @South AND spatial.MinLatitude <= @North
            AND ((@West <= @East AND spatial.MaxLongitude >= @West AND spatial.MinLongitude <= @East)
                 OR (@West > @East AND (spatial.MaxLongitude >= @West OR spatial.MinLongitude <= @East)))
            AND (@RecordTypeId IS NULL OR r.RecordTypeId = @RecordTypeId)
            AND (@FieldDefinitionId IS NULL OR fv.FieldDefinitionId = @FieldDefinitionId)
            """;

        string sql = $"""
            SELECT COUNT(*)
            FROM FieldValueLocationSpatial spatial
            INNER JOIN FieldValueLocationSpatialKeys spatialKey ON spatialKey.RowId = spatial.RowId
            INNER JOIN FieldValueLocations fl ON fl.FieldValueId = spatialKey.FieldValueId
            INNER JOIN FieldValues fv ON fv.Id = fl.FieldValueId
            INNER JOIN Records r ON r.Id = fv.RecordId
            WHERE {filters};

            SELECT fv.Id AS FieldValueId,
                   r.Id AS RecordId,
                   r.RecordTypeId,
                   rt.Name AS RecordTypeName,
                   r.DisplayName AS RecordDisplayName,
                   fv.FieldDefinitionId,
                   fd.Name AS FieldName,
                   fl.DisplayContext,
                   fl.Latitude,
                   fl.Longitude,
                   fl.AccuracyMetres,
                   fl.ApproximationRadiusKilometres
            FROM FieldValueLocationSpatial spatial
            INNER JOIN FieldValueLocationSpatialKeys spatialKey ON spatialKey.RowId = spatial.RowId
            INNER JOIN FieldValueLocations fl ON fl.FieldValueId = spatialKey.FieldValueId
            INNER JOIN FieldValues fv ON fv.Id = fl.FieldValueId
            INNER JOIN Records r ON r.Id = fv.RecordId
            INNER JOIN RecordTypes rt ON rt.Id = r.RecordTypeId
            INNER JOIN FieldDefinitions fd ON fd.Id = fv.FieldDefinitionId
            WHERE {filters}
            ORDER BY r.DisplayName COLLATE NOCASE, fd.Name COLLATE NOCASE, r.Id, fv.Ordinal
            LIMIT @PageSize OFFSET @Offset;
            """;

        await using SqliteConnection connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using SqlMapper.GridReader results = await connection.QueryMultipleAsync(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken)).ConfigureAwait(false);
        int totalCount = await results.ReadSingleAsync<int>().ConfigureAwait(false);
        IEnumerable<SpatialMapRow> rows = await results.ReadAsync<SpatialMapRow>().ConfigureAwait(false);

        SpatialMapEntry[] entries = rows.Select(row => new SpatialMapEntry(
            Guid.ParseExact(row.FieldValueId, "D"),
            Guid.ParseExact(row.RecordId, "D"),
            Guid.ParseExact(row.RecordTypeId, "D"),
            row.RecordTypeName,
            row.RecordDisplayName,
            Guid.ParseExact(row.FieldDefinitionId, "D"),
            row.FieldName,
            row.DisplayContext,
            row.Latitude,
            row.Longitude,
            row.AccuracyMetres,
            row.ApproximationRadiusKilometres)).ToArray();

        return new(entries, query.Page, query.PageSize, totalCount);
    }

    private sealed class SpatialMapRow
    {
        public required string FieldValueId { get; init; }
        public required string RecordId { get; init; }
        public required string RecordTypeId { get; init; }
        public required string RecordTypeName { get; init; }
        public required string RecordDisplayName { get; init; }
        public required string FieldDefinitionId { get; init; }
        public required string FieldName { get; init; }
        public string? DisplayContext { get; init; }
        public double Latitude { get; init; }
        public double Longitude { get; init; }
        public double? AccuracyMetres { get; init; }
        public double? ApproximationRadiusKilometres { get; init; }
    }
}
