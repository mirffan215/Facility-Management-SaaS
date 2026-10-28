using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Fms.Compliance.Domain;
using Microsoft.EntityFrameworkCore;

namespace Fms.Compliance.Persistence;

public sealed class ComplianceDbContext(DbContextOptions<ComplianceDbContext> options, ITenantContext tenant, PendingWrites? pending = null)
    : TenantDbContext(options, tenant, pending)
{
    public const string Schema = "compliance";

    public DbSet<Obligation> Obligations => Set<Obligation>();
    public DbSet<Completion> Completions => Set<Completion>();
    public DbSet<CompletionDocument> Documents => Set<CompletionDocument>();
    public DbSet<PermitType> PermitTypes => Set<PermitType>();
    public DbSet<Permit> Permits => Set<Permit>();
    public DbSet<PermitEvent> PermitEvents => Set<PermitEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        PendingWrites.MapSharedTables(b);

        b.Entity<Obligation>(e =>
        {
            e.ToTable("Obligations", t =>
            {
                t.HasCheckConstraint("CK_Obligations_Interval", "[IntervalMonths] IS NULL OR [IntervalMonths] BETWEEN 1 AND 240");
                t.HasCheckConstraint("CK_Obligations_Lead", "[LeadDays] BETWEEN 0 AND 365");
            });
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Authority).HasMaxLength(200);
            e.Property(x => x.LocationName).HasMaxLength(200);
            e.Property(x => x.AssetName).HasMaxLength(200);
            e.Property(x => x.OwnerName).HasMaxLength(200);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.HasIndex(x => new { x.TenantId, x.IsArchived, x.ValidUntil });
            e.HasIndex(x => new { x.TenantId, x.LocationId });
        });

        b.Entity<Completion>(e =>
        {
            e.ToTable("Completions");
            e.Property(x => x.Reference).HasMaxLength(100);
            e.Property(x => x.ProviderName).HasMaxLength(200);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.Property(x => x.RecordedByName).HasMaxLength(200);
            e.HasIndex(x => new { x.TenantId, x.ObligationId, x.PerformedOn });
        });

        b.Entity<CompletionDocument>(e =>
        {
            e.ToTable("Documents");
            e.Property(x => x.FileName).HasMaxLength(255).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
            e.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            e.Property(x => x.StorageKey).HasMaxLength(500).IsRequired();
            e.Property(x => x.DeleteReason).HasMaxLength(500);
            e.HasIndex(x => new { x.TenantId, x.CompletionId });
        });

        b.Entity<PermitType>(e =>
        {
            e.ToTable("PermitTypes", t => t.HasCheckConstraint("CK_PermitTypes_Duration", "[MaxDurationHours] BETWEEN 1 AND 720"));
            e.Property(x => x.Code).HasMaxLength(30).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.NameAr).HasMaxLength(200);
            e.Property(x => x.PrecautionsJson).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        });

        b.Entity<Permit>(e =>
        {
            e.ToTable("Permits", t => t.HasCheckConstraint("CK_Permits_Times", "[PlannedEnd] > [PlannedStart]"));
            e.Property(x => x.TypeCode).HasMaxLength(30).IsRequired();
            e.Property(x => x.TypeName).HasMaxLength(200).IsRequired();
            e.Property(x => x.WorkOrderNumber).HasMaxLength(30);
            e.Property(x => x.LocationName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(1000).IsRequired();
            e.Property(x => x.ContractorName).HasMaxLength(200);
            e.Property(x => x.PrecautionsJson).IsRequired();
            e.Property(x => x.RequestedByName).HasMaxLength(200).IsRequired();
            e.Property(x => x.ApprovedByName).HasMaxLength(200);
            e.Property(x => x.IssuedByName).HasMaxLength(200);
            e.Property(x => x.StartedByName).HasMaxLength(200);
            e.Property(x => x.ClosedByName).HasMaxLength(200);
            e.Property(x => x.RejectReason).HasMaxLength(500);
            e.Property(x => x.IsolationReference).HasMaxLength(200);
            e.Property(x => x.ClosureNote).HasMaxLength(1000);
            e.Property(x => x.CancelReason).HasMaxLength(500);
            e.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Status, x.PlannedStart });
            e.HasIndex(x => new { x.TenantId, x.LocationId, x.Status });
            e.HasIndex(x => new { x.TenantId, x.RequestedBy });
        });

        b.Entity<PermitEvent>(e =>
        {
            e.ToTable("PermitEvents");
            e.Property(x => x.ByName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Action).HasMaxLength(40).IsRequired();
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasIndex(x => new { x.TenantId, x.PermitId, x.At });
        });

        base.OnModelCreating(b);
    }
}
