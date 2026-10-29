using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Fms.BuildingBlocks.Security;
using Fms.Leases.Domain;
using Microsoft.EntityFrameworkCore;

namespace Fms.Leases.Persistence;

public sealed class LeasesDbContext(DbContextOptions<LeasesDbContext> options, ITenantContext tenant, PendingWrites? pending = null)
    : TenantDbContext(options, tenant, pending)
{
    public const string Schema = "leases";

    public DbSet<Lease> Leases => Set<Lease>();
    public DbSet<LeaseDocument> Documents => Set<LeaseDocument>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        PendingWrites.MapSharedTables(b);

        b.Entity<Lease>(e =>
        {
            e.ToTable("Leases", t =>
            {
                t.HasCheckConstraint("CK_Leases_Dates", "[EndOn] >= [StartOn]");
                t.HasCheckConstraint("CK_Leases_Rent", "[BaseRent] >= 0 AND [EscalationPercent] >= 0 AND [EscalationPercent] <= 100");
            });
            e.Property(x => x.Reference).HasMaxLength(30).IsRequired();
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Counterparty).HasMaxLength(200).IsRequired();
            e.Property(x => x.LocationName).HasMaxLength(200);
            e.Property(x => x.CurrencyCode).HasMaxLength(3).IsRequired();
            e.Property(x => x.AreaSquareMetres).HasPrecision(12, 2);
            e.Property(x => x.BaseRent).HasPrecision(19, 2);
            e.Property(x => x.EscalationPercent).HasPrecision(5, 2);
            e.Property(x => x.Deposit).HasPrecision(19, 2);
            // Free text of the lease is encrypted when a key is configured; the stored form is longer than the text, so the columns are unbounded.
            e.Property(x => x.Notes).HasConversion(FieldEncryption.Converter("leases.Leases.Notes"));
            e.Property(x => x.TerminationReason).HasConversion(FieldEncryption.Converter("leases.Leases.TerminationReason"));
            e.Property(x => x.LastAlertKey).HasMaxLength(40);
            e.HasIndex(x => new { x.TenantId, x.Reference }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Status, x.EndOn });
        });

        b.Entity<LeaseDocument>(e =>
        {
            e.ToTable("Documents");
            e.Property(x => x.FileName).HasMaxLength(255).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
            e.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            e.Property(x => x.StorageKey).HasMaxLength(400).IsRequired();
            e.Property(x => x.DeleteReason).HasMaxLength(500);
            e.HasIndex(x => new { x.TenantId, x.LeaseId });
        });

        base.OnModelCreating(b);
    }
}
