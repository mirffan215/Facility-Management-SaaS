using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Security;
using Fms.BuildingBlocks.Tenancy;
using Fms.Visitors.Domain;
using Microsoft.EntityFrameworkCore;

namespace Fms.Visitors.Persistence;

public sealed class VisitorsDbContext(DbContextOptions<VisitorsDbContext> options, ITenantContext tenant, PendingWrites? pending = null)
    : TenantDbContext(options, tenant, pending)
{
    public const string Schema = "visitors";

    public DbSet<Visit> Visits => Set<Visit>();
    public DbSet<WatchlistEntry> Watchlist => Set<WatchlistEntry>();
    public DbSet<VisitorSettings> Settings => Set<VisitorSettings>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        PendingWrites.MapSharedTables(b);

        b.Entity<Visit>(e =>
        {
            e.ToTable("Visits", t => t.HasCheckConstraint("CK_Visits_Times", "[ExpectedUntil] IS NULL OR [ExpectedUntil] >= [ExpectedAt]"));
            e.Property(x => x.HostName).HasMaxLength(200).IsRequired();
            e.Property(x => x.SiteName).HasMaxLength(200).IsRequired();
            e.Property(x => x.VisitorName).HasMaxLength(200).IsRequired();
            e.Property(x => x.VisitorCompany).HasMaxLength(200);
            // Contact details are encrypted at rest when the operator has configured a key. A value is several times longer than the text, so the columns are wide.
            e.Property(x => x.VisitorEmail).HasMaxLength(1600).HasConversion(FieldEncryption.Converter("visitors.Visits.VisitorEmail"));
            e.Property(x => x.VisitorPhone).HasMaxLength(400).HasConversion(FieldEncryption.Converter("visitors.Visits.VisitorPhone"));
            e.Property(x => x.Purpose).HasMaxLength(500);
            e.Property(x => x.PassHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.CancelReason).HasMaxLength(500);
            e.Property(x => x.BadgeNumber).HasMaxLength(20);
            e.Property(x => x.OverrideReason).HasMaxLength(500);
            e.HasIndex(x => x.PassHash).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.HostId, x.ExpectedAt });
            e.HasIndex(x => new { x.TenantId, x.Status, x.ExpectedAt });
            e.HasIndex(x => new { x.TenantId, x.SiteId, x.Status });
        });

        b.Entity<WatchlistEntry>(e =>
        {
            e.ToTable("Watchlist");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.NameKey).HasMaxLength(200).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(500).IsRequired();
            e.Property(x => x.RemoveReason).HasMaxLength(500);
            e.HasIndex(x => new { x.TenantId, x.NameKey });
        });

        b.Entity<VisitorSettings>(e =>
        {
            e.ToTable("Settings", t => t.HasCheckConstraint("CK_Settings_Retention", "[RetentionDays] BETWEEN 30 AND 3650"));
            e.Property(x => x.InductionText).HasMaxLength(4000);
            e.HasIndex(x => x.TenantId).IsUnique();
        });

        base.OnModelCreating(b);
    }
}
