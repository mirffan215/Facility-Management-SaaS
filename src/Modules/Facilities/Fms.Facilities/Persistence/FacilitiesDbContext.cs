using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Fms.Facilities.Domain;
using Microsoft.EntityFrameworkCore;

namespace Fms.Facilities.Persistence;

public sealed class FacilitiesDbContext(DbContextOptions<FacilitiesDbContext> options, ITenantContext tenant, PendingWrites? pending = null)
    : TenantDbContext(options, tenant, pending)
{
    public const string Schema = "facilities";

    public DbSet<Location> Locations => Set<Location>();
    public DbSet<CustomFieldDefinition> CustomFieldDefinitions => Set<CustomFieldDefinition>();
    public DbSet<AssetClass> AssetClasses => Set<AssetClass>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<Meter> Meters => Set<Meter>();
    public DbSet<MeterReading> MeterReadings => Set<MeterReading>();
    public DbSet<MeterReadingVoid> MeterReadingVoids => Set<MeterReadingVoid>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        PendingWrites.MapSharedTables(b);

        b.Entity<Location>(e =>
        {
            e.ToTable("Locations");
            e.Property(x => x.Code).HasMaxLength(50).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.NameAr).HasMaxLength(200);
            e.Property(x => x.Path).HasMaxLength(200).IsRequired();
            e.Property(x => x.Address).HasMaxLength(500);
            e.Property(x => x.TimeZone).HasMaxLength(64);
            e.Property(x => x.AreaSquareMetres).HasPrecision(12, 2);
            e.Property(x => x.CustomFieldsJson).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique().HasFilter("[IsArchived] = 0");
            e.HasIndex(x => new { x.TenantId, x.Path });
            e.HasIndex(x => new { x.TenantId, x.ParentId });
            e.HasOne<Location>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<AssetClass>(e =>
        {
            e.ToTable("AssetClasses");
            e.Property(x => x.Code).HasMaxLength(50).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.NameAr).HasMaxLength(200);
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique().HasFilter("[IsArchived] = 0");
            e.HasOne<AssetClass>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Asset>(e =>
        {
            e.ToTable("Assets");
            e.Property(x => x.Code).HasMaxLength(50).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.NameAr).HasMaxLength(200);
            e.Property(x => x.StatusReason).HasMaxLength(500);
            e.Property(x => x.Manufacturer).HasMaxLength(200);
            e.Property(x => x.Model).HasMaxLength(200);
            e.Property(x => x.SerialNumber).HasMaxLength(100);
            e.Property(x => x.TagCode).HasMaxLength(20).IsRequired();
            e.Property(x => x.CustomFieldsJson).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.TagCode }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.LocationId });
            e.HasIndex(x => new { x.TenantId, x.AssetClassId });
            e.HasIndex(x => new { x.TenantId, x.ParentAssetId });
            e.HasIndex(x => new { x.TenantId, x.Status });
            e.HasOne<AssetClass>().WithMany().HasForeignKey(x => x.AssetClassId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Location>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Asset>().WithMany().HasForeignKey(x => x.ParentAssetId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Meter>(e =>
        {
            e.ToTable("Meters");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.NameAr).HasMaxLength(200);
            e.Property(x => x.Unit).HasMaxLength(20).IsRequired();
            e.Property(x => x.MinValue).HasPrecision(18, 4);
            e.Property(x => x.MaxValue).HasPrecision(18, 4);
            e.HasIndex(x => new { x.TenantId, x.AssetId, x.Name }).IsUnique().HasFilter("[IsArchived] = 0");
            e.HasOne<Asset>().WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<MeterReading>(e =>
        {
            e.ToTable("MeterReadings");
            e.Property(x => x.Value).HasPrecision(18, 4);
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasIndex(x => new { x.TenantId, x.MeterId, x.ReadAt });
            e.HasIndex(x => new { x.TenantId, x.MeterId, x.ClientId }).IsUnique().HasFilter("[ClientId] IS NOT NULL");
            e.HasOne<Meter>().WithMany().HasForeignKey(x => x.MeterId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<MeterReadingVoid>(e =>
        {
            e.ToTable("MeterReadingVoids");
            e.Property(x => x.Reason).HasMaxLength(500).IsRequired();
            e.HasIndex(x => x.ReadingId).IsUnique();
            e.HasOne<MeterReading>().WithMany().HasForeignKey(x => x.ReadingId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<CustomFieldDefinition>(e =>
        {
            e.ToTable("CustomFieldDefinitions");
            e.Property(x => x.Key).HasMaxLength(50).IsRequired();
            e.Property(x => x.LabelEn).HasMaxLength(200).IsRequired();
            e.Property(x => x.LabelAr).HasMaxLength(200);
            e.Property(x => x.ChoicesJson).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.Target, x.Key }).IsUnique().HasFilter("[IsArchived] = 0");
            e.HasOne<AssetClass>().WithMany().HasForeignKey(x => x.AssetClassId).OnDelete(DeleteBehavior.Restrict);
        });

        base.OnModelCreating(b);
    }
}
