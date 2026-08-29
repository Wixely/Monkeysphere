using Monkeysphere.Core;

namespace Monkeysphere.Web;

public static class VCardEndpoints
{
    public static IEndpointRouteBuilder MapVCardExport(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/vcard/export.vcf",
            async (
                string ids,
                IVCardService vcards,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                Guid[] recordIds;
                try
                {
                    recordIds = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(Guid.Parse)
                        .ToArray();
                }
                catch (FormatException)
                {
                    return Results.BadRequest("Every exported record identifier must be a UUID.");
                }

                byte[] content = await vcards.ExportAsync(recordIds, cancellationToken).ConfigureAwait(false);
                context.Response.Headers.CacheControl = "private, no-store";
                context.Response.Headers.XContentTypeOptions = "nosniff";
                return Results.File(content, "text/vcard; charset=utf-8", "monkeysphere-contacts.vcf");
            })
            .RequireAuthorization();
        return endpoints;
    }
}
