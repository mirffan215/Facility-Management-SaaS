namespace Fms.BuildingBlocks.Identity;

/// <summary>
/// A work order as the Inventory module needs to see it. <see cref="AcceptsParts"/> is true while parts may still be reserved or issued
/// (approved up to completed); once the work is verified, closed or cancelled it is false.
/// </summary>
public sealed record WorkOrderRef(Guid Id, string Number, string Title, Guid? AssetId, Guid? LocationId, bool AcceptsParts);

/// <summary>What other modules may ask the Work module about work orders (answered for the current tenant only).</summary>
public interface IWorkOrderLookup
{
    Task<WorkOrderRef?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Whether a preventive-maintenance template exists in the current tenant (for the parts list a template carries).</summary>
    Task<bool> PmTemplateExistsAsync(Guid pmTemplateId, CancellationToken ct = default);
}

/// <summary>What the Work module may ask the Inventory module. Optional: without Inventory a work order simply has no parts cost.</summary>
public interface IWorkPartsLookup
{
    /// <summary>The cost of parts issued to the work order, net of returns, in the tenant's currency.</summary>
    Task<decimal> MaterialCostAsync(Guid workOrderId, CancellationToken ct = default);
}

/// <summary>Called by the Work module after it creates a preventive work order, so the parts its template lists can be reserved. Best effort: it never blocks the work order.</summary>
public interface IPmPartsReserver
{
    Task ReserveForTemplateAsync(Guid workOrderId, Guid pmTemplateId, CancellationToken ct = default);
}

/// <summary>Published by the Work module when a work order can no longer take parts (cancelled, verified or closed). Consumed by Inventory to release reservations.</summary>
public static class WorkOrderEnded
{
    public const string MessageType = "workorder.ended";
}

public sealed record WorkOrderEndedMessage(Guid WorkOrderId);
