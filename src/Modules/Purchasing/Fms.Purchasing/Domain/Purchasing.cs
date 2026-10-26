using System.ComponentModel.DataAnnotations;
using Fms.BuildingBlocks.Audit;
using Fms.BuildingBlocks.Security;
using Fms.BuildingBlocks.Tenancy;

namespace Fms.Purchasing.Domain;

/// <summary>The money limits that decide who must approve a request. One row per tenant; until it is saved, the defaults apply.</summary>
public sealed class PurchasingSettings : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>Requests up to this total need a first-level approver; above it, second level.</summary>
    public decimal Level1Limit { get; set; } = PurchasingRules.DefaultLevel1Limit;

    /// <summary>Requests up to this total need at most a second-level approver; above it, third level.</summary>
    public decimal Level2Limit { get; set; } = PurchasingRules.DefaultLevel2Limit;

    /// <summary>How far above the approved total an order may go (a price change since the estimate) before it needs a fresh approval.</summary>
    public decimal OverrunTolerancePercent { get; set; } = PurchasingRules.DefaultTolerancePercent;

    public string CurrencyCode { get; set; } = "SAR";

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

public enum RequestStatus { Draft = 1, Submitted = 2, Approved = 3, Rejected = 4, Cancelled = 5, Ordered = 6 }

public sealed class PurchaseRequest : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string? Justification { get; set; }
    public RequestStatus Status { get; set; } = RequestStatus.Draft;
    public Guid RequestedBy { get; set; }
    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>The vendor the requester has in mind. The buyer may choose another when ordering.</summary>
    public Guid? PreferredVendorId { get; set; }

    public Guid? WorkOrderId { get; set; }
    public decimal Total { get; set; }

    /// <summary>1, 2 or 3: the approval level the total needs, fixed when submitted.</summary>
    public int RequiredLevel { get; set; }

    public DateTimeOffset? SubmittedAt { get; set; }
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

public sealed class PurchaseRequestLine : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid RequestId { get; set; }
    public int Position { get; set; }

    /// <summary>The stock item being bought, or null for a service or something not held in stock.</summary>
    public Guid? ItemId { get; set; }

    /// <summary>Where received stock goes. Required with an item.</summary>
    public Guid? StoreId { get; set; }

    public string Description { get; set; } = "";
    public string Unit { get; set; } = "pcs";
    public decimal Quantity { get; set; }
    public decimal EstimatedUnitPrice { get; set; }
}

public enum OrderStatus { Issued = 1, Acknowledged = 2, PartiallyReceived = 3, Received = 4, Cancelled = 5 }

public sealed class PurchaseOrder : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public int Number { get; set; }
    public Guid RequestId { get; set; }
    public Guid VendorId { get; set; }

    /// <summary>The vendor as it was named when the order was issued, so the order reads the same later.</summary>
    public string VendorCode { get; set; } = "";

    public string VendorName { get; set; } = "";
    public OrderStatus Status { get; set; } = OrderStatus.Issued;
    public Guid IssuedBy { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateOnly? ExpectedOn { get; set; }
    public string CurrencyCode { get; set; } = "SAR";
    public decimal Total { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public Guid? AcknowledgedBy { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelReason { get; set; }

    /// <summary>The order's reference in the accounting system, filled in by the ERP integration. Never typed by hand.</summary>
    public string? ErpReference { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

public sealed class PurchaseOrderLine : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OrderId { get; set; }
    public int Position { get; set; }
    public Guid? ItemId { get; set; }
    public Guid? StoreId { get; set; }
    public string Description { get; set; } = "";
    public string Unit { get; set; } = "pcs";
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal ReceivedQty { get; set; }
}

/// <summary>Goods that arrived against an order line. Never edited: a mistake is dealt with in stock (an adjustment), not by rewriting what was delivered.</summary>
public sealed class GoodsReceipt : ITenantOwned, IAppendOnly
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid OrderId { get; set; }
    public Guid LineId { get; set; }
    public decimal Quantity { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public Guid? ReceivedBy { get; set; }
    public string? DeliveryNote { get; set; }
}

/// <summary>The rules of approval and receiving, free of the database so they can be tested exhaustively.</summary>
public static class PurchasingRules
{
    public const decimal DefaultLevel1Limit = 5_000m;
    public const decimal DefaultLevel2Limit = 50_000m;
    public const decimal DefaultTolerancePercent = 10m;
    public const decimal MaxAmount = 1_000_000_000_000m;

    public static decimal LineTotal(decimal quantity, decimal unitPrice) => Math.Round(quantity * unitPrice, 2);

    /// <summary>The approval level a total needs. A total exactly at a limit still falls in the lower level.</summary>
    public static int RequiredLevel(decimal total, decimal level1Limit, decimal level2Limit) => total <= level1Limit ? 1 : total <= level2Limit ? 2 : 3;

    /// <summary>The highest level a person's permissions allow: 0 when they cannot approve at all. Each level includes those below it.</summary>
    public static int LevelHeld(IReadOnlySet<string> permissions) =>
        permissions.Contains(Permissions.PurchasingApprove3) ? 3 : permissions.Contains(Permissions.PurchasingApprove2) ? 2 : permissions.Contains(Permissions.PurchasingApprove1) ? 1 : 0;

    /// <summary>Maker-checker: the level must be enough and the approver must be someone other than the requester.</summary>
    public static bool CanApprove(int levelHeld, int requiredLevel, Guid approver, Guid requester) => levelHeld >= requiredLevel && approver != requester;

    /// <summary>An order may exceed the approved total by the tolerance (a price that moved since the estimate), never more.</summary>
    public static bool WithinTolerance(decimal orderTotal, decimal approvedTotal, decimal tolerancePercent) =>
        orderTotal <= Math.Round(approvedTotal * (1 + tolerancePercent / 100m), 2);

    public static OrderStatus StatusAfterReceiving(IEnumerable<(decimal Ordered, decimal Received)> lines)
    {
        var all = lines.ToList();
        return all.All(l => l.Received >= l.Ordered) ? OrderStatus.Received : OrderStatus.PartiallyReceived;
    }

    public static string DisplayRequest(int number) => "PR-" + number.ToString("000000");

    public static string DisplayOrder(int number) => "PO-" + number.ToString("000000");
}
