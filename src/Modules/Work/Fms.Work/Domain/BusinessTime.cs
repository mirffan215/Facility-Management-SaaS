using System.Text.Json;

namespace Fms.Work.Domain;

/// <summary>Opening hours of one day, local time. <see cref="End"/> may be 24:00 (a full day).</summary>
public sealed record WorkingDay(TimeSpan Start, TimeSpan End);

/// <summary>
/// When work counts for an SLA clock: a time zone, weekly opening hours (one interval per day, or closed) and holidays.
/// All arithmetic is exact: intervals are converted to UTC per day, so daylight-saving changes cannot add or lose minutes.
/// </summary>
public sealed class BusinessCalendar
{
    private const int MaxDaysScanned = 3660;

    private readonly TimeZoneInfo _zone;
    private readonly IReadOnlyDictionary<DayOfWeek, WorkingDay?> _week;
    private readonly IReadOnlySet<DateOnly> _holidays;

    public BusinessCalendar(string timeZoneId, IReadOnlyDictionary<DayOfWeek, WorkingDay?> week, IReadOnlySet<DateOnly>? holidays = null)
    {
        _zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        _week = week;
        _holidays = holidays ?? new HashSet<DateOnly>();
    }

    /// <summary>Every minute counts, in every zone: used when no calendar is configured.</summary>
    public static BusinessCalendar AlwaysOpen { get; } = new("UTC", Enum.GetValues<DayOfWeek>().ToDictionary(d => d, _ => (WorkingDay?)new WorkingDay(TimeSpan.Zero, TimeSpan.FromHours(24))));

    /// <summary>Moves forward by working minutes. Zero minutes returns <paramref name="from"/> unchanged.</summary>
    public DateTimeOffset AddBusinessMinutes(DateTimeOffset from, double minutes)
    {
        if (minutes <= 0) return from;

        var remaining = TimeSpan.FromMinutes(minutes);
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, _zone).DateTime);
        for (var i = 0; i < MaxDaysScanned; i++, date = date.AddDays(1))
        {
            if (OpeningOn(date) is not { } open) continue;
            if (open.End <= from) continue;
            var start = open.Start > from ? open.Start : from;
            var available = open.End - start;
            if (remaining <= available) return start + remaining;
            remaining -= available;
        }

        throw new InvalidOperationException("The calendar has no working time in the next ten years.");
    }

    /// <summary>Working minutes between two instants, rounded up so a hold never costs anyone a part-minute (zero when <paramref name="to"/> is not after <paramref name="from"/>).</summary>
    public int BusinessMinutesBetween(DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from) return 0;

        var total = TimeSpan.Zero;
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, _zone).DateTime);
        var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(to, _zone).DateTime);
        for (var i = 0; i < MaxDaysScanned && date <= last; i++, date = date.AddDays(1))
        {
            if (OpeningOn(date) is not { } open) continue;
            var start = open.Start > from ? open.Start : from;
            var end = open.End < to ? open.End : to;
            if (end > start) total += end - start;
        }

        return (int)Math.Ceiling(total.TotalMinutes);
    }

    private (DateTimeOffset Start, DateTimeOffset End)? OpeningOn(DateOnly date)
    {
        if (_holidays.Contains(date) || _week.GetValueOrDefault(date.DayOfWeek) is not { } day) return null;
        return (ToUtc(date, day.Start), ToUtc(date, day.End));
    }

    private DateTimeOffset ToUtc(DateOnly date, TimeSpan timeOfDay)
    {
        var local = date.ToDateTime(TimeOnly.MinValue).Add(timeOfDay);
        if (_zone.IsInvalidTime(local)) local = local.AddHours(1); // the clock skipped this time (spring forward): use the first valid moment
        var offset = _zone.GetUtcOffset(local);
        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset).ToUniversalTime();
    }
}

/// <summary>The stored form of weekly hours: {"Sunday":{"start":"08:00","end":"17:00"},"Friday":null,...}.</summary>
public static class WorkingHours
{
    private sealed record Interval(string Start, string End);

    public static readonly IReadOnlyList<DayOfWeek> Days = Enum.GetValues<DayOfWeek>();

    public static string Serialize(IReadOnlyDictionary<DayOfWeek, WorkingDay?> week) =>
        JsonSerializer.Serialize(Days.ToDictionary(d => d.ToString(), d => week.GetValueOrDefault(d) is { } w ? new Interval(Format(w.Start), Format(w.End)) : null));

    /// <summary>Parses and validates. Returns null (with an explanation) when the hours are malformed.</summary>
    public static IReadOnlyDictionary<DayOfWeek, WorkingDay?>? TryParse(string? json, out string? error)
    {
        error = null;
        Dictionary<string, Interval?>? raw;
        try { raw = JsonSerializer.Deserialize<Dictionary<string, Interval?>>(json ?? "", new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { raw = null; }
        if (raw is null) { error = "Working hours are not valid."; return null; }

        var week = new Dictionary<DayOfWeek, WorkingDay?>();
        foreach (var day in Days)
        {
            var key = raw.Keys.FirstOrDefault(k => string.Equals(k, day.ToString(), StringComparison.OrdinalIgnoreCase));
            if (key is null || raw[key] is not { } interval) { week[day] = null; continue; }
            if (!TryTime(interval.Start, out var start) || !TryTime(interval.End, out var end) || start < TimeSpan.Zero || end > TimeSpan.FromHours(24) || end <= start)
            {
                error = $"{day}: opening must be before closing, within one day (HH:mm).";
                return null;
            }

            week[day] = new WorkingDay(start, end);
        }

        if (week.Values.All(v => v is null)) { error = "At least one day must have working hours."; return null; }
        return week;
    }

    private static bool TryTime(string? text, out TimeSpan time)
    {
        time = default;
        if (text == "24:00") { time = TimeSpan.FromHours(24); return true; }
        return TimeSpan.TryParseExact(text, @"hh\:mm", null, out time);
    }

    private static string Format(TimeSpan t) => t == TimeSpan.FromHours(24) ? "24:00" : t.ToString(@"hh\:mm");
}
