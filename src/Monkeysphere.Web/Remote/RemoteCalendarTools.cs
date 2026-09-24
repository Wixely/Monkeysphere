using System.ComponentModel;
using System.Security.Cryptography;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Monkeysphere.Core;

namespace Monkeysphere.Web.Remote;

/// <summary>
/// One day the calendar shows. A repeat says so and says how many years it is from the day the value
/// actually names, because "Ada — Birthday" on its own cannot be turned into an age and a client that
/// guessed would be wrong every leap year.
/// </summary>
public sealed record RemoteCalendarEntry(
    Guid FieldValueId,
    Guid RecordId,
    Guid RecordTypeId,
    string RecordTypeName,
    string RecordDisplayName,
    Guid FieldDefinitionId,
    string FieldName,
    DateOnly Date,
    int YearsSince,
    bool IsRepeat,
    bool RolledFromLeapDay);

/// <summary>
/// A range of the calendar. The entry count and whether the limit was reached travel with it, because
/// a range that stopped at its limit looks exactly like one that had nothing more to give.
/// </summary>
public sealed record RemoteCalendar(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<RemoteCalendarEntry> Entries,
    int AppliedLimit,
    bool LimitReached);

/// <summary>
/// An iCalendar document, paged the same way the contact export is: base64 byte ranges with a digest
/// of the whole, so a client can reassemble it and prove it did so without any single response
/// growing past its bound.
///
/// <c>GeneratedAtUtc</c> is part of the document rather than metadata about it: iCalendar stamps every
/// event with when the file was made, so a document generated a second later is a different document
/// with a different digest. It is echoed here so a client can pass it back for the remaining pages and
/// get the same bytes each time, which a paged export is worth nothing without.
/// </summary>
public sealed record RemoteCalendarExport(
    DateOnly From,
    DateOnly To,
    int EntryCount,
    string MediaType,
    string ContentDigest,
    DateTimeOffset GeneratedAtUtc,
    int Offset,
    int TotalBytes,
    int? NextOffset,
    string ContentBase64);

/// <summary>The bounds a calendar read is held to, so a client plans inside them.</summary>
public sealed record RemoteCalendarLimits(
    int MaximumDaysInRange = CalendarService.MaximumDaysInRange,
    int MaximumEntries = CalendarService.MaximumEntries,
    int DefaultEntries = CalendarService.DefaultEntries,
    int MaximumRecordTypes = RemoteCalendarBounds.MaximumRecordTypes,
    int MaximumChunkBytes = RemoteCalendarBounds.MaximumChunkBytes);

public static class RemoteCalendarBounds
{
    /// <summary>The same list bound the graph filter applies to the same kind of list, so the two agree.</summary>
    public const int MaximumRecordTypes = RelationshipGraphService.MaximumRecordTypes;

    /// <summary>The chunk the contact export already uses, for the same reason: one response, bounded.</summary>
    public const int MaximumChunkBytes = 16_384;
}

/// <summary>
/// Reading the calendar. Everything here takes <c>records.read</c> and nothing else: an entry names a
/// record and one of its dates, which is record content whatever format it arrives in. A separate
/// export grant would be theatre, because the iCalendar document says exactly what query_calendar
/// already returns — unlike the contact export, which exists so a credential can export without
/// holding a general read.
/// </summary>
[McpServerToolType]
[RemoteToolScopes("records.read")]
public sealed class MonkeysphereCalendarTools
{
    [McpServerTool(Name = "query_calendar", ReadOnly = true)]
    [Description("Lists the dated values falling in a range, each with the record it belongs to and the field it came from. Requires records.read and an explicit from and to. A stored date appears on the day it names and again on each repeat its field declares, so a birthday recorded decades ago still appears this year; a repeat says isRepeat true and yearsSince how many whole years have passed. A 29 February moved to a year without one says rolledFromLeapDay. Only day-precision, non-approximate values appear: a date recorded as roughly a decade is not an entry in a calendar. The range covers at most 367 days inclusive, limit is 1-1000 and defaults to 500, and limitReached true means more matched than were returned. Optional recordTypeId, recordTypeIds (at most 100) and fieldDefinitionId narrow it. Omitted domainId selects Default.")]
    public static Task<CallToolResult> QueryAsync(
        ICalendarService calendar,
        ICurrentDomainScope currentDomain,
        IHttpContextAccessor accessor,
        DateOnly from,
        DateOnly to,
        Guid? recordTypeId = null,
        IReadOnlyList<Guid>? recordTypeIds = null,
        Guid? fieldDefinitionId = null,
        int limit = CalendarService.DefaultEntries,
        Guid? domainId = null,
        CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, async () =>
        {
            RemoteTagAuthority.Demand(accessor, "records.read");
            using IDisposable domain = currentDomain.Use(domainId ?? MonkeysphereDomains.DefaultId);
            IReadOnlyList<CalendarEntry> entries = await ReadAsync(
                calendar, from, to, recordTypeId, recordTypeIds, fieldDefinitionId, limit, cancellationToken)
                .ConfigureAwait(false);

            // Said rather than left to be inferred from the count matching the limit, which is the
            // one case where a complete answer and a truncated one look identical.
            return new RemoteCalendar(from, to, [.. entries.Select(Map)], limit, entries.Count >= limit);
        });

