using Fms.Work.Domain;

namespace Fms.Work.Services;

public sealed record WorkOrderDto(
    Guid Id, int Number, string DisplayNumber, string Title, string? Description, WorkOrderType Type, WorkPriority Priority, WorkOrderStatus Status,
    Guid LocationId, string? LocationName, Guid? AssetId, string? AssetName, Guid? CategoryId, string? CategoryName,
    Guid RequestedBy, string? RequestedByName, DateTimeOffset RequestedAt, Guid? AssignedTo, string? AssignedToName,
    DateTimeOffset? DueAt, bool IsOverdue, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, DateTimeOffset? VerifiedAt, DateTimeOffset? ClosedAt,
    string? StatusReason, ClosureCode? ClosureCode, string? ClosureNotes, IReadOnlyList<WorkOrderAction> Allowed, string RowVersion);

public sealed record WorkOrderEventDto(Guid Id, WorkOrderAction Action, WorkOrderStatus? FromStatus, WorkOrderStatus ToStatus, Guid? ActorId, string? ActorName,
    DateTimeOffset At, string? Note, Guid? SubjectId, string? SubjectName);

public sealed record WorkOrderCommentDto(Guid Id, Guid AuthorId, string? AuthorName, DateTimeOffset At, string Body, bool IsInternal);

public sealed record WorkOrderDetailDto(WorkOrderDto Order, IReadOnlyList<WorkOrderEventDto> Events, IReadOnlyList<WorkOrderCommentDto> Comments);

public sealed record WorkOrderQuery(
    IReadOnlyList<WorkOrderStatus>? Statuses = null, WorkPriority? Priority = null, Guid? AssetId = null, Guid? LocationId = null, Guid? CategoryId = null,
    bool Mine = false, bool AssignedToMe = false, bool OpenOnly = false, bool OverdueOnly = false, string? Search = null, int? Page = null, int? PageSize = null);

public sealed record CreateWorkOrderRequest(
    string? Title, string? Description, WorkOrderType? Type, WorkPriority? Priority, Guid? LocationId, Guid? AssetId, Guid? CategoryId, DateTimeOffset? DueAt);

public sealed record UpdateWorkOrderRequest(
    string? Title, string? Description, WorkPriority? Priority, Guid? LocationId, Guid? AssetId, Guid? CategoryId, DateTimeOffset? DueAt, string? RowVersion);

public sealed record ReasonRequest(string? Reason);

public sealed record AssignRequest(Guid? AssigneeId);

public sealed record CompleteRequest(ClosureCode? Code, string? Notes);

public sealed record CommentRequest(string? Body, bool IsInternal = false);

public sealed record CategoryDto(Guid Id, string Code, string Name, string? NameAr, WorkPriority DefaultPriority, bool IsArchived, string RowVersion);

public sealed record SaveCategoryRequest(string? Code, string? Name, string? NameAr, WorkPriority? DefaultPriority, string? RowVersion);

public sealed record WorkSettingsDto(bool RequireApproval, bool RequireVerification, string? RowVersion);

public sealed record SaveWorkSettingsRequest(bool RequireApproval, bool RequireVerification, string? RowVersion);

public sealed record AssigneeDto(Guid Id, string? DisplayName, string? Email);

public sealed record AssetChoiceDto(Guid Id, string Code, string Name, Guid? LocationId);
