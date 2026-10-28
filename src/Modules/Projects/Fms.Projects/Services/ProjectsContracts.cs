using Fms.Projects.Domain;

namespace Fms.Projects.Services;

public sealed record ProjectSummaryDto(
    Guid Id, string Code, string Name, ProjectStatus Status, string? ManagerName, string? LocationName, DateOnly StartOn, DateOnly EndOn, string CurrencyCode, decimal Budget, int TasksDone, int TasksTotal, int PercentDone,
    int MilestonesLate, bool EndOverdue);

public sealed record PhaseDto(Guid Id, string Name, int SortOrder);

public sealed record TaskDto(Guid Id, Guid? PhaseId, string Title, Guid? AssigneeId, string? AssigneeName, WorkItemStatus Status, DateOnly? DueOn, DateOnly? DoneOn, string? Notes, bool IsOverdue, string RowVersion);

public sealed record MilestoneDto(Guid Id, string Name, DateOnly DueOn, DateOnly? AchievedOn, bool IsLate, string RowVersion);

public sealed record BudgetLineDto(Guid Id, BudgetCategory Category, string Description, decimal Amount, string RowVersion);

public sealed record LinkDto(Guid Id, LinkKind Kind, Guid RefId, string RefNumber, BudgetCategory Category, DateTimeOffset LinkedAt);

public sealed record ProjectDetailDto(
    Guid Id, string Code, string Name, string? Description, ProjectStatus Status, IReadOnlyList<ProjectStatus> NextStatuses, Guid? ManagerId, string? ManagerName, Guid? LocationId, string? LocationName,
    DateOnly StartOn, DateOnly EndOn, DateOnly? ClosedOn, string? CloseReason, string CurrencyCode, int PercentDone, string RowVersion,
    IReadOnlyList<PhaseDto> Phases, IReadOnlyList<TaskDto> Tasks, IReadOnlyList<MilestoneDto> Milestones, IReadOnlyList<BudgetLineDto> Budget, IReadOnlyList<LinkDto> Links);

public sealed record SaveProjectRequest(string? Code, string? Name, string? Description, Guid? ManagerId, Guid? LocationId, DateOnly? StartOn, DateOnly? EndOn, string? CurrencyCode, string? RowVersion);

public sealed record ChangeStatusRequest(ProjectStatus? Status, string? Reason, string? RowVersion);

public sealed record SavePhaseRequest(string? Name, int? SortOrder);

public sealed record SaveTaskRequest(Guid? PhaseId, string? Title, Guid? AssigneeId, DateOnly? DueOn, string? Notes, WorkItemStatus? Status, string? RowVersion);

public sealed record SaveMilestoneRequest(string? Name, DateOnly? DueOn, string? RowVersion);

public sealed record AchieveMilestoneRequest(string? RowVersion);

public sealed record SaveBudgetLineRequest(BudgetCategory? Category, string? Description, decimal? Amount, string? RowVersion);

public sealed record LinkRequest(LinkKind? Kind, string? Number, BudgetCategory? Category);

public sealed record UnlinkRequest(string? Reason);

public sealed record DeleteRequest(string? Reason);

public sealed record CostRowDto(BudgetCategory Category, decimal Budget, decimal Spent, decimal Committed, decimal Remaining);

public sealed record LinkCostDto(Guid LinkId, LinkKind Kind, string Number, string Title, string Status, BudgetCategory Category, decimal Spent, decimal Committed, bool Excluded, string? ExcludedBecause);

public sealed record ProjectCostsDto(
    string CurrencyCode, decimal Budget, decimal Spent, decimal Committed, decimal Remaining, bool OverBudget, IReadOnlyList<CostRowDto> ByCategory, IReadOnlyList<LinkCostDto> Items);
