using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Fms.Energy.Domain;
using Microsoft.EntityFrameworkCore;

namespace Fms.Energy.Persistence;

public sealed class EnergyDbContext(DbContextOptions<EnergyDbContext> options, ITenantContext tenant, PendingWrites? pending = null)
    : TenantDbContext(options, tenant, pending)
{
    public const string Schema = "energy";

    public DbSet<EnergyMeter> Meters => Set<EnergyMeter>();
    public DbSet<EnergyReading> Readings => Set<EnergyReading>();
    public DbSet<ReadingVoid> Voids => Set<ReadingVoid>();
    public DbSet<Tariff> Tariffs => Set<Tariff>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<AlarmRule> AlarmRules => Set<AlarmRule>();
    public DbSet<AlarmEvent> AlarmEvents => Set<AlarmEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        PendingWrites.MapSharedTables(b);

        b.Entity<EnergyMeter>(e =>
        {
            e.ToTable("Meters", t => t.HasCheckConstraint("CK_Meters_Rollover", "[RolloverAt] IS NULL OR [RolloverAt] > 0"));
            e.Property(x => x.Code).HasMaxLength(50).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Unit).HasMaxLength(20).IsRequired();
            e.Property(x => x.LocationName).HasMaxLength(200);
            e.Property(x => x.RolloverAt).HasPrecision(19, 4);
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.LocationId });
        });

        b.Entity<EnergyReading>(e =>
        {
            e.ToTable("Readings", t => t.HasCheckConstraint("CK_Readings_Value", "[Value] >= 0"));
            e.Property(x => x.Value).HasPrecision(19, 4);
            e.Property(x => x.Note).HasMaxLength(200);
            e.HasIndex(x => new { x.TenantId, x.MeterId, x.At }).IsUnique();
        });

        b.Entity<ReadingVoid>(e =>
        {
            e.ToTable("ReadingVoids");
            e.Property(x => x.Reason).HasMaxLength(500).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.ReadingId }).IsUnique();
        });

        b.Entity<Tariff>(e =>
        {
            e.ToTable("Tariffs", t => t.HasCheckConstraint("CK_Tariffs_Price", "[PricePerUnit] >= 0"));
            e.Property(x => x.PricePerUnit).HasPrecision(19, 6);
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.Kind, x.EffectiveFrom }).IsUnique();
        });

        b.Entity<Device>(e =>
        {
            e.ToTable("Devices");
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Prefix).HasMaxLength(12).IsRequired();
            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.MeterIdsJson).IsRequired();
            e.HasIndex(x => x.TokenHash).IsUnique();
        });

        b.Entity<AlarmRule>(e =>
        {
            e.ToTable("AlarmRules", t => t.HasCheckConstraint("CK_AlarmRules_Threshold", "[Threshold] > 0"));
            e.Property(x => x.Threshold).HasPrecision(19, 4);
            e.HasIndex(x => new { x.TenantId, x.MeterId, x.Kind }).IsUnique();
        });

        b.Entity<AlarmEvent>(e =>
        {
            e.ToTable("AlarmEvents");
            e.Property(x => x.Value).HasPrecision(19, 4);
            e.HasIndex(x => new { x.TenantId, x.MeterId, x.At });
        });

        base.OnModelCreating(b);
    }
}
