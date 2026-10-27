using Fms.BuildingBlocks.Reporting;
using Fms.Reporting.Services;

namespace Fms.UnitTests;

public sealed class ReportMathTests
{
    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(5, 0, null)]
    [InlineData(0, 4, 0.0)]
    [InlineData(1, 3, 33.3)]
    [InlineData(2, 3, 66.7)]
    [InlineData(4, 4, 100.0)]
    public void A_percentage_is_null_without_a_denominator_never_zero(int part, int whole, double? expected) =>
        Assert.Equal(expected is null ? null : (decimal)expected, ReportMath.Percent(part, whole));

    [Fact]
    public void An_average_of_nothing_is_null_not_zero()
    {
        Assert.Null(ReportMath.Average([]));
        Assert.Equal(2.5m, ReportMath.Average([1m, 2m, 3m, 4m]));
        Assert.Equal(3m, ReportMath.Average([2.6m, 3.4m], 0));
    }

    [Theory]
    [InlineData("2027-03-01", "2027-03-01")] // a Monday
    [InlineData("2027-03-03", "2027-03-01")]
    [InlineData("2027-03-07", "2027-03-01")] // a Sunday belongs to the week that began on the Monday before
    [InlineData("2027-03-08", "2027-03-08")]
    public void Weeks_start_on_Monday(string day, string start) => Assert.Equal(DateOnly.Parse(start), ReportMath.WeekStart(DateOnly.Parse(day)));

    [Fact]
    public void A_weekly_series_lists_every_week_so_a_quiet_week_is_zero_not_missing()
    {
        var points = ReportMath.Weekly([DateOnly.Parse("2027-03-02"), DateOnly.Parse("2027-03-03"), DateOnly.Parse("2027-03-23")], DateOnly.Parse("2027-03-01"), DateOnly.Parse("2027-03-28"));
        Assert.Equal(["2027-03-01", "2027-03-08", "2027-03-15", "2027-03-22"], points.Select(p => p.Label).ToArray());
        Assert.Equal([2m, 0m, 0m, 1m], points.Select(p => p.Value).ToArray());
    }

    [Fact]
    public void Days_outside_the_period_are_not_counted()
    {
        var points = ReportMath.Weekly([DateOnly.Parse("2027-02-20"), DateOnly.Parse("2027-04-20"), DateOnly.Parse("2027-03-02")], DateOnly.Parse("2027-03-01"), DateOnly.Parse("2027-03-07"));
        Assert.Equal(1m, Assert.Single(points).Value);
    }

    [Fact]
    public void The_period_runs_to_the_end_of_the_last_day()
    {
        var f = new ReportFilter(DateOnly.Parse("2027-03-01"), DateOnly.Parse("2027-03-31"));
        Assert.Equal(new DateTimeOffset(2027, 3, 1, 0, 0, 0, TimeSpan.Zero), f.FromAt);
        Assert.Equal(new DateTimeOffset(2027, 4, 1, 0, 0, 0, TimeSpan.Zero), f.ToAt);
    }
}

public sealed class ReportCsvTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("-1", "'-1")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("", "")]
    public void Cells_are_quoted_and_cannot_start_a_formula(string text, string expected) => Assert.Equal(expected, ReportService.Cell(text));

    [Fact]
    public void The_pack_exports_figures_then_chart_points_with_a_byte_order_mark_and_invariant_numbers()
    {
        var dashboard = new DashboardDto(DateOnly.Parse("2027-03-01"), DateOnly.Parse("2027-03-07"), null, null, DateTimeOffset.UnixEpoch,
        [
            new ReportGroupDto("work", [new Kpi("work.mttr", "work", 12.5m, KpiUnit.Hours, "repairs, emergencies"), new Kpi("work.slaResponse", "work", null, KpiUnit.Percent)],
                [new Series("work.createdByWeek", "work", KpiUnit.Count, [new SeriesPoint("2027-03-01", 4)])], false),
        ]);
        var lines = ReportService.ToCsv(dashboard).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("﻿Group,Kind,Key,Label,Value,Unit,Detail", lines[0]);
        Assert.Equal("work,kpi,work.mttr,,12.5,hours,\"repairs, emergencies\"", lines[1]);
        Assert.Equal("work,kpi,work.slaResponse,,,percent,", lines[2]); // no value is empty, not zero
        Assert.Equal("work,series,work.createdByWeek,2027-03-01,4,count,", lines[3]);
    }

    [Fact]
    public void Cost_per_area_needs_both_figures_and_a_positive_area()
    {
        List<ReportGroupDto> Groups(decimal? cost, decimal? area) =>
        [
            new("work", [new Kpi("work.cost", "work", cost, KpiUnit.Money)], [], false),
            new("facilities", [new Kpi("facilities.area", "facilities", area, KpiUnit.Area)], [], false),
        ];
        decimal? Result(List<ReportGroupDto> g) { ReportService.AddCostPerArea(g); return g[0].Kpis.Single(k => k.Key == "work.costPerArea").Value; }

        Assert.Equal(2.5m, Result(Groups(1000, 400)));
        Assert.Null(Result(Groups(1000, 0)));
        Assert.Null(Result(Groups(1000, null)));
        Assert.Null(Result(Groups(null, 400)));
        var failed = new List<ReportGroupDto> { new("work", [new Kpi("work.cost", "work", 5, KpiUnit.Money)], [], false), new("facilities", [], [], true) };
        Assert.Null(Result(failed));
    }
}
