namespace Monkeysphere.Web.Security;

public static class SecurityHeaders
{
    public static IApplicationBuilder UseMonkeysphereSecurityHeaders(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                IHeaderDictionary headers = context.Response.Headers;
                headers.XContentTypeOptions = "nosniff";
                headers.Append("Referrer-Policy", "no-referrer");
                headers.Append("X-Frame-Options", "DENY");
                headers.Append("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
                headers.Append("Content-Security-Policy", "base-uri 'self'; frame-ancestors 'none'; object-src 'none'");
                return Task.CompletedTask;
            });

            return next();
        });
}
