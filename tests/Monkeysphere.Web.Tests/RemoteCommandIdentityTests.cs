using System.Security.Claims;
using DnaX.RemoteAccess;
using Microsoft.AspNetCore.Http;
using Monkeysphere.Core;
using Monkeysphere.Web.Remote;

namespace Monkeysphere.Web.Tests;

public sealed class RemoteCommandIdentityTests
{
    [Fact]
    public void CommandOwnershipRequiresRemoteAuthenticationAndBindsTheWholeCredential()
    {
        DefaultHttpContext context = new();
        HttpContextAccessor accessor = new() { HttpContext = context };
        RemoteCommandIdentityProvider provider = new(accessor);
        Claim[] claims = [new(DnaXRemoteClaimTypes.Scope, "records.write"), new(DnaXRemoteClaimTypes.Surface, "Mcp")];
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies"));
        context.Request.Headers.Authorization = "Bearer test-only-first-same-suffix";
        Guid key = Guid.NewGuid();
        string hash = new('A', 64);
        Assert.Throws<UnauthorizedAccessException>(() => provider.Create(MonkeysphereDomains.DefaultId, "records.write", "records.create", key, hash));
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "DnaXRemoteAccess"));
        RecordCommandIdentity first = provider.Create(MonkeysphereDomains.DefaultId, "records.write", "records.create", key, hash);
        context.Request.Headers.Authorization = "Bearer test-only-second-same-suffix";
        RecordCommandIdentity second = provider.Create(MonkeysphereDomains.DefaultId, "records.write", "records.create", key, hash);
        Assert.NotEqual(first.CredentialFingerprint, second.CredentialFingerprint);
        Assert.Equal("mcp", second.Surface);
        Assert.Throws<UnauthorizedAccessException>(() => provider.Create(MonkeysphereDomains.DefaultId, "remote.admin", "records.create", key, hash));
        context.Request.Headers.Authorization = "";
        Assert.Throws<UnauthorizedAccessException>(() => provider.Create(MonkeysphereDomains.DefaultId, "records.write", "records.create", key, hash));
    }
}