    [McpServerTool(Name = "export_calendar", ReadOnly = true)]
    [Description("Exports a range of the calendar as an iCalendar (.ics) document, the same one the browser downloads. Requires records.read and an explicit from and to, and takes the same optional narrowing as query_calendar. The document is returned base64 in byte ranges: offset defaults to 0 and count to 16384, nextOffset is the offset to ask for next or null when the document is complete, and contentDigest is the SHA-256 of the whole document in hex so a client can prove it reassembled what the server sent. Pass generatedAtUtc from the first response back on every later page: iCalendar stamps each event with when the file was made, so without it a second page is a byte-for-byte different document and the digests will not match. A digest that still changes means the underlying records changed; start again at offset 0. The range covers at most 367 days and exports at most 1000 entries. Each event is a whole day with the record name and field name as its summary and the record type as its category; no field value, tag, note or image is included.")]
    public static Task<CallToolResult> ExportAsync(
        ICalendarService calendar,
        TimeProvider timeProvider,
        ICurrentDomainScope currentDomain,
        IHttpContextAccessor accessor,
        DateOnly from,
        DateOnly to,
        Guid? recordTypeId = null,
        IReadOnlyList<Guid>? recordTypeIds = null,
        Guid? fieldDefinitionId = null,
        int offset = 0,
        int count = RemoteCalendarBounds.MaximumChunkBytes,
        DateTimeOffset? generatedAtUtc = null,
        Guid? domainId = null,
        CancellationToken cancellationToken = default) =>
        RemoteReadResults.RunAsync(accessor, async () =>
        {
            RemoteTagAuthority.Demand(accessor, "records.read");
            if (offset < 0)
            {
                throw new DomainValidationException("Supply a non-negative offset.");
            }

            if (count < 1 || count > RemoteCalendarBounds.MaximumChunkBytes)
            {
                throw new DomainValidationException($"Supply count 1-{RemoteCalendarBounds.MaximumChunkBytes}.");
            }

            using IDisposable domain = currentDomain.Use(domainId ?? MonkeysphereDomains.DefaultId);

            // The browser's own export takes the widest limit rather than the calendar's default,
            // because a month someone is downloading should not silently lose its tail.
            IReadOnlyList<CalendarEntry> entries = await ReadAsync(
                calendar, from, to, recordTypeId, recordTypeIds, fieldDefinitionId,
                CalendarService.MaximumEntries, cancellationToken).ConfigureAwait(false);

            // Truncated to the second, because that is the precision iCalendar stamps and a value
            // carrying more would come back differently from the way it went into the document.
            DateTimeOffset stamp = Truncate(generatedAtUtc ?? timeProvider.GetUtcNow());
            byte[] content = ICalendarExport.Create(entries, stamp);

            // The end offset yields an empty final range and anything past it fails, matching
            // read_contact_import_evidence so a client written against one works against the other.
            if (offset > content.Length)
            {
                throw new DomainValidationException("The offset is beyond the exported document. Restart at offset 0.");
            }

            int length = Math.Min(count, content.Length - offset);
            int next = offset + length;
            return new RemoteCalendarExport(
                from, to, entries.Count, "text/calendar; charset=utf-8",
                Convert.ToHexString(SHA256.HashData(content)), stamp, offset, content.Length,
                next < content.Length ? next : null,
                Convert.ToBase64String(content, offset, length));
        });

    /// <summary>
    /// To the second in UTC, which is what an iCalendar timestamp carries. Anything finer would make a
    /// value the caller sends back differ from the one the document was built with, which is the exact
    /// failure this parameter exists to prevent.
    /// </summary>
    private static DateTimeOffset Truncate(DateTimeOffset value) =>
        new(value.UtcDateTime.AddTicks(-(value.UtcDateTime.Ticks % TimeSpan.TicksPerSecond)), TimeSpan.Zero);

    private static async Task<IReadOnlyList<CalendarEntry>> ReadAsync(
        ICalendarService calendar,
        DateOnly from,
        DateOnly to,
        Guid? recordTypeId,
        IReadOnlyList<Guid>? recordTypeIds,
        Guid? fieldDefinitionId,
        int limit,
        CancellationToken cancellationToken)
    {
        // Bounded before the query rather than after, because the list travels to the database as an
        // IN clause and a caller sending thousands of identifiers deserves a refusal, not a slow yes.
        Guid[] types = recordTypeIds?.Distinct().ToArray() ?? [];
        if (types.Length > RemoteCalendarBounds.MaximumRecordTypes)
        {
            throw new DomainValidationException(
                $"Supply at most {RemoteCalendarBounds.MaximumRecordTypes} record types.");
        }

        // Core checks the range width and the limit, so those are not restated here: one rule, in the
        // place the browser is held to it as well.
        return await calendar.QueryAsync(
            new CalendarQuery(from, to, recordTypeId, fieldDefinitionId, limit) { RecordTypeIds = types },
            cancellationToken).ConfigureAwait(false);
    }

    private static RemoteCalendarEntry Map(CalendarEntry entry) => new(
        entry.FieldValueId,
        entry.RecordId,
        entry.RecordTypeId,
        entry.RecordTypeName,
        entry.RecordDisplayName,
        entry.FieldDefinitionId,
        entry.FieldName,
        entry.Date,
        entry.YearsSince,
        entry.IsRepeat,
        entry.RolledFromLeapDay);
}
