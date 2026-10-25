using Fms.BuildingBlocks.Audit;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Fms.Platform.Domain;
using Microsoft.EntityFrameworkCore;

namespace Fms.Platform.Persistence;

public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options, ITenantContext tenant)
    : TenantDbContext(options, tenant)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantIdentityMapping> TenantIdentityMappings => Set<TenantIdentityMapping>();
    public DbSet<User> Users => Set<User>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Tenant>(e =>
        {
            e.ToTable("Tenants");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        });

        b.Entity<TenantIdentityMapping>(e =>
        {
            e.ToTable("TenantIdentityMappings");
            e.Property(x => x.Provider).HasMaxLength(64).IsRequired();
            e.Property(x => x.ExternalTenantId).HasMaxLength(200).IsRequired();
            e.HasIndex(x => new { x.Provider, x.ExternalTenantId }).IsUnique();
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<User>(e =>
        {
            e.ToTable("Users");
            e.Property(x => x.Provider).HasMaxLength(64).IsRequired();
            e.Property(x => x.Subject).HasMaxLength(256).IsRequired();
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.HasIndex(x => new { x.TenantId, x.Provider, x.Subject }).IsUnique();
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<AuditEvent>(e =>
        {
            e.ToTable("AuditEvents");
            e.Property(x => x.Action).HasMaxLength(100).IsRequired();
            e.Property(x => x.EntityType).HasMaxLength(100).IsRequired();
            e.Property(x => x.EntityId).HasMaxLength(100);
            e.Property(x => x.CorrelationId).HasMaxLength(100);
            e.HasIndex(x => new { x.TenantId, x.OccurredAt });
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        base.OnModelCreating(b);
    }
}
