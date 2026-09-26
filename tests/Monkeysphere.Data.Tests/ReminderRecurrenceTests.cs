using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Data.Tests;

/// <summary>
/// A reminder counted back from the day its value stored rather than from the next time that day comes
/// round, so one set on a birthday recorded in 1990 fell due in 1990 — and since dismissal was
/// permanent, it was useful exactly once, immediately, for an occurrence thirty-odd years past. The
/// whole feature was therefore useless on the dates people actually set reminders for.
///
/// The projection it needed already existed twice in this application: the calendar shows repeats and
/// the dashboard reports next occurrences. These pin that reminders now agree with both, and that
/// dismissal means "dealt with this one" rather than "never again".
/// </summary>
public sealed class ReminderRecurrenceTests
{
    private static readonly DateOnly Birth = new(1990, 6, 15);

    [Fact]
    public async Task AReminderOnARepeatingDateFallsDueBeforeItsNextOccurrence()
    {
        var (application, typeId, birthdayId) = await StartAsync();
        await using TestApplication owned = application;
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IReminderService reminders = application.Services.GetRequiredService<IReminderService>();
        RecordDetails ada = await records.CreateRecordAsync(typeId, "Ada", [Day(birthdayId, Birth)]);
        Guid valueId = ada.Values.Single(value => value.FieldDefinitionId == birthdayId).Id;

        _ = await reminders.CreateAsync(valueId, 7);
        ReminderItem item = Assert.Single(await reminders.ListActiveAsync());

        // The occurrence, not the stored day. Under the old behaviour this was 1990-06-08 and the
        // calendar page therefore said "Due" from the moment the reminder was created.
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        Assert.True(item.DueDate >= today.AddDays(-7), $"Due {item.DueDate} is not near today.");
        Assert.Equal(item.Entry.Date.AddDays(-7), item.DueDate);
        Assert.Equal(Birth, item.StoredDate);

        // And it says what it is: a repeat, and how many years on, the same way the calendar does.
        Assert.True(item.Entry.IsRepeat);
        Assert.Equal(item.Entry.Date.Year - 1990, item.Entry.YearsSince);
        Assert.Equal(6, item.Entry.Date.Month);
        Assert.Equal(15, item.Entry.Date.Day);

        // The reminder and the calendar now answer the same question the same way, which is the
        // agreement that was missing.
        IReadOnlyList<CalendarEntry> calendar = await application.Services.GetRequiredService<ICalendarService>()
            .QueryAsync(new(item.Entry.Date, item.Entry.Date));
        Assert.Equal(item.Entry.Date, Assert.Single(calendar).Date);
    }

    [Fact]
    public async Task DismissingDealsWithOneOccurrenceAndTheNextStillArrives()
    {
        var (application, typeId, birthdayId) = await StartAsync();
        await using TestApplication owned = application;
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IReminderService reminders = application.Services.GetRequiredService<IReminderService>();
        RecordDetails ada = await records.CreateRecordAsync(typeId, "Ada", [Day(birthdayId, Birth)]);
        Guid valueId = ada.Values.Single(value => value.FieldDefinitionId == birthdayId).Id;
        Reminder created = await reminders.CreateAsync(valueId, 7);

        DateOnly dismissed = Assert.Single(await reminders.ListActiveAsync()).Entry.Date;

        // The occurrence being dismissed is a real upcoming one rather than the 1990 day the value
        // stores, which is what makes the rest of this test about dismissal instead of about the
        // projection being wrong in a way dismissal happens to hide.
        Assert.True(dismissed.Year >= DateTime.Now.Year, $"Dismissed {dismissed}, which is not an occurrence anybody is waiting for.");
        Assert.True(await reminders.DismissAsync(created.Id));
        Assert.Empty(await reminders.ListActiveAsync());

        // Dismissing the same occurrence again changes nothing and says so, which is what lets a
        // caller tell a completed dismissal from a mistaken identifier.
        Assert.False(await reminders.DismissAsync(created.Id));

        // The reminder is still scheduled, so setting the same one again is refused rather than
        // quietly producing a second that would fire alongside it next year.
        await Assert.ThrowsAsync<DomainValidationException>(() => reminders.CreateAsync(valueId, 7));

        // Move the value's day so that the occurrence which was dismissed is no longer the next one —
        // the same thing the calendar turning over does, without waiting a year for it.
        _ = await records.UpdateRecordAsync(
            ada.Record.Id,
            "Ada",
            [Day(birthdayId, Birth.AddDays(1))],
            expectedRevision: ada.Revision);

        ReminderItem returned = Assert.Single(await reminders.ListActiveAsync());
        Assert.NotEqual(dismissed, returned.Entry.Date);
        Assert.Equal(returned.Entry.Date.AddDays(-7), returned.DueDate);

        // And it can be dismissed again, because dismissal is about an occurrence rather than about
        // the reminder.
        Assert.True(await reminders.DismissAsync(created.Id));
        Assert.Empty(await reminders.ListActiveAsync());
    }

