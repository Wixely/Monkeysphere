using System.ComponentModel;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Monkeysphere.Core;
using Monkeysphere.Data;

namespace Monkeysphere.Web.Remote;

/// <summary>
/// Whether the deployment is answering, and the few counts an operator checks before deciding
/// anything. Deliberately not a host monitor: no paths, no disk figures, no process detail, because
/// those describe the machine rather than the deployment and a remote caller has no business with
/// them. <c>get_instance_info</c> already carries the versions; this adds what changes at runtime.
/// </summary>
public sealed record RemoteOperationalStatus(
    bool Ready,
    int DomainCount,
    int BackupCount,
    DateTimeOffset? NewestBackupAtUtc,
    bool AuditEnabled,
    RemoteBackupSchedule Schedule);

/// <summary>
/// The deployment's configured backup schedule, read-only. Editing it needs a persistence and
/// configuration-precedence design that does not exist, so this reports what the deployment was
/// configured with and offers no way to change it.
/// </summary>
public sealed record RemoteBackupSchedule(
    string Frequency, string Time, string DayOfWeek, int DayOfMonth, string TimeZone, int RetentionCount,
    DateTimeOffset? NextRunAtUtc);

public sealed record RemoteBackup(Guid Id, string FileName, DateTimeOffset CreatedAtUtc, long ByteLength);

public sealed record RemoteBackupValidation(
    RemoteBackup Backup, int FormatVersion, int ApplicationSchemaVersion, int EntryCount, int OriginalImageCount);

public sealed class MonkeysphereOperationsQueries(
    IBackupService backups,
    IDomainCatalog domains,
    IOptions<BackupScheduleOptions> schedule,
    DnaX.RemoteAccess.IDnaXRemoteAccessAdministration administration,
    TimeProvider timeProvider,
    IHttpContextAccessor accessor)
{
    public async Task<RemoteOperationalStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        // instance.read rather than a backup grant: this says whether the deployment is answering and
        // how many backups exist, which is the question a client asks before anything else. It names
        // no backup and opens none.
        RemoteTagAuthority.Demand(accessor, "instance.read");
        IReadOnlyList<BackupInfo> stored = await backups.ListAsync(cancellationToken).ConfigureAwait(false);
        DnaX.RemoteAccess.DnaXRemoteAdministrationState state =
            await administration.GetStateAsync(cancellationToken).ConfigureAwait(false);
        return new(
            Ready: true,
            DomainCount: domains.Snapshot.Count,
            BackupCount: stored.Count,
            NewestBackupAtUtc: stored.Count == 0 ? null : stored.Max(backup => backup.CreatedAtUtc),
            AuditEnabled: state.AuditEnabled,
            Schedule: Describe(schedule.Value, timeProvider.GetUtcNow()));
    }

    public async Task<IReadOnlyList<RemoteBackup>> ListAsync(CancellationToken cancellationToken)
    {
        RemoteTagAuthority.Demand(accessor, "backups.read");
        return [.. (await backups.ListAsync(cancellationToken).ConfigureAwait(false)).Select(Project)];
    }

    public async Task<RemoteBackupValidation> ValidateAsync(Guid id, CancellationToken cancellationToken)
    {
        RemoteTagAuthority.Demand(accessor, "backups.read");
        BackupValidation validation;
        try
        {
            validation = await backups.ValidateAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException exception)
        {
            // A backup that is not there will never be there, so the caller must not be told to retry.
            // Translated here rather than in the shared error mapping, because a missing file deeper in
            // some other read can genuinely mean a broken deployment, where "temporarily unavailable" is
            // the more honest answer. This is the one place that knows the identifier was simply wrong.
            throw new RecordCommandNotFoundException("Backup was not found.", exception);
        }

        return new(Project(validation.Backup), validation.FormatVersion, validation.ApplicationSchemaVersion,
            validation.EntryCount, validation.OriginalImageCount);
    }

    public async Task<RemoteBackup> CreateAsync(CancellationToken cancellationToken)
    {
        RemoteTagAuthority.Demand(accessor, "backups.write");
        return Project(await backups.CreateAsync(cancellationToken).ConfigureAwait(false));
    }

    private static RemoteBackup Project(BackupInfo backup) =>
        new(backup.Id, backup.FileName, backup.CreatedAtUtc, backup.ByteLength);

    private static RemoteBackupSchedule Describe(BackupScheduleOptions options, DateTimeOffset nowUtc)
    {
        bool off = string.Equals(options.Frequency, "Off", StringComparison.OrdinalIgnoreCase);
        return new(options.Frequency, options.Time.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), options.DayOfWeek.ToString(),
            options.DayOfMonth, options.TimeZone, options.RetentionCount,
            off ? null : BackupScheduleCalculator.Next(nowUtc, options));
    }
}

[McpServerToolType]
public sealed class MonkeysphereOperationsTools
{
    [RemoteToolScopes("instance.read")]
    [McpServerTool(Name = "get_operational_status", ReadOnly = true, UseStructuredContent = true,
        OutputSchemaType = typeof(RemoteOperationalStatus))]
    [Description("Reports whether the deployment is answering and the few runtime counts an operator checks first: how many domains exist, how many backups are stored and when the newest was taken, whether remote-access auditing is on, and the deployment's configured backup schedule with its next run. Requires instance.read. Carries no host paths, disk figures or process detail: those describe the machine rather than the deployment. Versions come from get_instance_info. The schedule is read-only, and no tool changes it.")]
    public static Task<CallToolResult> GetStatusAsync(MonkeysphereOperationsQueries queries, IHttpContextAccessor accessor,
        CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, () => queries.GetStatusAsync(cancellationToken));

    [RemoteToolScopes("backups.read")]
    [McpServerTool(Name = "list_backups", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(RemoteBackup[]))]
    [Description("Lists the stored backup packages, newest first, with their identifier, file name, creation time and size. Requires backups.read. Backups are deployment-wide rather than per-domain, so this takes no domainId and one package covers every domain. Metadata only: reading a package's bytes is a separate grant.")]
    public static Task<CallToolResult> ListAsync(MonkeysphereOperationsQueries queries, IHttpContextAccessor accessor,
        CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, () => queries.ListAsync(cancellationToken));

    [RemoteToolScopes("backups.read")]
    [McpServerTool(Name = "validate_backup", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(RemoteBackupValidation))]
    [Description("Opens a stored backup and reports what it actually contains: its package format version, the application schema it was taken at, how many entries it holds and how many original images. Requires backups.read. This reads the package to answer, so it is the way to tell a restorable backup from a file of the right size. It restores nothing; restore is an offline operator action and is deliberately not available remotely.")]
    public static Task<CallToolResult> ValidateAsync(MonkeysphereOperationsQueries queries, IHttpContextAccessor accessor,
        Guid id, CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, () => queries.ValidateAsync(id, cancellationToken));

    [RemoteToolScopes("backups.write")]
    [McpServerTool(Name = "create_backup", ReadOnly = false, Destructive = false)]
    [Description("Takes a backup of the whole deployment: the domain registry, every domain's database and original media, and remote-access state. Requires backups.write. Returns the created package's identifier, file name, size and creation time, so a successful call is unambiguous. It takes no idempotency key, because a backup is deployment-wide rather than a domain command and a second one is a new timestamped package rather than a repeated write: if a response is lost, call list_backups and compare creation times rather than retrying blind. Large deployments make this slow, since it reads everything. It does not prune; retention applies to scheduled backups.")]
    public static Task<CallToolResult> CreateAsync(MonkeysphereOperationsQueries queries, IHttpContextAccessor accessor,
        CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, () => queries.CreateAsync(cancellationToken));
}
