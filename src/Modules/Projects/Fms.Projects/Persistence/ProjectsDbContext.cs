using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Fms.Projects.Domain;
using Microsoft.EntityFrameworkCore;

namespace Fms.Projects.Persistence;

public sealed class ProjectsDbContext(DbContextOptions<ProjectsDbContext> options, ITenantContext tenant, PendingWrites? pending = null)
    : TenantDbContext(options, tenant, pending)
{
    public const string Schema = "projects";

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Phase> Phases => Set<Phase>();
    public DbSet<ProjectTask> Tasks => Set<ProjectTask>();
    public DbSet<Milestone> Milestones => Set<Milestone>();
    public DbSet<BudgetLine> BudgetLines => Set<BudgetLine>();
    public DbSet<ProjectLink> Links => Set<ProjectLink>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        PendingWrites.MapSharedTables(b);

        b.Entity<Project>(e =>
        {
            e.ToTable("Projects", t => t.HasCheckConstraint("CK_Projects_Dates", "[EndOn] >= [StartOn]"));
            e.Property(x => x.Code).HasMaxLength(30).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.ManagerName).HasMaxLength(200);
            e.Property(x => x.LocationName).HasMaxLength(200);
            e.Property(x => x.CloseReason).HasMaxLength(500);
            e.Property(x => x.CurrencyCode).HasMaxLength(3).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Status });
        });

        b.Entity<Phase>(e =>
        {
            e.ToTable("Phases");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.ProjectId, x.SortOrder });
        });

        b.Entity<ProjectTask>(e =>
        {
            e.ToTable("Tasks");
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.AssigneeName).HasMaxLength(200);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.HasIndex(x => new { x.TenantId, x.ProjectId, x.Status });
            e.HasIndex(x => new { x.TenantId, x.AssigneeId });
        });

        b.Entity<Milestone>(e =>
        {
            e.ToTable("Milestones");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.ProjectId, x.DueOn });
        });

        b.Entity<BudgetLine>(e =>
        {
            e.ToTable("BudgetLines", t => t.HasCheckConstraint("CK_BudgetLines_Amount", "[Amount] >= 0"));
            e.Property(x => x.Description).HasMaxLength(200).IsRequired();
            e.Property(x => x.Amount).HasPrecision(19, 2);
            e.HasIndex(x => new { x.TenantId, x.ProjectId });
        });

        b.Entity<ProjectLink>(e =>
        {
            e.ToTable("Links");
            e.Property(x => x.RefNumber).HasMaxLength(30).IsRequired();
            e.Property(x => x.UnlinkReason).HasMaxLength(500);
            e.HasIndex(x => new { x.TenantId, x.ProjectId });
            e.HasIndex(x => new { x.TenantId, x.Kind, x.RefId }).IsUnique().HasFilter("[UnlinkedAt] IS NULL");
        });

        base.OnModelCreating(b);
    }
}
