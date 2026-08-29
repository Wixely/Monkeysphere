using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Monkeysphere.Web.Security;

public sealed class AbsoluteSessionCookieEvents(TimeProvider timeProvider) : CookieAuthenticationEvents
{
    private static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromHours(12);

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        string? startedClaim = context.Principal?.FindFirst("session_started")?.Value;
        if (!long.TryParse(startedClaim, NumberStyles.None, CultureInfo.InvariantCulture, out long startedSeconds) ||
            timeProvider.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(startedSeconds) > AbsoluteLifetime)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync().ConfigureAwait(false);
        }
    }
}
