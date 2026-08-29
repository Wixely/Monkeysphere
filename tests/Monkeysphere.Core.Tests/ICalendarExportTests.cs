using System.Text;
using Monkeysphere.Core;

namespace Monkeysphere.Core.Tests;

public sealed class ICalendarExportTests
{
    [Fact]
    public void ExportEscapesValuesUsesStableIdsAndFoldsUtf8Lines()
    {
        Guid fieldValueId = Guid.Parse("0198f100-0000-7000-8000-000000000001");
        CalendarEntry entry = new(
            fieldValueId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Friends, family; neighbours",
            new string('É', 50) + " Ada",
            Guid.NewGuid(),
            "Birthday",
            new DateOnly(2026, 9, 1));

        string result = Encoding.UTF8.GetString(ICalendarExport.Create(
            [entry],
            new DateTimeOffset(2026, 8, 29, 12, 30, 0, TimeSpan.Zero)));

        Assert.Contains($"UID:{fieldValueId:D}@monkeysphere.local\r\n", result, StringComparison.Ordinal);
        Assert.Contains("DTSTART;VALUE=DATE:20260901\r\n", result, StringComparison.Ordinal);
        Assert.Contains("DTEND;VALUE=DATE:20260902\r\n", result, StringComparison.Ordinal);
        Assert.Contains("CATEGORIES:Friends\\, family\\; neighbours\r\n", result, StringComparison.Ordinal);
        Assert.Contains("\r\n ", result, StringComparison.Ordinal);
        Assert.All(result.Split("\r\n", StringSplitOptions.RemoveEmptyEntries), line =>
            Assert.True(Encoding.UTF8.GetByteCount(line) <= 75, $"Line exceeds 75 octets: {line}"));
    }
}
