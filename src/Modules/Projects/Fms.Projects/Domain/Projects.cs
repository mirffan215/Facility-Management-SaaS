using System.ComponentModel.DataAnnotations;
using Fms.BuildingBlocks.Tenancy;

namespace Fms.Projects.Domain;

public enum ProjectStatus { Planning = 1, Active = 2, OnHold = 3, Completed = 4, Cancelled = 5 }

public enum BudgetCategory { Design = 1, Construction = 2, Equipment = 3, Furniture = 4, Professional = 5, Contingency = 6, Other = 7 }

public enum WorkItemStatus { ToDo = 1, InProgress = 2, Blocked = 3, Done = 4 }

public enum LinkKind { WorkOrder = 1, PurchaseOrder = 2 }

/// <summary>A capital works, fit-out or improvement project with a budget, a plan and the work and orders that spend it.</summary>
public sealed class Project : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public ProjectStatus Status { get; set; } = ProjectStatus.Planning;
    public Guid? ManagerId { get; set; }
    public string? ManagerName { get; set; }
    public Guid? LocationId { get; set; }
    public string? LocationName { get; set; }
    public DateOnly StartOn { get; set; }
    public DateOnly EndOn { get; set; }
    public DateOnly? ClosedOn { get; set; }
    public string? CloseReason { get; set; }

    /// <summary>Money in work orders and purchase orders is added up only when it is in this currency.</summary>
    public string CurrencyCode { get; set; } = "SAR";

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

public sealed class Phase : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
}

public sealed class ProjectTask : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid? PhaseId { get; set; }
    public string Title { get; set; } = "";
    public Guid? AssigneeId { get; set; }
    public string? AssigneeName { get; set; }
    public WorkItemStatus Status { get; set; } = WorkItemStatus.ToDo;
    public DateOnly? DueOn { get; set; }
    public DateOnly? DoneOn { get; set; }
    public string? Notes { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

public sealed class Milestone : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = "";
    public DateOnly DueOn { get; set; }
    public DateOnly? AchievedOn { get; set; }
    public Guid? AchievedBy { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

public sealed class BudgetLine : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ProjectId { get; set; }
    public BudgetCategory Category { get; set; }
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>A work order or purchase order charged to the project. A work order or order belongs to at most one project at a time; unlinking keeps the record.</summary>
public sealed class ProjectLink : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ProjectId { get; set; }
    public LinkKind Kind { get; set; }
    public Guid RefId { get; set; }
    public string RefNumber { get; set; } = "";
    public BudgetCategory Category { get; set; }
    public DateTimeOffset LinkedAt { get; set; }
    public Guid? LinkedBy { get; set; }
    public DateTimeOffset? UnlinkedAt { get; set; }
    public Guid? UnlinkedBy { get; set; }
    public string? UnlinkReason { get; set; }
}

public sealed record CostRow(BudgetCategory Category, decimal Budget, decimal Spent, decimal Committed)
{
    public decimal Remaining => Budget - Spent - Committed;
}

/// <summary>Adds up a project's money. Pure, so the arithmetic is tested without a database.</summary>
public static class ProjectMath
{
    /// <summary>A purchase order is spent as it arrives and committed for what is still to arrive; a cancelled one is neither.</summary>
    public static (decimal Spent, decimal Committed) ForOrder(decimal ordered, decimal received, bool cancelled)
    {
        if (cancelled) return (0, 0);
        var spent = Math.Min(received, ordered);
        return (spent, Math.Max(ordered - spent, 0));
    }

    public static IReadOnlyList<CostRow> Rows(IEnumerable<(BudgetCategory Category, decimal Amount)> budget, IEnumerable<(BudgetCategory Category, decimal Spent, decimal Committed)> usage)
    {
        var b = budget.GroupBy(x => x.Category).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
        var u = usage.GroupBy(x => x.Category).ToDictionary(g => g.Key, g => (Spent: g.Sum(x => x.Spent), Committed: g.Sum(x => x.Committed)));
        return b.Keys.Union(u.Keys).Order().Select(c => new CostRow(c, b.GetValueOrDefault(c), u.GetValueOrDefault(c).Spent, u.GetValueOrDefault(c).Committed)).ToList();
    }

    public static int PercentDone(int done, int total) => total == 0 ? 0 : (int)Math.Round(100.0 * done / total);

    /// <summary>The statuses a project may move to from where it is.</summary>
    public static IReadOnlyList<ProjectStatus> NextStatuses(ProjectStatus s) => s switch
    {
        ProjectStatus.Planning => [ProjectStatus.Active, ProjectStatus.Cancelled],
        ProjectStatus.Active => [ProjectStatus.OnHold, ProjectStatus.Completed, ProjectStatus.Cancelled],
        ProjectStatus.OnHold => [ProjectStatus.Active, ProjectStatus.Cancelled],
        _ => [],
    };

    public static bool IsClosed(ProjectStatus s) => s is ProjectStatus.Completed or ProjectStatus.Cancelled;
}
