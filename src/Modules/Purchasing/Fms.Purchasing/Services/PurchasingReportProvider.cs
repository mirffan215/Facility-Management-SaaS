using Fms.BuildingBlocks.Reporting;
using Fms.BuildingBlocks.Security;
using Fms.Purchasing.Domain;
using Fms.Purchasing.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Purchasing.Services;

/// <summary>Requests, approvals and orders. Purchasing is not kept by site, so the site filter does not apply. Values are in each order's own currency; one currency per tenant is assumed.</summary>
public sealed class PurchasingReportProvider(PurchasingDbContext db) : IReportProvider
{
    public string Group => "purchasing";
    public string Permission => Permissions.PurchasingRead;

    public async Task<ReportSection> BuildAsync(ReportFilter f, CancellationToken ct = default)
    {
        var requests = await db.Requests.AsNoTracking().Where(r => r.RequestedAt >= f.FromAt && r.RequestedAt < f.ToAt && r.Status != RequestStatus.Draft)
            .Select(r => new { r.Status, r.Total, r.SubmittedAt, r.DecidedAt, r.RequestedAt }).ToListAsync(ct);
        var waiting = await db.Requests.AsNoTracking().CountAsync(r => r.Status == RequestStatus.Submitted, ct);
        var orders = await db.Orders.AsNoTracking().Where(o => o.IssuedAt >= f.FromAt && o.IssuedAt < f.ToAt && o.Status != OrderStatus.Cancelled).Select(o => new { o.Total, o.IssuedAt, o.VendorName, o.Status }).ToListAsync(ct);
        var openOrders = await db.Orders.AsNoTracking().CountAsync(o => o.Status == OrderStatus.Issued || o.Status == OrderStatus.Acknowledged || o.Status == OrderStatus.PartiallyReceived, ct);

        var decided = requests.Where(r => r.SubmittedAt != null && r.DecidedAt != null).Select(r => (decimal)(r.DecidedAt!.Value - r.SubmittedAt!.Value).TotalHours).ToList();
        var approved = requests.Count(r => r.Status is RequestStatus.Approved or RequestStatus.Ordered);
        var rejected = requests.Count(r => r.Status == RequestStatus.Rejected);

        return new ReportSection(
        [
            new("purchasing.requests", Group, requests.Count, KpiUnit.Count),
            new("purchasing.waiting", Group, waiting, KpiUnit.Count),
            new("purchasing.approvalRate", Group, ReportMath.Percent(approved, approved + rejected), KpiUnit.Percent, $"{approved + rejected}"),
            new("purchasing.approvalTime", Group, ReportMath.Average(decided), KpiUnit.Hours, "submitted to decided"),
            new("purchasing.orderValue", Group, Math.Round(orders.Sum(o => o.Total), 2), KpiUnit.Money),
            new("purchasing.openOrders", Group, openOrders, KpiUnit.Count),
        ],
        [
            new("purchasing.ordersByWeek", Group, KpiUnit.Count, ReportMath.Weekly(orders.Select(o => DateOnly.FromDateTime(o.IssuedAt.UtcDateTime)), f.From, f.To)),
            new("purchasing.spendByVendor", Group, KpiUnit.Money, orders.GroupBy(o => o.VendorName).Select(g => new SeriesPoint(g.Key, Math.Round(g.Sum(o => o.Total), 2))).OrderByDescending(p => p.Value).Take(10).ToList()),
        ]);
    }
}
