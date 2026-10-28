using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Fms.Reporting.Domain;
using Microsoft.EntityFrameworkCore;

namespace Fms.Reporting.Persistence;

public sealed class ReportingDbContext(DbContextOptions<ReportingDbContext> options, ITenantContext tenant, PendingWrites? pending = null)
    : TenantDbContext(options, tenant, pending)
{
    public const string Schema = "reporting";

    public DbSet<FeedToken> FeedTokens => Set<FeedToken>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        PendingWrites.MapSharedTables(b);

        b.Entity<FeedToken>(e =>
        {
            e.ToTable("FeedTokens");
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Prefix).HasMaxLength(16).IsRequired();
            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.Scope).HasMaxLength(500).IsRequired();
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.CreatedAt });
        });

        base.OnModelCreating(b);
    }
}
