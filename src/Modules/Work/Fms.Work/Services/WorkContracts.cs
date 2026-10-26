using Fms.Work.Domain;

namespace Fms.Work.Services;

public sealed record WorkOrderDto(
    Guid Id, int Number, string DisplayNumber, string Title, string? Description, WorkOrderType Type, WorkPriority Priority, WorkOrderStatus Status,
    Guid LocationId, string? LocationName, Guid? AssetId, string? AssetName, Guid? CategoryId, string? CategoryName,
    Guid RequestedBy, string? RequestedByName, DateTimeOffset RequestedAt, Guid? AssignedTo, string? AssignedToName,
    DateTimeOffset? DueAt, bool IsOverdue, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, DateTimeOffset? VerifiedAt, DateTimeOffset? ClosedAt,
    string? StatusReason, ClosureCode? ClosureCode, string? ClosureNotes, IReadOnlyList<WorkOrderAction> Allowed, string RowVersion, SlaDto Sla);

public sealed record SlaDto(SlaState State, DateTimeOffset? ResponseDueAt, DateTimeOffset? ResolutionDueAt, DateTimeOffset? RespondedAt, bool ResponseBreached, bool Paused);

public sealed record WorkOrderEventDto(Guid Id, WorkOrderAction Action, WorkOrderStatus? FromStatus, WorkOrderStatus ToStatus, Guid? ActorId, string? ActorName,
    DateTimeOffset At, string? Note, Guid? SubjectId, string? SubjectName);

public sealed record WorkOrderCommentDto(Guid Id, Guid AuthorId, string? AuthorName, DateTimeOffset At, string Body, bool IsInternal);

public sealed record WorkOrderDetailDto(WorkOrderDto Order, IReadOnlyList<WorkOrderEventDto> Events, IReadOnlyList<WorkOrderCommentDto> Comments);

public sealed record WorkOrderQuery(
    IReadOnlyList<WorkOrderStatus>? Statuses = null, WorkPriority? Priority = null, Guid? AssetId = null, Guid? LocationId = null, Guid? CategoryId = null,
    bool Mine = false, bool AssignedToMe = false, bool OpenOnly = false, bool OverdueOnly = false, bool SlaBreached = false, string? Search = null, int? Page = null, int? PageSize = null);

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

public sealed record WorkSettingsDto(bool RequireApproval, bool RequireVerification, string? RowVersion, string CurrencyCode = "SAR", decimal LabourRatePerHour = 0, bool RequireCompletionSignature = false);

public sealed record SaveWorkSettingsRequest(bool RequireApproval, bool RequireVerification, string? RowVersion, string? CurrencyCode = null, decimal? LabourRatePerHour = null, bool? RequireCompletionSignature = null);

public sealed record AssigneeDto(Guid Id, string? DisplayName, string? Email);

public sealed record AssetChoiceDto(Guid Id, string Code, string Name, Guid? LocationId);

public sealed record DayHoursDto(string? Start, string? End);

public sealed record HolidayDto(DateOnly Date, string Name);

public sealed record CalendarDto(
    Guid Id, string Name, string TimeZoneId, IReadOnlyDictionary<string, DayHoursDto?> Hours, bool IsDefault, bool IsArchived,
    IReadOnlyList<HolidayDto> Holidays, IReadOnlyList<Guid> SiteIds, string RowVersion);

public sealed record SaveCalendarRequest(
    string? Name, string? TimeZoneId, Dictionary<string, DayHoursDto?>? Hours, bool IsDefault, IReadOnlyList<HolidayDto>? Holidays, IReadOnlyList<Guid>? SiteIds, string? RowVersion);

public sealed record SlaTargetDto(WorkPriority Priority, int ResponseMinutes, int ResolutionMinutes);

public sealed record SlaPolicyDto(Guid Id, string Name, Guid? CategoryId, string? CategoryName, bool IsDefault, bool IsArchived, IReadOnlyList<SlaTargetDto> Targets, string RowVersion);

public sealed record SaveSlaPolicyRequest(string? Name, Guid? CategoryId, bool IsDefault, IReadOnlyList<SlaTargetDto>? Targets, string? RowVersion);


// ---------------- checklists, time, costs, signatures, files ----------------

public sealed record TemplateItemDto(string Text, bool Required);

public sealed record ChecklistTemplateDto(Guid Id, string Name, string? NameAr, bool IsArchived, IReadOnlyList<TemplateItemDto> Items, string RowVersion);

public sealed record SaveChecklistTemplateRequest(string? Name, string? NameAr, IReadOnlyList<TemplateItemDto>? Items, string? RowVersion);

public sealed record ChecklistItemDto(Guid Id, int Position, string Text, bool Required, ChecklistResult Result, string? Note, string? ResultByName, DateTimeOffset? ResultAt);

public sealed record AttachChecklistRequest(Guid? TemplateId);

public sealed record SetChecklistResultRequest(ChecklistResult? Result, string? Note);

public sealed record TimeEntryDto(Guid Id, Guid WorkerId, string? WorkerName, DateOnly WorkedOn, int Minutes, decimal HourlyRate, decimal LabourCost, string? Note, DateTimeOffset RecordedAt, bool IsVoided, string? VoidReason);

