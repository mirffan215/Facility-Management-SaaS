using System.Globalization;
using System.Text;

namespace Fms.Bookings.Services;

public sealed record IcsEvent(string Uid, string Summary, string? Location, string? Description, DateTimeOffset Start, DateTimeOffset End, bool Tentative, DateTimeOffset Stamp);

/// <summary>Writes an iCalendar (RFC 5545) feed. Text is escaped and long lines are folded, so no booking title can break the file or inject a property.</summary>
public static class IcsWriter
{
    public static string Build(string calendarName, IEnumerable<IcsEvent> events)
    {
        var sb = new StringBuilder();
        Line(sb, "BEGIN:VCALENDAR");
        Line(sb, "VERSION:2.0");
        Line(sb, "PRODID:-//Facility Management//Bookings//EN");
        Line(sb, "CALSCALE:GREGORIAN");
        Line(sb, "METHOD:PUBLISH");
        Line(sb, "X-WR-CALNAME:" + Escape(calendarName));
        foreach (var e in events)
        {
            Line(sb, "BEGIN:VEVENT");
            Line(sb, "UID:" + Escape(e.Uid));
            Line(sb, "DTSTAMP:" + Utc(e.Stamp));
            Line(sb, "DTSTART:" + Utc(e.Start));
            Line(sb, "DTEND:" + Utc(e.End));
            Line(sb, "SUMMARY:" + Escape(e.Summary));
            if (!string.IsNullOrWhiteSpace(e.Location)) Line(sb, "LOCATION:" + Escape(e.Location));
            if (!string.IsNullOrWhiteSpace(e.Description)) Line(sb, "DESCRIPTION:" + Escape(e.Description));
            Line(sb, "STATUS:" + (e.Tentative ? "TENTATIVE" : "CONFIRMED"));
            Line(sb, "END:VEVENT");
        }

        Line(sb, "END:VCALENDAR");
        return sb.ToString();
    }

    public static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Backslash, semicolon, comma and line breaks are escaped; other control characters are dropped.</summary>
    public static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case ';': sb.Append("\\;"); break;
                case ',': sb.Append("\\,"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': break;
                default:
                    if (!char.IsControl(c)) sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>Folds a content line at 75 octets (counting UTF-8), continuing with a space, ending with CRLF.</summary>
    private static void Line(StringBuilder sb, string line)
    {
        var count = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (count + size > 75)
            {
                sb.Append("\r\n ");
                count = 1;
            }

            sb.Append(rune.ToString());
            count += size;
        }

        sb.Append("\r\n");
    }
}
