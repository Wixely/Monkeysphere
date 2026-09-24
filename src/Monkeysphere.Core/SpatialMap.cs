namespace Monkeysphere.Core;

public sealed record SpatialMapQuery(
    double South = -90,
    double West = -180,
    double North = 90,
    double East = 180,
    Guid? RecordTypeId = null,
    Guid? FieldDefinitionId = null,
    int Page = 1,
    int PageSize = SpatialMapService.DefaultPageSize,
    IReadOnlyList<Guid>? FieldDefinitionIds = null);

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
    double? ApproximationRadiusKilometres)
{
    /// <summary>The record's cover image, so a pin names its record the way every other list does.</summary>
    public Guid? ImageId { get; init; }

    /// <summary>The record type's symbol, shown when there is no image.</summary>
    public string? RecordTypeSymbol { get; init; }
}

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
    /// <summary>
    /// How far a caller may page through pins, and how many location fields one query may select
    /// across. Named because a remote caller is told them by get_capabilities, and a bound a client
    /// plans against should not be a literal that exists only inside the check enforcing it.
    /// </summary>
    public const int MaximumPage = 10_000;

    public const int MaximumPageSize = 500;

    public const int DefaultPageSize = 100;

    public const int MaximumLocationFields = 20;

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

        if (query.Page < 1 || query.Page > MaximumPage)
        {
            throw new DomainValidationException($"Map page must be between 1 and {MaximumPage:N0}.");
        }

        if (query.PageSize < 1 || query.PageSize > MaximumPageSize)
        {
            throw new DomainValidationException($"Map page size must be between 1 and {MaximumPageSize:N0}.");
        }

        if (query.FieldDefinitionIds is not null && query.FieldDefinitionIds.Count > MaximumLocationFields)
        {
            throw new DomainValidationException($"Map queries cannot select more than {MaximumLocationFields} location fields.");
        }

        return store.QueryAsync(query, cancellationToken);
    }
}
