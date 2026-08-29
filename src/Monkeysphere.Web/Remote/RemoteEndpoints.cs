using DnaX.RemoteAccess;

namespace Monkeysphere.Web.Remote;

public static class RemoteEndpoints
{
    public static IEndpointRouteBuilder MapMonkeysphereRemoteApi(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder api = endpoints.MapDnaXRemoteApi();
        api.AddEndpointFilter(async (context, next) =>
            context.HttpContext.User.HasDnaXRemoteScope("records.read")
                ? await next(context).ConfigureAwait(false)
                : Results.StatusCode(StatusCodes.Status403Forbidden));
        api.MapGet("/record-types", async (MonkeysphereRemoteQueries queries, CancellationToken cancellationToken) =>
                Results.Ok(await queries.ListRecordTypesAsync(cancellationToken).ConfigureAwait(false)))
            .WithDnaXRemoteAction("records.list_types");
        api.MapGet("/record-types/{id:guid}", async (Guid id, MonkeysphereRemoteQueries queries, CancellationToken cancellationToken) =>
        {
            RemoteRecordType? result = await queries.GetRecordTypeAsync(id, cancellationToken).ConfigureAwait(false);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).WithDnaXRemoteAction("records.get_type");
        api.MapGet("/records", async (
            string? query,
            Guid? recordTypeId,
            int? page,
            int? pageSize,
            MonkeysphereRemoteQueries queries,
            CancellationToken cancellationToken) =>
                Results.Ok(await queries.SearchRecordsAsync(
                    query,
                    recordTypeId,
                    page ?? 1,
                    pageSize ?? 25,
                    cancellationToken).ConfigureAwait(false)))
            .WithDnaXRemoteAction("records.search");
        api.MapGet("/records/{id:guid}", async (Guid id, MonkeysphereRemoteQueries queries, CancellationToken cancellationToken) =>
        {
            RemoteRecord? result = await queries.GetRecordAsync(id, cancellationToken).ConfigureAwait(false);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).WithDnaXRemoteAction("records.get");
        return endpoints;
    }
}
