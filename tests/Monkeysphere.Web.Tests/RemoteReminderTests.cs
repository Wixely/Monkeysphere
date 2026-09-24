using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;
using Monkeysphere.Web.Remote;

namespace Monkeysphere.Web.Tests;

/// <summary>
/// A reminder is the one thing on the calendar page that an operator had to be present to set. These
/// pin the loop a client actually walks — read the calendar, take a value's id, set a reminder on it,
/// see it listed, dismiss it — and the eligibility rule that keeps a reminder off a date nobody is
/// sure of, which is the same rule the calendar itself applies and must not disagree with.
/// </summary>
public sealed partial class RemoteDiscoveryTests
{
    [Fact]
    public async Task McpSetsAReminderOnAValueItFoundInTheCalendarAndDismissesIt()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (typeId, birthdayId) = await InstallPersonAsync(factory);
        using IServiceScope scope = factory.Services.CreateScope();
        RecordDetails ada = await scope.ServiceProvider.GetRequiredService<IMonkeysphereService>()
            .CreateRecordAsync(typeId, "Ada", [Day(birthdayId, Birth)]);

        var (_, credential, surface) = await EnableRelationshipWritesAsync(
            factory, ["records.read", "records.write"]);
        Guid domainId = MonkeysphereDomains.DefaultId;
        int year = DateTime.UtcNow.Year;

