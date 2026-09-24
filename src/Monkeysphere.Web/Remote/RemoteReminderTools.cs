using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

/// <summary>
/// One active reminder: the lead time, the calendar value it watches, and the day it falls due. The
/// entry travels with it because a reminder identified only by its own id says nothing about what it
/// is for, and a caller would have to read the whole calendar back to find out.
/// </summary>
public sealed record RemoteReminder(
    Guid Id,
    Guid RecordId,
    Guid FieldDefinitionId,
    int ValueOrdinal,
    int LeadDays,
    DateTimeOffset CreatedAtUtc,
    RemoteCalendarEntry Entry,
    DateOnly DueDate);

/// <summary>What a dismissal reports, so the result names the reminder that is now gone from the list.</summary>
public sealed record RemoteReminderDismissal(Guid Id, bool Dismissed = true);

/// <summary>The bounds a reminder is held to.</summary>
public sealed record RemoteReminderLimits(int MaximumLeadDays = ReminderService.MaximumLeadDays);

/// <summary>
/// Reading and setting reminders.
///
/// Listing takes <c>records.read</c>, because a reminder names a record and one of its dates.
/// Creating and dismissing take <c>records.write</c>, and a reminders-specific grant was considered
/// and rejected: it would never be useful on its own, since the <c>fieldValueId</c> a reminder needs
/// can only come from reading the calendar, and it would confer strictly less than
/// <c>records.write</c> already implies — a credential that may rewrite the date itself is not
/// escalated by being able to set a reminder about it. A permission checkbox that adds no control is
/// worse than no checkbox.
/// </summary>
[McpServerToolType]
[RemoteToolScopes("records.read")]
public sealed class MonkeysphereReminderTools
{
    [McpServerTool(Name = "list_reminders", ReadOnly = true)]
    [Description("Lists the reminders that have not been dismissed, each with the calendar value it watches and the day it falls due. Requires records.read. Omitted domainId selects Default. dueDate is the stored date less the lead time, so a reminder set on a value that repeats every year reads as due rather than counting down to the next repeat — the same thing the calendar page shows, and dismissal is permanent rather than per-occurrence. Returns no field values, tags or notes beyond the entry itself.")]
    public static Task<CallToolResult> ListAsync(
        IReminderService reminders,
        ICurrentDomainScope currentDomain,
        IHttpContextAccessor accessor,
        Guid? domainId = null,
        CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, async () =>
        {
            RemoteTagAuthority.Demand(accessor, "records.read");
            using IDisposable domain = currentDomain.Use(domainId ?? MonkeysphereDomains.DefaultId);
            return (await reminders.ListActiveAsync(cancellationToken).ConfigureAwait(false))
                .Select(Map).ToArray();
        });

    [RemoteToolScopes("records.write")]
    [McpServerTool(Name = "create_reminder", ReadOnly = false, Destructive = false)]
    [Description("Sets a reminder on one dated value. Requires records.write, an explicit domainId and the fieldValueId of a calendar entry, which query_calendar returns. leadDays is 0-3650 and is how many days before the date the reminder falls due. Only a day-precision, non-approximate value is eligible: a date recorded as roughly a decade has no day to count back from. Takes no idempotencyKey and issues no receipt, because the same value and lead time cannot be scheduled twice — a repeat reports validation_failed saying it is already scheduled rather than creating a second reminder.")]
    public static Task<CallToolResult> CreateAsync(
        IReminderService reminders,
        ICurrentDomainScope currentDomain,
        IHttpContextAccessor accessor,
        Guid domainId,
        Guid fieldValueId,
        int leadDays,
        CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, async () =>
        {
            RemoteTagAuthority.Demand(accessor, "records.write");
            using IDisposable domain = currentDomain.Use(domainId);
            Reminder created = await reminders.CreateAsync(fieldValueId, leadDays, cancellationToken).ConfigureAwait(false);

            // Read back through the list so the result carries the same entry and due date a later
            // list_reminders will show, rather than a bare row the caller would have to resolve. It is
            // always there: the insert only succeeds for an eligible value and a new reminder is never
            // already dismissed. If that ever stopped holding, an error is the right answer — a
            // fabricated entry with an empty record on it would be worse than a failure.
            return (await reminders.ListActiveAsync(cancellationToken).ConfigureAwait(false))
                .Where(item => item.Reminder.Id == created.Id).Select(Map).Single();
        });

    [RemoteToolScopes("records.write")]
    [McpServerTool(Name = "dismiss_reminder", ReadOnly = false, Destructive = true)]
    [Description("Dismisses a reminder. Requires records.write, an explicit domainId and id. Destructive in that it cannot be undone and the reminder does not come back on the next repeat of the date; no record, value or tag is touched. Dismissing one that is already dismissed or does not exist reports not_found, so a caller can tell a completed dismissal from a mistaken identifier.")]
    public static Task<CallToolResult> DismissAsync(
        IReminderService reminders,
        ICurrentDomainScope currentDomain,
        IHttpContextAccessor accessor,
        Guid domainId,
        Guid id,
        CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, async () =>
        {
            RemoteTagAuthority.Demand(accessor, "records.write");
            using IDisposable domain = currentDomain.Use(domainId);
            return await reminders.DismissAsync(id, cancellationToken).ConfigureAwait(false)
                ? new RemoteReminderDismissal(id)
                : throw new RecordCommandNotFoundException("Reminder was not found in this domain, or was already dismissed.");
        });

    private static RemoteReminder Map(ReminderItem item) => new(
        item.Reminder.Id,
        item.Reminder.RecordId,
        item.Reminder.FieldDefinitionId,
        item.Reminder.ValueOrdinal,
        item.Reminder.LeadDays,
        item.Reminder.CreatedAtUtc,
        new RemoteCalendarEntry(
            item.Entry.FieldValueId,
            item.Entry.RecordId,
            item.Entry.RecordTypeId,
            item.Entry.RecordTypeName,
            item.Entry.RecordDisplayName,
            item.Entry.FieldDefinitionId,
            item.Entry.FieldName,
            item.Entry.Date,
            item.Entry.YearsSince,
            item.Entry.IsRepeat,
            item.Entry.RolledFromLeapDay),
        item.DueDate);
}
