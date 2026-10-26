namespace Fms.BuildingBlocks.Identity;

/// <summary>A vendor as other modules need to see it. <see cref="CanReceiveWork"/> is false for vendors on hold, blocked or archived.</summary>
public sealed record VendorRef(Guid Id, string Code, string Name, string? NameAr, bool CanReceiveWork);

/// <summary>A contract as other modules need to see it. <see cref="InForce"/> means started, not ended and not terminated, today.</summary>
public sealed record ContractRef(Guid Id, Guid VendorId, string Number, string Title, DateOnly StartOn, DateOnly EndOn, bool InForce);

/// <summary>The contract's response and resolution targets for one priority (1 low to 4 urgent), in minutes.</summary>
public sealed record ContractSlaRef(int Priority, int ResponseMinutes, int ResolutionMinutes);

/// <summary>What other modules may ask the Vendors module (answered for the current tenant only).</summary>
public interface IVendorLookup
{
    Task<VendorRef?> GetVendorAsync(Guid vendorId, CancellationToken ct = default);

    Task<IReadOnlyDictionary<Guid, VendorRef>> VendorsAsync(IEnumerable<Guid> vendorIds, CancellationToken ct = default);

    Task<ContractRef?> GetContractAsync(Guid contractId, CancellationToken ct = default);

    /// <summary>
    /// Whether the contract covers the asset: by the asset itself, its class, or the site it is in. A contract that lists no coverage is general and covers everything.
    /// </summary>
    Task<bool> CoversAsync(Guid contractId, Guid? assetId, Guid? locationId, CancellationToken ct = default);

    /// <summary>The contract's target for a priority, or null when it sets none.</summary>
    Task<ContractSlaRef?> SlaAsync(Guid contractId, int priority, CancellationToken ct = default);
}
