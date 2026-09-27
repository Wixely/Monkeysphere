using System.ComponentModel;
using System.Security.Claims;
using DnaX.RemoteAccess;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

/// <summary>
/// One remote surface as an administrator sees it, minus anything secret. The endpoint path is
/// included because it is not a secret on its own — a caller already had to know it to be here — while
/// the credential appears only as the suffix the page shows, which is enough to tell two
/// credentials apart and not enough to use one.
/// </summary>
public sealed record RemoteSurfaceState(
    string Surface, string Availability, bool IsActive, string RouteMode, string? EndpointPath,
    string? CredentialEnding, IReadOnlyList<string> Scopes, long Version);

public sealed record RemoteAccessState(bool Enabled, bool AuditEnabled, RemoteSurfaceState Api, RemoteSurfaceState Mcp);

public sealed record RemoteActivityEntry(
    DateTimeOffset OccurredAtUtc, string Surface, string Action, string Result, int StatusCode);

public sealed record RemoteActivityPage(IReadOnlyList<RemoteActivityEntry> Items, int Returned, int Limit);

/// <summary>
/// The outcome of an administrative change, with the disclosure that matters: whether the caller has
/// just broken its own connection. Acting on the surface you are calling through is allowed, because
/// refusing it would make a compromised credential impossible to revoke remotely, but it must never be
/// a surprise.
/// </summary>
public sealed record RemoteAdministrationResult(
    string Surface, bool IsActive, string? EndpointPath, string? CredentialEnding,
    IReadOnlyList<string> Scopes, long Version, bool AffectsThisConnection, string Disclosure,
    string? Credential = null);

