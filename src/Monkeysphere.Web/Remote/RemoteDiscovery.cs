using System.ComponentModel;
using System.Reflection;
using System.Security.Claims;
using DnaX.RemoteAccess;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Monkeysphere.Core;
using Monkeysphere.Data;

namespace Monkeysphere.Web.Remote;

public sealed record RemoteInstanceInfo(string Application, string Version, int DatabaseSchemaVersion, string ContractVersion);

public sealed record RemoteToolCapability(string Name, IReadOnlyList<string> AnyOfScopes, bool Allowed);

public sealed record RemoteRequestLimits(long MaximumRequestBodyBytes, int MaximumConcurrentRequests, double RequestTimeoutSeconds);

public sealed record RemoteRecordWriteLimits(int MaximumFields, int MaximumPatchChanges, int MaximumRetainedCommandsPerDomain,
    int MaximumReceiptBytes, int RetryWindowHours, int TombstoneRetentionDays,
    int MaximumBatchRecords, int PreviewLifetimeMinutes, int MaximumRetainedPreviewsPerDomain, int MaximumPreviewBytes,
    int MaximumPendingMediaCleanupPerDomain);

public sealed record RemoteCapabilities(
    string ContractVersion,
    IReadOnlyList<string> GrantedScopes,
    IReadOnlyList<RemoteToolCapability> Tools,
    Guid DefaultDomainId,
    bool SupportsDomainSelection,
    bool SupportsWrites,
    bool SupportsFileTransfer,
    RemoteRequestLimits RequestLimits,
    RemoteRecordWriteLimits RecordWriteLimits);

[McpServerToolType]
[RemoteToolScopes("records.read", "instance.read", "records.write", "records.delete")]
public sealed class MonkeysphereDiscoveryTools
{
    private const string ContractVersion = "1.4";

    [McpServerTool(Name = "get_instance_info", UseStructuredContent = true, ReadOnly = true)]
    [Description("Gets the application version, database schema version, and MCP contract version without deployment secrets or host paths. Requires records.read, instance.read, records.write or records.delete.")]
    public static RemoteInstanceInfo GetInstanceInfo(IHttpContextAccessor accessor)
    {
        _ = DemandDiscoveryScope(accessor);
        string version = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? typeof(Program).Assembly.GetName().Version?.ToString(3)
            ?? "Unknown";
        return new("Monkeysphere", version, MonkeysphereSchema.Manifest.CurrentVersion, ContractVersion);
    }

    [McpServerTool(Name = "get_capabilities", UseStructuredContent = true, ReadOnly = true)]
    [Description("Lists implemented MCP tools, the caller's allowed actions, domain-selection support, and effective request and record-write limits. Does not grant permissions. Requires records.read, instance.read, records.write or records.delete.")]
    public static RemoteCapabilities GetCapabilities(
        IHttpContextAccessor accessor,
        IOptions<DnaXRemoteAccessOptions> options)
    {
        ClaimsPrincipal principal = DemandDiscoveryScope(accessor);
        string[] scopes = principal.FindAll(DnaXRemoteClaimTypes.Scope)
            .Select(claim => claim.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        RemoteToolCapability[] capabilities = new[] { typeof(MonkeysphereRemoteTools), typeof(MonkeysphereDiscoveryTools), typeof(MonkeysphereSchemaTools), typeof(MonkeysphereRecordWriteTools), typeof(MonkeysphereRecordBatchTools), typeof(MonkeysphereRecordDeletionTools) }
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Select(method => (Method: method, Tool: method.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(item => item.Tool is not null)
            .Select(item =>
            {
                IReadOnlyList<string> required = (item.Method.GetCustomAttribute<RemoteToolScopesAttribute>() ??
                    item.Method.DeclaringType!.GetCustomAttribute<RemoteToolScopesAttribute>() ??
                    throw new InvalidOperationException("MCP tools must declare their required scopes.")).AnyOfScopes;
                return new RemoteToolCapability(item.Tool!.Name!, required, required.Any(principal.HasDnaXRemoteScope));
            })
            .OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray();
        DnaXRemoteLimitOptions limits = options.Value.Limits;
        return new(
            ContractVersion, scopes, capabilities, MonkeysphereDomains.DefaultId,
            SupportsDomainSelection: true, SupportsWrites: true, SupportsFileTransfer: false,
            new(limits.MaximumRequestBodyBytes, limits.MaximumConcurrentRequestsPerSurface, limits.RequestTimeout.TotalSeconds),
            new(RecordCommandLimits.MaximumFields, RecordCommandLimits.MaximumPatchChanges,
                RecordCommandLimits.MaximumRetainedCommandsPerDomain, RecordCommandLimits.MaximumReceiptBytes,
                RecordCommandLimits.RetryWindowHours, RecordCommandLimits.TombstoneRetentionDays,
                RecordCommandLimits.MaximumBatchRecords, RecordCommandLimits.PreviewLifetimeMinutes,
                RecordCommandLimits.MaximumRetainedPreviewsPerDomain, RecordCommandLimits.MaximumPreviewBytes,
                RecordCommandLimits.MaximumPendingMediaCleanupPerDomain));
    }

    private static ClaimsPrincipal DemandDiscoveryScope(IHttpContextAccessor accessor)
    {
        ClaimsPrincipal? principal = accessor.HttpContext?.User;
        if (principal is null || !principal.Identities.Any(identity => identity.IsAuthenticated) ||
            (!principal.HasDnaXRemoteScope("records.read") && !principal.HasDnaXRemoteScope("instance.read") && !principal.HasDnaXRemoteScope("records.write") && !principal.HasDnaXRemoteScope("records.delete")))
        {
            throw new UnauthorizedAccessException("The records.read, instance.read, records.write or records.delete scope is required.");
        }

        return principal;
    }
}
