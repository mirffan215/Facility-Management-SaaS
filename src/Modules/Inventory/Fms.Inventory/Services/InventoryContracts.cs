using Fms.Inventory.Domain;

namespace Fms.Inventory.Services;

public sealed record StoreDto(Guid Id, string Code, string Name, string? NameAr, Guid? SiteId, bool IsArchived, string RowVersion);

public sealed record SaveStoreRequest(string? Code, string? Name, string? NameAr, Guid? SiteId, string? RowVersion);

public sealed record ItemDto(Guid Id, string Code, string Name, string? NameAr, string? Description, string Unit, string? Barcode, bool IsArchived, decimal OnHand, decimal Available, string RowVersion);

public sealed record SaveItemRequest(string? Code, string? Name, string? NameAr, string? Description, string? Unit, string? Barcode, string? RowVersion);

public sealed record StockLevelDto(
    Guid StoreId, string StoreCode, string StoreName, Guid ItemId, string ItemCode, string ItemName, string Unit, decimal OnHand, decimal Reserved, decimal Available,
    decimal AverageCost, decimal Value, decimal? MinQty, decimal? MaxQty, bool IsLow, decimal SuggestedOrder, string RowVersion);

public sealed record SetLevelRequest(Guid? StoreId, Guid? ItemId, decimal? MinQty, decimal? MaxQty, string? RowVersion);

public sealed record ReceiveRequest(Guid? StoreId, Guid? ItemId, decimal? Quantity, decimal? UnitCost, string? Reference, string? Note);

public sealed record AdjustRequest(Guid? StoreId, Guid? ItemId, decimal? Delta, string? Reason);

public sealed record TransferRequest(Guid? FromStoreId, Guid? ToStoreId, Guid? ItemId, decimal? Quantity, string? Note);

public sealed record MovementDto(
    Guid Id, Guid StoreId, string? StoreName, Guid ItemId, string? ItemCode, string? ItemName, MovementType Type, decimal Quantity, decimal UnitCost, decimal BalanceAfter,
    DateTimeOffset At, Guid? By, string? ByName, Guid? WorkOrderId, string? Reference, string? Reason);

public sealed record WorkPartDto(
    Guid ItemId, string ItemCode, string ItemName, string Unit, Guid StoreId, string StoreName, decimal Reserved, decimal Issued, decimal UnitCost, decimal Cost, decimal StoreAvailable);

public sealed record WorkPartsDto(Guid WorkOrderId, bool AcceptsParts, IReadOnlyList<WorkPartDto> Lines, decimal TotalCost);

public sealed record PartRequest(Guid? ItemId, Guid? StoreId, decimal? Quantity, string? Reason);

public sealed record PmTemplatePartDto(Guid ItemId, string ItemCode, string ItemName, string Unit, decimal Quantity, Guid? StoreId, string? StoreName);

public sealed record PmPartInput(Guid? ItemId, decimal? Quantity, Guid? StoreId);

public sealed record SavePmPartsRequest(IReadOnlyList<PmPartInput>? Parts);

public sealed record StockTakeLineDto(Guid ItemId, string ItemCode, string ItemName, string Unit, decimal SystemQty, decimal? CountedQty, decimal? Variance, decimal UnitCost, decimal? VarianceValue);

public sealed record StockTakeDto(
    Guid Id, Guid StoreId, string StoreName, StockTakeStatus Status, string? Note, DateTimeOffset StartedAt, string? StartedByName, DateTimeOffset? SubmittedAt, string? SubmittedByName,
    DateTimeOffset? DecidedAt, string? DecidedByName, int Lines, int Counted, int Differences, decimal NetVarianceValue, IReadOnlyList<StockTakeLineDto>? Detail, string RowVersion);

public sealed record StartStockTakeRequest(Guid? StoreId, string? Note);

public sealed record CountInput(Guid? ItemId, decimal? CountedQty);

public sealed record SaveCountsRequest(IReadOnlyList<CountInput>? Counts, string? RowVersion);

public sealed record RowVersionRequest(string? RowVersion);

public sealed record LowStockMessage(Guid StoreId, Guid ItemId);

public static class InventoryMessages
{
    public const string LowStock = "inventory.low";
}
