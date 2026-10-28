using System.ComponentModel.DataAnnotations;
using Fms.BuildingBlocks.Audit;
using Fms.BuildingBlocks.Tenancy;

namespace Fms.Compliance.Domain;

public enum ObligationCategory { Fire = 1, Electrical = 2, Lifts = 3, Pressure = 4, Water = 5, Environmental = 6, HealthAndSafety = 7, Other = 8 }

public enum CompletionResult { Passed = 1, PassedWithRemarks = 2, Failed = 3 }

/// <summary>Where an obligation stands today. Computed from dates, never stored (except what was last alerted).</summary>
public enum ComplianceStatus { Scheduled = 1, Compliant = 2, DueSoon = 3, Overdue = 4, Failed = 5 }

/// <summary>A statutory or contractual thing that must be done, usually repeatedly: a fire alarm inspection, a lift certificate, a water hygiene test.</summary>
public sealed class Obligation : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Title { get; set; } = "";
    public ObligationCategory Category { get; set; }
    public string? Authority { get; set; }
    public Guid? LocationId { get; set; }
    public string? LocationName { get; set; }
    public Guid? AssetId { get; set; }
    public string? AssetName { get; set; }

    /// <summary>Months between completions, or null for a one-off obligation.</summary>
    public int? IntervalMonths { get; set; }

    /// <summary>How many days before the due date people are warned.</summary>
    public int LeadDays { get; set; } = 30;

    /// <summary>When it must first be done, until a completion is recorded.</summary>
    public DateOnly? FirstDueOn { get; set; }

    public Guid? OwnerId { get; set; }
    public string? OwnerName { get; set; }
    public string? Notes { get; set; }
    public bool IsArchived { get; set; }

    // The newest completion, kept here so lists and alerts do not have to read the whole history.
    public DateOnly? LastCompletedOn { get; set; }
    public DateOnly? ValidUntil { get; set; }
    public CompletionResult? LastResult { get; set; }

    // What people were last warned about, so each change of state warns once.
    public ComplianceStatus? LastAlertStatus { get; set; }
    public DateOnly? LastAlertOn { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>One time the obligation was done (an inspection, a certificate issued). Never edited or deleted: a mistake is corrected by recording again.</summary>
public sealed class Completion : ITenantOwned, IAppendOnly
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ObligationId { get; set; }
    public DateOnly PerformedOn { get; set; }
    public DateOnly? ValidUntil { get; set; }
    public CompletionResult Result { get; set; }
    public string? Reference { get; set; }
    public string? ProviderName { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public Guid? RecordedBy { get; set; }
    public string? RecordedByName { get; set; }
}

/// <summary>A certificate or report attached to a completion. Soft-deleted with a reason; the file's hash stays in the record.</summary>
public sealed class CompletionDocument : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid CompletionId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string StorageKey { get; set; } = "";
    public DateTimeOffset UploadedAt { get; set; }
    public Guid? UploadedBy { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }
    public string? DeleteReason { get; set; }
}

/// <summary>A kind of permit an organisation issues (hot work, confined space, work on live electrics) and its fixed requirements.</summary>
public sealed class PermitType : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? NameAr { get; set; }
    public int MaxDurationHours { get; set; } = 8;

    /// <summary>While one of these is in force in a place, no other permit may be in force there at the same time.</summary>
    public bool Exclusive { get; set; }

    /// <summary>The permit cannot be issued without a reference to the isolation (lock-out) that makes the work safe.</summary>
    public bool RequiresIsolation { get; set; }

    /// <summary>JSON list of the precautions the issuer must confirm.</summary>
    public string PrecautionsJson { get; set; } = "[]";

    public bool IsArchived { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

public enum PermitStatus { Requested = 1, Approved = 2, Rejected = 3, Issued = 4, Started = 5, Closed = 6, Cancelled = 7 }

/// <summary>Permission to carry out dangerous work in one place for a limited time. Moves one way through a fixed lifecycle.</summary>
public sealed class Permit : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int Number { get; set; }
    public Guid TypeId { get; set; }
    public string TypeCode { get; set; } = "";
    public string TypeName { get; set; } = "";
    public bool TypeExclusive { get; set; }
    public bool TypeRequiresIsolation { get; set; }
    public Guid? WorkOrderId { get; set; }
    public string? WorkOrderNumber { get; set; }
    public Guid LocationId { get; set; }
    public string LocationName { get; set; } = "";
    public string Description { get; set; } = "";
    public string? ContractorName { get; set; }
    public DateTimeOffset PlannedStart { get; set; }
    public DateTimeOffset PlannedEnd { get; set; }
    public PermitStatus Status { get; set; } = PermitStatus.Requested;

    /// <summary>JSON list of { text, confirmed } copied from the type when requested and confirmed when issued.</summary>
    public string PrecautionsJson { get; set; } = "[]";

    public Guid RequestedBy { get; set; }
    public string RequestedByName { get; set; } = "";
    public DateTimeOffset RequestedAt { get; set; }
    public Guid? ApprovedBy { get; set; }
    public string? ApprovedByName { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string? RejectReason { get; set; }
    public Guid? IssuedBy { get; set; }
    public string? IssuedByName { get; set; }
    public DateTimeOffset? IssuedAt { get; set; }
    public string? IsolationReference { get; set; }
    public Guid? StartedBy { get; set; }
    public string? StartedByName { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public Guid? ClosedBy { get; set; }
    public string? ClosedByName { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public string? ClosureNote { get; set; }
    public bool? AreaSafe { get; set; }
    public string? CancelReason { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Who did what to a permit and when. Append-only.</summary>
public sealed class PermitEvent : ITenantOwned, IAppendOnly
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PermitId { get; set; }
    public DateTimeOffset At { get; set; }
    public Guid? ById { get; set; }
    public string ByName { get; set; } = "";
    public string Action { get; set; } = "";
    public string? Note { get; set; }
}

/// <summary>The date arithmetic that decides where an obligation stands. Pure, so it is tested without a database.</summary>
public static class ComplianceRules
{
    public const int MaxIntervalMonths = 240;
    public const int MaxLeadDays = 365;

    public static DateOnly? DueOn(Obligation o) => o.ValidUntil ?? (o.LastCompletedOn is null ? o.FirstDueOn : null);

    public static ComplianceStatus StatusOf(Obligation o, DateOnly today)
    {
        if (o.LastResult == CompletionResult.Failed) return ComplianceStatus.Failed;
        var due = DueOn(o);
        if (due is null) return o.LastCompletedOn is null ? ComplianceStatus.Scheduled : ComplianceStatus.Compliant;
        if (due < today) return ComplianceStatus.Overdue;
        if (due <= today.AddDays(o.LeadDays)) return ComplianceStatus.DueSoon;
        return o.LastCompletedOn is null ? ComplianceStatus.Scheduled : ComplianceStatus.Compliant;
    }

    /// <summary>Default end of validity for a completion: the performed date plus the interval, or null for a one-off.</summary>
    public static DateOnly? DefaultValidUntil(DateOnly performedOn, int? intervalMonths) => intervalMonths is { } m ? performedOn.AddMonths(m) : null;

    /// <summary>Whether a permit's end time has passed while it is still in force.</summary>
    public static bool IsOverdue(PermitStatus status, DateTimeOffset plannedEnd, DateTimeOffset now) => status is PermitStatus.Issued or PermitStatus.Started && now > plannedEnd;

    public static bool Overlaps(DateTimeOffset aStart, DateTimeOffset aEnd, DateTimeOffset bStart, DateTimeOffset bEnd) => aStart < bEnd && bStart < aEnd;
}
