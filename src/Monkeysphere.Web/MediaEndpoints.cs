using Monkeysphere.Core;

namespace Monkeysphere.Web;

public static class MediaEndpoints
{
    public static IEndpointRouteBuilder MapRecordImages(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/records/{recordId:guid}/images/{imageId:guid}/{variant}",
            async (
                Guid recordId,
                Guid imageId,
                string variant,
                IRecordImageService images,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                RecordImageVariant? requestedVariant = variant switch
                {
                    "preview" => RecordImageVariant.Preview,
                    "thumbnail" => RecordImageVariant.Thumbnail,
                    "original" => RecordImageVariant.Original,
                    _ => null,
                };
                if (requestedVariant is null)
                {
                    return Results.NotFound();
                }

                RecordImageFile? image = await images.OpenAsync(
                    recordId,
                    imageId,
                    requestedVariant.Value,
                    cancellationToken).ConfigureAwait(false);
                if (image is null)
                {
                    return Results.NotFound();
                }

                context.Response.Headers.CacheControl = "private, no-store";
                context.Response.Headers.XContentTypeOptions = "nosniff";
                return Results.Stream(
                    image.Content,
                    image.ContentType,
                    image.DownloadFileName,
                    enableRangeProcessing: false);
            })
            .RequireAuthorization();
        return endpoints;
    }
}
