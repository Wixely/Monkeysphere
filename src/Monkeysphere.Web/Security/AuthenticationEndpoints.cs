using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Monkeysphere.Web.Security;

public static class AuthenticationEndpoints
{
    public static IEndpointRouteBuilder MapAdministratorAuthentication(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/auth/login", LoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting("login");
        endpoints.MapPost("/auth/logout", LogoutAsync)
            .RequireAuthorization();
        return endpoints;
    }

    private static async Task LoginAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        AdministratorCredential credential,
        TimeProvider timeProvider)
    {
        if (!await ValidateAntiforgeryAsync(context, antiforgery).ConfigureAwait(false))
        {
            return;
        }

        IFormCollection form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
        string username = form["username"].ToString();
        string password = form["password"].ToString();
        string returnUrl = LocalReturnUrl(form["returnUrl"].ToString());

        if (!credential.Verify(username, password))
        {
            context.Response.Redirect($"/login?error=1&returnUrl={Uri.EscapeDataString(returnUrl)}");
            return;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        ClaimsIdentity identity = new(
        [
            new Claim(ClaimTypes.NameIdentifier, "administrator"),
            new Claim(ClaimTypes.Name, credential.Username),
            new Claim("session_started", now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                IsPersistent = false,
                IssuedUtc = now,
                ExpiresUtc = now.AddMinutes(30),
                AllowRefresh = true,
            }).ConfigureAwait(false);
        context.Response.Redirect(returnUrl);
    }

    private static async Task LogoutAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (!await ValidateAntiforgeryAsync(context, antiforgery).ConfigureAwait(false))
        {
            return;
        }

        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
        context.Response.Redirect("/login");
    }

    private static async Task<bool> ValidateAntiforgeryAsync(HttpContext context, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return false;
        }
    }

    private static string LocalReturnUrl(string candidate) =>
        !string.IsNullOrWhiteSpace(candidate) &&
        candidate[0] == '/' &&
        (candidate.Length == 1 || candidate[1] != '/') &&
        !candidate.Contains('\\', StringComparison.Ordinal)
            ? candidate
            : "/";
}
