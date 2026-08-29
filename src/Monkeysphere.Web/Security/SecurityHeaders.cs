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
                headers["Referrer-Policy"] = "no-referrer";
                headers["X-Frame-Options"] = "DENY";
                headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
                headers["Content-Security-Policy"] = "base-uri 'self'; frame-ancestors 'none'; object-src 'none'";
                return Task.CompletedTask;
            });

            return next();
        });
}
