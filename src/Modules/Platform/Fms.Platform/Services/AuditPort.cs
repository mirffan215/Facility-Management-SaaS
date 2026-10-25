using Fms.BuildingBlocks.Audit;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Security;
using Fms.Platform.Persistence;

namespace Fms.Platform.Services;

/// <summary>Adds the audit event to the context so it commits or rolls back with the business change.</summary>
public sealed class AuditPort(PlatformDbContext db, ICurrentUser user, TimeProvider clock) : IAuditPort
{
    public void Record(string action, string entityType, string? entityId = null, string? changeSummaryJson = null) =>
        db.AuditEvents.Add(new AuditEvent
        {
            OccurredAt = clock.GetUtcNow(),
            ActorId = user.UserId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            ChangeSummaryJson = changeSummaryJson,
            CorrelationId = System.Diagnostics.Activity.Current?.TraceId.ToString(),
        });
}
