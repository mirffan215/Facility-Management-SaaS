using System.Globalization;
using System.Text;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Reporting;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Microsoft.Extensions.Logging;

namespace Fms.Reporting.Services;

public sealed record ReportGroupDto(string Group, IReadOnlyList<Kpi> Kpis, IReadOnlyList<Series> Series, bool Failed);

public sealed record DashboardDto(DateOnly From, DateOnly To, Guid? SiteId, string? SiteName, DateTimeOffset GeneratedAt, IReadOnlyList<ReportGroupDto> Groups);

/// <summary>
/// The KPI pack. Asks every module's provider the caller may see and puts the answers side by side, plus the few figures that need two modules (cost per square metre).
/// One module failing does not blank the dashboard: its group is marked failed and the others still show.
/// </summary>
public sealed class ReportService(IEnumerable<IReportProvider> providers, ICurrentPermissions permissions, IFacilityLookup facilities, TimeProvider clock, ILogger<ReportService> log)
{
    public const int DefaultDays = 30;

    public async Task<Result<DashboardDto>> DashboardAsync(DateOnly? from, DateOnly? to, Guid? siteId, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var end = to ?? today;
        var start = from ?? end.AddDays(-(DefaultDays - 1));
        if (end < start) return Result<DashboardDto>.Invalid("to", "The end cannot be before the start.");
        if (end.DayNumber - start.DayNumber + 1 > ReportMath.MaxDays) return Result<DashboardDto>.Invalid("to", $"A report covers at most {ReportMath.MaxDays} days.");

        IReadOnlyCollection<Guid>? places = null;
        string? siteName = null;
        if (siteId is { } site)
        {
            var tree = await facilities.LocationsUnderAsync(site, ct);
            if (tree.Count == 0 || tree[0].Kind != "Site") return Result<DashboardDto>.Invalid("siteId", "Choose one of your sites.");
            places = tree.Select(l => l.Id).ToList();
            siteName = tree[0].Name;
        }

        var filter = new ReportFilter(start, end, siteId, places);
        var granted = await permissions.GetAsync(ct);
        var groups = new List<ReportGroupDto>();
        foreach (var p in providers.Where(p => granted.Contains(p.Permission)).OrderBy(p => Order(p.Group)))
        {
            try
            {
                var section = await p.BuildAsync(filter, ct);
                groups.Add(new ReportGroupDto(p.Group, section.Kpis, section.Series, false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "The {Group} report could not be built", p.Group);
                groups.Add(new ReportGroupDto(p.Group, [], [], true));
            }
        }

        AddCostPerArea(groups);
        return Result<DashboardDto>.Ok(new DashboardDto(start, end, siteId, siteName, clock.GetUtcNow(), groups));
    }

    /// <summary>Work cost in the period over the estate's floor area, when both are known. Shown with the work figures.</summary>
    public static void AddCostPerArea(List<ReportGroupDto> groups)
    {
        var work = groups.FirstOrDefault(g => g.Group == "work" && !g.Failed);
        var estate = groups.FirstOrDefault(g => g.Group == "facilities" && !g.Failed);
        var cost = work?.Kpis.FirstOrDefault(k => k.Key == "work.cost")?.Value;
        var area = estate?.Kpis.FirstOrDefault(k => k.Key == "facilities.area")?.Value;
        var value = cost is { } c && area is > 0 ? Math.Round(c / area.Value, 2) : (decimal?)null;
        if (work is null) return;
        var index = groups.IndexOf(work);
        groups[index] = work with { Kpis = [.. work.Kpis, new Kpi("work.costPerArea", "work", value, KpiUnit.Money, "work cost per square metre in the period")] };
    }

    private static int Order(string group) => group switch { "work" => 0, "facilities" => 1, "inventory" => 2, "purchasing" => 3, "vendors" => 4, "spaces" => 5, "bookings" => 6, "visitors" => 7, _ => 9 };

    /// <summary>The dashboard as a spreadsheet: one row per figure, then one row per chart point. Cells that start with a formula character are neutralised.</summary>
    public static string ToCsv(DashboardDto d)
    {
        var sb = new StringBuilder("﻿");
        sb.Append("Group,Kind,Key,Label,Value,Unit,Detail\r\n");
        foreach (var g in d.Groups)
        {
            foreach (var k in g.Kpis) sb.Append(Row(g.Group, "kpi", k.Key, "", k.Value, k.Unit, k.Detail));
            foreach (var s in g.Series)
                foreach (var p in s.Points) sb.Append(Row(g.Group, "series", s.Key, p.Label, p.Value, s.Unit, null));
        }

        return sb.ToString();
    }

    private static string Row(string group, string kind, string key, string label, decimal? value, string unit, string? detail) =>
        string.Join(',', Cell(group), Cell(kind), Cell(key), Cell(label), value is { } v ? v.ToString(CultureInfo.InvariantCulture) : "", Cell(unit), Cell(detail ?? "")) + "\r\n";

    public static string Cell(string text)
    {
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r') text = "'" + text;
        return text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r') ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }
}
