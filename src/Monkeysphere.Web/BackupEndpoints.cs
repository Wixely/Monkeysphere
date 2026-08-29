using Monkeysphere.Core;

namespace Monkeysphere.Web;

public static class BackupEndpoints
{
    public static IEndpointRouteBuilder MapBackupDownloads(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/backups/{id:guid}/download", async (
            Guid id,
            IBackupService backups,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            Stream? content = await backups.OpenAsync(id, cancellationToken).ConfigureAwait(false);
            if (content is null)
            {
                return Results.NotFound();
            }

            BackupInfo? backup = (await backups.ListAsync(cancellationToken).ConfigureAwait(false))
                .SingleOrDefault(item => item.Id == id);
            if (backup is null)
            {
                await content.DisposeAsync().ConfigureAwait(false);
                return Results.NotFound();
            }

            context.Response.Headers.CacheControl = "private, no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(content, "application/vnd.monkeysphere.backup+zip", backup.FileName);
        });
        return endpoints;
    }
}
