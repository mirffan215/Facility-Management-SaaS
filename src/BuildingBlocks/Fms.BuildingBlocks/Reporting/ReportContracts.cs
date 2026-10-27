namespace Fms.BuildingBlocks.Reporting;

/// <summary>
/// What a dashboard asks of every module. <see cref="LocationIds"/> is set when one site was chosen: the ids of that site and everything beneath it, for modules whose data
/// is kept by place. Modules whose data is not kept by place ignore it and say so in their documentation.
/// </summary>
public sealed record ReportFilter(DateOnly From, DateOnly To, Guid? SiteId = null, IReadOnlyCollection<Guid>? LocationIds = null)
{
    /// <summary>The period as moments: from the start of the first day to the start of the day after the last (so "to" is inclusive).</summary>
    public DateTimeOffset FromAt => new(From.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    public DateTimeOffset ToAt => new(To.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
}

public static class KpiUnit
{
    public const string Count = "count";
    public const string Percent = "percent";
    public const string Hours = "hours";
    public const string Minutes = "minutes";
    public const string Money = "money";
    public const string Area = "area";
}

/// <summary>One headline figure. <see cref="Value"/> is null when there is nothing to measure (no denominator): shown as a dash, never as zero.</summary>
public sealed record Kpi(string Key, string Group, decimal? Value, string Unit, string? Detail = null);

public sealed record SeriesPoint(string Label, decimal Value);

/// <summary>A chart's data: labelled points (weeks, statuses, priorities...).</summary>
public sealed record Series(string Key, string Group, string Unit, IReadOnlyList<SeriesPoint> Points);

public sealed record ReportSection(IReadOnlyList<Kpi> Kpis, IReadOnlyList<Series> Series);

/// <summary>Implemented by each module that has figures to show. The reporting module asks every provider the caller is allowed to see.</summary>
public interface IReportProvider
{
    /// <summary>work, inventory, purchasing, vendors, facilities, spaces, bookings, visitors.</summary>
    string Group { get; }

    /// <summary>The permission that lets someone see this group's figures (on top of being allowed to see reports at all).</summary>
    string Permission { get; }

    Task<ReportSection> BuildAsync(ReportFilter filter, CancellationToken ct = default);
}

/// <summary>Small, exact calculations shared by the providers (and tested on their own).</summary>
public static class ReportMath
{
    /// <summary>A share as a percentage with one decimal, or null when there is nothing to divide by.</summary>
    public static decimal? Percent(int part, int whole) => whole <= 0 ? null : Math.Round(part * 100m / whole, 1);

    public static decimal? Average(IReadOnlyCollection<decimal> values, int decimals = 1) => values.Count == 0 ? null : Math.Round(values.Average(), decimals);

    /// <summary>The Monday of the week a day is in, as the label of a weekly series.</summary>
    public static DateOnly WeekStart(DateOnly day)
    {
        var back = ((int)day.DayOfWeek + 6) % 7; // Monday = 0
        return day.AddDays(-back);
    }

    public static string WeekLabel(DateOnly day) => WeekStart(day).ToString("yyyy-MM-dd");

    /// <summary>Every week start from the first day's week to the last day's, so a quiet week shows as zero instead of being missing.</summary>
    public static IReadOnlyList<string> WeekLabels(DateOnly from, DateOnly to)
    {
        var list = new List<string>();
        for (var d = WeekStart(from); d <= to; d = d.AddDays(7)) list.Add(d.ToString("yyyy-MM-dd"));
        return list;
    }

    public static IReadOnlyList<SeriesPoint> Weekly(IEnumerable<DateOnly> days, DateOnly from, DateOnly to)
    {
        var counts = days.Where(d => d >= from && d <= to).GroupBy(WeekLabel).ToDictionary(g => g.Key, g => g.Count());
        return WeekLabels(from, to).Select(l => new SeriesPoint(l, counts.GetValueOrDefault(l))).ToList();
    }

    public const int MaxDays = 400;
}
