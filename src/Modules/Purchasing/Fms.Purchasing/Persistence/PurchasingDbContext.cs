using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Fms.Purchasing.Domain;
using Microsoft.EntityFrameworkCore;

namespace Fms.Purchasing.Persistence;

public sealed class PurchasingDbContext(DbContextOptions<PurchasingDbContext> options, ITenantContext tenant, PendingWrites? pending = null)
    : TenantDbContext(options, tenant, pending)
{
    public const string Schema = "purchasing";

    public DbSet<PurchasingSettings> Settings => Set<PurchasingSettings>();
    public DbSet<PurchaseRequest> Requests => Set<PurchaseRequest>();
    public DbSet<PurchaseRequestLine> RequestLines => Set<PurchaseRequestLine>();
    public DbSet<PurchaseOrder> Orders => Set<PurchaseOrder>();
    public DbSet<PurchaseOrderLine> OrderLines => Set<PurchaseOrderLine>();
    public DbSet<GoodsReceipt> Receipts => Set<GoodsReceipt>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        PendingWrites.MapSharedTables(b);

        b.Entity<PurchasingSettings>(e =>
        {
            e.ToTable("Settings", t => t.HasCheckConstraint("CK_PurchasingSettings_Limits", "[Level1Limit] > 0 AND [Level2Limit] >= [Level1Limit] AND [OverrunTolerancePercent] BETWEEN 0 AND 100"));
            e.Property(x => x.Level1Limit).HasPrecision(18, 2);
            e.Property(x => x.Level2Limit).HasPrecision(18, 2);
            e.Property(x => x.OverrunTolerancePercent).HasPrecision(5, 2);
            e.Property(x => x.CurrencyCode).HasMaxLength(3).IsRequired();
            e.HasIndex(x => x.TenantId).IsUnique();
        });

        b.Entity<PurchaseRequest>(e =>
        {
            e.ToTable("Requests");
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Justification).HasMaxLength(2000);
            e.Property(x => x.DecisionNote).HasMaxLength(500);
            e.Property(x => x.Total).HasPrecision(18, 2);
            e.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Status });
            e.HasIndex(x => new { x.TenantId, x.RequestedBy });
        });

        b.Entity<PurchaseRequestLine>(e =>
        {
            e.ToTable("RequestLines");
            e.Property(x => x.Description).HasMaxLength(300).IsRequired();
            e.Property(x => x.Unit).HasMaxLength(20).IsRequired();
            e.Property(x => x.Quantity).HasPrecision(18, 4);
            e.Property(x => x.EstimatedUnitPrice).HasPrecision(18, 4);
            e.HasIndex(x => new { x.TenantId, x.RequestId });
        });

        b.Entity<PurchaseOrder>(e =>
        {
            e.ToTable("Orders");
            e.Property(x => x.VendorCode).HasMaxLength(50).IsRequired();
            e.Property(x => x.VendorName).HasMaxLength(200).IsRequired();
            e.Property(x => x.CurrencyCode).HasMaxLength(3).IsRequired();
            e.Property(x => x.Total).HasPrecision(18, 2);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.CancelReason).HasMaxLength(500);
            e.Property(x => x.ErpReference).HasMaxLength(50);
            e.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.RequestId }).IsUnique().HasFilter("[Status] <> 5"); // one live order per request
            e.HasIndex(x => new { x.TenantId, x.VendorId });
        });

        b.Entity<PurchaseOrderLine>(e =>
        {
            e.ToTable("OrderLines", t => t.HasCheckConstraint("CK_OrderLines_Received", "[ReceivedQty] >= 0 AND [ReceivedQty] <= [Quantity]"));
            e.Property(x => x.Description).HasMaxLength(300).IsRequired();
            e.Property(x => x.Unit).HasMaxLength(20).IsRequired();
            e.Property(x => x.Quantity).HasPrecision(18, 4);
            e.Property(x => x.UnitPrice).HasPrecision(18, 4);
            e.Property(x => x.ReceivedQty).HasPrecision(18, 4);
            e.HasIndex(x => new { x.TenantId, x.OrderId });
        });

        b.Entity<GoodsReceipt>(e =>
        {
            e.ToTable("GoodsReceipts");
            e.Property(x => x.Quantity).HasPrecision(18, 4);
            e.Property(x => x.DeliveryNote).HasMaxLength(100);
            e.HasIndex(x => new { x.TenantId, x.OrderId });
        });

        base.OnModelCreating(b);
    }
}
