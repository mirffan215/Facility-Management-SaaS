using Fms.BuildingBlocks.Tenancy;

namespace Fms.BuildingBlocks.Audit;

/// <summary>Marks entities that may only ever be inserted: updates and deletes are refused on save.</summary>
public interface IAppendOnly;

/// <summary>Immutable audit record. Stored per tenant; never updated or deleted.</summary>
public sealed class AuditEvent : ITenantOwned, IAppendOnly
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public Guid? ActorId { get; set; }
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string? EntityId { get; set; }
    public string? ChangeSummaryJson { get; set; }
    public string? CorrelationId { get; set; }
}

public sealed class AppendOnlyViolationException(string entity)
    : InvalidOperationException($"{entity} is append-only; updates and deletes are not allowed.");
