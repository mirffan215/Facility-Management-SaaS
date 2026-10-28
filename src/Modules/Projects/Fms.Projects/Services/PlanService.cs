using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Projects.Domain;
using Fms.Projects.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Projects.Services;

/// <summary>The plan of a project: phases, tasks, milestones and the budget. Nothing here can be changed once the project is completed or cancelled.</summary>
public sealed class PlanService(ProjectsDbContext db, IUserDirectory users, IAuditPort audit, TimeProvider clock, ICurrentUser user)
{
    public const int MaxPerProject = 500;

    private async Task<(Project? Project, Result<T>? Failure)> OpenAsync<T>(Guid projectId, CancellationToken ct)
    {
        var p = await db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == projectId, ct);
        if (p is null) return (null, Result<T>.NotFound());
        if (ProjectMath.IsClosed(p.Status)) return (null, Result<T>.Conflict("A completed or cancelled project cannot be changed."));
        return (p, null);
    }

    // ---- phases

    public async Task<Result<PhaseDto>> AddPhaseAsync(Guid projectId, SavePhaseRequest r, CancellationToken ct = default)
    {
        var (p, fail) = await OpenAsync<PhaseDto>(projectId, ct);
        if (fail is not null) return fail;
        var name = Common.Clean(r.Name);
        if (name is null || name.Length > 200) return Result<PhaseDto>.Invalid("name", "Name the phase (at most 200 characters).");
        if (await db.Phases.CountAsync(x => x.ProjectId == projectId, ct) >= 50) return Result<PhaseDto>.Conflict("At most 50 phases.");
        var order = r.SortOrder ?? (await db.Phases.Where(x => x.ProjectId == projectId).MaxAsync(x => (int?)x.SortOrder, ct) ?? 0) + 1;
        var phase = new Phase { ProjectId = p!.Id, Name = name, SortOrder = order };
        db.Phases.Add(phase);
        audit.Record("project.phase_added", nameof(Phase), phase.Id.ToString(), JsonSerializer.Serialize(new { projectId, name }));
        await db.SaveChangesAsync(ct);
        return Result<PhaseDto>.Ok(new PhaseDto(phase.Id, phase.Name, phase.SortOrder));
    }

    public async Task<Result<PhaseDto>> UpdatePhaseAsync(Guid id, SavePhaseRequest r, CancellationToken ct = default)
    {
        var phase = await db.Phases.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (phase is null) return Result<PhaseDto>.NotFound();
        var (_, fail) = await OpenAsync<PhaseDto>(phase.ProjectId, ct);
        if (fail is not null) return fail;
        var name = Common.Clean(r.Name);
        if (name is null || name.Length > 200) return Result<PhaseDto>.Invalid("name", "Name the phase (at most 200 characters).");
        phase.Name = name;
        if (r.SortOrder is { } o) phase.SortOrder = o;
        audit.Record("project.phase_updated", nameof(Phase), id.ToString(), JsonSerializer.Serialize(new { name }));
        await db.SaveChangesAsync(ct);
        return Result<PhaseDto>.Ok(new PhaseDto(phase.Id, phase.Name, phase.SortOrder));
    }

    public async Task<Result<PhaseDto>> DeletePhaseAsync(Guid id, DeleteRequest r, CancellationToken ct = default)
    {
        var phase = await db.Phases.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (phase is null) return Result<PhaseDto>.NotFound();
        var (_, fail) = await OpenAsync<PhaseDto>(phase.ProjectId, ct);
        if (fail is not null) return fail;
        if (await db.Tasks.AnyAsync(t => t.PhaseId == id, ct)) return Result<PhaseDto>.Conflict("Move or remove its tasks first.");
        var reason = Common.Clean(r.Reason);
        if (reason is null || reason.Length > 500) return Result<PhaseDto>.Invalid("reason", "A reason is required (at most 500 characters).");
        db.Phases.Remove(phase);
        audit.Record("project.phase_deleted", nameof(Phase), id.ToString(), JsonSerializer.Serialize(new { phase.Name, reason }));
        await db.SaveChangesAsync(ct);
        return Result<PhaseDto>.Ok(new PhaseDto(phase.Id, phase.Name, phase.SortOrder));
    }

    // ---- tasks

    public async Task<Result<TaskDto>> AddTaskAsync(Guid projectId, SaveTaskRequest r, CancellationToken ct = default)
    {
        var (p, fail) = await OpenAsync<TaskDto>(projectId, ct);
        if (fail is not null) return fail;
        if (await db.Tasks.CountAsync(x => x.ProjectId == projectId, ct) >= MaxPerProject) return Result<TaskDto>.Conflict($"At most {MaxPerProject} tasks per project.");
        var t = new ProjectTask { ProjectId = p!.Id };
        var invalid = await ApplyTaskAsync(t, r, ct);
        if (invalid is not null) return invalid;
        if (t.Status == WorkItemStatus.Done) t.DoneOn = Common.Today(clock);
        db.Tasks.Add(t);
        audit.Record("project.task_added", nameof(ProjectTask), t.Id.ToString(), JsonSerializer.Serialize(new { projectId, t.Title }));
        await db.SaveChangesAsync(ct);
        return Result<TaskDto>.Ok(ToDto(t, Common.Today(clock)));
    }

    public async Task<Result<TaskDto>> UpdateTaskAsync(Guid id, SaveTaskRequest r, CancellationToken ct = default)
    {
        var t = await db.Tasks.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return Result<TaskDto>.NotFound();
        var (_, fail) = await OpenAsync<TaskDto>(t.ProjectId, ct);
        if (fail is not null) return fail;
        if (!Common.TryVersion(r.RowVersion, out var version)) return Result<TaskDto>.Invalid("rowVersion", "Reload and try again.");
        db.Entry(t).Property(x => x.RowVersion).OriginalValue = version;
        var before = t.Status;
        var invalid = await ApplyTaskAsync(t, r, ct);
        if (invalid is not null) return invalid;
        if (t.Status == WorkItemStatus.Done && before != WorkItemStatus.Done) t.DoneOn = Common.Today(clock);
        else if (t.Status != WorkItemStatus.Done) t.DoneOn = null;
        audit.Record("project.task_updated", nameof(ProjectTask), id.ToString(), JsonSerializer.Serialize(new { t.Title, from = before, to = t.Status }));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<TaskDto>.Conflict("Someone else changed this task. Reload and try again.");
        }

        return Result<TaskDto>.Ok(ToDto(t, Common.Today(clock)));
    }

    public async Task<Result<TaskDto>> DeleteTaskAsync(Guid id, DeleteRequest r, CancellationToken ct = default)
    {
        var t = await db.Tasks.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return Result<TaskDto>.NotFound();
        var (_, fail) = await OpenAsync<TaskDto>(t.ProjectId, ct);
        if (fail is not null) return fail;
        var reason = Common.Clean(r.Reason);
        if (reason is null || reason.Length > 500) return Result<TaskDto>.Invalid("reason", "A reason is required (at most 500 characters).");
        db.Tasks.Remove(t);
        audit.Record("project.task_deleted", nameof(ProjectTask), id.ToString(), JsonSerializer.Serialize(new { t.Title, reason }));
        await db.SaveChangesAsync(ct);
        return Result<TaskDto>.Ok(ToDto(t, Common.Today(clock)));
    }

    private async Task<Result<TaskDto>?> ApplyTaskAsync(ProjectTask t, SaveTaskRequest r, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var title = Common.Clean(r.Title);
        if (title is null || title.Length > 200) errors.Add("title", "Give the task a title (at most 200 characters).");
        var notes = Common.Clean(r.Notes);
        if (notes is { Length: > 1000 }) errors.Add("notes", "At most 1000 characters.");
        if (r.Status is { } s && !Enum.IsDefined(s)) errors.Add("status", "Unknown status.");
        if (r.PhaseId is { } phaseId && !await db.Phases.AnyAsync(x => x.Id == phaseId && x.ProjectId == t.ProjectId, ct)) errors.Add("phaseId", "That phase is not part of this project.");
        DirectoryUser? assignee = null;
        if (r.AssigneeId is { } aid && ((assignee = (await users.GetAsync([aid], ct)).GetValueOrDefault(aid)) is null || !assignee.IsActive)) errors.Add("assigneeId", "Choose an active person.");
        if (errors.Any) return errors.ToResult<TaskDto>();
        t.Title = title!;
        t.Notes = notes;
        t.PhaseId = r.PhaseId;
        t.DueOn = r.DueOn;
        t.AssigneeId = assignee?.Id;
        t.AssigneeName = assignee is null ? null : Names.Of(assignee);
        t.Status = r.Status ?? t.Status;
        return null;
    }

    // ---- milestones

    public async Task<Result<MilestoneDto>> AddMilestoneAsync(Guid projectId, SaveMilestoneRequest r, CancellationToken ct = default)
    {
        var (p, fail) = await OpenAsync<MilestoneDto>(projectId, ct);
        if (fail is not null) return fail;
        if (await db.Milestones.CountAsync(x => x.ProjectId == projectId, ct) >= 100) return Result<MilestoneDto>.Conflict("At most 100 milestones.");
        var m = new Milestone { ProjectId = p!.Id };
        var invalid = Apply(m, r);
        if (invalid is not null) return invalid;
        db.Milestones.Add(m);
        audit.Record("project.milestone_added", nameof(Milestone), m.Id.ToString(), JsonSerializer.Serialize(new { projectId, m.Name, m.DueOn }));
        await db.SaveChangesAsync(ct);
        return Result<MilestoneDto>.Ok(ToDto(m, Common.Today(clock)));
    }

    public async Task<Result<MilestoneDto>> UpdateMilestoneAsync(Guid id, SaveMilestoneRequest r, CancellationToken ct = default)
    {
        var m = await db.Milestones.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null) return Result<MilestoneDto>.NotFound();
        var (_, fail) = await OpenAsync<MilestoneDto>(m.ProjectId, ct);
        if (fail is not null) return fail;
        if (!Common.TryVersion(r.RowVersion, out var version)) return Result<MilestoneDto>.Invalid("rowVersion", "Reload and try again.");
        db.Entry(m).Property(x => x.RowVersion).OriginalValue = version;
        var invalid = Apply(m, r);
        if (invalid is not null) return invalid;
        audit.Record("project.milestone_updated", nameof(Milestone), id.ToString(), JsonSerializer.Serialize(new { m.Name, m.DueOn }));
        return await SaveAsync(m, ct);
    }

    public async Task<Result<MilestoneDto>> AchieveMilestoneAsync(Guid id, AchieveMilestoneRequest r, CancellationToken ct = default)
    {
        var m = await db.Milestones.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null) return Result<MilestoneDto>.NotFound();
        var (_, fail) = await OpenAsync<MilestoneDto>(m.ProjectId, ct);
        if (fail is not null) return fail;
        if (m.AchievedOn is not null) return Result<MilestoneDto>.Conflict("That milestone is already achieved.");
        if (!Common.TryVersion(r.RowVersion, out var version)) return Result<MilestoneDto>.Invalid("rowVersion", "Reload and try again.");
        db.Entry(m).Property(x => x.RowVersion).OriginalValue = version;
        m.AchievedOn = Common.Today(clock);
        m.AchievedBy = user.UserId;
        audit.Record("project.milestone_achieved", nameof(Milestone), id.ToString(), JsonSerializer.Serialize(new { m.Name, m.DueOn, m.AchievedOn }));
        return await SaveAsync(m, ct);
    }

    public async Task<Result<MilestoneDto>> DeleteMilestoneAsync(Guid id, DeleteRequest r, CancellationToken ct = default)
    {
        var m = await db.Milestones.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null) return Result<MilestoneDto>.NotFound();
        var (_, fail) = await OpenAsync<MilestoneDto>(m.ProjectId, ct);
        if (fail is not null) return fail;
        if (m.AchievedOn is not null) return Result<MilestoneDto>.Conflict("An achieved milestone stays on the record.");
        var reason = Common.Clean(r.Reason);
        if (reason is null || reason.Length > 500) return Result<MilestoneDto>.Invalid("reason", "A reason is required (at most 500 characters).");
        db.Milestones.Remove(m);
        audit.Record("project.milestone_deleted", nameof(Milestone), id.ToString(), JsonSerializer.Serialize(new { m.Name, reason }));
        await db.SaveChangesAsync(ct);
        return Result<MilestoneDto>.Ok(ToDto(m, Common.Today(clock)));
    }

    private static Result<MilestoneDto>? Apply(Milestone m, SaveMilestoneRequest r)
    {
        var errors = new ValidationErrors();
        var name = Common.Clean(r.Name);
        if (name is null || name.Length > 200) errors.Add("name", "Name the milestone (at most 200 characters).");
        if (r.DueOn is null) errors.Add("dueOn", "Say when it is due.");
        if (errors.Any) return errors.ToResult<MilestoneDto>();
        m.Name = name!;
        m.DueOn = r.DueOn!.Value;
        return null;
    }

    private async Task<Result<MilestoneDto>> SaveAsync(Milestone m, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<MilestoneDto>.Conflict("Someone else changed this. Reload and try again.");
        }

        return Result<MilestoneDto>.Ok(ToDto(m, Common.Today(clock)));
    }

    // ---- budget

    public async Task<Result<BudgetLineDto>> AddBudgetAsync(Guid projectId, SaveBudgetLineRequest r, CancellationToken ct = default)
    {
        var (p, fail) = await OpenAsync<BudgetLineDto>(projectId, ct);
        if (fail is not null) return fail;
        if (await db.BudgetLines.CountAsync(x => x.ProjectId == projectId, ct) >= MaxPerProject) return Result<BudgetLineDto>.Conflict($"At most {MaxPerProject} budget lines.");
        var b = new BudgetLine { ProjectId = p!.Id };
        var invalid = Apply(b, r);
        if (invalid is not null) return invalid;
        db.BudgetLines.Add(b);
        audit.Record("project.budget_added", nameof(BudgetLine), b.Id.ToString(), JsonSerializer.Serialize(new { projectId, b.Category, b.Description, b.Amount }));
        await db.SaveChangesAsync(ct);
        return Result<BudgetLineDto>.Ok(ToDto(b));
    }

    public async Task<Result<BudgetLineDto>> UpdateBudgetAsync(Guid id, SaveBudgetLineRequest r, CancellationToken ct = default)
    {
        var b = await db.BudgetLines.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (b is null) return Result<BudgetLineDto>.NotFound();
        var (_, fail) = await OpenAsync<BudgetLineDto>(b.ProjectId, ct);
        if (fail is not null) return fail;
        if (!Common.TryVersion(r.RowVersion, out var version)) return Result<BudgetLineDto>.Invalid("rowVersion", "Reload and try again.");
        db.Entry(b).Property(x => x.RowVersion).OriginalValue = version;
        var before = new { b.Category, b.Description, b.Amount };
        var invalid = Apply(b, r);
        if (invalid is not null) return invalid;
        audit.Record("project.budget_changed", nameof(BudgetLine), id.ToString(), JsonSerializer.Serialize(new { before, after = new { b.Category, b.Description, b.Amount } }));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<BudgetLineDto>.Conflict("Someone else changed this. Reload and try again.");
        }

        return Result<BudgetLineDto>.Ok(ToDto(b));
    }

    public async Task<Result<BudgetLineDto>> DeleteBudgetAsync(Guid id, DeleteRequest r, CancellationToken ct = default)
    {
        var b = await db.BudgetLines.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (b is null) return Result<BudgetLineDto>.NotFound();
        var (_, fail) = await OpenAsync<BudgetLineDto>(b.ProjectId, ct);
        if (fail is not null) return fail;
        var reason = Common.Clean(r.Reason);
        if (reason is null || reason.Length > 500) return Result<BudgetLineDto>.Invalid("reason", "A reason is required (at most 500 characters).");
        db.BudgetLines.Remove(b);
        audit.Record("project.budget_removed", nameof(BudgetLine), id.ToString(), JsonSerializer.Serialize(new { b.Category, b.Description, b.Amount, reason }));
        await db.SaveChangesAsync(ct);
        return Result<BudgetLineDto>.Ok(ToDto(b));
    }

    private static Result<BudgetLineDto>? Apply(BudgetLine b, SaveBudgetLineRequest r)
    {
        var errors = new ValidationErrors();
        if (r.Category is not { } c || !Enum.IsDefined(c)) errors.Add("category", "Choose a category.");
        var description = Common.Clean(r.Description);
        if (description is null || description.Length > 200) errors.Add("description", "Describe it (at most 200 characters).");
        if (r.Amount is not { } amount || amount < 0 || amount > 1_000_000_000_000m) errors.Add("amount", "Enter an amount of zero or more.");
        if (errors.Any) return errors.ToResult<BudgetLineDto>();
        b.Category = r.Category!.Value;
        b.Description = description!;
        b.Amount = Math.Round(r.Amount!.Value, 2);
        return null;
    }

    internal static TaskDto ToDto(ProjectTask t, DateOnly today) =>
        new(t.Id, t.PhaseId, t.Title, t.AssigneeId, t.AssigneeName, t.Status, t.DueOn, t.DoneOn, t.Notes, t.Status != WorkItemStatus.Done && t.DueOn is { } d && d < today, Common.Version(t.RowVersion));

    internal static MilestoneDto ToDto(Milestone m, DateOnly today) => new(m.Id, m.Name, m.DueOn, m.AchievedOn, m.AchievedOn is null && m.DueOn < today, Common.Version(m.RowVersion));

    private static BudgetLineDto ToDto(BudgetLine b) => new(b.Id, b.Category, b.Description, b.Amount, Common.Version(b.RowVersion));
}
