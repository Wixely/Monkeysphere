using Monkeysphere.Core;

namespace Monkeysphere.Web;

public static class CalendarEndpoints
{
    public static IEndpointRouteBuilder MapCalendarExport(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/calendar/export.ics",
            async (
                DateOnly from,
                DateOnly to,
                Guid? recordTypeId,
                Guid? fieldDefinitionId,
                ICalendarService calendar,
                TimeProvider timeProvider,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                IReadOnlyList<CalendarEntry> entries = await calendar.QueryAsync(
                    new(from, to, recordTypeId, fieldDefinitionId, Limit: 1_000),
                    cancellationToken).ConfigureAwait(false);
                context.Response.Headers.CacheControl = "private, no-store";
                context.Response.Headers.XContentTypeOptions = "nosniff";
                return Results.File(
                    ICalendarExport.Create(entries, timeProvider.GetUtcNow()),
                    "text/calendar; charset=utf-8",
                    "monkeysphere-calendar.ics");
            })
            .RequireAuthorization();
        return endpoints;
    }
}
