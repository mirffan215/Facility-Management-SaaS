namespace Fms.BuildingBlocks.Identity;

/// <summary>A work order and what it has cost so far (own labour, costs entered, parts issued from stock), in the tenant's currency.</summary>
public sealed record WorkCostInfo(Guid Id, string Number, string Title, string Status, decimal Total, string CurrencyCode);

/// <summary>What the Projects module may ask the Work module. Answered for the current tenant only.</summary>
public interface IWorkCostLookup
{
    Task<IReadOnlyDictionary<Guid, WorkCostInfo>> GetAsync(IEnumerable<Guid> ids, CancellationToken ct = default);

    /// <summary>Finds a work order by the number people see (WO-000012, or just 12).</summary>
    Task<WorkCostInfo?> FindByNumberAsync(string number, CancellationToken ct = default);
}

/// <summary>A purchase order with what was ordered and what has arrived, both at the order's prices.</summary>
public sealed record PurchaseOrderInfo(Guid Id, string Number, string VendorName, string Status, decimal Ordered, decimal Received, string CurrencyCode, bool IsCancelled);

/// <summary>What the Projects module may ask the Purchasing module. Answered for the current tenant only.</summary>
public interface IPurchaseOrderLookup
{
    Task<IReadOnlyDictionary<Guid, PurchaseOrderInfo>> GetAsync(IEnumerable<Guid> ids, CancellationToken ct = default);

    /// <summary>Finds an order by the number people see (PO-000012, or just 12).</summary>
    Task<PurchaseOrderInfo?> FindByNumberAsync(string number, CancellationToken ct = default);
}
