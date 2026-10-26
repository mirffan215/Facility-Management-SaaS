namespace Fms.BuildingBlocks.Identity;

public sealed record ItemRef(Guid Id, string Code, string Name, string Unit, bool Active);

public sealed record StoreRef(Guid Id, string Code, string Name, bool Active);

/// <summary>What the Purchasing module may ask the Inventory module: whether the item and store on an order line are real. Answered for the current tenant only.</summary>
public interface IInventoryCatalog
{
    Task<ItemRef?> GetItemAsync(Guid itemId, CancellationToken ct = default);

    Task<StoreRef?> GetStoreAsync(Guid storeId, CancellationToken ct = default);

    Task<IReadOnlyDictionary<Guid, ItemRef>> ItemsAsync(IEnumerable<Guid> itemIds, CancellationToken ct = default);

    Task<IReadOnlyDictionary<Guid, StoreRef>> StoresAsync(IEnumerable<Guid> storeIds, CancellationToken ct = default);
}

/// <summary>
/// Published by Purchasing when goods for a stock item arrive. Inventory turns it into a stock receipt, once: the receipt id is the idempotency key, so a message
/// delivered twice (the outbox delivers at least once) puts the stock in once.
/// </summary>
public static class GoodsReceived
{
    public const string MessageType = "purchasing.goods_received";
}

public sealed record GoodsReceivedMessage(Guid ReceiptId, string OrderNumber, Guid StoreId, Guid ItemId, decimal Quantity, decimal UnitCost);