public sealed class MonkeysphereAdministrationCommands(
    IDnaXRemoteAccessAdministration administration,
    IHttpContextAccessor accessor)
{
    public const int MaximumActivityLimit = 200;

    private const string SelfDisruption =
        "This change applies to the surface this call arrived on, so the connection making it may stop working immediately. " +
        "A rotated credential replaces the one in use, a revoked credential ends it, and a rotated endpoint moves the address. " +
        "Reconnect with the new values, or recover from the browser.";

    private const string NoDisruption = "This change applies to a different surface, so the connection making it is unaffected.";

    public async Task<RemoteAccessState> GetStateAsync(CancellationToken cancellationToken)
    {
        RemoteTagAuthority.Demand(accessor, "admin.read");
        DnaXRemoteAdministrationState state = await administration.GetStateAsync(cancellationToken).ConfigureAwait(false);
        return new(state.Enabled, state.AuditEnabled, Project(state.Api), Project(state.Mcp));
    }

    public async Task<RemoteActivityPage> GetActivityAsync(int limit, CancellationToken cancellationToken)
    {
        RemoteTagAuthority.Demand(accessor, "admin.read");
        if (limit is < 1 or > MaximumActivityLimit)
            throw new DomainValidationException($"Supply limit 1-{MaximumActivityLimit}.");
        DnaXRemoteAdministrationState state = await administration.GetStateAsync(cancellationToken).ConfigureAwait(false);
        if (!state.AuditEnabled)
        {
            // An empty page and "auditing is off" are different facts, and reporting the first for the
            // second would have an operator believe nothing had happened.
            throw new DomainValidationException(
                "Remote-access auditing is disabled for this deployment, so there is no activity to report.");
        }

        IReadOnlyList<DnaXRemoteAuditRecord> activity =
            await administration.GetRecentActivityAsync(limit, cancellationToken).ConfigureAwait(false);
        RemoteActivityEntry[] items = [.. activity.Select(record => new RemoteActivityEntry(
            record.Event.OccurredAtUtc, record.Event.Surface.ToString(), record.Event.Action,
            record.Event.Result.ToString(), record.Event.StatusCode))];
        return new(items, items.Length, limit);
    }

    public Task<RemoteAdministrationResult> SetActivationAsync(string surface, bool active, CancellationToken cancellationToken) =>
        ApplyAsync(surface, async (target, version) =>
            await administration.SetActivationAsync(target, active, allowAnonymous: false, expectedVersion: version,
                cancellationToken: cancellationToken).ConfigureAwait(false), cancellationToken);

    public Task<RemoteAdministrationResult> RotateEndpointAsync(string surface, CancellationToken cancellationToken) =>
        ApplyAsync(surface, async (target, version) =>
            await administration.RotateEndpointAsync(target, version, cancellationToken).ConfigureAwait(false), cancellationToken);

    public Task<RemoteAdministrationResult> RevokeCredentialAsync(string surface, CancellationToken cancellationToken) =>
        ApplyAsync(surface, async (target, version) =>
            await administration.RevokeCredentialAsync(target, version, cancellationToken).ConfigureAwait(false), cancellationToken);

    /// <summary>
    /// Rotates a surface's credential, and refuses to widen it.
    ///
    /// This is the control that keeps remote administration from being a way to grant yourself
    /// anything: the new scope set must be a subset of what the calling credential already holds, so a
    /// rotation here can narrow or preserve but never add. Without it a credential holding nothing but
    /// <c>admin.manage</c> could mint itself one that reads every record and downloads every backup,
    /// which is a privilege-escalation path rather than an administration feature. Widening stays an
    /// operator action at the Remote access page, which is also why the browser path is left alone.
    /// </summary>
    public async Task<RemoteAdministrationResult> RotateCredentialAsync(string surface, IReadOnlyList<string> scopes,
        CancellationToken cancellationToken)
    {
        RemoteTagAuthority.Demand(accessor, "admin.manage");
        if (scopes is null || scopes.Count == 0) throw new DomainValidationException("Select at least one permission.");
        HashSet<string> held = CallerScopes();
        string[] requested = [.. scopes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        string[] widening = [.. requested.Where(scope => !held.Contains(scope))];
        if (widening.Length > 0)
        {
            throw new UnauthorizedAccessException(
                $"A remote rotation cannot grant a permission this credential does not already hold: {string.Join(", ", widening)}. " +
                "Narrow the selection, or widen it from the Remote access page in the browser.");
        }

        return await ApplyAsync(surface, async (target, version) =>
            await administration.RotateCredentialAsync(target, version, requested, cancellationToken: cancellationToken)
                .ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    private async Task<RemoteAdministrationResult> ApplyAsync(string surface,
        Func<DnaXRemoteSurface, long, Task<object?>> change, CancellationToken cancellationToken)
    {
        RemoteTagAuthority.Demand(accessor, "admin.manage");
        DnaXRemoteSurface target = Parse(surface);
        DnaXRemoteAdministrationState before = await administration.GetStateAsync(cancellationToken).ConfigureAwait(false);
        DnaXRemoteEffectiveSurface effective = target == DnaXRemoteSurface.Api ? before.Api : before.Mcp;

        object? outcome = await change(target, effective.Version).ConfigureAwait(false);

        DnaXRemoteAdministrationState after = await administration.GetStateAsync(cancellationToken).ConfigureAwait(false);
        RemoteSurfaceState state = Project(target == DnaXRemoteSurface.Api ? after.Api : after.Mcp);
        bool self = string.Equals(CallerSurface(), target.ToString(), StringComparison.OrdinalIgnoreCase);
        return new(state.Surface, state.IsActive, state.EndpointPath, state.CredentialEnding, state.Scopes,
            state.Version, self, self ? SelfDisruption : NoDisruption,
            (outcome as DnaXGeneratedCredential)?.Secret);
    }

    private HashSet<string> CallerScopes()
    {
        ClaimsIdentity? identity = accessor.HttpContext?.User.Identities
            .FirstOrDefault(candidate => candidate.IsAuthenticated && candidate.AuthenticationType == "DnaXRemoteAccess");
        return identity is null
            ? []
            : [.. identity.FindAll(DnaXRemoteClaimTypes.Scope).Select(claim => claim.Value)];
    }

    private string CallerSurface() => accessor.HttpContext?.User.Identities
        .FirstOrDefault(candidate => candidate.IsAuthenticated && candidate.AuthenticationType == "DnaXRemoteAccess")?
        .FindFirst(DnaXRemoteClaimTypes.Surface)?.Value ?? string.Empty;

    private static DnaXRemoteSurface Parse(string surface) =>
        Enum.TryParse(surface, ignoreCase: true, out DnaXRemoteSurface parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new DomainValidationException("Use a surface of api or mcp.");

    private static RemoteSurfaceState Project(DnaXRemoteEffectiveSurface surface) => new(
        surface.Surface.ToString(), surface.Availability.ToString(), surface.IsActive,
        surface.RouteMode.ToString(), surface.EndpointPath,
        surface.Credential?.Suffix, surface.Credential?.Scopes ?? [], surface.Version);
}

[McpServerToolType]
public sealed class MonkeysphereAdministrationTools
{
    [RemoteToolScopes("admin.read")]
    [McpServerTool(Name = "get_remote_access_state", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(RemoteAccessState))]
    [Description("Reports both remote surfaces as an administrator sees them: whether remote access is enabled at all, whether each surface is available under deployment policy and currently active, its route mode and endpoint path, the permissions its credential carries, and a concurrency version. Requires admin.read. No secret is returned: a credential appears only as the trailing characters of its fingerprint, which distinguishes two credentials without being usable as one. The version is what the change tools take to detect a competing edit.")]
    public static Task<CallToolResult> GetStateAsync(MonkeysphereAdministrationCommands commands, IHttpContextAccessor accessor,
        CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, () => commands.GetStateAsync(cancellationToken));

    [RemoteToolScopes("admin.read")]
    [McpServerTool(Name = "list_remote_activity", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(RemoteActivityPage))]
    [Description("Lists recent redacted remote-access activity, newest first: when, which surface, which action, the result and the status code. Requires admin.read. Limit is 1-200. The records are redacted by DnaX and carry no credential, request body or record content. If auditing is disabled for the deployment this fails with a validation error saying so, rather than returning an empty page, because 'nothing was recorded' and 'nothing happened' are different facts.")]
    public static Task<CallToolResult> GetActivityAsync(MonkeysphereAdministrationCommands commands, IHttpContextAccessor accessor,
        int limit = 50, CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, () => commands.GetActivityAsync(limit, cancellationToken));

    [RemoteToolScopes("admin.manage")]
    [McpServerTool(Name = "set_remote_activation", ReadOnly = false, Destructive = true)]
    [Description("Activates or deactivates a remote surface. Requires admin.manage; surface is api or mcp. Deployment policy wins: a surface the deployment does not permit to be activated at runtime stays refused however this is called. Deactivating the surface this call arrived on stops that connection working, and the result says so in affectsThisConnection and disclosure. Anonymous access is never enabled by this tool. Recovery is always available from the browser.")]
    public static Task<CallToolResult> SetActivationAsync(MonkeysphereAdministrationCommands commands, IHttpContextAccessor accessor,
        string surface, bool active, CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, () => commands.SetActivationAsync(surface, active, cancellationToken));

    [RemoteToolScopes("admin.manage")]
    [McpServerTool(Name = "rotate_remote_credential", ReadOnly = false, Destructive = true)]
    [Description("Replaces a surface's credential and returns the new secret once. Requires admin.manage; surface is api or mcp. The selected permissions must be a subset of what the calling credential already holds: a remote rotation can narrow or preserve a grant but never add one, so administration cannot become a way to grant yourself record reads or backup downloads. Widening is an operator action at the Remote access page. Rotating the surface this call arrived on replaces the credential in use, and the result says so. The secret is returned in the result only and is never written to logs or audit.")]
    public static Task<CallToolResult> RotateCredentialAsync(MonkeysphereAdministrationCommands commands, IHttpContextAccessor accessor,
        string surface, IReadOnlyList<string> scopes, CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, () => commands.RotateCredentialAsync(surface, scopes, cancellationToken));

    [RemoteToolScopes("admin.manage")]
    [McpServerTool(Name = "revoke_remote_credential", ReadOnly = false, Destructive = true)]
    [Description("Revokes a surface's credential, leaving it with none. Requires admin.manage; surface is api or mcp. This is how a credential believed to be compromised is stopped, including the one making the call: revoking your own ends that connection, and the result says so. A surface with no credential answers nothing until an operator mints a new one from the browser.")]
    public static Task<CallToolResult> RevokeCredentialAsync(MonkeysphereAdministrationCommands commands, IHttpContextAccessor accessor,
        string surface, CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, () => commands.RevokeCredentialAsync(surface, cancellationToken));

    [RemoteToolScopes("admin.manage")]
    [McpServerTool(Name = "rotate_remote_endpoint", ReadOnly = false, Destructive = true)]
    [Description("Moves a surface to a newly randomized endpoint path. Requires admin.manage; surface is api or mcp. The credential is unchanged, but the address is not: rotating the endpoint this call arrived on moves it immediately, and the result carries the new path along with the disclosure. Deployment policy that forbids endpoint rotation refuses this however it is called.")]
    public static Task<CallToolResult> RotateEndpointAsync(MonkeysphereAdministrationCommands commands, IHttpContextAccessor accessor,
        string surface, CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, () => commands.RotateEndpointAsync(surface, cancellationToken));
}
