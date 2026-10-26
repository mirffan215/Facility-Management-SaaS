using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Fms.Work.Domain;
using Microsoft.EntityFrameworkCore;

namespace Fms.Work.Persistence;

public sealed class WorkDbContext(DbContextOptions<WorkDbContext> options, ITenantContext tenant, PendingWrites? pending = null)
    : TenantDbContext(options, tenant, pending)
{
    public const string Schema = "work";

    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();
    public DbSet<WorkOrderEvent> WorkOrderEvents => Set<WorkOrderEvent>();
    public DbSet<WorkOrderComment> WorkOrderComments => Set<WorkOrderComment>();
    public DbSet<WorkCategory> Categories => Set<WorkCategory>();
    public DbSet<WorkSettings> Settings => Set<WorkSettings>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        PendingWrites.MapSharedTables(b);

        b.Entity<WorkCategory>(e =>
        {
            e.ToTable("Categories");
            e.Property(x => x.Code).HasMaxLength(50).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.NameAr).HasMaxLength(200);
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique().HasFilter("[IsArchived] = 0");
        });

        b.Entity<WorkSettings>(e =>
        {
            e.ToTable("Settings");
            e.HasIndex(x => x.TenantId).IsUnique();
        });

        b.Entity<WorkOrder>(e =>
        {
            e.ToTable("WorkOrders");
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(4000);
            e.Property(x => x.StatusReason).HasMaxLength(500);
            e.Property(x => x.ClosureNotes).HasMaxLength(2000);
            e.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Status });
            e.HasIndex(x => new { x.TenantId, x.RequestedBy });
            e.HasIndex(x => new { x.TenantId, x.AssignedTo });
            e.HasIndex(x => new { x.TenantId, x.AssetId });
            e.HasIndex(x => new { x.TenantId, x.LocationId });
            e.HasOne<WorkCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<WorkOrderEvent>(e =>
        {
            e.ToTable("WorkOrderEvents");
            e.Property(x => x.Note).HasMaxLength(2000);
            e.HasIndex(x => new { x.TenantId, x.WorkOrderId, x.At });
            e.HasOne<WorkOrder>().WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<WorkOrderComment>(e =>
        {
            e.ToTable("WorkOrderComments");
            e.Property(x => x.Body).HasMaxLength(2000).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.WorkOrderId, x.At });
            e.HasOne<WorkOrder>().WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        });

        base.OnModelCreating(b);
    }
}