        // The loop a client walks: the value's id comes from the calendar, because nothing else
        // exposes it and a reminder has nothing else to attach to.
        using JsonDocument calendarResult = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "query_calendar",
            new { domainId, from = $"{year}-06-01", to = $"{year}-06-30" });
        RemoteCalendarEntry entry = Assert.Single(Structured(calendarResult).Deserialize<RemoteCalendar>(JsonOptions)!.Entries);

        using JsonDocument createdResult = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_reminder",
            new { domainId, fieldValueId = entry.FieldValueId, leadDays = 7 });
        RemoteReminder created = Structured(createdResult).Deserialize<RemoteReminder>(JsonOptions)!;

        // The result carries what the reminder is for, not just its own id: a caller handed a bare
        // identifier would have to read the whole calendar back to find out what it watches.
        Assert.Equal(7, created.LeadDays);
        Assert.Equal(ada.Record.Id, created.Entry.RecordId);
        Assert.Equal("Ada", created.Entry.RecordDisplayName);
        Assert.Equal("Birthday", created.Entry.FieldName);

        // The due date is the stored date less the lead time, which for a value that repeats every
        // year is a day long past. That is what the calendar page shows too, and the two agreeing
        // matters more here than the answer being the one a client might prefer.
        Assert.Equal(Birth.AddDays(-7), created.DueDate);

        using JsonDocument listed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "list_reminders", new { domainId });
        RemoteReminder listedReminder = Assert.Single(Structured(listed).Deserialize<RemoteReminder[]>(JsonOptions)!);
        Assert.Equal(created.Id, listedReminder.Id);
        Assert.Equal(created.DueDate, listedReminder.DueDate);

        // The same value and lead time cannot be scheduled twice, so a retry is refused rather than
        // quietly making a second reminder. That is why there is no idempotency key here.
        using JsonDocument repeated = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_reminder",
            new { domainId, fieldValueId = entry.FieldValueId, leadDays = 7 });
        AssertWriteError(repeated, "validation_failed");

        // A different lead time on the same value is a different reminder, though: somebody may want
        // a month's warning and a day's.
        using JsonDocument second = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_reminder",
            new { domainId, fieldValueId = entry.FieldValueId, leadDays = 30 });
        _ = Structured(second);
        using JsonDocument both = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "list_reminders", new { domainId });
        Assert.Equal(2, Structured(both).Deserialize<RemoteReminder[]>(JsonOptions)!.Length);

        using JsonDocument dismissed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "dismiss_reminder",
            new { domainId, id = created.Id });
        Assert.Equal(created.Id, Structured(dismissed).Deserialize<RemoteReminderDismissal>(JsonOptions)!.Id);

        using JsonDocument remaining = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "list_reminders", new { domainId });
        Assert.Equal(30, Assert.Single(Structured(remaining).Deserialize<RemoteReminder[]>(JsonOptions)!).LeadDays);

        // Dismissing what is already dismissed says so rather than succeeding twice, so a caller can
        // tell a completed dismissal from a mistaken identifier.
        using JsonDocument again = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "dismiss_reminder",
            new { domainId, id = created.Id });
        AssertWriteError(again, "not_found");

        // And the record and its birthday are untouched by any of it.
        Assert.Single(Structured(await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "query_calendar",
            new { domainId, from = $"{year}-06-01", to = $"{year}-06-30" })).Deserialize<RemoteCalendar>(JsonOptions)!.Entries);
    }

    [Fact]
    public async Task AReminderCannotBeSetOnADateNobodyIsSureOf()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (typeId, birthdayId) = await InstallPersonAsync(factory);
        Guid vagueValueId;
        Guid roughValueId;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IMonkeysphereService records = scope.ServiceProvider.GetRequiredService<IMonkeysphereService>();
            RecordDetails vague = await records.CreateRecordAsync(typeId, "Vague", [new(birthdayId,
                Temporal: new TemporalValueInput("1815", TemporalPrecision.Year, false, null))]);
            vagueValueId = vague.Values.Single(value => value.FieldDefinitionId == birthdayId).Id;
            RecordDetails rough = await records.CreateRecordAsync(typeId, "Roughly", [new(birthdayId,
                Temporal: new TemporalValueInput("1990-06-15", TemporalPrecision.Day, true, "about then"))]);
            roughValueId = rough.Values.Single(value => value.FieldDefinitionId == birthdayId).Id;
        }

        var (_, credential, surface) = await EnableRelationshipWritesAsync(
            factory, ["records.read", "records.write"]);
        Guid domainId = MonkeysphereDomains.DefaultId;

        // Neither has a day to count back from. A reminder on a year is a reminder with no date, and
        // one on a guess would fire on a day nobody claimed. The calendar excludes both and so does
        // this, which is the agreement that matters: a client cannot set a reminder on something the
        // calendar would never have shown it.
        foreach (Guid valueId in (Guid[])[vagueValueId, roughValueId, Guid.CreateVersion7()])
        {
            using JsonDocument response = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_reminder",
                new { domainId, fieldValueId = valueId, leadDays = 7 });
            AssertWriteError(response, "validation_failed");
        }

        using JsonDocument none = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "list_reminders", new { domainId });
        Assert.Empty(Structured(none).Deserialize<RemoteReminder[]>(JsonOptions)!);
    }

    [Fact]
    public async Task McpRefusesLeadTimesOutsideTheirBoundAndReportsTheBound()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (typeId, birthdayId) = await InstallPersonAsync(factory);
        Guid valueId;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            RecordDetails ada = await scope.ServiceProvider.GetRequiredService<IMonkeysphereService>()
                .CreateRecordAsync(typeId, "Ada", [Day(birthdayId, Birth)]);
            valueId = ada.Values.Single(value => value.FieldDefinitionId == birthdayId).Id;
        }

        var (_, credential, surface) = await EnableRelationshipWritesAsync(
            factory, ["records.read", "records.write"]);
        Guid domainId = MonkeysphereDomains.DefaultId;

        foreach (int leadDays in (int[])[-1, ReminderService.MaximumLeadDays + 1])
        {
            using JsonDocument response = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_reminder",
                new { domainId, fieldValueId = valueId, leadDays });
            AssertWriteError(response, "validation_failed");
        }

        // Both ends of the range are accepted, which is the half a refusal test cannot show. Zero is
        // "on the day itself" rather than a missing value.
        foreach (int leadDays in (int[])[0, ReminderService.MaximumLeadDays])
        {
            using JsonDocument response = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_reminder",
                new { domainId, fieldValueId = valueId, leadDays });
            Assert.Equal(leadDays, Structured(response).Deserialize<RemoteReminder>(JsonOptions)!.LeadDays);
        }

        using JsonDocument capabilities = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_capabilities");
        Assert.Equal(
            ReminderService.MaximumLeadDays,
            Structured(capabilities).Deserialize<RemoteCapabilities>(JsonOptions)!.ReminderLimits!.MaximumLeadDays);
    }

    [Theory]
    [InlineData("records.read", true, false)]
    [InlineData("records.write", false, true)]
    [InlineData("views.manage", false, false)]
    public async Task ReadingRemindersTakesTheReadGrantAndSettingThemTheWriteGrant(string grant, bool canRead, bool canWrite)
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var (typeId, birthdayId) = await InstallPersonAsync(factory);
        Guid valueId;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            RecordDetails ada = await scope.ServiceProvider.GetRequiredService<IMonkeysphereService>()
                .CreateRecordAsync(typeId, "Ada", [Day(birthdayId, Birth)]);
            valueId = ada.Values.Single(value => value.FieldDefinitionId == birthdayId).Id;
        }

        var (_, credential, surface) = await EnableRelationshipWritesAsync(factory, [grant]);
        Guid domainId = MonkeysphereDomains.DefaultId;

        using JsonDocument capabilities = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "get_capabilities");
        RemoteCapabilities granted = Structured(capabilities).Deserialize<RemoteCapabilities>(JsonOptions)!;
        Assert.Equal(canRead, granted.Tools.Single(tool => tool.Name == "list_reminders").Allowed);
        Assert.Equal(canWrite, granted.Tools.Single(tool => tool.Name == "create_reminder").Allowed);
        Assert.Equal(canWrite, granted.Tools.Single(tool => tool.Name == "dismiss_reminder").Allowed);

        // Listing a reminder discloses a record and one of its dates, so it is a read; setting one
        // stores state, so a read-only credential must not be able to.
        using JsonDocument listed = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "list_reminders", new { domainId });
        if (canRead) _ = Structured(listed); else AssertWriteError(listed, "permission_denied");

        using JsonDocument created = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_reminder",
            new { domainId, fieldValueId = valueId, leadDays = 7 });
        if (canWrite) _ = Structured(created); else AssertWriteError(created, "permission_denied");
    }

    [Fact]
    public async Task ARemindersDomainIsTheOneItWasSetIn()
    {
        await using RemoteEnabledApplicationFactory factory = new();
        using HttpClient client = factory.CreateClient();
        MonkeysphereDomain other = await factory.Services.GetRequiredService<IDomainRegistry>().CreateAsync("Other reminder domain");
        var (typeId, birthdayId) = await InstallPersonAsync(factory);
        Guid valueId;
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            RecordDetails ada = await scope.ServiceProvider.GetRequiredService<IMonkeysphereService>()
                .CreateRecordAsync(typeId, "Ada", [Day(birthdayId, Birth)]);
            valueId = ada.Values.Single(value => value.FieldDefinitionId == birthdayId).Id;
        }

        var (_, credential, surface) = await EnableRelationshipWritesAsync(
            factory, ["records.read", "records.write"]);

        using JsonDocument created = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_reminder",
            new { domainId = MonkeysphereDomains.DefaultId, fieldValueId = valueId, leadDays = 7 });
        Guid reminderId = Structured(created).Deserialize<RemoteReminder>(JsonOptions)!.Id;

        // The value belongs to another domain's records, so the reminder cannot be set from here.
        using JsonDocument elsewhere = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "create_reminder",
            new { domainId = other.Id, fieldValueId = valueId, leadDays = 7 });
        AssertWriteError(elsewhere, "validation_failed");

        using JsonDocument theirList = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "list_reminders",
            new { domainId = other.Id });
        Assert.Empty(Structured(theirList).Deserialize<RemoteReminder[]>(JsonOptions)!);

        // Nor dismissed by naming another domain, which would otherwise reach across the isolation
        // the domain selector exists to enforce.
        using JsonDocument theirDismiss = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "dismiss_reminder",
            new { domainId = other.Id, id = reminderId });
        AssertWriteError(theirDismiss, "not_found");

        using JsonDocument ourList = await SendAsync(client, surface.EndpointPath!, credential.Secret, "tools/call", "list_reminders",
            new { domainId = MonkeysphereDomains.DefaultId });
        Assert.Equal(reminderId, Assert.Single(Structured(ourList).Deserialize<RemoteReminder[]>(JsonOptions)!).Id);
    }
}
