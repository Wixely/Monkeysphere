using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;
using Monkeysphere.Web.Remote;

namespace Monkeysphere.Web.Tests;

/// <summary>
/// The calendar's whole point is that a date recorded decades ago still comes round, and its whole
/// discipline is that a date nobody is sure of does not. Both are properties of the application
/// rather than of the page, so these pin that they survive the trip through MCP — along with the
/// export reassembling byte for byte, which is the only way a caller can trust a paged document.
/// </summary>
public sealed partial class RemoteDiscoveryTests
{
    private static readonly DateOnly Birth = new(1990, 6, 15);

    [Fact]
    public async Task McpReadsTheCalendarWithItsRepeatsAndWithoutTheDatesNobodyIsSureOf()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (typeId, birthdayId) = await InstallPersonAsync(factory);
        using IServiceScope scope = factory.Services.CreateScope();
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        RecordDetails ada = await records.CreateRecordAsync(typeId, "Ada", [Day(birthdayId, Birth)]);

        // Recorded as "some time in 1815", which is a fact about a person and not an entry in a
        // calendar: there is no day to put it on, and inventing one would be a lie with a date on it.
        _ = await records.CreateRecordAsync(typeId, "Vague", [new(birthdayId,
            Temporal: new TemporalValueInput("1815", TemporalPrecision.Year, false, null))]);

        // And a day somebody has guessed at, which has a day but not a reliable one.
        _ = await records.CreateRecordAsync(typeId, "Roughly", [new(birthdayId,
            Temporal: new TemporalValueInput("1990-06-15", TemporalPrecision.Day, true, "about then"))]);

        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read"]);
        Guid domainId = MonkeysphereDomains.DefaultId;
        int year = DateTime.UtcNow.Year;

