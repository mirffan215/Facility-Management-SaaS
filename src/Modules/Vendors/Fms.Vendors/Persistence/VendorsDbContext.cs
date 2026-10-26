using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Fms.Vendors.Domain;
using Microsoft.EntityFrameworkCore;

namespace Fms.Vendors.Persistence;

public sealed class VendorsDbContext(DbContextOptions<VendorsDbContext> options, ITenantContext tenant, PendingWrites? pending = null)
    : TenantDbContext(options, tenant, pending)
{
    public const string Schema = "vendors";

    public DbSet<Vendor> Vendors => Set<Vendor>();
    public DbSet<VendorContact> Contacts => Set<VendorContact>();
    public DbSet<Contract> Contracts => Set<Contract>();
    public DbSet<ContractCoverage> Coverage => Set<ContractCoverage>();
    public DbSet<ContractSla> SlaTargets => Set<ContractSla>();
    public DbSet<ContractDocument> Documents => Set<ContractDocument>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        PendingWrites.MapSharedTables(b);

        b.Entity<Vendor>(e =>
        {
            e.ToTable("Vendors");
            e.Property(x => x.Code).HasMaxLength(50).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.NameAr).HasMaxLength(200);
            e.Property(x => x.StatusReason).HasMaxLength(500);
            e.Property(x => x.TaxNumber).HasMaxLength(30);
            e.Property(x => x.RegistrationNumber).HasMaxLength(30);
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.Phone).HasMaxLength(40);
            e.Property(x => x.Address).HasMaxLength(500);
            e.Property(x => x.Country).HasMaxLength(2);
            e.Property(x => x.PaymentTerms).HasMaxLength(200);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.ErpAccount).HasMaxLength(50);
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.TaxNumber }).IsUnique().HasFilter("[TaxNumber] IS NOT NULL");
        });

        b.Entity<VendorContact>(e =>
        {
            e.ToTable("Contacts");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Role).HasMaxLength(100);
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.Phone).HasMaxLength(40);
            e.HasIndex(x => new { x.TenantId, x.VendorId });
        });

        b.Entity<Contract>(e =>
        {
            e.ToTable("Contracts", t => t.HasCheckConstraint("CK_Contracts_Dates", "[EndOn] >= [StartOn]"));
            e.Property(x => x.Number).HasMaxLength(50).IsRequired();
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Value).HasPrecision(18, 2);
            e.Property(x => x.CurrencyCode).HasMaxLength(3).IsRequired();
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.TerminationReason).HasMaxLength(500);
            e.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.VendorId });
            e.HasIndex(x => new { x.TenantId, x.EndOn });
        });

        b.Entity<ContractCoverage>(e =>
        {
            e.ToTable("ContractCoverage");
            e.HasIndex(x => new { x.TenantId, x.ContractId, x.Kind, x.TargetId }).IsUnique();
        });

        b.Entity<ContractSla>(e =>
        {
            e.ToTable("ContractSla", t => t.HasCheckConstraint("CK_ContractSla_Values", "[Priority] BETWEEN 1 AND 4 AND [ResponseMinutes] > 0 AND [ResolutionMinutes] >= [ResponseMinutes]"));
            e.HasIndex(x => new { x.TenantId, x.ContractId, x.Priority }).IsUnique();
        });

        b.Entity<ContractDocument>(e =>
        {
            e.ToTable("ContractDocuments");
            e.Property(x => x.FileName).HasMaxLength(255).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
            e.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            e.Property(x => x.StorageKey).HasMaxLength(500).IsRequired();
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.DeleteReason).HasMaxLength(500);
            e.HasIndex(x => new { x.TenantId, x.ContractId });
        });

        base.OnModelCreating(b);
    }
}
