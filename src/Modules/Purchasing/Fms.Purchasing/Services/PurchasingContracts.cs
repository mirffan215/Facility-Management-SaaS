using Fms.Purchasing.Domain;

namespace Fms.Purchasing.Services;

public sealed record SettingsDto(decimal Level1Limit, decimal Level2Limit, decimal OverrunTolerancePercent, string CurrencyCode, string? RowVersion);

public sealed record SaveSettingsRequest(decimal? Level1Limit, decimal? Level2Limit, decimal? OverrunTolerancePercent, string? CurrencyCode, string? RowVersion);

public sealed record RequestLineInput(Guid? ItemId, Guid? StoreId, string? Description, string? Unit, decimal? Quantity, decimal? EstimatedUnitPrice);

public sealed record SaveRequestRequest(string? Title, string? Justification, Guid? PreferredVendorId, Guid? WorkOrderId, IReadOnlyList<RequestLineInput>? Lines, string? RowVersion);

public sealed record RequestLineDto(Guid Id, int Position, Guid? ItemId, string? ItemCode, Guid? StoreId, string? StoreName, string Description, string Unit, decimal Quantity, decimal EstimatedUnitPrice, decimal Total);

public sealed record RequestDto(
    Guid Id, int Number, string Display, string Title, string? Justification, RequestStatus Status, Guid RequestedBy, string? RequestedByName, DateTimeOffset RequestedAt, Guid? PreferredVendorId,
    string? PreferredVendorName, Guid? WorkOrderId, decimal Total, int RequiredLevel, DateTimeOffset? SubmittedAt, string? DecidedByName, DateTimeOffset? DecidedAt, string? DecisionNote,
    Guid? OrderId, string? OrderDisplay, bool CanApprove, IReadOnlyList<RequestLineDto>? Lines, string RowVersion);

public sealed record DecisionRequest(string? Note, string? RowVersion);

public sealed record PriceOverride(Guid? LineId, decimal? UnitPrice);

public sealed record OrderFromRequestRequest(Guid? VendorId, DateOnly? ExpectedOn, string? Notes, IReadOnlyList<PriceOverride>? Prices, string? RowVersion);

public sealed record OrderLineDto(
    Guid Id, int Position, Guid? ItemId, string? ItemCode, Guid? StoreId, string? StoreName, string Description, string Unit, decimal Quantity, decimal UnitPrice, decimal Total, decimal ReceivedQty,
    decimal Outstanding);

public sealed record ReceiptDto(Guid Id, Guid LineId, decimal Quantity, DateTimeOffset ReceivedAt, string? ReceivedByName, string? DeliveryNote);

public sealed record OrderDto(
    Guid Id, int Number, string Display, Guid RequestId, string? RequestDisplay, Guid VendorId, string VendorCode, string VendorName, OrderStatus Status, string? IssuedByName, DateTimeOffset IssuedAt,
    DateOnly? ExpectedOn, string CurrencyCode, decimal Total, string? Notes, DateTimeOffset? AcknowledgedAt, DateTimeOffset? CancelledAt, string? CancelReason, string? ErpReference,
    IReadOnlyList<OrderLineDto>? Lines, IReadOnlyList<ReceiptDto>? Receipts, string RowVersion);

public sealed record ReceiveLineInput(Guid? LineId, decimal? Quantity);

public sealed record ReceiveRequest(string? DeliveryNote, IReadOnlyList<ReceiveLineInput>? Lines);

public sealed record CancelOrderRequest(string? Reason, string? RowVersion);

public enum PurchasingEvent { Submitted = 1, Approved = 2, Rejected = 3, Ordered = 4 }

public sealed record PurchasingEventMessage(Guid RequestId, PurchasingEvent Event, Guid? ActorId);

public static class PurchasingMessages
{
    public const string Event = "purchasing.event";
}
