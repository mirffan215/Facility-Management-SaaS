using System.ComponentModel.DataAnnotations;
using Fms.BuildingBlocks.Audit;
using Fms.BuildingBlocks.Security;
using Fms.BuildingBlocks.Tenancy;

namespace Fms.Work.Domain;

public enum WorkOrderType { Corrective = 1, Emergency = 2, Planned = 3 }

public enum WorkPriority { Low = 1, Medium = 2, High = 3, Urgent = 4 }

public enum WorkOrderStatus { Requested = 1, Approved = 2, Assigned = 3, InProgress = 4, OnHold = 5, Completed = 6, Verified = 7, Closed = 8, Cancelled = 9 }

/// <summary>Everything that can happen to a work order. Each one is recorded in its history.</summary>
public enum WorkOrderAction
{
    Created = 1, Approved, Rejected, Assigned, Started, Held, Resumed, Completed, Verified, Closed, Reopened, Cancelled, Edited,
}

public enum ClosureCode { Fixed = 1, NoFaultFound = 2, TemporaryFix = 3, PartsRequired = 4, Duplicate = 5, NotRequired = 6 }

/// <summary>A request for work, and (once approved) the job that fulfils it. One record from the first report to closure.</summary>
public sealed class WorkOrder : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>Human number, 1, 2, 3... per tenant. Shown as WO-000001.</summary>
    public int Number { get; set; }

    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public WorkOrderType Type { get; set; } = WorkOrderType.Corrective;
    public WorkPriority Priority { get; set; } = WorkPriority.Medium;
    public WorkOrderStatus Status { get; set; } = WorkOrderStatus.Requested;
    public Guid LocationId { get; set; }
    public Guid? AssetId { get; set; }
    public Guid? CategoryId { get; set; }
    public Guid RequestedBy { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public Guid? AssignedTo { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public Guid? CompletedBy { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public Guid? VerifiedBy { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>Why it is on hold, cancelled or was last reopened (the most recent reason).</summary>
    public string? StatusReason { get; set; }

    public ClosureCode? ClosureCode { get; set; }
    public string? ClosureNotes { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // --- SLA clocks (see SlaRules). Started once, when the work is authorised. ---
    public Guid? SlaPolicyId { get; set; }
    public Guid? SlaCalendarId { get; set; }
    public DateTimeOffset? SlaStartedAt { get; set; }
    public DateTimeOffset? ResponseDueAt { get; set; }
    public DateTimeOffset? ResolutionDueAt { get; set; }

    /// <summary>When work first started (the response).</summary>
    public DateTimeOffset? RespondedAt { get; set; }

    /// <summary>Start of the current resolution stretch: when the clocks started, or when work resumed after a hold. "At risk" is measured from here.</summary>
    public DateTimeOffset? ResolutionWindowStartAt { get; set; }

    public DateTimeOffset? SlaPausedAt { get; set; }
    public int? ResolutionRemainingMinutes { get; set; }
    public DateTimeOffset? ResponseBreachNotifiedAt { get; set; }
    public DateTimeOffset? ResolutionWarnedAt { get; set; }
    public DateTimeOffset? ResolutionBreachNotifiedAt { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>One line of a work order's history. Append-only, also enforced by the database.</summary>
public sealed class WorkOrderEvent : ITenantOwned, IAppendOnly
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid WorkOrderId { get; set; }
    public WorkOrderAction Action { get; set; }
    public WorkOrderStatus? FromStatus { get; set; }
    public WorkOrderStatus ToStatus { get; set; }
    public Guid? ActorId { get; set; }
    public DateTimeOffset At { get; set; }
    public string? Note { get; set; }

    /// <summary>The person involved besides the actor (the assignee for an assignment).</summary>
    public Guid? SubjectId { get; set; }
}

/// <summary>A message on a work order. Append-only. Internal comments are for staff and invisible to the requester.</summary>
public sealed class WorkOrderComment : ITenantOwned, IAppendOnly
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid WorkOrderId { get; set; }
    public Guid AuthorId { get; set; }
    public DateTimeOffset At { get; set; }
    public string Body { get; set; } = "";
    public bool IsInternal { get; set; }
}

/// <summary>What kind of problem it is (Electrical, Plumbing, Cleaning...). Sets the default priority of a request.</summary>
public sealed class WorkCategory : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? NameAr { get; set; }
    public WorkPriority DefaultPriority { get; set; } = WorkPriority.Medium;
    public bool IsArchived { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Per-tenant switches on the work order lifecycle. A tenant without a row uses the defaults (both on).</summary>
public sealed class WorkSettings : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>When on, a corrective request waits for approval before it can be assigned.</summary>
    public bool RequireApproval { get; set; } = true;

    /// <summary>When on, finished work is checked by someone other than the person who did it before it can be closed.</summary>
    public bool RequireVerification { get; set; } = true;

    /// <summary>When on, work cannot be completed until the technician has signed.</summary>
    public bool RequireCompletionSignature { get; set; }

    /// <summary>ISO 4217 code that costs are shown in. A tenant works in one currency here.</summary>
    public string CurrencyCode { get; set; } = "SAR";

    /// <summary>Own labour rate per hour, frozen onto each time entry when it is recorded. Zero means labour is not costed.</summary>
    public decimal LabourRatePerHour { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

public sealed record WorkflowSettings(bool RequireApproval = true, bool RequireVerification = true);

/// <summary>Who is acting on a work order, for deciding what they may do.</summary>
public sealed record Actor(Guid? UserId, IReadOnlySet<string> Permissions)
{
    public bool Has(string permission) => Permissions.Contains(permission);
}

/// <summary>The lifecycle, as pure functions. The service and the API both ask this, so there is one answer to "may I?".</summary>
public static class WorkOrderRules
{
    public static readonly IReadOnlyList<WorkOrderAction> UserActions =
    [
        WorkOrderAction.Approved, WorkOrderAction.Rejected, WorkOrderAction.Assigned, WorkOrderAction.Started, WorkOrderAction.Held,
        WorkOrderAction.Resumed, WorkOrderAction.Completed, WorkOrderAction.Verified, WorkOrderAction.Closed, WorkOrderAction.Reopened,
        WorkOrderAction.Cancelled, WorkOrderAction.Edited,
    ];

    public static bool IsOpen(WorkOrderStatus s) => s is not (WorkOrderStatus.Closed or WorkOrderStatus.Cancelled);

    /// <summary>Where a new request starts. Emergencies are never held up by approval.</summary>
    public static WorkOrderStatus InitialStatus(WorkOrderType type, WorkflowSettings settings) =>
        type == WorkOrderType.Emergency || !settings.RequireApproval ? WorkOrderStatus.Approved : WorkOrderStatus.Requested;

    public static string DisplayNumber(int number) => "WO-" + number.ToString("000000");

    public static bool RequiresReason(WorkOrderAction a) => a is WorkOrderAction.Rejected or WorkOrderAction.Held or WorkOrderAction.Reopened or WorkOrderAction.Cancelled;

    /// <summary>The actions this person may take on this work order right now.</summary>
    public static IReadOnlyList<WorkOrderAction> Allowed(WorkOrder wo, Actor actor, WorkflowSettings settings) =>
        UserActions.Where(a => Can(a, wo, actor, settings)).ToList();

    public static bool Can(WorkOrderAction action, WorkOrder wo, Actor actor, WorkflowSettings settings)
    {
        if (actor.UserId is not { } me) return false;
        var s = wo.Status;
        var manage = actor.Has(Permissions.WorkOrdersManage);
        var isRequester = wo.RequestedBy == me;
        var isAssignee = wo.AssignedTo == me;
        var doesTheWork = manage || (isAssignee && actor.Has(Permissions.WorkOrdersWork));
        var ownOpenRequest = s == WorkOrderStatus.Requested && isRequester;

        return action switch
        {
            // Maker-checker: nobody approves (or rejects) their own request.
            WorkOrderAction.Approved or WorkOrderAction.Rejected => s == WorkOrderStatus.Requested && actor.Has(Permissions.WorkOrdersApprove) && !isRequester,
            WorkOrderAction.Assigned => s is WorkOrderStatus.Approved or WorkOrderStatus.Assigned or WorkOrderStatus.InProgress or WorkOrderStatus.OnHold && actor.Has(Permissions.WorkOrdersAssign),
            WorkOrderAction.Started => s == WorkOrderStatus.Assigned && doesTheWork,
            WorkOrderAction.Held => s == WorkOrderStatus.InProgress && doesTheWork,
            WorkOrderAction.Resumed => s == WorkOrderStatus.OnHold && doesTheWork,
            WorkOrderAction.Completed => s == WorkOrderStatus.InProgress && doesTheWork,
            // Segregation of duties: whoever did the work cannot be the one who checks it.
            WorkOrderAction.Verified => s == WorkOrderStatus.Completed && actor.Has(Permissions.WorkOrdersVerify) && me != wo.AssignedTo && me != wo.CompletedBy,
            WorkOrderAction.Closed => (s == WorkOrderStatus.Verified || (s == WorkOrderStatus.Completed && !settings.RequireVerification))
                                       && (manage || actor.Has(Permissions.WorkOrdersVerify)),
            WorkOrderAction.Reopened => s is WorkOrderStatus.Completed or WorkOrderStatus.Verified or WorkOrderStatus.Closed && manage,
            WorkOrderAction.Cancelled => s is WorkOrderStatus.Requested or WorkOrderStatus.Approved or WorkOrderStatus.Assigned or WorkOrderStatus.InProgress or WorkOrderStatus.OnHold
                                         && (manage || ownOpenRequest),
            WorkOrderAction.Edited => s is WorkOrderStatus.Requested or WorkOrderStatus.Approved or WorkOrderStatus.Assigned or WorkOrderStatus.InProgress or WorkOrderStatus.OnHold
                                      && (manage || ownOpenRequest),
            _ => false,
        };
    }

    public static bool IsOverdue(WorkOrder wo, DateTimeOffset now) => wo.DueAt is { } due && due < now && IsOpen(wo.Status) && wo.Status is not (WorkOrderStatus.Completed or WorkOrderStatus.Verified);
}
