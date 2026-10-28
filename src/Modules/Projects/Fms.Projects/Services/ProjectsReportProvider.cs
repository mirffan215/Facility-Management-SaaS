using Fms.BuildingBlocks.Reporting;
using Fms.BuildingBlocks.Security;
using Fms.Projects.Domain;
using Fms.Projects.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Projects.Services;

/// <summary>Projects in flight and how they are going. Money is not added across projects (they may be in different currencies); budgets are summed only when one currency is in use.</summary>
public sealed class ProjectsReportProvider(ProjectsDbContext db, TimeProvider clock) : IReportProvider
{
    public string Group => "projects";
    public string Permission => Permissions.ProjectsRead;

    public async Task<ReportSection> BuildAsync(ReportFilter f, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var projects = db.Projects.AsNoTracking().AsQueryable();
        if (f.LocationIds is { } places) projects = projects.Where(p => p.LocationId != null && places.Contains(p.LocationId.Value));
        var all = await projects.Select(p => new { p.Id, p.Status, p.EndOn, p.CurrencyCode }).ToListAsync(ct);
        var ids = all.Select(p => p.Id).ToList();
        var open = all.Where(p => !ProjectMath.IsClosed(p.Status)).ToList();
        var lateMilestones = await db.Milestones.AsNoTracking().CountAsync(m => ids.Contains(m.ProjectId) && m.AchievedOn == null && m.DueOn < today, ct);
        var overdueTasks = await db.Tasks.AsNoTracking().CountAsync(t => ids.Contains(t.ProjectId) && t.Status != WorkItemStatus.Done && t.DueOn != null && t.DueOn < today, ct);
        var completedInPeriod = await projects.CountAsync(p => p.Status == ProjectStatus.Completed && p.ClosedOn != null && p.ClosedOn >= f.From && p.ClosedOn <= f.To, ct);
        var currencies = open.Select(p => p.CurrencyCode).Distinct().ToList();
        var openIds = open.Select(p => p.Id).ToList();
        var budget = currencies.Count == 1 ? await db.BudgetLines.AsNoTracking().Where(b => openIds.Contains(b.ProjectId)).SumAsync(b => (decimal?)b.Amount, ct) : null;
        return new ReportSection(
        [
            new("projects.open", Group, open.Count, KpiUnit.Count),
            new("projects.late", Group, open.Count(p => p.EndOn < today), KpiUnit.Count, "past their planned end"),
            new("projects.lateMilestones", Group, lateMilestones, KpiUnit.Count),
            new("projects.overdueTasks", Group, overdueTasks, KpiUnit.Count),
            new("projects.completed", Group, completedInPeriod, KpiUnit.Count),
            new("projects.budget", Group, budget, KpiUnit.Money, currencies.Count == 1 ? currencies[0] : null),
        ],
        [
            new("projects.byStatus", Group, KpiUnit.Count, all.GroupBy(p => p.Status).OrderBy(g => g.Key).Select(g => new SeriesPoint(g.Key.ToString(), g.Count())).ToList()),
        ]);
    }
}
