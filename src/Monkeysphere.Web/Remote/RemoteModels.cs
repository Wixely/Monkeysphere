using System.ComponentModel;
using System.Security.Claims;
using DnaX.RemoteAccess;
using ModelContextProtocol.Server;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

public sealed record RemoteDomain(Guid Id, string Name, bool IsDefault);

public sealed record RemoteRecordType(
    Guid Id,
    string Name,
    string? Symbol,
    string Lifecycle,
    IReadOnlyList<RemoteFieldDefinition> Fields);

public sealed record RemoteFieldDefinition(
    Guid Id,
    string Name,
    string TypeId,
    bool IsRequired,
    int SortOrder);

public sealed record RemoteRecordSummary(
    Guid Id,
    Guid RecordTypeId,
    string RecordTypeName,
    string DisplayName,
    DateTimeOffset UpdatedAtUtc);

public sealed record RemoteRecord(
    RemoteRecordSummary Record,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<RemoteRecordImage> Images,
    IReadOnlyList<RemoteRecordValue> Values,
    IReadOnlyList<RemoteRelationship> Relationships);

public sealed record RemoteRecordImage(
    Guid Id,
    int Ordinal,
    string OriginalFileName,
    string OriginalContentType,
    long OriginalByteLength,
    int Width,
    int Height,
    DateTimeOffset CreatedAtUtc,
    string? Caption,
    bool IsCover,
    ImageCorrection? Correction);

public sealed record RemoteRelationship(
    Guid Id,
    Guid RelationshipTypeId,
    string Label,
    Guid RelatedRecordId,
    string RelatedDisplayName,
    bool IsOutgoing,
    string? Note,
    DateTimeOffset UpdatedAtUtc);

public sealed record RemoteRecordValue(
    Guid FieldDefinitionId,
    string FieldName,
    string TypeId,
    string? Value,
    IReadOnlyList<string> Tags,
    RemoteLocationValue? Location);

public sealed record RemoteLocationValue(
    string? DisplayContext,
    double? Latitude,
    double? Longitude,
    double? AccuracyMetres,
    double? ApproximationRadiusKilometres);

