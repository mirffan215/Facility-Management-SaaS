using Fms.Work.Domain;

namespace Fms.Work.Services;

/// <summary>Outbox message types published by this module and the payloads they carry.</summary>
public static class WorkMessages
{
    /// <summary>Something happened to a work order (a lifecycle action). Consumed by notifications.</summary>
    public const string Event = "workorder.event";

    /// <summary>An SLA clock needs attention (about to breach, or breached). Published by the SLA monitor.</summary>
    public const string Sla = "workorder.sla";
}

public sealed record WorkOrderEventMessage(Guid WorkOrderId, WorkOrderAction Action, Guid? ActorId, string? Reason);

public enum SlaAlert { ResponseBreached = 1, ResolutionAtRisk = 2, ResolutionBreached = 3 }

public sealed record SlaAlertMessage(Guid WorkOrderId, SlaAlert Alert);
