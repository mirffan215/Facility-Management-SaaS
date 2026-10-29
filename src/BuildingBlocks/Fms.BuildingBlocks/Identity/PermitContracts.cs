namespace Fms.BuildingBlocks.Identity;

/// <summary>A permit-to-work tied to a work order, as the Work module needs to show it. <paramref name="Status"/> is the permit's own status name.</summary>
public sealed record WorkPermitRef(Guid Id, int Number, Guid WorkOrderId, string TypeName, string Status, string LocationName, DateTimeOffset PlannedStart, DateTimeOffset PlannedEnd);

/// <summary>What other modules may ask the Compliance module about permits. Answered for the current tenant only.</summary>
public interface IWorkPermitLookup
{
    /// <summary>The permits still in play (requested, approved, issued or started) for these work orders, soonest planned start first.</summary>
    Task<IReadOnlyList<WorkPermitRef>> ForWorkOrdersAsync(IEnumerable<Guid> workOrderIds, CancellationToken ct = default);
}
