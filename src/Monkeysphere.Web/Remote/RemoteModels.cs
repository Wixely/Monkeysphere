using System.ComponentModel;
using System.Security.Claims;
using DnaX.RemoteAccess;
using ModelContextProtocol.Server;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

public sealed record RemoteRecordType(
    Guid Id,
    string Name,
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
    DateTimeOffset CreatedAtUtc);

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
    IReadOnlyList<string> Tags);

public sealed record RemotePage<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed class MonkeysphereRemoteQueries(
    IMonkeysphereService service,
    IRelationshipService relationshipService,
    IHttpContextAccessor httpContextAccessor)
{
    public async Task<IReadOnlyList<RemoteRecordType>> ListRecordTypesAsync(CancellationToken cancellationToken = default)
    {
        DemandReadScope();
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
    {
        DemandReadScope();
        RecordTypeDetails? details = await service.GetRecordTypeAsync(id, cancellationToken).ConfigureAwait(false);
        return details is null ? null : MapType(details);
    }

    public async Task<RemotePage<RemoteRecordSummary>> SearchRecordsAsync(
        string? query = null,
        Guid? recordTypeId = null,
        int page = 1,
        int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        DemandReadScope();
        int boundedPageSize = Math.Clamp(pageSize, 1, 100);
        PagedResult<RecordSummary> result = await service.SearchRecordsAsync(
            new RecordSearch(query, recordTypeId, Page: Math.Max(page, 1), PageSize: boundedPageSize),
            cancellationToken).ConfigureAwait(false);
        return new(result.Items.Select(MapSummary).ToArray(), result.Page, result.PageSize, result.TotalCount);
    }

    public async Task<RemoteRecord?> GetRecordAsync(Guid id, CancellationToken cancellationToken = default)
    {
        DemandReadScope();
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
    {
        DemandReadScope();
        return await GetRecordRelationshipsCoreAsync(id, cancellationToken).ConfigureAwait(false);
    }

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
                image.CreatedAtUtc)).ToArray(),
            details.Values.Select(value => new RemoteRecordValue(
                value.FieldDefinitionId,
                value.FieldName,
                value.TypeId,
                FormatValue(value),
                value.Tags)).ToArray(),
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

    private static string? FormatValue(RecordValue value) => value.TemporalValue is not null && value.TemporalPrecision is TemporalPrecision precision
        ? TemporalValues.Format(value.TemporalValue, precision, value.IsApproximate, value.ApproximationNote)
        : value.TextValue ?? value.NumberValue ?? value.DateValue;
}

[McpServerToolType]
public sealed class MonkeysphereRemoteTools
{
    [McpServerTool(Name = "list_record_types", UseStructuredContent = true)]
    [Description("Lists Monkeysphere record types and their field definitions.")]
    public static Task<IReadOnlyList<RemoteRecordType>> ListRecordTypesAsync(
        MonkeysphereRemoteQueries queries,
        CancellationToken cancellationToken = default) =>
        queries.ListRecordTypesAsync(cancellationToken);

    [McpServerTool(Name = "get_record_type", UseStructuredContent = true)]
    [Description("Gets one Monkeysphere record type and its field definitions.")]
    public static Task<RemoteRecordType?> GetRecordTypeAsync(
        MonkeysphereRemoteQueries queries,
        Guid id,
        CancellationToken cancellationToken = default) =>
        queries.GetRecordTypeAsync(id, cancellationToken);

    [McpServerTool(Name = "search_records", UseStructuredContent = true)]
    [Description("Searches Monkeysphere records with bounded pagination.")]
    public static Task<RemotePage<RemoteRecordSummary>> SearchRecordsAsync(
        MonkeysphereRemoteQueries queries,
        string? query = null,
        string? recordTypeId = null,
        int page = 1,
        int pageSize = 25,
        CancellationToken cancellationToken = default) =>
        queries.SearchRecordsAsync(
            query,
            Guid.TryParse(recordTypeId, out Guid parsedTypeId) ? parsedTypeId : null,
            page,
            pageSize,
            cancellationToken);

    [McpServerTool(Name = "get_record", UseStructuredContent = true)]
    [Description("Gets one Monkeysphere record and its values.")]
    public static Task<RemoteRecord?> GetRecordAsync(
        MonkeysphereRemoteQueries queries,
        Guid id,
        CancellationToken cancellationToken = default) =>
        queries.GetRecordAsync(id, cancellationToken);

    [McpServerTool(Name = "get_record_relationships", UseStructuredContent = true)]
    [Description("Gets the bounded relationships visible from one Monkeysphere record.")]
    public static Task<IReadOnlyList<RemoteRelationship>> GetRecordRelationshipsAsync(
        MonkeysphereRemoteQueries queries,
        Guid id,
        CancellationToken cancellationToken = default) =>
        queries.GetRecordRelationshipsAsync(id, cancellationToken);
}