    [Fact]
    public async Task AReminderOnADateThatDoesNotRepeatStaysGoneOnceDismissed()
    {
        var (application, typeId, _) = await StartAsync();
        await using TestApplication owned = application;
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IReminderService reminders = application.Services.GetRequiredService<IReminderService>();

        // A plain exact-date field declares no recurrence, so its value happens once.
        FieldDefinition joined = await records.CreateAndAttachFieldAsync(typeId, new("Joined", FieldTypes.ExactDate, false));
        RecordDetails grace = await records.CreateRecordAsync(typeId, "Grace", [new(joined.Id, "2020-03-04")]);
        Guid valueId = grace.Values.Single(value => value.FieldDefinitionId == joined.Id).Id;
        Reminder created = await reminders.CreateAsync(valueId, 0);

        ReminderItem item = Assert.Single(await reminders.ListActiveAsync());

        // The day itself, which is in the past and stays there: projecting a one-off forward would
        // invent an anniversary nobody asked for.
        Assert.Equal(new DateOnly(2020, 3, 4), item.Entry.Date);
        Assert.Equal(new DateOnly(2020, 3, 4), item.DueDate);
        Assert.False(item.Entry.IsRepeat);

        // Its only occurrence is the one dismissed, so dismissing it is permanent without a second
        // rule saying so.
        Assert.True(await reminders.DismissAsync(created.Id));
        Assert.Empty(await reminders.ListActiveAsync());
        Assert.False(await reminders.DismissAsync(created.Id));
    }

    [Fact]
    public async Task RemindersComeBackSoonestDueFirst()
    {
        var (application, typeId, birthdayId) = await StartAsync();
        await using TestApplication owned = application;
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        IReminderService reminders = application.Services.GetRequiredService<IReminderService>();
        RecordDetails ada = await records.CreateRecordAsync(typeId, "Ada", [Day(birthdayId, Birth)]);
        Guid valueId = ada.Values.Single(value => value.FieldDefinitionId == birthdayId).Id;

        // A month's warning and a day's on the same date are two reminders, not a conflict.
        _ = await reminders.CreateAsync(valueId, 1);
        _ = await reminders.CreateAsync(valueId, 30);

        IReadOnlyList<ReminderItem> active = await reminders.ListActiveAsync();
        Assert.Equal(2, active.Count);

        // The longer lead falls due first, which is the order the page wanted all along and could not
        // get while every due date was decades old.
        Assert.Equal(30, active[0].Reminder.LeadDays);
        Assert.Equal(1, active[1].Reminder.LeadDays);
        Assert.True(active[0].DueDate < active[1].DueDate);
        Assert.Equal(active[0].Entry.Date, active[1].Entry.Date);

        // Both are about an occurrence still to come, so the ordering is of dates somebody can act on
        // rather than of two equally stale ones that happen to sort correctly.
        Assert.True(active[0].Entry.Date.Year >= DateTime.Now.Year, $"Ordered stale dates: {active[0].Entry.Date}.");
    }

    private static FieldValueInput Day(Guid fieldId, DateOnly date) => new(
        fieldId,
        Temporal: new TemporalValueInput(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), TemporalPrecision.Day, false, null));

    /// <summary>
    /// The Person preset, whose Birthday declares that it comes round every year. A field built here
    /// would have to restate that configuration to test the same thing.
    /// </summary>
    private static async Task<(TestApplication Application, Guid TypeId, Guid BirthdayId)> StartAsync()
    {
        TestApplication application = await TestApplication.CreateAsync();
        await application.Services.GetRequiredService<IPresetService>().InstallPresetAsync("monkeysphere.person");
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordType person = (await records.ListRecordTypesAsync()).Single(type => type.PresetKey == "monkeysphere.person");
        RecordTypeDetails details = (await records.GetRecordTypeAsync(person.Id))!;
        return (application, person.Id, details.Fields.Single(field => field.Definition.Name == "Birthday").Definition.Id);
    }
}