public sealed record AddTimeRequest(DateOnly? WorkedOn, int? Minutes, string? Note);

public sealed record CostLineDto(Guid Id, CostKind Kind, string Description, string? Reference, decimal Quantity, decimal UnitCost, decimal Total, string? RecordedByName, DateTimeOffset RecordedAt, bool IsVoided, string? VoidReason);

public sealed record AddCostRequest(CostKind? Kind, string? Description, string? Reference, decimal? Quantity, decimal? UnitCost);

public sealed record CostSummaryDto(string CurrencyCode, int Minutes, decimal LabourCost, decimal Material, decimal Contractor, decimal Other, decimal Total);

public sealed record SignatureDto(Guid Id, SignatureRole Role, string SignerName, DateTimeOffset SignedAt, string? RecordedByName);

public sealed record AddSignatureRequest(SignatureRole? Role, string? SignerName, string? ImageBase64);

public sealed record AttachmentDto(Guid Id, AttachmentKind Kind, string FileName, string ContentType, long SizeBytes, string? Description, DateTimeOffset UploadedAt, string? UploadedByName, Guid UploadedBy);

public sealed record DownloadedFile(string FileName, string ContentType, Stream Content);

public sealed class WorkOptions
{
    public const string SectionName = "Work";

    public long MaxAttachmentBytes { get; set; } = 15L * 1024 * 1024;
    public int MaxAttachmentsPerOrder { get; set; } = 40;
    public int MaxSignatureBytes { get; set; } = 256 * 1024;
    public int MaxChecklistItems { get; set; } = 100;
}

// ---------------- offline bundle ----------------

/// <summary>Everything a technician's phone needs to work one job without a connection.</summary>
public sealed record OfflineJobDto(
    WorkOrderDto Order, IReadOnlyList<WorkOrderEventDto> Events, IReadOnlyList<WorkOrderCommentDto> Comments, IReadOnlyList<ChecklistItemDto> Checklist,
    IReadOnlyList<SignatureDto> Signatures, IReadOnlyList<AttachmentDto> Attachments, IReadOnlyList<TimeEntryDto> Time, IReadOnlyList<CostLineDto> Costs, string? CurrencyCode);

/// <summary>The jobs assigned to the signed-in person that are still open, with the server's clock so the phone can correct its own.</summary>
public sealed record OfflineJobsDto(DateTimeOffset ServerTime, IReadOnlyList<OfflineJobDto> Jobs, bool RequireCompletionSignature = false);

// ---------------- preventive maintenance ----------------

public sealed record PmTemplateDto(
    Guid Id, string Name, string? NameAr, string? Instructions, WorkPriority Priority, int? EstimatedMinutes, Guid? CategoryId, Guid? ChecklistTemplateId,
    string? ChecklistName, bool IsArchived, string RowVersion);

public sealed record SavePmTemplateRequest(
    string? Name, string? NameAr, string? Instructions, WorkPriority? Priority, int? EstimatedMinutes, Guid? CategoryId, Guid? ChecklistTemplateId, string? RowVersion);

public sealed record PmScheduleDto(
    Guid Id, string Name, Guid TemplateId, string? TemplateName, PmTargetKind TargetKind, Guid? AssetId, string? AssetName, Guid? AssetClassId, string? AssetClassName,
    PmTimeBasis TimeBasis, PmIntervalUnit IntervalUnit, int Interval, DateOnly StartOn, int LeadDays, string? MeterName, decimal? MeterInterval, Guid? AssignToUserId,
    string? AssignToName, bool IsArchived, int Assets, int Open, string RowVersion);

public sealed record SavePmScheduleRequest(
    string? Name, Guid? TemplateId, PmTargetKind? TargetKind, Guid? AssetId, Guid? AssetClassId, PmTimeBasis? TimeBasis, PmIntervalUnit? IntervalUnit, int? Interval,
    DateOnly? StartOn, int? LeadDays, string? MeterName, decimal? MeterInterval, Guid? AssignToUserId, string? RowVersion);

public sealed record PmPlanDto(
    Guid Id, Guid ScheduleId, Guid AssetId, string? AssetCode, string? AssetName, DateOnly? NextDueOn, DateOnly? LastCompletedOn, Guid? OpenWorkOrderId, bool IsActive,
    decimal? LastServiceMeterValue, decimal? CurrentMeterValue, bool MeterMissing);

public enum ForecastKind { Open = 1, Projected = 2, MeterEstimate = 3 }

/// <summary>One thing on the maintenance calendar: a work order already created, or one that will be.</summary>
public sealed record ForecastItemDto(
    DateOnly Date, ForecastKind Kind, Guid ScheduleId, string ScheduleName, string? TemplateName, Guid AssetId, string? AssetCode, string? AssetName, Guid? LocationId,
    Guid? AssetClassId, Guid? WorkOrderId, string? WorkOrderNumber, WorkOrderStatus? Status, bool Overdue);

public sealed record ForecastDto(DateOnly From, DateOnly To, DateOnly Today, IReadOnlyList<ForecastItemDto> Items, bool Truncated);