        using JsonDocument juneResult = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "query_calendar",
            new { domainId, from = $"{year}-06-01", to = $"{year}-06-30" });
        RemoteCalendar june = Structured(juneResult).Deserialize<RemoteCalendar>(JsonOptions)!;

        // One entry, not three. The vague and the approximate are absent, and the birthday recorded in
        // 1990 is here as a repeat that says how many years have passed — a client cannot work that
        // out from the date alone and would be wrong every leap year if it tried.
        RemoteCalendarEntry entry = Assert.Single(june.Entries);
        Assert.Equal(ada.Record.Id, entry.RecordId);
        Assert.Equal(new DateOnly(year, 6, 15), entry.Date);
        Assert.True(entry.IsRepeat);
        Assert.Equal(year - 1990, entry.YearsSince);
        Assert.False(entry.RolledFromLeapDay);
        Assert.Equal("Ada", entry.RecordDisplayName);
        Assert.Equal("Birthday", entry.FieldName);
        Assert.Equal(CalendarService.DefaultEntries, june.AppliedLimit);
        Assert.False(june.LimitReached);

        // Browsing back to the year it happened shows the day itself rather than a repeat of it.
        using JsonDocument thenResult = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "query_calendar",
            new { domainId, from = "1990-06-01", to = "1990-06-30" });
        RemoteCalendarEntry then = Assert.Single(Structured(thenResult).Deserialize<RemoteCalendar>(JsonOptions)!.Entries);
        Assert.False(then.IsRepeat);
        Assert.Equal(0, then.YearsSince);

        // Reaching the limit is said rather than left to be inferred from a count, which is the one
        // case where a complete answer and a truncated one look identical.
        using JsonDocument cappedResult = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "query_calendar",
            new { domainId, from = $"{year}-01-01", to = $"{year}-12-31", limit = 1 });
        RemoteCalendar capped = Structured(cappedResult).Deserialize<RemoteCalendar>(JsonOptions)!;
        Assert.Single(capped.Entries);
        Assert.True(capped.LimitReached);

        // Narrowing by field is the same narrowing the page offers, and by a field with no values
        // yields an empty calendar rather than an error.
        FieldDefinition unrelated = await records.CreateAndAttachFieldAsync(typeId, new("Joined", FieldTypes.ExactDate, false));
        using JsonDocument narrowed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "query_calendar",
            new { domainId, from = $"{year}-06-01", to = $"{year}-06-30", fieldDefinitionId = unrelated.Id });
        Assert.Empty(Structured(narrowed).Deserialize<RemoteCalendar>(JsonOptions)!.Entries);
    }

    [Fact]
    public async Task TheExportedCalendarReassemblesByteForByteAndSaysWhatItContains()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (typeId, birthdayId) = await InstallPersonAsync(factory);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            // A name carrying a comma and a semicolon, because those are the characters iCalendar
            // requires escaping and a client reassembling the document has to get them back intact.
            _ = await scope.ServiceProvider.GetRequiredService<IMonkeysphereService>()
                .CreateRecordAsync(typeId, "Lovelace, Ada; Countess", [Day(birthdayId, Birth)]);
        }

        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read"]);
        Guid domainId = MonkeysphereDomains.DefaultId;
        int year = DateTime.UtcNow.Year;

        using JsonDocument firstResult = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "export_calendar",
            new { domainId, from = $"{year}-06-01", to = $"{year}-06-30", count = 64 });
        RemoteCalendarExport first = Structured(firstResult).Deserialize<RemoteCalendarExport>(JsonOptions)!;
        Assert.Equal(1, first.EntryCount);
        Assert.Equal("text/calendar; charset=utf-8", first.MediaType);
        Assert.NotNull(first.NextOffset);

        // Walked to the end the way a client must, one bounded range at a time, passing back the
        // generation stamp. iCalendar puts the file's creation time into every event, so without that
        // the second page would be a byte-for-byte different document from the first and no amount of
        // careful reassembly would produce either one.
        List<byte> assembled = [.. Convert.FromBase64String(first.ContentBase64)];
        int? next = first.NextOffset;
        while (next is int offset)
        {
            using JsonDocument pageResult = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "export_calendar",
                new { domainId, from = $"{year}-06-01", to = $"{year}-06-30", offset, count = 64, generatedAtUtc = first.GeneratedAtUtc });
            RemoteCalendarExport page = Structured(pageResult).Deserialize<RemoteCalendarExport>(JsonOptions)!;
            Assert.Equal(first.TotalBytes, page.TotalBytes);
            Assert.Equal(first.ContentDigest, page.ContentDigest);
            Assert.Equal(first.GeneratedAtUtc, page.GeneratedAtUtc);
            assembled.AddRange(Convert.FromBase64String(page.ContentBase64));
            next = page.NextOffset;
        }

        // The digest is of the whole document, so this proves the paging lost and duplicated nothing.
        Assert.Equal(first.TotalBytes, assembled.Count);
        Assert.Equal(first.ContentDigest, Convert.ToHexString(SHA256.HashData([.. assembled])));

        string document = Encoding.UTF8.GetString([.. assembled]);
        Assert.StartsWith("BEGIN:VCALENDAR\r\n", document, StringComparison.Ordinal);
        Assert.Contains("END:VCALENDAR", document, StringComparison.Ordinal);
        Assert.Contains($"DTSTART;VALUE=DATE:{year}0615", document, StringComparison.Ordinal);

        // Escaped as iCalendar demands, which is the detail a hand-rolled reassembly would lose.
        Assert.Contains("Lovelace\\, Ada\\; Countess", document, StringComparison.Ordinal);

        // The export carries the day and who it belongs to, and nothing else about them: no field
        // value, no tag, no note. A calendar handed to another application should disclose the
        // occasion rather than the record.
        Assert.DoesNotContain("about then", document, StringComparison.Ordinal);

        // The stamp is truncated to the second the document carries, so a client passing back what it
        // was given gets that same value rather than one that quietly fails to reproduce the bytes.
        Assert.Equal(first.GeneratedAtUtc, first.GeneratedAtUtc.UtcDateTime.AddTicks(
            -(first.GeneratedAtUtc.UtcDateTime.Ticks % TimeSpan.TicksPerSecond)));

        // A stamp of somebody else's choosing makes a different document, which is the honest answer:
        // the timestamp is part of the file, not a label on the response.
        using JsonDocument restamped = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "export_calendar",
            new { domainId, from = $"{year}-06-01", to = $"{year}-06-30", count = 64, generatedAtUtc = first.GeneratedAtUtc.AddMinutes(1) });
        Assert.NotEqual(first.ContentDigest, Structured(restamped).Deserialize<RemoteCalendarExport>(JsonOptions)!.ContentDigest);

        // The end offset is an empty final range rather than an error, matching the contact export so
        // a client written against one works against the other. Past it fails.
        using JsonDocument atEnd = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "export_calendar",
            new { domainId, from = $"{year}-06-01", to = $"{year}-06-30", offset = first.TotalBytes, generatedAtUtc = first.GeneratedAtUtc });
        RemoteCalendarExport empty = Structured(atEnd).Deserialize<RemoteCalendarExport>(JsonOptions)!;
        Assert.Equal(string.Empty, empty.ContentBase64);
        Assert.Null(empty.NextOffset);
        using JsonDocument beyond = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "export_calendar",
            new { domainId, from = $"{year}-06-01", to = $"{year}-06-30", offset = first.TotalBytes + 1 });
        AssertWriteError(beyond, "validation_failed");
    }

    [Fact]
    public async Task McpRefusesCalendarRangesAndPagesOutsideTheirBounds()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        _ = await InstallPersonAsync(factory);
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read"]);
        Guid domainId = MonkeysphereDomains.DefaultId;

        object[] refused =
        [
            // A range that runs backwards is a mistake rather than an empty calendar.
            new { domainId, from = "2026-06-30", to = "2026-06-01" },
            // 368 days inclusive. 367 is allowed, so that "this date next year" is one call.
            new { domainId, from = "2026-01-01", to = "2027-01-04" },
            new { domainId, from = "2026-06-01", to = "2026-06-30", limit = 0 },
            new { domainId, from = "2026-06-01", to = "2026-06-30", limit = CalendarService.MaximumEntries + 1 },
            new { domainId, from = "2026-06-01", to = "2026-06-30",
                recordTypeIds = Enumerable.Range(0, RemoteCalendarBounds.MaximumRecordTypes + 1).Select(_ => Guid.CreateVersion7()).ToArray() },
        ];
        foreach (object request in refused)
        {
            using JsonDocument response = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "query_calendar", request);
            AssertWriteError(response, "validation_failed");
        }

        // Exactly 367 days inclusive is the widest accepted range, which is the boundary itself
        // rather than a value comfortably inside it.
        using JsonDocument widest = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "query_calendar",
            new { domainId, from = "2026-01-01", to = "2027-01-02" });
        Assert.Empty(Structured(widest).Deserialize<RemoteCalendar>(JsonOptions)!.Entries);

        object[] badExports =
        [
            new { domainId, from = "2026-06-01", to = "2026-06-30", offset = -1 },
            new { domainId, from = "2026-06-01", to = "2026-06-30", count = 0 },
            new { domainId, from = "2026-06-01", to = "2026-06-30", count = RemoteCalendarBounds.MaximumChunkBytes + 1 },
            new { domainId, from = "2026-06-30", to = "2026-06-01" },
        ];
        foreach (object request in badExports)
        {
            using JsonDocument response = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "export_calendar", request);
            AssertWriteError(response, "validation_failed");
        }
    }

    [Theory]
    [InlineData("records.read", true)]
    [InlineData("views.manage", false)]
    [InlineData("structure.write", false)]
    public async Task ReadingTheCalendarTakesTheReadGrantWhateverFormatItArrivesIn(string grant, bool allowed)
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        _ = await InstallPersonAsync(factory);
        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, [grant]);
        Guid domainId = MonkeysphereDomains.DefaultId;

        using JsonDocument capabilities = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_capabilities");
        RemoteCapabilities granted = Structured(capabilities).Deserialize<RemoteCapabilities>(JsonOptions)!;
        Assert.Equal(allowed, granted.Tools.Single(tool => tool.Name == "query_calendar").Allowed);

        // The export is the same content in another format, so it takes the same grant and not one of
        // its own. A separate export permission here would look like a control and be none.
        Assert.Equal(allowed, granted.Tools.Single(tool => tool.Name == "export_calendar").Allowed);

        foreach (string tool in (string[])["query_calendar", "export_calendar"])
        {
            using JsonDocument response = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", tool,
                new { domainId, from = "2026-06-01", to = "2026-06-30" });
            if (allowed) _ = Structured(response); else AssertWriteError(response, "permission_denied");
        }
    }

    [Fact]
    public async Task TheCalendarIsReadPerDomainAndCapabilitiesReportItsBounds()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        MonkeysphereDomain other = await factory.Services.GetRequiredService<IDomainRegistry>().CreateAsync("Other calendar domain");
        var (typeId, birthdayId) = await InstallPersonAsync(factory);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            _ = await scope.ServiceProvider.GetRequiredService<IMonkeysphereService>()
                .CreateRecordAsync(typeId, "Ada", [Day(birthdayId, Birth)]);
        }

        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, ["records.read"]);
        int year = DateTime.UtcNow.Year;

        using JsonDocument ours = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "query_calendar",
            new { domainId = MonkeysphereDomains.DefaultId, from = $"{year}-06-01", to = $"{year}-06-30" });
        Assert.Single(Structured(ours).Deserialize<RemoteCalendar>(JsonOptions)!.Entries);

        // A birthday in one domain is not a birthday in another, and the export agrees with the query
        // about that rather than reaching across on its own.
        using JsonDocument theirs = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "query_calendar",
            new { domainId = other.Id, from = $"{year}-06-01", to = $"{year}-06-30" });
        Assert.Empty(Structured(theirs).Deserialize<RemoteCalendar>(JsonOptions)!.Entries);
        using JsonDocument theirExport = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "export_calendar",
            new { domainId = other.Id, from = $"{year}-06-01", to = $"{year}-06-30" });
        Assert.Equal(0, Structured(theirExport).Deserialize<RemoteCalendarExport>(JsonOptions)!.EntryCount);

        using JsonDocument capabilities = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_capabilities");
        RemoteCalendarLimits limits = Structured(capabilities).Deserialize<RemoteCapabilities>(JsonOptions)!.CalendarLimits!;
        Assert.Equal(CalendarService.MaximumDaysInRange, limits.MaximumDaysInRange);
        Assert.Equal(CalendarService.MaximumEntries, limits.MaximumEntries);
        Assert.Equal(CalendarService.DefaultEntries, limits.DefaultEntries);
        Assert.Equal(RemoteCalendarBounds.MaximumRecordTypes, limits.MaximumRecordTypes);
        Assert.Equal(RemoteCalendarBounds.MaximumChunkBytes, limits.MaximumChunkBytes);
    }

    private static FieldValueInput Day(Guid fieldId, DateOnly date) => new(
        fieldId,
        Temporal: new TemporalValueInput(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), TemporalPrecision.Day, false, null));

    /// <summary>
    /// The Person preset, because its Birthday field already declares that it comes round every year
    /// and a hand-built field would have to restate that configuration to test the same thing.
    /// </summary>
    private static async Task<(Guid TypeId, Guid BirthdayId)> InstallPersonAsync(RemoteEnabledApplicationFactory factory)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPresetService>().InstallPresetAsync("monkeysphere.person");
        IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
        RecordType person = (await records.ListRecordTypesAsync()).Single(type => type.PresetKey == "monkeysphere.person");
        RecordTypeDetails details = (await records.GetRecordTypeAsync(person.Id))!;
        return (person.Id, details.Fields.Single(field => field.Definition.Name == "Birthday").Definition.Id);
    }
}
