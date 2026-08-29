using System.Text;

namespace Monkeysphere.Core;

public static class ICalendarExport
{
    public static byte[] Create(IReadOnlyList<CalendarEntry> entries, DateTimeOffset generatedAtUtc)
    {
        StringBuilder calendar = new();
        Append(calendar, "BEGIN:VCALENDAR");
        Append(calendar, "VERSION:2.0");
        Append(calendar, "PRODID:-//Wixely//Monkeysphere//EN");
        Append(calendar, "CALSCALE:GREGORIAN");
        Append(calendar, "METHOD:PUBLISH");
        foreach (CalendarEntry entry in entries)
        {
            Append(calendar, "BEGIN:VEVENT");
            Append(calendar, $"UID:{entry.FieldValueId:D}@monkeysphere.local");
            Append(calendar, $"DTSTAMP:{generatedAtUtc.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}");
            Append(calendar, $"DTSTART;VALUE=DATE:{entry.Date:yyyyMMdd}");
            Append(calendar, $"DTEND;VALUE=DATE:{entry.Date.AddDays(1):yyyyMMdd}");
            Append(calendar, "SUMMARY:" + Escape($"{entry.RecordDisplayName} — {entry.FieldName}"));
            Append(calendar, "CATEGORIES:" + Escape(entry.RecordTypeName));
            Append(calendar, "END:VEVENT");
        }

        Append(calendar, "END:VCALENDAR");
        return Encoding.UTF8.GetBytes(calendar.ToString());
    }

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\r\n", "\\n", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\n", StringComparison.Ordinal)
        .Replace(",", "\\,", StringComparison.Ordinal)
        .Replace(";", "\\;", StringComparison.Ordinal);

    private static void Append(StringBuilder target, string line)
    {
        int octets = 0;
        int limit = 75;
        foreach (Rune rune in line.EnumerateRunes())
        {
            int runeOctets = rune.Utf8SequenceLength;
            if (octets > 0 && octets + runeOctets > limit)
            {
                target.Append("\r\n ");
                octets = 0;
                limit = 74;
            }

            target.Append(rune.ToString());
            octets += runeOctets;
        }

        target.Append("\r\n");
    }
}
