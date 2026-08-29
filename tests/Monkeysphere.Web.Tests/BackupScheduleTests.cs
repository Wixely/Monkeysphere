using Monkeysphere.Web;

namespace Monkeysphere.Web.Tests;

public sealed class BackupScheduleTests
{
    [Fact]
    public void DailyWeeklyAndMonthlySchedulesProduceTheNextLocalOccurrence()
    {
        DateTimeOffset now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(
            new DateTimeOffset(2026, 8, 30, 2, 0, 0, TimeSpan.Zero),
            BackupScheduleCalculator.Next(now, new() { Frequency = "Daily", Time = new(2, 0) }));
        Assert.Equal(
            new DateTimeOffset(2026, 8, 30, 3, 0, 0, TimeSpan.Zero),
            BackupScheduleCalculator.Next(now, new() { Frequency = "Weekly", DayOfWeek = DayOfWeek.Sunday, Time = new(3, 0) }));
        Assert.Equal(
            new DateTimeOffset(2026, 8, 31, 4, 0, 0, TimeSpan.Zero),
            BackupScheduleCalculator.Next(now, new() { Frequency = "Monthly", DayOfMonth = 31, Time = new(4, 0) }));
    }
}
