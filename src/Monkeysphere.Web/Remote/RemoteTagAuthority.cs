using System.Security.Claims;
using DnaX.RemoteAccess;

namespace Monkeysphere.Web.Remote;

/// <summary>
/// Demands a scope before a tag tool runs.
///
/// <see cref="RemoteToolScopesAttribute"/> is read by <c>get_capabilities</c> to report what a tool
/// needs; it does not gate invocation. Every tool therefore enforces its own scope, and a tool that
/// forgets runs for any authenticated credential regardless of what discovery advertises. That is
/// not a reporting bug but an authorization hole, so it is checked first in every tag tool and
/// pinned by a test that calls a destructive tag tool with an unrelated grant.
/// </summary>
internal static class RemoteTagAuthority
{
    internal static void Demand(IHttpContextAccessor accessor, params string[] anyOf)
    {
        ClaimsPrincipal? principal = accessor.HttpContext?.User;
        if (principal is null ||
            !principal.Identities.Any(identity => identity.IsAuthenticated) ||
            !anyOf.Any(principal.HasDnaXRemoteScope))
        {
            throw new UnauthorizedAccessException($"The {string.Join(" or ", anyOf)} scope is required.");
        }
    }
}
