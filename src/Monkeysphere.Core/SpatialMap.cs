namespace Monkeysphere.Core;

public sealed record SpatialMapQuery(
    double South = -90,
    double West = -180,
    double North = 90,
    double East = 180,
    Guid? RecordTypeId = null,
    Guid? FieldDefinitionId = null,
    int Page = 1,
    int PageSize = 100);

public sealed record SpatialMapEntry(
    Guid FieldValueId,
    Guid RecordId,
    Guid RecordTypeId,
    string RecordTypeName,
    string RecordDisplayName,
    Guid FieldDefinitionId,
    string FieldName,
    string? DisplayContext,
    double Latitude,
    double Longitude,
    double? AccuracyMetres,
    double? ApproximationRadiusKilometres);

public interface ISpatialMapStore
{
    Task<PagedResult<SpatialMapEntry>> QueryAsync(
        SpatialMapQuery query,
        CancellationToken cancellationToken = default);
}

public interface ISpatialMapService
{
    Task<PagedResult<SpatialMapEntry>> QueryAsync(
        SpatialMapQuery query,
        CancellationToken cancellationToken = default);
}

public sealed class SpatialMapService(ISpatialMapStore store) : ISpatialMapService
{
    public Task<PagedResult<SpatialMapEntry>> QueryAsync(
        SpatialMapQuery query,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(query.South) || query.South is < -90 or > 90 ||
            !double.IsFinite(query.North) || query.North is < -90 or > 90 ||
            query.South > query.North)
        {
            throw new DomainValidationException("Map latitude bounds must be finite, valid, and ordered south to north.");
        }

        if (!double.IsFinite(query.West) || query.West is < -180 or > 180 ||
            !double.IsFinite(query.East) || query.East is < -180 or > 180)
        {
            throw new DomainValidationException("Map longitude bounds must be finite and between -180 and 180 degrees.");
        }

        if (query.Page is < 1 or > 10_000)
        {
            throw new DomainValidationException("Map page must be between 1 and 10,000.");
        }

        if (query.PageSize is < 1 or > 500)
        {
            throw new DomainValidationException("Map page size must be between 1 and 500.");
        }

        return store.QueryAsync(query, cancellationToken);
    }
}
