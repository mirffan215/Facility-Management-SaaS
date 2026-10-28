using Fms.Ai.Domain;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Fms.Ai.Persistence;

public sealed class AiDbContext(DbContextOptions<AiDbContext> options, ITenantContext tenant, PendingWrites? pending = null)
    : TenantDbContext(options, tenant, pending)
{
    public const string Schema = "ai";

    public DbSet<AiSettings> Settings => Set<AiSettings>();
    public DbSet<AiUsage> Usage => Set<AiUsage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        PendingWrites.MapSharedTables(b);

        b.Entity<AiSettings>(e =>
        {
            e.ToTable("Settings", t => t.HasCheckConstraint("CK_Settings_DailyLimit", "[DailyLimit] >= 0 AND [DailyLimit] <= 5000"));
            e.HasIndex(x => x.TenantId).IsUnique();
        });

        b.Entity<AiUsage>(e =>
        {
            e.ToTable("Usage");
            e.HasIndex(x => new { x.TenantId, x.At });
            e.HasIndex(x => new { x.TenantId, x.UserId, x.At });
        });

        base.OnModelCreating(b);
    }
}
