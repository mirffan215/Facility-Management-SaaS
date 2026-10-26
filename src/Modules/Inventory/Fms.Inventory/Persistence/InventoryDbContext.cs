using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Fms.Inventory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Fms.Inventory.Persistence;

public sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options, ITenantContext tenant, PendingWrites? pending = null)
    : TenantDbContext(options, tenant, pending)
{
    public const string Schema = "inventory";

    public DbSet<Store> Stores => Set<Store>();
    public DbSet<Item> Items => Set<Item>();
    public DbSet<StockLevel> Levels => Set<StockLevel>();
    public DbSet<StockMovement> Movements => Set<StockMovement>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<PmTemplatePart> PmTemplateParts => Set<PmTemplatePart>();
    public DbSet<StockTake> StockTakes => Set<StockTake>();
    public DbSet<StockTakeLine> StockTakeLines => Set<StockTakeLine>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        PendingWrites.MapSharedTables(b);

        b.Entity<Store>(e =>
        {
            e.ToTable("Stores");
            e.Property(x => x.Code).HasMaxLength(50).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.NameAr).HasMaxLength(200);
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        });

        b.Entity<Item>(e =>
        {
            e.ToTable("Items");
            e.Property(x => x.Code).HasMaxLength(50).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.NameAr).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.Unit).HasMaxLength(20).IsRequired();
            e.Property(x => x.Barcode).HasMaxLength(100);
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Barcode }).IsUnique().HasFilter("[Barcode] IS NOT NULL");
        });

        b.Entity<StockLevel>(e =>
        {
            e.ToTable("StockLevels", t => t.HasCheckConstraint("CK_StockLevels_Quantities", "[OnHand] >= 0 AND [Reserved] >= 0 AND [Reserved] <= [OnHand]"));
            e.Property(x => x.OnHand).HasPrecision(18, 4);
            e.Property(x => x.Reserved).HasPrecision(18, 4);
            e.Property(x => x.AverageCost).HasPrecision(18, 4);
            e.Property(x => x.MinQty).HasPrecision(18, 4);
            e.Property(x => x.MaxQty).HasPrecision(18, 4);
            e.HasIndex(x => new { x.TenantId, x.StoreId, x.ItemId }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.ItemId });
        });

        b.Entity<StockMovement>(e =>
        {
            e.ToTable("StockMovements");
            e.Property(x => x.Quantity).HasPrecision(18, 4);
            e.Property(x => x.UnitCost).HasPrecision(18, 4);
            e.Property(x => x.BalanceAfter).HasPrecision(18, 4);
            e.Property(x => x.Reference).HasMaxLength(100);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.HasIndex(x => new { x.TenantId, x.StoreId, x.ItemId, x.At });
            e.HasIndex(x => new { x.TenantId, x.WorkOrderId }).HasFilter("[WorkOrderId] IS NOT NULL");
        });

        b.Entity<Reservation>(e =>
        {
            e.ToTable("Reservations", t => t.HasCheckConstraint("CK_Reservations_Quantity", "[Quantity] >= 0"));
            e.Property(x => x.Quantity).HasPrecision(18, 4);
            e.HasIndex(x => new { x.TenantId, x.WorkOrderId, x.StoreId, x.ItemId }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.StoreId, x.ItemId });
        });

        b.Entity<PmTemplatePart>(e =>
        {
            e.ToTable("PmTemplateParts");
            e.Property(x => x.Quantity).HasPrecision(18, 4);
            e.HasIndex(x => new { x.TenantId, x.PmTemplateId, x.ItemId }).IsUnique();
        });

        b.Entity<StockTake>(e =>
        {
            e.ToTable("StockTakes");
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasIndex(x => new { x.TenantId, x.StoreId, x.Status });
        });

        b.Entity<StockTakeLine>(e =>
        {
            e.ToTable("StockTakeLines");
            e.Property(x => x.SystemQty).HasPrecision(18, 4);
            e.Property(x => x.UnitCost).HasPrecision(18, 4);
            e.Property(x => x.CountedQty).HasPrecision(18, 4);
            e.HasIndex(x => new { x.TenantId, x.StockTakeId, x.ItemId }).IsUnique();
        });

        base.OnModelCreating(b);
    }
}
