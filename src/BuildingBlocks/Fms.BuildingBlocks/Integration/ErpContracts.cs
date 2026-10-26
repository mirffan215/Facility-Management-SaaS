namespace Fms.BuildingBlocks.Integration;

public enum ErpEntity { Vendor = 1, PurchaseOrder = 2, GoodsReceipt = 3 }

/// <summary>
/// Messages between the modules that own the data and the ERP integration. A module announces a change with <see cref="ErpMessages.Export"/> (inside the same
/// transaction as the change, through the outbox); the integration queues it for the accounting system; when the system accepts it, the integration
/// tells the owner the system's reference with <see cref="ErpMessages.VendorSynced"/> or <see cref="ErpMessages.OrderSynced"/>.
/// </summary>
public static class ErpMessages
{
    public const string Export = "erp.export";
    public const string VendorSynced = "erp.vendor.synced";
    public const string OrderSynced = "erp.order.synced";
}

/// <summary>A snapshot of what to send, taken when the change happened. <see cref="Revision"/> only has to grow for the same entity (ticks of the change time will do).</summary>
public sealed record ErpExportMessage(ErpEntity Entity, Guid EntityId, long Revision, string PayloadJson);

public sealed record ErpReferenceMessage(Guid EntityId, string ErpReference);

public sealed record ErpExportCommand(string CompanyId, ErpEntity Entity, Guid EntityId, string IdempotencyKey, string PayloadJson);

/// <summary>The accounting system's answer to a send. A failure that is <see cref="Retryable"/> (a timeout, a missing prerequisite) is tried again later; any other is set aside for a person.</summary>
public sealed record ErpResult(bool Success, string? ErpReference, bool Retryable, string? Error);

public sealed record ErpVerifyCommand(string CompanyId, ErpEntity Entity, Guid EntityId, string? ErpReference, string PayloadJson);

public enum ErpMatch { Matched = 1, Mismatch = 2, Missing = 3 }

public sealed record ErpVerification(ErpMatch Match, string? Detail);

/// <summary>
/// The only way the product talks to an accounting system. An adapter must be <b>idempotent</b>: sending the same <see cref="ErpExportCommand.IdempotencyKey"/>
/// twice leaves the system with one record, and the second call returns the same reference. It must never write to the ERP's database directly, only through
/// its documented interfaces.
/// </summary>
public interface IErpAdapter
{
    string Name { get; }

    Task<ErpResult> ExportAsync(ErpExportCommand command, CancellationToken ct = default);

    /// <summary>Checks that the system holds what was sent (compares what it has with the payload).</summary>
    Task<ErpVerification> VerifyAsync(ErpVerifyCommand command, CancellationToken ct = default);
}
