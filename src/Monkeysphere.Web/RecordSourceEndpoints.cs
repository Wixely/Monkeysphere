using System.Globalization;
using System.Text;
using Monkeysphere.Core;

namespace Monkeysphere.Web;

public static class RecordSourceEndpoints
{
    /// <summary>
    /// Serves one retained source value in full. The panel shows a bounded preview because an
    /// embedded payload can be megabytes; this is how a person gets at the whole thing. It is
    /// treated as sensitive record data: authenticated, private, no-store, and never sniffable.
    /// </summary>
    public static IEndpointRouteBuilder MapRecordSourceValues(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/records/{recordId:guid}/source/{ordinal:int}",
            async (
                Guid recordId,
                int ordinal,
                IRecordSourceService sources,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                if (ordinal < 0)
                {
                    return Results.NotFound();
                }

                RecordSourceSnapshot? snapshot = await sources.GetAsync(recordId, cancellationToken).ConfigureAwait(false);
                RecordSourceValue? value = snapshot?.Values.FirstOrDefault(item => item.Ordinal == ordinal);
                if (value is null)
                {
                    return Results.NotFound();
                }

                // Read the whole value through the service so its bounds and digest stay the single
                // definition of what one retained value is, rather than a second path to the bytes.
                StringBuilder assembled = new(value.ValueLength);
                int? offset = 0;
                while (offset is int position)
                {
                    RecordSourceValueRange range = await sources
                        .ReadValueAsync(recordId, ordinal, position, RecordSourceLimits.MaximumRangeLength, cancellationToken)
                        .ConfigureAwait(false);
                    assembled.Append(range.Content);
                    offset = range.NextOffset;
                }

                context.Response.Headers.CacheControl = "private, no-store";
                context.Response.Headers.XContentTypeOptions = "nosniff";
                // Deliberately text/plain whatever the value holds: an embedded payload must never be
                // served as the type it claims to be, or a retained value becomes a delivery vector.
                // Returned as an attachment for the same reason retained image originals are.
                return Results.File(
                    Encoding.UTF8.GetBytes(assembled.ToString()),
                    "text/plain; charset=utf-8",
                    DownloadFileName(recordId, value));
            })
            .RequireAuthorization();
        return endpoints;
    }

    internal static string DownloadFileName(Guid recordId, RecordSourceValue value) =>
        string.Create(CultureInfo.InvariantCulture, $"{recordId:N}-{value.Ordinal}-{Sanitize(value.Name)}.txt");

    private static string Sanitize(string name)
    {
        char[] cleaned = name.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '-').ToArray();
        return cleaned.Length == 0 ? "value" : new string(cleaned);
    }
}
