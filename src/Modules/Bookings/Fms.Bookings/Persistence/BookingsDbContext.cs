using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Fms.Bookings.Domain;
using Microsoft.EntityFrameworkCore;

namespace Fms.Bookings.Persistence;

public sealed class BookingsDbContext(DbContextOptions<BookingsDbContext> options, ITenantContext tenant, PendingWrites? pending = null)
    : TenantDbContext(options, tenant, pending)
{
    public const string Schema = "bookings";

    public DbSet<BookableResource> Resources => Set<BookableResource>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<CalendarLink> CalendarLinks => Set<CalendarLink>();
    public DbSet<CalendarFeedToken> FeedTokens => Set<CalendarFeedToken>();
    public DbSet<CalendarSyncSettings> SyncSettings => Set<CalendarSyncSettings>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        PendingWrites.MapSharedTables(b);

        b.Entity<BookableResource>(e =>
        {
            e.ToTable("Resources", t =>
            {
                t.HasCheckConstraint("CK_Resources_Durations", "[MinMinutes] >= 5 AND [MaxMinutes] >= [MinMinutes] AND [MaxMinutes] <= 1440 AND [BufferMinutes] BETWEEN 0 AND 480");
                t.HasCheckConstraint("CK_Resources_Days", "[DaysMask] BETWEEN 1 AND 127");
                t.HasCheckConstraint("CK_Resources_Capacity", "[Capacity] BETWEEN 1 AND 10000");
            });
            e.Property(x => x.Code).HasMaxLength(50).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.NameAr).HasMaxLength(200);
            e.Property(x => x.TimeZone).HasMaxLength(64).IsRequired();
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        });

        b.Entity<Booking>(e =>
        {
            e.ToTable("Bookings", t => t.HasCheckConstraint("CK_Bookings_Times", "[EndsAt] > [StartsAt]"));
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.Property(x => x.DecisionNote).HasMaxLength(500);
            e.Property(x => x.CancelReason).HasMaxLength(500);
            e.HasIndex(x => new { x.TenantId, x.ResourceId, x.StartsAt });
            e.HasIndex(x => new { x.TenantId, x.OrganizerId, x.StartsAt });
            e.HasIndex(x => new { x.TenantId, x.Status, x.StartsAt });
        });

        b.Entity<CalendarLink>(e =>
        {
            e.ToTable("CalendarLinks");
            e.Property(x => x.ExternalEventId).HasMaxLength(200).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.BookingId }).IsUnique();
        });

        b.Entity<CalendarFeedToken>(e =>
        {
            e.ToTable("CalendarFeedTokens");
            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.UserId }).IsUnique();
        });

        b.Entity<CalendarSyncSettings>(e =>
        {
            e.ToTable("CalendarSyncSettings");
            e.Property(x => x.Adapter).HasMaxLength(50).IsRequired();
            e.HasIndex(x => x.TenantId).IsUnique();
        });

        base.OnModelCreating(b);
    }
}
