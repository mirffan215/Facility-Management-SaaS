using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Fms.Integration.Domain;
using Microsoft.EntityFrameworkCore;

namespace Fms.Integration.Persistence;

public sealed class IntegrationDbContext(DbContextOptions<IntegrationDbContext> options, ITenantContext tenant, PendingWrites? pending = null)
    : TenantDbContext(options, tenant, pending)
{
    public const string Schema = "integration";

    public DbSet<ErpSettings> ErpSettings => Set<ErpSettings>();
    public DbSet<ErpExportItem> ErpItems => Set<ErpExportItem>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        PendingWrites.MapSharedTables(b);

        b.Entity<ErpSettings>(e =>
        {
            e.ToTable("ErpSettings");
            e.Property(x => x.Adapter).HasMaxLength(50).IsRequired();
            e.Property(x => x.CompanyId).HasMaxLength(50).IsRequired();
            e.HasIndex(x => x.TenantId).IsUnique();
        });

        b.Entity<ErpExportItem>(e =>
        {
            e.ToTable("ErpExportItems");
            e.Property(x => x.IdempotencyKey).HasMaxLength(100).IsRequired();
            e.Property(x => x.LastError).HasMaxLength(2000);
            e.Property(x => x.ErpReference).HasMaxLength(100);
            e.Property(x => x.ReconcileDetail).HasMaxLength(500);
            e.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Entity, x.EntityId, x.Revision });
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
        });

        base.OnModelCreating(b);
    }
}
