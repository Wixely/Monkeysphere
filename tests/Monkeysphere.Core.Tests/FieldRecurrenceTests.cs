using System.Text.Json;
using Monkeysphere.Core;

namespace Monkeysphere.Core.Tests;

/// <summary>
/// A calendar that only showed the year a date was stored in showed nothing: a birthday from 1990
/// never appeared again. These cover what repeats, how often, and the one day of the year that does
/// not exist in three years out of four.
/// </summary>
public sealed class FieldRecurrenceTests
{
    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    [Fact]
    public void ADateThatDoesNotRepeatAppearsOnlyOnTheDayItHappened()
    {
        DateOnly released = D(2019, 5, 20);

        Assert.Single(FieldRecurrences.Occurrences(released, FieldRecurrence.None, D(2019, 1, 1), D(2019, 12, 31)));
        Assert.Empty(FieldRecurrences.Occurrences(released, FieldRecurrence.None, D(2026, 1, 1), D(2026, 12, 31)));
    }

    [Fact]
    public void AnAnnualDateComesRoundEveryYearAndKnowsHowManyHavePassed()
    {
        DateOnly born = D(1990, 6, 15);

        DateOccurrence occurrence = Assert.Single(
            FieldRecurrences.Occurrences(born, FieldRecurrence.Annual, D(2026, 6, 1), D(2026, 6, 30)));

        Assert.Equal(D(2026, 6, 15), occurrence.Date);
        Assert.Equal(36, occurrence.YearsSince);
        Assert.True(occurrence.IsRepeat);
        Assert.False(occurrence.RolledFromLeapDay);
    }

    // Browsing back to the month it happened must show the day itself, not a repeat of it.
    [Fact]
    public void TheOriginalDayIsShownAsItselfRatherThanAsARepeat()
    {
        DateOnly born = D(1990, 6, 15);

        DateOccurrence occurrence = Assert.Single(
            FieldRecurrences.Occurrences(born, FieldRecurrence.Annual, D(1990, 6, 1), D(1990, 6, 30)));

        Assert.Equal(0, occurrence.YearsSince);
        Assert.False(occurrence.IsRepeat);
    }

    [Fact]
    public void AnIntervalOfFourYearsSkipsTheYearsBetween()
    {
        DateOnly games = D(2024, 7, 26);
        FieldRecurrence everyFour = new(true, 4);

        Assert.Empty(FieldRecurrences.Occurrences(games, everyFour, D(2026, 1, 1), D(2027, 12, 31)));

        DateOccurrence occurrence = Assert.Single(
            FieldRecurrences.Occurrences(games, everyFour, D(2028, 1, 1), D(2028, 12, 31)));
        Assert.Equal(D(2028, 7, 26), occurrence.Date);
        Assert.Equal(4, occurrence.YearsSince);
    }

    [Theory]
    [InlineData(LeapDayRoll.Backward, 2, 28)]
    [InlineData(LeapDayRoll.Forward, 3, 1)]
    public void TheTwentyNinthOfFebruaryMovesTheWayTheFieldSays(LeapDayRoll roll, int month, int day)
    {
        DateOnly born = D(2000, 2, 29);
        FieldRecurrence recurrence = new(true, 1, roll);

        // 2026 is not a leap year, so the day it names does not exist.
        DateOccurrence occurrence = Assert.Single(
            FieldRecurrences.Occurrences(born, recurrence, D(2026, 1, 1), D(2026, 12, 31)));

        Assert.Equal(D(2026, month, day), occurrence.Date);
        Assert.True(occurrence.RolledFromLeapDay);
        Assert.Equal(26, occurrence.YearsSince);
    }

    [Fact]
    public void TheTwentyNinthOfFebruaryStaysPutInALeapYear()
    {
        DateOccurrence occurrence = Assert.Single(FieldRecurrences.Occurrences(
            D(2000, 2, 29), new(true, 1, LeapDayRoll.Forward), D(2028, 1, 1), D(2028, 12, 31)));

        Assert.Equal(D(2028, 2, 29), occurrence.Date);
        Assert.False(occurrence.RolledFromLeapDay);
    }

