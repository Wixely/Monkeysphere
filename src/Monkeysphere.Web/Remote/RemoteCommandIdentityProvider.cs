using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DnaX.RemoteAccess;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

public sealed class RemoteCommandIdentityProvider(IHttpContextAccessor accessor)
{
    public RecordCommandIdentity Create(Guid domainId, string requiredScope, string action, Guid idempotencyKey, string requestHash)
    {
        HttpContext? context = accessor.HttpContext;
        ClaimsIdentity? identity = context?.User.Identities.FirstOrDefault(identity =>
            identity.IsAuthenticated && identity.AuthenticationType == "DnaXRemoteAccess");
        if (identity is null || !identity.HasClaim(DnaXRemoteClaimTypes.Scope, requiredScope) ||
            !AuthenticationHeaderValue.TryParse(context!.Request.Headers.Authorization, out AuthenticationHeaderValue? authorization) ||
            !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(authorization.Parameter))
        {
            throw new UnauthorizedAccessException("An authenticated remote credential with the required scope is required.");
        }
        string surface = identity.FindFirst(DnaXRemoteClaimTypes.Surface)?.Value.ToLowerInvariant() ?? string.Empty;
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(authorization.Parameter)));
        RecordCommandIdentity result = new(domainId, surface, fingerprint, action, idempotencyKey, requestHash);
        result.Validate();
        return result;
    }
}
