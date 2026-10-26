using System.ComponentModel.DataAnnotations;
using Fms.BuildingBlocks.Audit;
using Fms.BuildingBlocks.Security;
using Fms.BuildingBlocks.Tenancy;

namespace Fms.Work.Domain;

// ---------------- checklists ----------------

/// <summary>A reusable list of things to check or do (a pump inspection, a handover). Copied onto a work order, never linked live.</summary>
public sealed class ChecklistTemplate : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public string? NameAr { get; set; }
    public bool IsArchived { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

public sealed class ChecklistTemplateItem : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid TemplateId { get; set; }
    public int Position { get; set; }
    public string Text { get; set; } = "";
    public bool Required { get; set; }
}

public enum ChecklistResult { Pending = 0, Done = 1, Failed = 2, NotApplicable = 3 }

/// <summary>One line of a work order's checklist, with its outcome. Changes are audited; the text is a copy and does not follow the template.</summary>
public sealed class WorkOrderChecklistItem : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid WorkOrderId { get; set; }
    public Guid? TemplateId { get; set; }
    public int Position { get; set; }
    public string Text { get; set; } = "";
    public bool Required { get; set; }
    public ChecklistResult Result { get; set; }
    public string? Note { get; set; }
    public Guid? ResultBy { get; set; }
    public DateTimeOffset? ResultAt { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

// ---------------- time and cost ----------------

/// <summary>Time worked on a work order. Append-only: a wrong entry is voided, never edited. The hourly rate is frozen at entry.</summary>
public sealed class WorkOrderTimeEntry : ITenantOwned, IAppendOnly
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid WorkOrderId { get; set; }
    public Guid WorkerId { get; set; }
    public DateOnly WorkedOn { get; set; }
    public int Minutes { get; set; }
    public decimal HourlyRate { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

public enum CostKind { Material = 1, Contractor = 2, Other = 3 }

/// <summary>Money spent on a work order other than own labour. Append-only; corrected by voiding and re-entering.</summary>
public sealed class WorkOrderCostLine : ITenantOwned, IAppendOnly
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid WorkOrderId { get; set; }
    public CostKind Kind { get; set; }
    public string Description { get; set; } = "";

    /// <summary>Supplier, invoice or purchase order number.</summary>
    public string? Reference { get; set; }

    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public Guid RecordedBy { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

public enum LedgerEntryType { Time = 1, Cost = 2 }

/// <summary>Cancels a time entry or cost line. Append-only, one per entry; the reason stays on record.</summary>
public sealed class WorkOrderVoid : ITenantOwned, IAppendOnly
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid WorkOrderId { get; set; }
    public LedgerEntryType EntryType { get; set; }
    public Guid EntryId { get; set; }
    public string Reason { get; set; } = "";
    public Guid VoidedBy { get; set; }
    public DateTimeOffset VoidedAt { get; set; }
}

// ---------------- signatures and files ----------------

public enum SignatureRole { Technician = 1, Customer = 2 }

/// <summary>A drawn signature (PNG in file storage) with the signer's printed name. Append-only evidence of who signed off, when.</summary>
public sealed class WorkOrderSignature : ITenantOwned, IAppendOnly
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid WorkOrderId { get; set; }
    public SignatureRole Role { get; set; }
    public string SignerName { get; set; } = "";
    public string StorageKey { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public DateTimeOffset SignedAt { get; set; }
    public Guid RecordedBy { get; set; }
}

public enum AttachmentKind { Before = 1, After = 2, Other = 3 }

/// <summary>A photo or document on a work order. Soft-deleted with a reason; the stored bytes are kept.</summary>
public sealed class WorkOrderAttachment : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid WorkOrderId { get; set; }
    public AttachmentKind Kind { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string StorageKey { get; set; } = "";
    public string? Description { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
    public Guid UploadedBy { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }
    public string? DeleteReason { get; set; }
}

/// <summary>Who may record what, and when. Pure, like <see cref="WorkOrderRules"/>.</summary>
public static class EvidenceRules
{
    /// <summary>Checklist results can change while the job is being done.</summary>
    public static bool ChecklistOpen(WorkOrderStatus s) => s is WorkOrderStatus.InProgress or WorkOrderStatus.OnHold;

    /// <summary>A checklist can be added from approval until work finishes.</summary>
    public static bool ChecklistAttachable(WorkOrderStatus s) => s is WorkOrderStatus.Approved or WorkOrderStatus.Assigned or WorkOrderStatus.InProgress or WorkOrderStatus.OnHold;

    /// <summary>Time and cost can still be added just after completion (late receipts), but not once checked or closed.</summary>
    public static bool LedgerOpen(WorkOrderStatus s) => s is WorkOrderStatus.InProgress or WorkOrderStatus.OnHold or WorkOrderStatus.Completed;

    /// <summary>Signatures are taken while the work is finishing.</summary>
    public static bool SignatureOpen(WorkOrderStatus s) => s is WorkOrderStatus.InProgress or WorkOrderStatus.OnHold or WorkOrderStatus.Completed;

    public static bool FilesOpen(WorkOrderStatus s) => WorkOrderRules.IsOpen(s);

    /// <summary>The person doing the job (or a manager) records the work itself.</summary>
    public static bool DoesTheWork(WorkOrder wo, Actor actor) =>
        actor.UserId is { } me && (actor.Has(Permissions.WorkOrdersManage) || (wo.AssignedTo == me && actor.Has(Permissions.WorkOrdersWork)));

    /// <summary>Photos help everyone involved: the worker, dispatchers, and the person who reported it.</summary>
    public static bool MayAddFiles(WorkOrder wo, Actor actor) =>
        DoesTheWork(wo, actor) || actor.Has(Permissions.WorkOrdersAssign) || (actor.UserId is { } me && wo.RequestedBy == me);

    public static decimal LineTotal(WorkOrderCostLine l) => Math.Round(l.Quantity * l.UnitCost, 2, MidpointRounding.AwayFromZero);

    public static decimal LabourCost(int minutes, decimal hourlyRate) => Math.Round(minutes / 60m * hourlyRate, 2, MidpointRounding.AwayFromZero);
}
