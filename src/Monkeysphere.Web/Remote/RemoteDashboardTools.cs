using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

/// <summary>
/// What the dashboard counts and how far ahead it looks. Deployment configuration rather than record
/// content, so it carries no revision: there is one row and the last write wins, as with the graph
/// settings.
/// </summary>
public sealed record RemoteDashboardSettings(
    IReadOnlyList<Guid> RecordTypeIds,
    IReadOnlyList<Guid> RecurringFieldDefinitionIds,
    int UpcomingDays,
    int DefaultUpcomingDays,
    int MaximumUpcomingDays,
    int MaximumRecurringFields,
    int MaximumUpcomingItems,
    int MaximumCategories);

/// <summary>
/// One date the dashboard is looking forward to. <c>occursAt</c> is the <em>next</em> occurrence
/// rather than the stored date, which is what makes this different from a calendar entry and from a
/// reminder: the dashboard projects forward, and a client comparing it with today gets a real answer.
/// </summary>
public sealed record RemoteUpcomingDate(
    Guid FieldValueId,
    Guid RecordId,
    Guid RecordTypeId,
    string RecordTypeName,
    string RecordDisplayName,
    Guid FieldDefinitionId,
    string FieldName,
    string StoredValue,
    string Precision,
    DateTimeOffset OccursAt,
    bool HasTime);

/// <summary>
/// The dashboard's projection and its configuration. Reading either takes <c>records.read</c> or
/// <c>structure.write</c> for the settings, the way the graph settings split, and <c>records.read</c>
/// for the dates themselves because an upcoming date names a record.
/// </summary>
[McpServerToolType]
[RemoteToolScopes("records.read")]
public sealed class MonkeysphereDashboardTools
{
    [RemoteToolScopes("records.read", "structure.write")]
    [McpServerTool(Name = "get_dashboard_settings", ReadOnly = true)]
    [Description("Gets one domain's dashboard configuration: the record types it shows as categories, the recurring date fields it looks ahead over, and how many days ahead that is. Also returns the shipped default and the bounds a value must lie within, so a caller can tell a chosen look-ahead from the default without knowing this build's numbers. Requires records.read or structure.write. Omitted domainId selects Default. recordTypeIds is what the dashboard shows rather than only what was saved: a record type created since the configuration was written appears in it, because a type nobody has taken off the dashboard belongs on it. The list is bounded by maximumCategories, with saved categories keeping their places. A deployment that has never saved a configuration gets every active type with people first.")]
    public static Task<CallToolResult> GetSettingsAsync(
        IDashboardService dashboard,
        ICurrentDomainScope currentDomain,
        IHttpContextAccessor accessor,
        Guid? domainId = null,
        CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, async () =>
        {
            RemoteTagAuthority.Demand(accessor, "records.read", "structure.write");
            using IDisposable domain = currentDomain.Use(domainId ?? MonkeysphereDomains.DefaultId);
            return Map(await dashboard.GetConfigurationAsync(cancellationToken).ConfigureAwait(false));
        });

    [RemoteToolScopes("structure.write")]
    [McpServerTool(Name = "set_dashboard_settings", ReadOnly = false, Destructive = false)]
    [Description("Sets one domain's dashboard configuration. Requires structure.write and an explicit domainId. Every value is optional and an omitted one is left as it stands, so changing the look-ahead does not clear the categories. Record types must be active and recurring fields must be active date or temporal fields; a retired or foreign one is refused rather than dropped. At most maximumCategories record types; upcomingDays is 1-366. Sending recordTypeIds records every other active type as taken off the dashboard, which is what makes the choice stick — otherwise the next read would treat an unlisted type as newly created and put it back. Affects what the dashboard shows; no record changes.")]
    public static Task<CallToolResult> SetSettingsAsync(
        IDashboardService dashboard,
        ICurrentDomainScope currentDomain,
        IHttpContextAccessor accessor,
        Guid domainId,
        IReadOnlyList<Guid>? recordTypeIds = null,
        IReadOnlyList<Guid>? recurringFieldDefinitionIds = null,
        int? upcomingDays = null,
        CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, async () =>
        {
            RemoteTagAuthority.Demand(accessor, "structure.write");
            using IDisposable domain = currentDomain.Use(domainId);
            DashboardConfiguration current = await dashboard.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);

            // Merged onto what is stored rather than replacing it, matching set_graph_settings: a
            // caller changing the look-ahead should not have to restate the categories to keep them.
            // This is the opposite choice from the saved views, and deliberately so — a view's lists
            // are the view, while these are independent settings that happen to share a row.
            DashboardConfiguration next = current with
            {
                RecordTypeIds = recordTypeIds ?? current.RecordTypeIds,
                RecurringFieldDefinitionIds = recurringFieldDefinitionIds ?? current.RecurringFieldDefinitionIds,
                UpcomingDays = upcomingDays ?? current.UpcomingDays,
            };
            return Map(await dashboard.SaveConfigurationAsync(next, cancellationToken).ConfigureAwait(false));
        });

    [McpServerTool(Name = "list_upcoming_dates", ReadOnly = true)]
    [Description("Lists the dates coming up within the dashboard's look-ahead, soonest first. Requires records.read. Omitted domainId selects Default. occursAt is the next occurrence rather than the stored date, which is what distinguishes this from query_calendar: a birthday recorded decades ago is reported on the day it next falls, so comparing it with today gives a real answer. hasTime says whether the stored value named a time of day or only a date. Bounded to 100 items; narrow the look-ahead or the categories with set_dashboard_settings to see a different set rather than a longer one.")]
    public static Task<CallToolResult> ListUpcomingAsync(
        IDashboardService dashboard,
        ICurrentDomainScope currentDomain,
        IHttpContextAccessor accessor,
        Guid? domainId = null,
        CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, async () =>
        {
            RemoteTagAuthority.Demand(accessor, "records.read");
            using IDisposable domain = currentDomain.Use(domainId ?? MonkeysphereDomains.DefaultId);
            return (await dashboard.ListUpcomingAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
                .Select(Map).ToArray();
        });

    private static RemoteDashboardSettings Map(DashboardConfiguration configuration) => new(
        configuration.RecordTypeIds,
        configuration.RecurringFieldDefinitionIds,
        configuration.UpcomingDays,
        DashboardService.DefaultUpcomingDays,
        DashboardService.MaximumUpcomingDays,
        DashboardService.MaximumRecurringFields,
        DashboardService.MaximumUpcomingItems,
        DashboardService.MaximumCategories);

    private static RemoteUpcomingDate Map(DashboardUpcomingDate upcoming) => new(
        upcoming.Source.FieldValueId,
        upcoming.Source.RecordId,
        upcoming.Source.RecordTypeId,
        upcoming.Source.RecordTypeName,
        upcoming.Source.RecordDisplayName,
        upcoming.Source.FieldDefinitionId,
        upcoming.Source.FieldName,
        upcoming.Source.Value,
        upcoming.Source.Precision.ToString().ToLowerInvariant(),
        upcoming.OccursAt,
        upcoming.HasTime);
}
