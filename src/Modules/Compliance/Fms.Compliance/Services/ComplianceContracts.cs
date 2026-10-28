using Fms.Compliance.Domain;

namespace Fms.Compliance.Services;

public sealed record ObligationDto(
    Guid Id, string Title, ObligationCategory Category, string? Authority, Guid? LocationId, string? LocationName, Guid? AssetId, string? AssetName, int? IntervalMonths, int LeadDays,
    DateOnly? FirstDueOn, DateOnly? DueOn, int? DaysToDue, Guid? OwnerId, string? OwnerName, string? Notes, bool IsArchived, DateOnly? LastCompletedOn, DateOnly? ValidUntil, CompletionResult? LastResult,
    ComplianceStatus Status, string RowVersion, bool CreateWorkOrderOnFail = false);

public sealed record SaveObligationRequest(
    string? Title, ObligationCategory? Category, string? Authority, Guid? LocationId, Guid? AssetId, int? IntervalMonths, int? LeadDays, DateOnly? FirstDueOn, Guid? OwnerId, string? Notes, string? RowVersion, bool? CreateWorkOrderOnFail = null);

public sealed record ArchiveObligationRequest(string? RowVersion);

public sealed record RecordCompletionRequest(DateOnly? PerformedOn, DateOnly? ValidUntil, CompletionResult? Result, string? Reference, string? ProviderName, string? Notes, string? ObligationRowVersion);

public sealed record CompletionDto(
    Guid Id, Guid ObligationId, DateOnly PerformedOn, DateOnly? ValidUntil, CompletionResult Result, string? Reference, string? ProviderName, string? Notes, DateTimeOffset RecordedAt, string? RecordedByName,
    IReadOnlyList<CompletionDocumentDto> Documents, Guid? WorkOrderId = null, string? WorkOrderNumber = null);

public sealed record CompletionDocumentDto(Guid Id, Guid CompletionId, string FileName, string ContentType, long SizeBytes, string Sha256, DateTimeOffset UploadedAt);

public sealed record DeleteDocumentRequest(string? Reason);

public sealed record ObligationDetailDto(ObligationDto Obligation, IReadOnlyList<CompletionDto> Completions);

public sealed record ComplianceSummaryDto(int Total, int Compliant, int DueSoon, int Overdue, int Failed, int Scheduled, int? PercentCompliant);

public sealed record DownloadedDocument(CompletionDocumentDto Document, Stream Content);

// ---- permits ----

public sealed record PermitTypeDto(Guid Id, string Code, string Name, string? NameAr, int MaxDurationHours, bool Exclusive, bool RequiresIsolation, IReadOnlyList<string> Precautions, bool IsArchived, string RowVersion);

public sealed record SavePermitTypeRequest(string? Code, string? Name, string? NameAr, int? MaxDurationHours, bool? Exclusive, bool? RequiresIsolation, IReadOnlyList<string>? Precautions, string? RowVersion);

public sealed record PermitPrecautionDto(string Text, bool Confirmed);

public sealed record PermitDto(
    Guid Id, int Number, Guid TypeId, string TypeCode, string TypeName, bool Exclusive, bool RequiresIsolation, Guid? WorkOrderId, string? WorkOrderNumber, Guid LocationId, string LocationName, string Description,
    string? ContractorName, DateTimeOffset PlannedStart, DateTimeOffset PlannedEnd, PermitStatus Status, bool IsOverdue, IReadOnlyList<PermitPrecautionDto> Precautions, Guid RequestedBy, string RequestedByName,
    DateTimeOffset RequestedAt, string? ApprovedByName, DateTimeOffset? ApprovedAt, string? RejectReason, string? IssuedByName, DateTimeOffset? IssuedAt, string? IsolationReference, string? StartedByName,
    DateTimeOffset? StartedAt, string? ClosedByName, DateTimeOffset? ClosedAt, string? ClosureNote, bool? AreaSafe, string? CancelReason, string RowVersion);

public sealed record PermitEventDto(DateTimeOffset At, string ByName, string Action, string? Note);

public sealed record PermitDetailDto(PermitDto Permit, IReadOnlyList<PermitEventDto> Events);

public sealed record RequestPermitRequest(Guid? TypeId, Guid? LocationId, Guid? WorkOrderId, string? Description, string? ContractorName, DateTimeOffset? PlannedStart, DateTimeOffset? PlannedEnd);

public sealed record PermitDecisionRequest(string? Reason, string? RowVersion);

public sealed record IssuePermitRequest(IReadOnlyList<string>? ConfirmedPrecautions, string? IsolationReference, string? RowVersion);

public sealed record StartPermitRequest(string? RowVersion);

public sealed record ClosePermitRequest(bool? AreaSafe, string? Note, string? RowVersion);

public static class PermitMessages
{
    public const string Notice = "compliance.notice";
}
