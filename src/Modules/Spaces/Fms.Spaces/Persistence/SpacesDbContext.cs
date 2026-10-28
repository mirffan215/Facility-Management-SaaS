using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Fms.Spaces.Domain;
using Microsoft.EntityFrameworkCore;

namespace Fms.Spaces.Persistence;

public sealed class SpacesDbContext(DbContextOptions<SpacesDbContext> options, ITenantContext tenant, PendingWrites? pending = null)
    : TenantDbContext(options, tenant, pending)
{
    public const string Schema = "spaces";

    public DbSet<SpaceProfile> Profiles => Set<SpaceProfile>();
    public DbSet<FloorPlan> Plans => Set<FloorPlan>();
    public DbSet<PlanPin> Pins => Set<PlanPin>();
    public DbSet<Allocation> Allocations => Set<Allocation>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        PendingWrites.MapSharedTables(b);

        b.Entity<SpaceProfile>(e =>
        {
            e.ToTable("Profiles", t => t.HasCheckConstraint("CK_Profiles_Capacity", "[Capacity] BETWEEN 0 AND 100000"));
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.HasIndex(x => new { x.TenantId, x.LocationId }).IsUnique();
        });

        b.Entity<FloorPlan>(e =>
        {
            e.ToTable("Plans");
            e.Property(x => x.FileName).HasMaxLength(255).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
            e.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            e.Property(x => x.StorageKey).HasMaxLength(500).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.FloorId, x.SupersededAt });
        });

        b.Entity<PlanPin>(e =>
        {
            e.ToTable("Pins", t => t.HasCheckConstraint("CK_Pins_Position", "[X] BETWEEN 0 AND 1 AND [Y] BETWEEN 0 AND 1"));
            e.HasIndex(x => new { x.TenantId, x.FloorId, x.Kind, x.TargetId }).IsUnique();
        });

        b.Entity<Allocation>(e =>
        {
            e.ToTable("Allocations", t =>
            {
                t.HasCheckConstraint("CK_Allocations_Dates", "[EndOn] IS NULL OR [EndOn] >= [StartOn]");
                t.HasCheckConstraint("CK_Allocations_Headcount", "[Headcount] BETWEEN 1 AND 100000");
                t.HasCheckConstraint("CK_Allocations_Occupant", "([Kind] = 1 AND [UserId] IS NOT NULL) OR ([Kind] = 2 AND [TeamName] IS NOT NULL)");
            });
            e.Property(x => x.TeamName).HasMaxLength(200);
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.EndReason).HasMaxLength(500);
            e.HasIndex(x => new { x.TenantId, x.SpaceId, x.StartOn });
            e.HasIndex(x => new { x.TenantId, x.UserId });
        });

        base.OnModelCreating(b);
    }
}
