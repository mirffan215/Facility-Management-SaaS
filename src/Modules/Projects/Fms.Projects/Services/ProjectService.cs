using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Projects.Domain;
using Fms.Projects.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Projects.Services;

/// <summary>Projects, their status, the work and orders charged to them, and what they have cost against budget.</summary>
public sealed class ProjectService(
    ProjectsDbContext db, IFacilityLookup facilities, IUserDirectory users, IWorkCostLookup workCosts, IPurchaseOrderLookup orders, ICurrentUser user, IAuditPort audit, TimeProvider clock)
{
    public const int MaxProjects = 2000;
    public const int MaxLinks = 300;

    public async Task<Page<ProjectSummaryDto>> ListAsync(ProjectStatus? status, string? q, int? page, int? pageSize, CancellationToken ct = default)
    {
        var query = db.Projects.AsNoTracking().AsQueryable();
        if (status is { } s) query = query.Where(p => p.Status == s);
        if (Common.Clean(q) is { } text) query = query.Where(p => p.Code.Contains(text) || p.Name.Contains(text) || (p.ManagerName != null && p.ManagerName.Contains(text)));
        var total = await query.CountAsync(ct);
        var (number, size) = Page<ProjectSummaryDto>.Normalize(page, pageSize);
        var rows = await query.OrderBy(p => p.Status == ProjectStatus.Completed || p.Status == ProjectStatus.Cancelled).ThenByDescending(p => p.CreatedAt).Skip((number - 1) * size).Take(size).ToListAsync(ct);
        var ids = rows.Select(p => p.Id).ToList();
        var budget = await db.BudgetLines.AsNoTracking().Where(b => ids.Contains(b.ProjectId)).GroupBy(b => b.ProjectId).Select(g => new { Id = g.Key, Sum = g.Sum(x => x.Amount) }).ToDictionaryAsync(x => x.Id, x => x.Sum, ct);
        var tasks = await db.Tasks.AsNoTracking().Where(t => ids.Contains(t.ProjectId)).GroupBy(t => t.ProjectId).Select(g => new { Id = g.Key, Total = g.Count(), Done = g.Count(x => x.Status == WorkItemStatus.Done) }).ToDictionaryAsync(x => x.Id, ct);
        var today = Common.Today(clock);
        var late = await db.Milestones.AsNoTracking().Where(m => ids.Contains(m.ProjectId) && m.AchievedOn == null && m.DueOn < today).GroupBy(m => m.ProjectId).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);
        return new Page<ProjectSummaryDto>(rows.Select(p =>
        {
            var t = tasks.GetValueOrDefault(p.Id);
            return new ProjectSummaryDto(p.Id, p.Code, p.Name, p.Status, p.ManagerName, p.LocationName, p.StartOn, p.EndOn, p.CurrencyCode, budget.GetValueOrDefault(p.Id), t?.Done ?? 0, t?.Total ?? 0, ProjectMath.PercentDone(t?.Done ?? 0, t?.Total ?? 0),
                late.GetValueOrDefault(p.Id), !ProjectMath.IsClosed(p.Status) && p.EndOn < today);
        }).ToList(), total, number, size);
    }

    public async Task<Result<ProjectDetailDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var p = await db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return Result<ProjectDetailDto>.NotFound();
        return Result<ProjectDetailDto>.Ok(await DetailAsync(p, ct));
    }

    public async Task<Result<ProjectDetailDto>> CreateAsync(SaveProjectRequest r, CancellationToken ct = default)
    {
        if (await db.Projects.CountAsync(ct) >= MaxProjects) return Result<ProjectDetailDto>.Conflict($"At most {MaxProjects} projects.");
        var p = new Project { CreatedAt = clock.GetUtcNow(), CreatedBy = user.UserId };
        var invalid = await ApplyAsync(p, r, ct);
        if (invalid is not null) return invalid;
        if (await db.Projects.AnyAsync(x => x.Code == p.Code, ct)) return Result<ProjectDetailDto>.Conflict($"A project with code {p.Code} already exists.");
        db.Projects.Add(p);
        audit.Record("project.created", nameof(Project), p.Id.ToString(), JsonSerializer.Serialize(new { p.Code, p.Name }));
        await db.SaveChangesAsync(ct);
        return Result<ProjectDetailDto>.Ok(await DetailAsync(p, ct));
    }

    public async Task<Result<ProjectDetailDto>> UpdateAsync(Guid id, SaveProjectRequest r, CancellationToken ct = default)
    {
        var p = await db.Projects.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return Result<ProjectDetailDto>.NotFound();
        if (ProjectMath.IsClosed(p.Status)) return Result<ProjectDetailDto>.Conflict("A completed or cancelled project cannot be changed.");
        if (!Common.TryVersion(r.RowVersion, out var version)) return Result<ProjectDetailDto>.Invalid("rowVersion", "Reload and try again.");
        db.Entry(p).Property(x => x.RowVersion).OriginalValue = version;
        var invalid = await ApplyAsync(p, r, ct);
        if (invalid is not null) return invalid;
        if (await db.Projects.AnyAsync(x => x.Code == p.Code && x.Id != id, ct)) { db.ChangeTracker.Clear(); return Result<ProjectDetailDto>.Conflict($"A project with code {p.Code} already exists."); }
        audit.Record("project.updated", nameof(Project), id.ToString(), JsonSerializer.Serialize(new { p.Code, p.Name }));
        return await SaveAsync(p, ct);
    }

    public async Task<Result<ProjectDetailDto>> ChangeStatusAsync(Guid id, ChangeStatusRequest r, CancellationToken ct = default)
    {
        var p = await db.Projects.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return Result<ProjectDetailDto>.NotFound();
        if (!Common.TryVersion(r.RowVersion, out var version)) return Result<ProjectDetailDto>.Invalid("rowVersion", "Reload and try again.");
        db.Entry(p).Property(x => x.RowVersion).OriginalValue = version;
        if (r.Status is not { } to || !Enum.IsDefined(to)) return Result<ProjectDetailDto>.Invalid("status", "Choose a status.");
        if (!ProjectMath.NextStatuses(p.Status).Contains(to)) return Result<ProjectDetailDto>.Conflict($"A project that is {p.Status} cannot become {to}.");
        var reason = Common.Clean(r.Reason);
        if (reason is { Length: > 500 }) return Result<ProjectDetailDto>.Invalid("reason", "At most 500 characters.");
        if (to is ProjectStatus.Cancelled or ProjectStatus.OnHold && reason is null) return Result<ProjectDetailDto>.Invalid("reason", "A reason is required.");

        if (to == ProjectStatus.Completed)
        {
            var openTasks = await db.Tasks.CountAsync(t => t.ProjectId == id && t.Status != WorkItemStatus.Done, ct);
            var openMilestones = await db.Milestones.CountAsync(m => m.ProjectId == id && m.AchievedOn == null, ct);
            if (openTasks > 0 || openMilestones > 0) return Result<ProjectDetailDto>.Conflict($"Finish the {openTasks} open task(s) and {openMilestones} milestone(s) first.");
        }

        var from = p.Status;
        p.Status = to;
        if (ProjectMath.IsClosed(to)) { p.ClosedOn = Common.Today(clock); p.CloseReason = reason; }
        audit.Record("project.status_changed", nameof(Project), id.ToString(), JsonSerializer.Serialize(new { from, to, reason }));
        return await SaveAsync(p, ct);
    }

    // ---- links

    public async Task<Result<LinkDto>> LinkAsync(Guid projectId, LinkRequest r, CancellationToken ct = default)
    {
        var p = await db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == projectId, ct);
        if (p is null) return Result<LinkDto>.NotFound();
        if (ProjectMath.IsClosed(p.Status)) return Result<LinkDto>.Conflict("A completed or cancelled project cannot take new work or orders.");
        var errors = new ValidationErrors();
        if (r.Kind is not { } kind || !Enum.IsDefined(kind)) errors.Add("kind", "Choose work order or purchase order.");
        var number = Common.Clean(r.Number);
        if (number is null || number.Length > 30) errors.Add("number", "Enter the number, such as WO-000012 or PO-000004.");
        if (r.Category is not { } category || !Enum.IsDefined(category)) errors.Add("category", "Choose the budget category it is charged to.");
        if (errors.Any) return errors.ToResult<LinkDto>();

        Guid refId;
        string refNumber;
        if (r.Kind == LinkKind.WorkOrder)
        {
            var wo = await workCosts.FindByNumberAsync(number!, ct);
            if (wo is null) return Result<LinkDto>.Invalid("number", "No work order has that number.");
            (refId, refNumber) = (wo.Id, wo.Number);
        }
        else
        {
            var po = await orders.FindByNumberAsync(number!, ct);
            if (po is null) return Result<LinkDto>.Invalid("number", "No purchase order has that number.");
            (refId, refNumber) = (po.Id, po.Number);
        }

        if (await db.Links.CountAsync(l => l.ProjectId == projectId && l.UnlinkedAt == null, ct) >= MaxLinks) return Result<LinkDto>.Conflict($"At most {MaxLinks} items per project.");
        var existing = await db.Links.AsNoTracking().FirstOrDefaultAsync(l => l.Kind == r.Kind && l.RefId == refId && l.UnlinkedAt == null, ct);
        if (existing is not null) return Result<LinkDto>.Conflict(existing.ProjectId == projectId ? $"{refNumber} is already on this project." : $"{refNumber} already belongs to another project. Unlink it there first.");

        var link = new ProjectLink { ProjectId = projectId, Kind = r.Kind!.Value, RefId = refId, RefNumber = refNumber, Category = r.Category!.Value, LinkedAt = clock.GetUtcNow(), LinkedBy = user.UserId };
        db.Links.Add(link);
        audit.Record("project.linked", nameof(ProjectLink), link.Id.ToString(), JsonSerializer.Serialize(new { projectId, link.Kind, link.RefNumber, link.Category }));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return Result<LinkDto>.Conflict($"{refNumber} was just linked to a project.");
        }

        return Result<LinkDto>.Ok(ToDto(link));
    }

    public async Task<Result<LinkDto>> UnlinkAsync(Guid linkId, UnlinkRequest r, CancellationToken ct = default)
    {
        var link = await db.Links.FirstOrDefaultAsync(l => l.Id == linkId && l.UnlinkedAt == null, ct);
        if (link is null) return Result<LinkDto>.NotFound();
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == link.ProjectId, ct);
        if (ProjectMath.IsClosed(project.Status)) return Result<LinkDto>.Conflict("A completed or cancelled project keeps what was charged to it.");
        var reason = Common.Clean(r.Reason);
        if (reason is null || reason.Length > 500) return Result<LinkDto>.Invalid("reason", "A reason is required (at most 500 characters).");
        link.UnlinkedAt = clock.GetUtcNow();
        link.UnlinkedBy = user.UserId;
        link.UnlinkReason = reason;
        audit.Record("project.unlinked", nameof(ProjectLink), linkId.ToString(), JsonSerializer.Serialize(new { link.ProjectId, link.RefNumber, reason }));
        await db.SaveChangesAsync(ct);
        return Result<LinkDto>.Ok(ToDto(link));
    }

    // ---- costs

    public async Task<Result<ProjectCostsDto>> CostsAsync(Guid projectId, CancellationToken ct = default)
    {
        var p = await db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == projectId, ct);
        if (p is null) return Result<ProjectCostsDto>.NotFound();
        var budget = await db.BudgetLines.AsNoTracking().Where(b => b.ProjectId == projectId).Select(b => new { b.Category, b.Amount }).ToListAsync(ct);
        var links = await db.Links.AsNoTracking().Where(l => l.ProjectId == projectId && l.UnlinkedAt == null).OrderBy(l => l.LinkedAt).ToListAsync(ct);
        var wo = await workCosts.GetAsync(links.Where(l => l.Kind == LinkKind.WorkOrder).Select(l => l.RefId), ct);
        var po = await orders.GetAsync(links.Where(l => l.Kind == LinkKind.PurchaseOrder).Select(l => l.RefId), ct);

        var items = new List<LinkCostDto>();
        var usage = new List<(BudgetCategory, decimal, decimal)>();
        foreach (var l in links)
        {
            string title, status, currency;
            decimal spent, committed;
            if (l.Kind == LinkKind.WorkOrder)
            {
                if (!wo.TryGetValue(l.RefId, out var w)) { items.Add(new LinkCostDto(l.Id, l.Kind, l.RefNumber, "", "Missing", l.Category, 0, 0, true, "No longer found.")); continue; }
                (title, status, currency, spent, committed) = (w.Title, w.Status, w.CurrencyCode, w.Total, 0);
            }
            else
            {
                if (!po.TryGetValue(l.RefId, out var o)) { items.Add(new LinkCostDto(l.Id, l.Kind, l.RefNumber, "", "Missing", l.Category, 0, 0, true, "No longer found.")); continue; }
                (spent, committed) = ProjectMath.ForOrder(o.Ordered, o.Received, o.IsCancelled);
                (title, status, currency) = (o.VendorName, o.Status, o.CurrencyCode);
            }

            if (!string.Equals(currency, p.CurrencyCode, StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new LinkCostDto(l.Id, l.Kind, l.RefNumber, title, status, l.Category, 0, 0, true, $"In {currency}, not {p.CurrencyCode}, so it is not added up."));
                continue;
            }

            items.Add(new LinkCostDto(l.Id, l.Kind, l.RefNumber, title, status, l.Category, spent, committed, false, null));
            usage.Add((l.Category, spent, committed));
        }

        var rows = ProjectMath.Rows(budget.Select(b => (b.Category, b.Amount)), usage);
        var (totalBudget, totalSpent, totalCommitted) = (rows.Sum(x => x.Budget), rows.Sum(x => x.Spent), rows.Sum(x => x.Committed));
        return Result<ProjectCostsDto>.Ok(new ProjectCostsDto(p.CurrencyCode, totalBudget, totalSpent, totalCommitted, totalBudget - totalSpent - totalCommitted, totalSpent + totalCommitted > totalBudget,
            rows.Select(x => new CostRowDto(x.Category, x.Budget, x.Spent, x.Committed, x.Remaining)).ToList(), items));
    }

    // ---- helpers

    private async Task<Result<ProjectDetailDto>?> ApplyAsync(Project p, SaveProjectRequest r, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var code = Common.Clean(r.Code)?.ToUpperInvariant();
        if (code is null || code.Length > 30 || code.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))) errors.Add("code", "A short code of letters, digits, - _ or . (at most 30).");
        var name = Common.Clean(r.Name);
        if (name is null || name.Length > 200) errors.Add("name", "Give the project a name (at most 200 characters).");
        var description = Common.Clean(r.Description);
        if (description is { Length: > 2000 }) errors.Add("description", "At most 2000 characters.");
        if (r.StartOn is not { } start) errors.Add("startOn", "Say when it starts.");
        if (r.EndOn is not { } end) errors.Add("endOn", "Say when it should finish.");
        else if (r.StartOn is { } s && end < s) errors.Add("endOn", "It cannot finish before it starts.");
        var currency = Common.Clean(r.CurrencyCode)?.ToUpperInvariant() ?? p.CurrencyCode;
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetter)) errors.Add("currencyCode", "Use a three-letter currency code, such as SAR.");
        else if (currency != p.CurrencyCode && await db.BudgetLines.AnyAsync(b => b.ProjectId == p.Id, ct)) errors.Add("currencyCode", "The currency cannot change once there is a budget.");
        FacilityRef? location = null;
        if (r.LocationId is { } lid && (location = await facilities.GetLocationAsync(lid, ct)) is null) errors.Add("locationId", "That place does not exist.");
        DirectoryUser? manager = null;
        if (r.ManagerId is { } mid && ((manager = (await users.GetAsync([mid], ct)).GetValueOrDefault(mid)) is null || !manager.IsActive)) errors.Add("managerId", "Choose an active person.");
        if (errors.Any) return errors.ToResult<ProjectDetailDto>();

        p.Code = code!;
        p.Name = name!;
        p.Description = description;
        p.StartOn = r.StartOn!.Value;
        p.EndOn = r.EndOn!.Value;
        p.CurrencyCode = currency;
        p.LocationId = location?.Id;
        p.LocationName = location?.Name;
        p.ManagerId = manager?.Id;
        p.ManagerName = manager is null ? null : Names.Of(manager);
        return null;
    }

    private async Task<Result<ProjectDetailDto>> SaveAsync(Project p, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<ProjectDetailDto>.Conflict("Someone else changed this. Reload and try again.");
        }

        return Result<ProjectDetailDto>.Ok(await DetailAsync(p, ct));
    }

    internal async Task<ProjectDetailDto> DetailAsync(Project p, CancellationToken ct)
    {
        var today = Common.Today(clock);
        var phases = await db.Phases.AsNoTracking().Where(x => x.ProjectId == p.Id).OrderBy(x => x.SortOrder).ThenBy(x => x.Name).ToListAsync(ct);
        var tasks = await db.Tasks.AsNoTracking().Where(x => x.ProjectId == p.Id).OrderBy(x => x.DueOn == null).ThenBy(x => x.DueOn).ThenBy(x => x.Title).ToListAsync(ct);
        var milestones = await db.Milestones.AsNoTracking().Where(x => x.ProjectId == p.Id).OrderBy(x => x.DueOn).ToListAsync(ct);
        var budget = await db.BudgetLines.AsNoTracking().Where(x => x.ProjectId == p.Id).OrderBy(x => x.Category).ThenBy(x => x.Description).ToListAsync(ct);
        var links = await db.Links.AsNoTracking().Where(x => x.ProjectId == p.Id && x.UnlinkedAt == null).OrderBy(x => x.LinkedAt).ToListAsync(ct);
        return new ProjectDetailDto(p.Id, p.Code, p.Name, p.Description, p.Status, ProjectMath.NextStatuses(p.Status), p.ManagerId, p.ManagerName, p.LocationId, p.LocationName, p.StartOn, p.EndOn, p.ClosedOn, p.CloseReason,
            p.CurrencyCode, ProjectMath.PercentDone(tasks.Count(t => t.Status == WorkItemStatus.Done), tasks.Count), Common.Version(p.RowVersion),
            phases.Select(x => new PhaseDto(x.Id, x.Name, x.SortOrder)).ToList(), tasks.Select(t => PlanService.ToDto(t, today)).ToList(), milestones.Select(m => PlanService.ToDto(m, today)).ToList(),
            budget.Select(b => new BudgetLineDto(b.Id, b.Category, b.Description, b.Amount, Common.Version(b.RowVersion))).ToList(), links.Select(ToDto).ToList());
    }

    private static LinkDto ToDto(ProjectLink l) => new(l.Id, l.Kind, l.RefId, l.RefNumber, l.Category, l.LinkedAt);
}
