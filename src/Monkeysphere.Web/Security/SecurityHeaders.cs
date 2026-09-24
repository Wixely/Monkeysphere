using System.Security.Cryptography;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Security;

public static class SecurityHeaders
{
    /// <summary>
    /// Where the per-request script nonce is kept, so the document that renders the import map can
    /// use the same value the header just allowed.
    /// </summary>
    public const string NonceItemKey = "monkeysphere.csp.nonce";

    /// <summary>
    /// The one external host the map may reach, and only when an operator has turned it on. The map
    /// builds its tile address from this rather than naming the host again, because a page may only
    /// reach a host the policy names and two spellings of it would drift into a silently blank map.
    /// </summary>
    public const string TileHost = "https://tile.openstreetmap.org";

    /// <summary>
    /// The directives that never depend on anything. Everything not named here falls back to
    /// default-src, which is why that is 'self' rather than absent: a directive nobody thought of
    /// should fail closed rather than be unrestricted.
    /// </summary>
    private const string BeforeNonce =
        "default-src 'self'; " +
        "base-uri 'self'; " +
        "frame-ancestors 'none'; " +
        "frame-src 'none'; " +
        "object-src 'none'; " +
        "form-action 'self'; " +
        "worker-src 'self'; " +
        "manifest-src 'self'; " +
        "font-src 'self'; " +
        // Every script this application serves is a file of its own; the single inline one is the
        // framework's import map, which carries the nonce below. No 'unsafe-inline' and no
        // 'unsafe-eval': Blazor Server runs its components on the server and needs neither.
        "script-src 'self' 'nonce-";

    /// <summary>The rest of the fixed directives, picking up after the nonce value.</summary>
    private const string AfterNonce =
        "'; " +
        // Stylesheets are files too, with one named exception. Cytoscape inserts a single rule of
        // its own on first use — ".__________cytoscape_container { position: relative; }" — and
        // without it the graph container stays statically positioned and the library says so. It is
        // allowed by hash rather than by opening the directive: the hash covers that exact text and
        // nothing else, and a Cytoscape upgrade already requires a deliberate re-hash of the
        // vendored file, which is when a change here would be noticed.
        "style-src 'self' 'sha256-pgvDUBa4IjFA2yuSJ2cqcyxmNYJMborsd0ORcRv9vw8='; " +
        // Style attributes are a separate directive because several components position or colour
        // one element from data — a menu at the point that was right-clicked, a tag pill in its own
        // colour — and no stylesheet can say that. Allowing attributes is not allowing a <style>
        // block, nor a stylesheet from anywhere.
        "style-src-attr 'unsafe-inline'";

    public static IApplicationBuilder UseMonkeysphereSecurityHeaders(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            // Generated per request and never reused, which is the whole value of a nonce: a script
            // injected into one response cannot carry a value that was only ever valid for another.
            string nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            context.Items[NonceItemKey] = nonce;

            context.Response.OnStarting(async () =>
            {
                IHeaderDictionary headers = context.Response.Headers;
                headers.XContentTypeOptions = "nosniff";
                headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
                headers["X-Frame-Options"] = "DENY";
                headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
                headers["Content-Security-Policy"] =
                    BeforeNonce + nonce + AfterNonce + await MapSourcesAsync(context).ConfigureAwait(false);
            });

            return next();
        });

    /// <summary>
    /// The image and connection sources, which are the only part of the policy that varies. A
    /// deployment with external tiles off never names the tile host at all, so an injected element
    /// cannot reach it either — that is the point of deciding this per response rather than
    /// allowing the host permanently because one page might want it.
    /// </summary>
    private static async Task<string> MapSourcesAsync(HttpContext context)
    {
        const string ownSources = "; img-src 'self' data: blob:; connect-src 'self'";
        const string withTiles =
            "; img-src 'self' data: blob: " + TileHost +
            "; connect-src 'self' " + TileHost;

        // Only a document can ask for a tile. Asking the database on every static file and every
        // framework request would be a query per asset for an answer none of them can use.
        if (context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) != true)
        {
            return ownSources;
        }

        try
        {
            MapConfiguration map = await context.RequestServices
                .GetRequiredService<IMapSettingsService>()
                .GetAsync(context.RequestAborted).ConfigureAwait(false);
            return map.ExternalTilesEnabled ? withTiles : ownSources;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Fails closed. A policy that cannot be determined is the strict one, so a database
            // that is unreachable narrows what the browser may do rather than widening it.
            return ownSources;
        }
    }
}
