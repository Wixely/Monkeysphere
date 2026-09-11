using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Monkeysphere.Core;

namespace Monkeysphere.Data.Tests;

/// <summary>
/// The calendar matched the literal stored year, so a birthday recorded in 1990 was only visible by
/// navigating to 1990 and every month looked empty. These cover the fix end to end: what repeats,
/// what does not, and that the day itself is still shown where it actually happened.
/// </summary>
public sealed class CalendarRecurrenceTests
{
    private static readonly DateOnly Birth = new(1990, 6, 15);

    private static async Task<(TestApplication App, Guid TypeId, Guid BirthdayId)> StartAsync()
    {
        TestApplication application = await TestApplication.CreateAsync();
        await application.Services.GetRequiredService<IPresetService>().InstallPresetAsync("monkeysphere.person");
        IMonkeysphereService records = application.Services.GetRequiredService<IMonkeysphereService>();
        RecordType person = (await records.ListRecordTypesAsync()).Single(type => type.PresetKey == "monkeysphere.person");
        RecordTypeDetails details = (await records.GetRecordTypeAsync(person.Id))!;
        Guid birthday = details.Fields.Single(field => field.Definition.Name == "Birthday").Definition.Id;
        return (application, person.Id, birthday);
    }

    private static FieldValueInput Day(Guid fieldId, DateOnly date) =>
        new(fieldId, Temporal: new TemporalValueInput(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), TemporalPrecision.Day, false, null));

    [Fact]
    public async Task ABirthdayFromLongAgoAppearsInThisYearsMonth()
    {
        var (app, typeId, birthday) = await StartAsync();
        await using TestApplication _ = app;
        IMonkeysphereService records = app.Services.GetRequiredService<IMonkeysphereService>();
        await records.CreateRecordAsync(typeId, "Has A Birthday", [Day(birthday, Birth)]);

        ICalendarService calendar = app.Services.GetRequiredService<ICalendarService>();
        int year = DateTime.UtcNow.Year;
        IReadOnlyList<CalendarEntry> june = await calendar.QueryAsync(
            new(new DateOnly(year, 6, 1), new DateOnly(year, 6, 30)));

        CalendarEntry entry = Assert.Single(june);
        Assert.Equal(new DateOnly(year, 6, 15), entry.Date);
        Assert.True(entry.IsRepeat);
        Assert.Equal(year - 1990, entry.YearsSince);
        Assert.Equal("Has A Birthday", entry.RecordDisplayName);
    }

    // Browsing back to the month it happened must show the day itself, once, not a repeat.
    [Fact]
    public async Task TheYearItHappenedShowsTheDayItselfExactlyOnce()
    {
        var (app, typeId, birthday) = await StartAsync();
        await using TestApplication _ = app;
        await app.Services.GetRequiredService<IMonkeysphereService>()
            .CreateRecordAsync(typeId, "Has A Birthday", [Day(birthday, Birth)]);

        IReadOnlyList<CalendarEntry> june1990 = await app.Services.GetRequiredService<ICalendarService>()
            .QueryAsync(new(new DateOnly(1990, 6, 1), new DateOnly(1990, 6, 30)));

        CalendarEntry entry = Assert.Single(june1990);
        Assert.False(entry.IsRepeat);
        Assert.Equal(0, entry.YearsSince);
    }

    [Fact]
    public async Task ADateOnAFieldThatDoesNotRepeatStaysInItsOwnYear()
    {
        var (app, typeId, _) = await StartAsync();
        await using TestApplication __ = app;
        IMonkeysphereService records = app.Services.GetRequiredService<IMonkeysphereService>();

        // A plain date field created by hand carries no recurrence.
        FieldDefinition once = await records.CreateAndAttachFieldAsync(typeId, new("Joined", FieldTypes.ExactDate, false));
        await records.CreateRecordAsync(typeId, "Joined Once", [new FieldValueInput(once.Id, "2019-06-20")]);

        ICalendarService calendar = app.Services.GetRequiredService<ICalendarService>();
        Assert.Single(await calendar.QueryAsync(new(new DateOnly(2019, 6, 1), new DateOnly(2019, 6, 30))));
        Assert.Empty(await calendar.QueryAsync(new(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30))));
    }

    [Fact]
    public async Task AFieldSetToRepeatEveryFourYearsSkipsTheYearsBetween()
    {
        var (app, typeId, _) = await StartAsync();
        await using TestApplication __ = app;
        IMonkeysphereService records = app.Services.GetRequiredService<IMonkeysphereService>();

        FieldDefinition games = await records.CreateAndAttachFieldAsync(typeId, new("Games", FieldTypes.ExactDate, false));
        await records.SetFieldRecurrenceAsync(games.Id, new(true, 4));
        await records.CreateRecordAsync(typeId, "Olympic Watcher", [new FieldValueInput(games.Id, "2024-07-26")]);

        ICalendarService calendar = app.Services.GetRequiredService<ICalendarService>();
        Assert.Empty(await calendar.QueryAsync(new(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31))));
        CalendarEntry entry = Assert.Single(
            await calendar.QueryAsync(new(new DateOnly(2028, 7, 1), new DateOnly(2028, 7, 31))));
        Assert.Equal(new DateOnly(2028, 7, 26), entry.Date);
        Assert.Equal(4, entry.YearsSince);
    }

    [Theory]
    [InlineData(LeapDayRoll.Backward, 2, 28)]
    [InlineData(LeapDayRoll.Forward, 3, 1)]
    public async Task ALeapDayBirthdayLandsWhereTheFieldSaysInACommonYear(LeapDayRoll roll, int month, int day)
    {
        var (app, typeId, birthday) = await StartAsync();
        await using TestApplication _ = app;
        IMonkeysphereService records = app.Services.GetRequiredService<IMonkeysphereService>();
        await records.SetFieldRecurrenceAsync(birthday, new(true, 1, roll));
        await records.CreateRecordAsync(typeId, "Leap Day Person", [Day(birthday, new DateOnly(2000, 2, 29))]);

        // 2026 has no 29 February.
        IReadOnlyList<CalendarEntry> window = await app.Services.GetRequiredService<ICalendarService>()
            .QueryAsync(new(new DateOnly(2026, 2, 1), new DateOnly(2026, 3, 31)));

        CalendarEntry entry = Assert.Single(window);
        Assert.Equal(new DateOnly(2026, month, day), entry.Date);
        Assert.True(entry.RolledFromLeapDay);
    }

    [Fact]
    public async Task AMonthShowsEveryRecordTypeUnlessTheQueryNarrowsIt()
    {
        var (app, typeId, birthday) = await StartAsync();
        await using TestApplication __ = app;
        IMonkeysphereService records = app.Services.GetRequiredService<IMonkeysphereService>();
        await records.CreateRecordAsync(typeId, "A Person", [Day(birthday, Birth)]);

        RecordType other = await records.CreateRecordTypeAsync("Companion");
        FieldDefinition otherBirthday = await records.CreateAndAttachFieldAsync(other.Id, new("Birthday", FieldTypes.ExactDate, false));
        await records.SetFieldRecurrenceAsync(otherBirthday.Id, FieldRecurrence.Annual);
        await records.CreateRecordAsync(other.Id, "A Cat", [new FieldValueInput(otherBirthday.Id, "2015-06-02")]);

        ICalendarService calendar = app.Services.GetRequiredService<ICalendarService>();
        int year = DateTime.UtcNow.Year;
        DateOnly from = new(year, 6, 1);
        DateOnly to = new(year, 6, 30);

        Assert.Equal(2, (await calendar.QueryAsync(new(from, to))).Count);
        Assert.Equal("A Cat", Assert.Single(
            await calendar.QueryAsync(new(from, to) { RecordTypeIds = [other.Id] })).RecordDisplayName);
        Assert.Equal("A Person", Assert.Single(
            await calendar.QueryAsync(new(from, to, typeId))).RecordDisplayName);
    }

    // The calendar now shows who a date belongs to, so it must carry the record's own identity.
    [Fact]
    public async Task AnEntryCarriesTheRecordTypeSymbolSoItCanBeShownWithItsName()
    {
        var (app, typeId, birthday) = await StartAsync();
        await using TestApplication _ = app;
        await app.Services.GetRequiredService<IMonkeysphereService>()
            .CreateRecordAsync(typeId, "Has A Birthday", [Day(birthday, Birth)]);

        int year = DateTime.UtcNow.Year;
        CalendarEntry entry = Assert.Single(await app.Services.GetRequiredService<ICalendarService>()
            .QueryAsync(new(new DateOnly(year, 6, 1), new DateOnly(year, 6, 30))));

        Assert.False(string.IsNullOrWhiteSpace(entry.RecordTypeSymbol));
        Assert.Null(entry.ImageId);
    }
}
