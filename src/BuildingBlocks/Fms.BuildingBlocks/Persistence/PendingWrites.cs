using Fms.BuildingBlocks.Audit;
using Fms.BuildingBlocks.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Fms.BuildingBlocks.Persistence;

/// <summary>
/// Audit events and outbox messages produced during a request or job. They are moved into the next module context that saves,
/// so they commit or roll back together with that context's change regardless of which module made it. Scoped.
/// </summary>
public sealed class PendingWrites
{
    private readonly List<object> _items = [];

    public void Add(object item) => _items.Add(item);

    public bool Any => _items.Count > 0;

    /// <summary>Adds every pending item the context maps to its change tracker, and removes it from the buffer.</summary>
    internal void DrainInto(DbContext context)
    {
        if (_items.Count == 0) return;

        foreach (var item in _items.Where(i => context.Model.FindEntityType(i.GetType()) is not null).ToList())
        {
            context.Add(item);
            _items.Remove(item);
        }
    }

    /// <summary>
    /// Mapping for contexts that do not own the migrations of the shared tables. The Platform module creates AuditEvents and
    /// OutboxMessages; other modules map them (excluded from their migrations) so their SaveChanges can write them.
    /// </summary>
    public static void MapSharedTables(ModelBuilder b)
    {
        b.Entity<AuditEvent>(e =>
        {
            e.ToTable("AuditEvents", "dbo", t => t.ExcludeFromMigrations());
            e.Property(x => x.Action).HasMaxLength(100).IsRequired();
            e.Property(x => x.EntityType).HasMaxLength(100).IsRequired();
            e.Property(x => x.EntityId).HasMaxLength(100);
            e.Property(x => x.CorrelationId).HasMaxLength(100);
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("OutboxMessages", "dbo", t => t.ExcludeFromMigrations());
            e.Property(x => x.Type).HasMaxLength(100).IsRequired();
            e.Property(x => x.PayloadJson).IsRequired();
            e.Property(x => x.LockedBy).HasMaxLength(100);
            e.Property(x => x.LastError).HasMaxLength(2000);
        });
    }
}
