using System.ComponentModel;
using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RemoteRecordFilter(Guid FieldDefinitionId, string Operator, string Value)
{
    internal RecordFilter ToCore() => new(FieldDefinitionId, Operator switch
    {
        "equals" => FieldFilterOperator.Equals,
        "contains" => FieldFilterOperator.Contains,
        "greater_than" => FieldFilterOperator.GreaterThan,
        "less_than" => FieldFilterOperator.LessThan,
        "before" => FieldFilterOperator.Before,
        "after" => FieldFilterOperator.After,
        _ => throw new DomainValidationException("Filter operator must be equals, contains, greater_than, less_than, before or after."),
    }, Value);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RemoteRecordSort(Guid? FieldDefinitionId = null, bool Descending = false);

public sealed partial class MonkeysphereRemoteQueries
{
    public async Task<RemotePage<RemoteRecordSummary>> QueryRecordsAsync(string? query, Guid? recordTypeId,
        IReadOnlyList<RemoteRecordFilter>? filters, RemoteRecordSort? sort, int page, int pageSize, Guid? domainId,
        CancellationToken cancellationToken)
    {
        DemandReadScope();
        DiscoveryPagination.Validate(page, pageSize);
        if (filters is not null && (filters.Count > 10 || filters.Any(filter => filter is null)))
            throw new DomainValidationException("Supply at most 10 non-null filters.");
        using IDisposable? domain = UseDomain(domainId);
        if (recordTypeId is Guid typeId && await service.GetRecordTypeAsync(typeId, cancellationToken).ConfigureAwait(false) is null)
            throw new RecordCommandNotFoundException("Record type was not found in this domain.");
        RecordFilter[] coreFilters = (filters ?? []).Select(filter => filter.ToCore()).ToArray();
        HashSet<Guid> referencedFields = coreFilters.Select(filter => filter.FieldDefinitionId).ToHashSet();
        if (sort?.FieldDefinitionId is Guid sortField) referencedFields.Add(sortField);
        if (referencedFields.Count > 0)
        {
            IReadOnlyList<FieldDefinition> fields = await service.ListFieldDefinitionsAsync(cancellationToken).ConfigureAwait(false);
            if (!referencedFields.IsSubsetOf(fields.Select(field => field.Id)))
                throw new RecordCommandNotFoundException("A filter or sort field was not found in this domain.");
        }
        PagedResult<RecordSummary> result = await service.SearchRecordsAsync(new RecordSearch(query, recordTypeId,
            Page: page, PageSize: pageSize, Filters: coreFilters,
            Sort: sort is null ? null : new(sort.FieldDefinitionId, sort.Descending)), cancellationToken).ConfigureAwait(false);
        return new(result.Items.Select(MapSummary).ToArray(), result.Page, result.PageSize, result.TotalCount);
    }
}

[McpServerToolType]
[RemoteToolScopes("records.read")]
public sealed class MonkeysphereRecordQueryTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "query_records", ReadOnly = true)]
    [Description("Searches names, aliases and stored values with up to 10 AND-combined field filters and optional sorting. Requires records.read. Operators: equals, contains, greater_than, less_than, before, after. Numeric comparisons take finite invariant numbers; temporal comparisons take application temporal values. Omit sort for recently updated first; sort {} orders by display name, or supply fieldDefinitionId to sort the first stored field value. Descending defaults false. Pages are 1-10000, pageSize 1-100, query at most 500 characters, filter values at most 2000. Omitted domainId selects Default. Unknown or foreign type/filter/sort IDs fail. Results and totalCount share a database snapshot; pages across separate calls are live, not pinned.")]
    public static async Task<CallToolResult> QueryAsync(MonkeysphereRemoteQueries queries, IHttpContextAccessor accessor, string? query = null,
        Guid? recordTypeId = null, IReadOnlyList<RemoteRecordFilter>? filters = null, RemoteRecordSort? sort = null,
        int page = 1, int pageSize = 25, Guid? domainId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            RemotePage<RemoteRecordSummary> pageResult = await queries.QueryRecordsAsync(query, recordTypeId, filters, sort,
                page, pageSize, domainId, cancellationToken).ConfigureAwait(false);
            JsonElement json = JsonSerializer.SerializeToElement(pageResult, JsonOptions);
            return new() { StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or DomainValidationException or
            RecordCommandNotFoundException or DbException or IOException or OperationCanceledException)
        {
            string code = exception switch
            {
                UnauthorizedAccessException => "permission_denied",
                RecordCommandNotFoundException => "not_found",
                DomainValidationException => "validation_failed",
                _ => "temporarily_unavailable",
            };
            string message = code == "temporarily_unavailable" ? "The query could not complete. Retry the request."
                : exception.Message[..Math.Min(exception.Message.Length, 1024)];
            JsonElement json = JsonSerializer.SerializeToElement(new { error = new RemoteWriteError(code, message, accessor.HttpContext?.TraceIdentifier ?? "") }, JsonOptions);
            return new() { IsError = true, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
        }
    }
}
