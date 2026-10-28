using Fms.Leases.Domain;

namespace Fms.Leases.Services;

public sealed record DeadlineDto(DeadlineKind Kind, DateOnly On, int DaysAway);

public sealed record LeaseSummaryDto(
    Guid Id, string Reference, string Title, LeaseKind Kind, LeaseState State, string Counterparty, string? LocationName, DateOnly StartOn, DateOnly EndOn, string CurrencyCode, decimal BaseRent,
    RentFrequency Frequency, DeadlineDto? NextDeadline);

public sealed record LeaseDto(
    Guid Id, string Reference, string Title, LeaseKind Kind, LeaseState State, string Counterparty, Guid? LocationId, string? LocationName, decimal? AreaSquareMetres, DateOnly StartOn, DateOnly EndOn,
    int NoticeDays, DateOnly? NoticeDeadline, DateOnly? BreakOn, int BreakNoticeDays, DateOnly? BreakDeadline, int? RenewalMonths, string CurrencyCode, decimal BaseRent, RentFrequency Frequency,
    decimal EscalationPercent, int EscalationEveryMonths, int RentFreeMonths, decimal? Deposit, int AlertLeadDays, string? Notes, DateOnly? ActivatedOn, DateOnly? TerminatedOn, string? TerminationReason,
    bool TermsLocked, IReadOnlyList<DeadlineDto> Deadlines, decimal TotalRent, string RowVersion);

public sealed record SaveLeaseRequest(
    string? Reference, string? Title, LeaseKind? Kind, string? Counterparty, Guid? LocationId, decimal? AreaSquareMetres, DateOnly? StartOn, DateOnly? EndOn, int? NoticeDays, DateOnly? BreakOn, int? BreakNoticeDays,
    int? RenewalMonths, string? CurrencyCode, decimal? BaseRent, RentFrequency? Frequency, decimal? EscalationPercent, int? EscalationEveryMonths, int? RentFreeMonths, decimal? Deposit, int? AlertLeadDays,
    string? Notes, string? RowVersion);

public sealed record ActivateRequest(string? RowVersion);

public sealed record ExtendRequest(DateOnly? NewEndOn, string? Reason, string? RowVersion);

public sealed record TerminateRequest(DateOnly? On, string? Reason, string? RowVersion);

public sealed record DeleteRequest(string? Reason);

public sealed record LeaseDocumentDto(Guid Id, Guid LeaseId, string FileName, string ContentType, long SizeBytes, string Sha256, DateTimeOffset UploadedAt);

public sealed record DownloadedLeaseDocument(LeaseDocumentDto Document, Stream Content);

public sealed record InstallmentDto(int Number, DateOnly DueOn, DateOnly PeriodEnd, decimal Amount, int EscalationStep, bool IsRentFree, bool IsPartial);

public sealed record ScheduleDto(Guid LeaseId, string CurrencyCode, decimal Total, IReadOnlyList<InstallmentDto> Installments);

public sealed record RentTotalDto(LeaseKind Kind, string CurrencyCode, decimal NextTwelveMonths);

public sealed record LeaseSummaryTotalsDto(int Active, int Draft, int Expired, int ExpiringSoon, int DeadlinesSoon, IReadOnlyList<RentTotalDto> Rent);