public sealed record RemotePage<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed class MonkeysphereRemoteQueries(
    IMonkeysphereService service,
    IRelationshipService relationshipService,
    IDomainCatalog domains,
    ICurrentDomainScope currentDomain,
    IHttpContextAccessor httpContextAccessor)
{
    public Task<IReadOnlyList<RemoteDomain>> ListDomainsAsync(CancellationToken cancellationToken = default)
    {
        DemandReadScope();
        IReadOnlyList<RemoteDomain> results = domains.Snapshot
            .Select(domain => new RemoteDomain(domain.Id, domain.Name, domain.IsDefault))
            .ToArray();
        return Task.FromResult(results);
    }

    public async Task<IReadOnlyList<RemoteRecordType>> ListRecordTypesAsync(CancellationToken cancellationToken = default)
        => await ListRecordTypesAsync(null, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<RemoteRecordType>> ListRecordTypesAsync(
        Guid? domainId,
        CancellationToken cancellationToken = default)
    {
        DemandReadScope();
        using IDisposable? domainScope = UseDomain(domainId);
        IReadOnlyList<RecordType> types = await service.ListRecordTypesAsync(cancellationToken).ConfigureAwait(false);
        List<RemoteRecordType> results = [];
        foreach (RecordType type in types.Take(100))
        {
            RecordTypeDetails? details = await service.GetRecordTypeAsync(type.Id, cancellationToken).ConfigureAwait(false);
            if (details is not null)
            {
                results.Add(MapType(details));
            }
        }

        return results;
    }

    public async Task<RemoteRecordType?> GetRecordTypeAsync(Guid id, CancellationToken cancellationToken = default)
        => await GetRecordTypeAsync(id, null, cancellationToken).ConfigureAwait(false);

    public async Task<RemoteRecordType?> GetRecordTypeAsync(
        Guid id,
        Guid? domainId,
        CancellationToken cancellationToken = default)
    {
        DemandReadScope();
        using IDisposable? domainScope = UseDomain(domainId);
        RecordTypeDetails? details = await service.GetRecordTypeAsync(id, cancellationToken).ConfigureAwait(false);
        return details is null ? null : MapType(details);
    }

    public async Task<RemotePage<RemoteRecordSummary>> SearchRecordsAsync(
        string? query = null,
        Guid? recordTypeId = null,
        int page = 1,
        int pageSize = 25,
        CancellationToken cancellationToken = default)
        => await SearchRecordsAsync(query, recordTypeId, page, pageSize, null, cancellationToken).ConfigureAwait(false);

    public async Task<RemotePage<RemoteRecordSummary>> SearchRecordsAsync(
        string? query,
        Guid? recordTypeId,
        int page,
        int pageSize,
        Guid? domainId,
        CancellationToken cancellationToken = default)
    {
        DemandReadScope();
        using IDisposable? domainScope = UseDomain(domainId);
        int boundedPageSize = Math.Clamp(pageSize, 1, 100);
        PagedResult<RecordSummary> result = await service.SearchRecordsAsync(
            new RecordSearch(query, recordTypeId, Page: Math.Max(page, 1), PageSize: boundedPageSize),
            cancellationToken).ConfigureAwait(false);
        return new(result.Items.Select(MapSummary).ToArray(), result.Page, result.PageSize, result.TotalCount);
    }

    public async Task<RemoteRecord?> GetRecordAsync(Guid id, CancellationToken cancellationToken = default)
        => await GetRecordAsync(id, null, cancellationToken).ConfigureAwait(false);

    public async Task<RemoteRecord?> GetRecordAsync(
        Guid id,
        Guid? domainId,
        CancellationToken cancellationToken = default)
    {
        DemandReadScope();
        using IDisposable? domainScope = UseDomain(domainId);
        RecordDetails? record = await service.GetRecordAsync(id, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        IReadOnlyList<RemoteRelationship> relationships = await GetRecordRelationshipsCoreAsync(id, cancellationToken).ConfigureAwait(false);
        return MapRecord(record, relationships);
    }

    public async Task<IReadOnlyList<RemoteRelationship>> GetRecordRelationshipsAsync(
        Guid id,
        CancellationToken cancellationToken = default)
        => await GetRecordRelationshipsAsync(id, null, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<RemoteRelationship>> GetRecordRelationshipsAsync(
        Guid id,
        Guid? domainId,
        CancellationToken cancellationToken = default)
    {
        DemandReadScope();
        using IDisposable? domainScope = UseDomain(domainId);
        return await GetRecordRelationshipsCoreAsync(id, cancellationToken).ConfigureAwait(false);
    }

    private IDisposable? UseDomain(Guid? domainId) => domainId is Guid id ? currentDomain.Use(id) : null;

    private void DemandReadScope()
    {
        ClaimsPrincipal? principal = httpContextAccessor.HttpContext?.User;
        if (principal is null ||
            !principal.Identities.Any(identity => identity.IsAuthenticated) ||
            !principal.HasDnaXRemoteScope("records.read"))
        {
            throw new UnauthorizedAccessException("The records.read scope is required.");
        }
    }

    private static RemoteRecordType MapType(RecordTypeDetails details) =>
        new(
            details.RecordType.Id,
            details.RecordType.Name,
            details.RecordType.Symbol,
            details.RecordType.Lifecycle.ToString().ToLowerInvariant(),
            details.Fields.Select(field => new RemoteFieldDefinition(
                field.Definition.Id,
                field.Definition.Name,
                field.Definition.TypeId,
                field.IsRequired,
                field.SortOrder)).ToArray());

    private static RemoteRecordSummary MapSummary(RecordSummary record) =>
        new(record.Id, record.RecordTypeId, record.RecordTypeName, record.DisplayName, record.UpdatedAtUtc);

    private static RemoteRecord MapRecord(RecordDetails details, IReadOnlyList<RemoteRelationship> relationships) =>
        new(
            MapSummary(details.Record),
            details.Aliases,
            details.Images.Select(image => new RemoteRecordImage(
                image.Id,
                image.Ordinal,
                image.OriginalFileName,
                image.OriginalContentType,
                image.OriginalByteLength,
                image.Width,
                image.Height,
                image.CreatedAtUtc,
                image.Caption,
                image.IsCover,
                image.Correction)).ToArray(),
            details.Values.Select(value => new RemoteRecordValue(
                value.FieldDefinitionId,
                value.FieldName,
                value.TypeId,
                FormatValue(value),
                value.Tags,
                value.Location is null
                    ? null
                    : new RemoteLocationValue(
                        value.Location.DisplayContext,
                        value.Location.Latitude,
                        value.Location.Longitude,
                        value.Location.AccuracyMetres,
                        value.Location.ApproximationRadiusKilometres))).ToArray(),
            relationships);

    private async Task<IReadOnlyList<RemoteRelationship>> GetRecordRelationshipsCoreAsync(
        Guid id,
        CancellationToken cancellationToken) =>
        (await relationshipService.ListForRecordAsync(id, 100, cancellationToken).ConfigureAwait(false))
            .Select(item => new RemoteRelationship(
                item.Id,
                item.RelationshipTypeId,
                item.Label,
                item.RelatedRecordId,
                item.RelatedDisplayName,
                item.IsOutgoing,
                item.Note,
                item.UpdatedAtUtc))
            .ToArray();

    private static string? FormatValue(RecordValue value) => value switch
    {
        { TemporalValue: not null, TemporalPrecision: TemporalPrecision precision } =>
            TemporalValues.Format(value.TemporalValue, precision, value.IsApproximate, value.ApproximationNote),
        { Location: not null } => LocationValues.Format(value.Location),
        _ => value.TextValue ?? value.NumberValue ?? value.DateValue,
    };
}

[McpServerToolType]
public sealed class MonkeysphereRemoteTools
{
    [McpServerTool(Name = "list_domains", UseStructuredContent = true)]
    [Description("Lists the available isolated Monkeysphere domains.")]
    public static Task<IReadOnlyList<RemoteDomain>> ListDomainsAsync(
        MonkeysphereRemoteQueries queries,
        CancellationToken cancellationToken = default) =>
        queries.ListDomainsAsync(cancellationToken);

    [McpServerTool(Name = "list_record_types", UseStructuredContent = true)]
    [Description("Lists Monkeysphere record types and their field definitions.")]
    public static Task<IReadOnlyList<RemoteRecordType>> ListRecordTypesAsync(
        MonkeysphereRemoteQueries queries,
        string? domainId = null,
        CancellationToken cancellationToken = default) =>
        queries.ListRecordTypesAsync(ParseOptionalGuid(domainId, "domainId"), cancellationToken);

    [McpServerTool(Name = "get_record_type", UseStructuredContent = true)]
    [Description("Gets one Monkeysphere record type and its field definitions.")]
    public static Task<RemoteRecordType?> GetRecordTypeAsync(
        MonkeysphereRemoteQueries queries,
        Guid id,
        string? domainId = null,
        CancellationToken cancellationToken = default) =>
        queries.GetRecordTypeAsync(id, ParseOptionalGuid(domainId, "domainId"), cancellationToken);

    [McpServerTool(Name = "search_records", UseStructuredContent = true)]
    [Description("Searches Monkeysphere records with bounded pagination.")]
    public static Task<RemotePage<RemoteRecordSummary>> SearchRecordsAsync(
        MonkeysphereRemoteQueries queries,
        string? query = null,
        string? recordTypeId = null,
        int page = 1,
        int pageSize = 25,
        string? domainId = null,
        CancellationToken cancellationToken = default) =>
        queries.SearchRecordsAsync(
            query,
            ParseOptionalGuid(recordTypeId, "recordTypeId"),
            page,
            pageSize,
            ParseOptionalGuid(domainId, "domainId"),
            cancellationToken);

    private static Guid? ParseOptionalGuid(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Guid.TryParse(value, out Guid parsed)
            ? parsed
            : throw new DomainValidationException($"{parameterName} must be a UUID.");
    }

    [McpServerTool(Name = "get_record", UseStructuredContent = true)]
    [Description("Gets one Monkeysphere record and its values.")]
    public static Task<RemoteRecord?> GetRecordAsync(
        MonkeysphereRemoteQueries queries,
        Guid id,
        string? domainId = null,
        CancellationToken cancellationToken = default) =>
        queries.GetRecordAsync(id, ParseOptionalGuid(domainId, "domainId"), cancellationToken);

    [McpServerTool(Name = "get_record_relationships", UseStructuredContent = true)]
    [Description("Gets the bounded relationships visible from one Monkeysphere record.")]
    public static Task<IReadOnlyList<RemoteRelationship>> GetRecordRelationshipsAsync(
        MonkeysphereRemoteQueries queries,
        Guid id,
        string? domainId = null,
        CancellationToken cancellationToken = default) =>
        queries.GetRecordRelationshipsAsync(id, ParseOptionalGuid(domainId, "domainId"), cancellationToken);
}