    // A range covering several years yields one appearance per interval, in order.
    [Fact]
    public void ARangeOfYearsYieldsEveryAppearanceInIt()
    {
        IReadOnlyList<DateOccurrence> occurrences =
            FieldRecurrences.Occurrences(D(2020, 3, 1), new(true, 2), D(2019, 1, 1), D(2026, 12, 31));

        Assert.Equal(
            [D(2020, 3, 1), D(2022, 3, 1), D(2024, 3, 1), D(2026, 3, 1)],
            occurrences.Select(item => item.Date));
        Assert.False(occurrences[0].IsRepeat);
        Assert.All(occurrences.Skip(1), item => Assert.True(item.IsRepeat));
    }

    [Fact]
    public void ARepeatIsNeverProducedBeforeTheDayItself()
    {
        Assert.Empty(FieldRecurrences.Occurrences(D(2030, 4, 2), FieldRecurrence.Annual, D(2026, 1, 1), D(2029, 12, 31)));
    }

    [Fact]
    public void RecurrenceRoundTripsThroughFieldConfiguration()
    {
        FieldRecurrence recurrence = new(true, 4, LeapDayRoll.Forward);
        string json = FieldRecurrences.Configure(FieldTypes.ExactDate, null, recurrence);
        FieldDefinition field = Definition(FieldTypes.ExactDate, json);

        Assert.Equal(recurrence, FieldRecurrences.Of(field));
    }

    // Configure promises to leave other settings alone, so it must actually read what is already there.
    [Fact]
    public void SettingARecurrenceKeepsTheRestOfTheFieldsConfiguration()
    {
        string json = FieldRecurrences.Configure(
            FieldTypes.ExactDate, """{"calendarSystem":"gregorian","hint":{"nested":true}}""", FieldRecurrence.Annual);

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal("gregorian", document.RootElement.GetProperty("calendarSystem").GetString());
        Assert.True(document.RootElement.GetProperty("hint").GetProperty("nested").GetBoolean());
        Assert.Equal(FieldRecurrence.Annual, FieldRecurrences.Of(Definition(FieldTypes.ExactDate, json)));
    }

    [Fact]
    public void ReplacingARecurrenceDoesNotLeaveTheOldOneBehind()
    {
        string first = FieldRecurrences.Configure(FieldTypes.ExactDate, null, new(true, 4, LeapDayRoll.Forward));
        string second = FieldRecurrences.Configure(FieldTypes.ExactDate, first, FieldRecurrence.None);

        Assert.Equal(FieldRecurrence.None, FieldRecurrences.Of(Definition(FieldTypes.ExactDate, second)));
    }

    [Fact]
    public void AFieldWithNoRecurrenceOrUnreadableConfigurationDoesNotRepeat()
    {
        Assert.Equal(FieldRecurrence.None, FieldRecurrences.Of(Definition(FieldTypes.ExactDate, "{}")));
        Assert.Equal(FieldRecurrence.None, FieldRecurrences.Of(Definition(FieldTypes.ExactDate, "not json")));

        // A field that carries no date cannot repeat, whatever its configuration says.
        Assert.Equal(FieldRecurrence.None, FieldRecurrences.Of(
            Definition(FieldTypes.Text, FieldRecurrences.Configure(FieldTypes.ExactDate, null, FieldRecurrence.Annual))));
        Assert.False(FieldRecurrences.Supports(FieldTypes.Text));
        Assert.Throws<DomainValidationException>(() =>
            FieldRecurrences.Configure(FieldTypes.Text, null, FieldRecurrence.Annual));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(FieldRecurrence.MaximumIntervalYears + 1)]
    public void AnImpossibleIntervalIsRefused(int interval) =>
        Assert.Throws<DomainValidationException>(() => new FieldRecurrence(true, interval).Validated());

    private static FieldDefinition Definition(string typeId, string configurationJson) => new(
        Guid.CreateVersion7(), "Birthday", typeId, configurationJson, FieldLifecycle.Active,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
