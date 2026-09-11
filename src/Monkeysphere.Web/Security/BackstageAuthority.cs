using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using DnaX.RemoteAccess;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Security;

/// <summary>
/// The MCP grant that lets a remote credential read records held back by backstage policy. It is
/// deliberately a plain grant with no expiry and no activation: a credential either carries it or
/// it does not, and revoking it is the control. The deployment gate still applies on top, so
/// turning backstage off withholds hidden records from this credential as well.
/// </summary>
public static class BackstageScopes
{
    public const string Backstage = "backstage";
}

/// <summary>
/// Establishes who is asking. The browser gives an account; a remote credential gives a grant
/// instead. Anything else is neither, and so sees ordinary records only.
///
/// An interactive Blazor circuit has no HttpContext once it is running, so the account also comes
/// from the circuit's authentication state. That read must be synchronous because every read path
/// asks this question inline; an authentication state that is not yet resolved answers "no
/// account", which withholds records rather than exposing them.
/// </summary>
public sealed class HttpBackstageAccount(IHttpContextAccessor accessor, AuthenticationStateProvider authentication) : IBackstageAccount
{
    public string? AccountId
    {
        get
        {
            ClaimsIdentity? cookie = CurrentUser()?.Identities.FirstOrDefault(identity =>
                identity.IsAuthenticated && identity.AuthenticationType != "DnaXRemoteAccess");
            // One administrator today. When accounts arrive this claim already carries their id.
            return cookie?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        }
    }

    public bool HasBackstageGrant =>
        // Only ever presented on a real request; a circuit never carries a remote credential.
        accessor.HttpContext?.User.Identities.Any(identity =>
            identity.IsAuthenticated
            && identity.AuthenticationType == "DnaXRemoteAccess"
            && identity.HasClaim(DnaXRemoteClaimTypes.Scope, BackstageScopes.Backstage)) == true;

    private ClaimsPrincipal? CurrentUser()
    {
        if (accessor.HttpContext?.User is ClaimsPrincipal user)
        {
            return user;
        }

        try
        {
            Task<AuthenticationState> state = authentication.GetAuthenticationStateAsync();
            return state.IsCompletedSuccessfully ? state.Result.User : null;
        }
        catch (InvalidOperationException)
        {
            // Blazor's provider refuses to answer outside a component's scope, which is where a
            // hosted service or a scope created for background work lives. No account, so no
            // backstage: this has to withhold rather than throw, or ordinary work would fail.
            return null;
        }
    }
}

/// <summary>
/// The one place backstage authority is decided. Both routes require the deployment gate, so
/// turning backstage off is a true kill switch rather than merely hiding the settings.
/// </summary>
public sealed class BackstageVisibility(
    BackstageAvailability availability,
    IBackstageAccount account,
    CachedBackstageSessions sessions) : IBackstageVisibility
{
    public bool IncludeBackstageRecords =>
        availability.Enabled && (account.HasBackstageGrant || sessions.IsActive(account.AccountId));
}
