using Fms.BuildingBlocks.Integration;
using Fms.BuildingBlocks.Messaging;
using Fms.BuildingBlocks.Persistence;
using Fms.Integration.Domain;
using Fms.Integration.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Integration.Services;

/// <summary>
/// Turns a module's "this changed" message into something to send. Does nothing when the tenant has not turned the connection on. Safe to run twice for the same message:
/// the key (entity, id, revision) is unique. A newer version queued before an older one is sent makes the older one pointless, so it is marked superseded.
/// </summary>
public sealed class ErpExportHandler(IntegrationDbContext db, TimeProvider clock) : IOutboxHandler
{
    public string MessageType => ErpMessages.Export;

    public async Task HandleAsync(OutboxMessageContext message, CancellationToken ct)
    {
        var m = message.Payload<ErpExportMessage>();
        var settings = await db.ErpSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (settings is not { Enabled: true }) return; // not connected: nothing to send, and nothing is kept to send later

        var key = $"{m.Entity}:{m.EntityId}:{m.Revision}";
        if (await db.ErpItems.AnyAsync(i => i.IdempotencyKey == key, ct)) return;

        var now = clock.GetUtcNow();
        var newer = await db.ErpItems.AnyAsync(i => i.Entity == m.Entity && i.EntityId == m.EntityId && i.Revision > m.Revision && i.Status != ExportStatus.Superseded, ct);
        db.ErpItems.Add(new ErpExportItem
        {
            Entity = m.Entity, EntityId = m.EntityId, Revision = m.Revision, IdempotencyKey = key, PayloadJson = m.PayloadJson, CreatedAt = now, NextAttemptAt = now,
            Status = newer ? ExportStatus.Superseded : ExportStatus.Pending,
        });
        if (!newer)
        {
            foreach (var older in await db.ErpItems.Where(i => i.Entity == m.Entity && i.EntityId == m.EntityId && i.Revision < m.Revision && i.Status == ExportStatus.Pending).ToListAsync(ct))
                older.Status = ExportStatus.Superseded;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear(); // the same message was handled a moment ago
        }
    }
}
