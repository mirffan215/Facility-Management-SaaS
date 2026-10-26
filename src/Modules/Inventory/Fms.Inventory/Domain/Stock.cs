using System.ComponentModel.DataAnnotations;
using Fms.BuildingBlocks.Audit;
using Fms.BuildingBlocks.Tenancy;

namespace Fms.Inventory.Domain;

/// <summary>A place stock is kept (a storeroom, a van). Optionally tied to a site so planners can prefer the nearest.</summary>
public sealed class Store : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? NameAr { get; set; }
    public Guid? SiteId { get; set; }
    public bool IsArchived { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>A part or consumable (a filter, a belt). The same item can be held in many stores; quantities live on <see cref="StockLevel"/>.</summary>
public sealed class Item : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? NameAr { get; set; }
    public string? Description { get; set; }

    /// <summary>How it is counted: "pcs", "litre", "m". Free text, shown beside every quantity.</summary>
    public string Unit { get; set; } = "pcs";

    public string? Barcode { get; set; }
    public bool IsArchived { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// One item in one store. <see cref="OnHand"/> is what is physically there; <see cref="Reserved"/> is promised to work orders, so what can still
/// be promised or used by others is <see cref="StockRules.Available"/>. The average cost is moving-weighted over receipts.
/// </summary>
public sealed class StockLevel : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid StoreId { get; set; }
    public Guid ItemId { get; set; }
    public decimal OnHand { get; set; }
    public decimal Reserved { get; set; }
    public decimal AverageCost { get; set; }
    public decimal? MinQty { get; set; }
    public decimal? MaxQty { get; set; }

    /// <summary>True while a low-stock notice has been sent and stock has not recovered above the minimum, so a notice goes out once per dip.</summary>
    public bool LowNotified { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

public enum MovementType
{
    Receipt = 1,
    Issue = 2,
    Return = 3,
    TransferOut = 4,
    TransferIn = 5,
    Adjustment = 6,
    CountAdjustment = 7,
}

/// <summary>Every change of on-hand stock, once, forever. Corrections are new movements (a return, an adjustment), never edits.</summary>
public sealed class StockMovement : ITenantOwned, IAppendOnly
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid StoreId { get; set; }
    public Guid ItemId { get; set; }
    public MovementType Type { get; set; }

    /// <summary>Signed: positive adds to stock, negative takes away.</summary>
    public decimal Quantity { get; set; }

    public decimal UnitCost { get; set; }
    public decimal BalanceAfter { get; set; }
    public DateTimeOffset At { get; set; }
    public Guid? By { get; set; }
    public Guid? WorkOrderId { get; set; }
    public Guid? TransferId { get; set; }
    public Guid? StockTakeId { get; set; }

    /// <summary>An outside document: a delivery note or invoice number on a receipt.</summary>
    public string? Reference { get; set; }

    public string? Reason { get; set; }
}

public enum ReservationSource { Manual = 1, PmTemplate = 2 }

/// <summary>Stock held back for a work order. <see cref="Quantity"/> is what is still held; issuing or releasing reduces it, so a finished reservation stays as a row with zero.</summary>
public sealed class Reservation : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid WorkOrderId { get; set; }
    public Guid StoreId { get; set; }
    public Guid ItemId { get; set; }
    public decimal Quantity { get; set; }
    public ReservationSource Source { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>A part a preventive-maintenance template needs for each visit; reserved automatically when the visit's work order is created.</summary>
public sealed class PmTemplatePart : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PmTemplateId { get; set; }
    public Guid ItemId { get; set; }
    public decimal Quantity { get; set; }

    /// <summary>Where to take it from; empty means the best store for the work order's site.</summary>
    public Guid? StoreId { get; set; }
}

public enum StockTakeStatus { Counting = 1, Submitted = 2, Approved = 3, Cancelled = 4 }

/// <summary>A physical count of one store. Quantities on screen are frozen when it starts; approval posts the differences as adjustments.</summary>
public sealed class StockTake : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid StoreId { get; set; }
    public StockTakeStatus Status { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public Guid StartedBy { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public Guid? SubmittedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public Guid? DecidedBy { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

public sealed class StockTakeLine : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid StockTakeId { get; set; }
    public Guid ItemId { get; set; }

    /// <summary>On hand when the count started.</summary>
    public decimal SystemQty { get; set; }

    public decimal UnitCost { get; set; }
    public decimal? CountedQty { get; set; }
}

/// <summary>The arithmetic of stock, kept free of the database so it can be tested exhaustively.</summary>
public static class StockRules
{
    public const int QuantityDecimals = 4;
    public const int CostDecimals = 4;
    public const decimal MaxQuantity = 1_000_000_000m;
    public const decimal MaxUnitCost = 1_000_000_000m;

    /// <summary>What can still be promised or used by others: on hand less what is held for work orders.</summary>
    public static decimal Available(decimal onHand, decimal reserved) => onHand - reserved;

    /// <summary>Moving weighted average after adding stock. An empty store takes the new cost outright.</summary>
    public static decimal AverageAfterReceipt(decimal onHand, decimal averageCost, decimal quantity, decimal unitCost)
    {
        var total = onHand + quantity;
        if (onHand <= 0 || total <= 0) return Math.Round(unitCost, CostDecimals);
        return Math.Round((onHand * averageCost + quantity * unitCost) / total, CostDecimals);
    }

    /// <summary>Low when what is available has fallen to the minimum or below. No minimum, never low.</summary>
    public static bool IsLow(decimal onHand, decimal reserved, decimal? minQty) => minQty is { } min && Available(onHand, reserved) <= min;

    /// <summary>How much to order to get back up to the maximum (or at least to twice the minimum when no maximum is set). Zero when not low.</summary>
    public static decimal SuggestedOrder(decimal onHand, decimal reserved, decimal? minQty, decimal? maxQty)
    {
        if (!IsLow(onHand, reserved, minQty)) return 0;
        var target = maxQty ?? (minQty ?? 0) * 2;
        return Math.Max(0, target - Available(onHand, reserved));
    }

    /// <summary>True when the quantity has no more decimals than we store and is within bounds.</summary>
    public static bool IsValidQuantity(decimal quantity) => quantity > 0 && quantity <= MaxQuantity && decimal.Round(quantity, QuantityDecimals) == quantity;

    public static bool IsValidCost(decimal cost) => cost >= 0 && cost <= MaxUnitCost && decimal.Round(cost, CostDecimals) == cost;
}
