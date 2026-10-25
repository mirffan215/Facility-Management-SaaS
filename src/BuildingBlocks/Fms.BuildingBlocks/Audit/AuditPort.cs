using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Security;

namespace Fms.BuildingBlocks.Audit;

/// <summary>Buffers the audit event so it is saved in the same transaction as the business change, in whichever module context saves.</summary>
public sealed class AuditPort(PendingWrites pending, ICurrentUser user, TimeProvider clock) : IAuditPort
{
    public void Record(string action, string entityType, string? entityId = null, string? changeSummaryJson = null) =>
        pending.Add(new AuditEvent
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
