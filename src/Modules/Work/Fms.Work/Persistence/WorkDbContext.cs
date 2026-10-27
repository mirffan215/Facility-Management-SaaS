using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Fms.Work.Domain;
using Microsoft.EntityFrameworkCore;

namespace Fms.Work.Persistence;

public sealed class WorkDbContext(DbContextOptions<WorkDbContext> options, ITenantContext tenant, PendingWrites? pending = null)
    : TenantDbContext(options, tenant, pending)
{
    public const string Schema = "work";

    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();
    public DbSet<WorkOrderEvent> WorkOrderEvents => Set<WorkOrderEvent>();
    public DbSet<WorkOrderComment> WorkOrderComments => Set<WorkOrderComment>();
    public DbSet<WorkCategory> Categories => Set<WorkCategory>();
    public DbSet<WorkSettings> Settings => Set<WorkSettings>();
    public DbSet<WorkflowVersion> WorkflowVersions => Set<WorkflowVersion>();
    public DbSet<ChecklistTemplate> ChecklistTemplates => Set<ChecklistTemplate>();
    public DbSet<ChecklistTemplateItem> ChecklistTemplateItems => Set<ChecklistTemplateItem>();
    public DbSet<WorkOrderChecklistItem> ChecklistItems => Set<WorkOrderChecklistItem>();
    public DbSet<WorkOrderTimeEntry> TimeEntries => Set<WorkOrderTimeEntry>();
    public DbSet<WorkOrderCostLine> CostLines => Set<WorkOrderCostLine>();
    public DbSet<WorkOrderVoid> Voids => Set<WorkOrderVoid>();
    public DbSet<WorkOrderSignature> Signatures => Set<WorkOrderSignature>();
    public DbSet<WorkOrderAttachment> Attachments => Set<WorkOrderAttachment>();
    public DbSet<PmTemplate> PmTemplates => Set<PmTemplate>();
    public DbSet<PmSchedule> PmSchedules => Set<PmSchedule>();
    public DbSet<PmPlan> PmPlans => Set<PmPlan>();
    public DbSet<PmOccurrence> PmOccurrences => Set<PmOccurrence>();
    public DbSet<WorkCalendar> Calendars => Set<WorkCalendar>();
    public DbSet<WorkCalendarHoliday> CalendarHolidays => Set<WorkCalendarHoliday>();
    public DbSet<WorkCalendarSite> CalendarSites => Set<WorkCalendarSite>();
    public DbSet<SlaPolicy> SlaPolicies => Set<SlaPolicy>();
    public DbSet<SlaTarget> SlaTargets => Set<SlaTarget>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        PendingWrites.MapSharedTables(b);

        b.Entity<WorkCategory>(e =>
        {
            e.ToTable("Categories");
            e.Property(x => x.Code).HasMaxLength(50).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.NameAr).HasMaxLength(200);
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique().HasFilter("[IsArchived] = 0");
        });

        b.Entity<WorkSettings>(e =>
        {
            e.ToTable("Settings");
            e.HasIndex(x => x.TenantId).IsUnique();
        });

        b.Entity<WorkflowVersion>(e =>
        {
            e.ToTable("WorkflowVersions");
            e.Property(x => x.RulesJson).IsRequired();
            e.Property(x => x.Note).HasMaxLength(600).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.Version }).IsUnique();
        });

        b.Entity<WorkSettings>().Property(x => x.CurrencyCode).HasMaxLength(3).IsRequired();
        b.Entity<WorkSettings>().Property(x => x.LabourRatePerHour).HasPrecision(12, 2);

        b.Entity<ChecklistTemplate>(e =>
        {
            e.ToTable("ChecklistTemplates");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.NameAr).HasMaxLength(200);
            e.HasIndex(x => new { x.TenantId, x.Name }).IsUnique().HasFilter("[IsArchived] = 0");
        });

        b.Entity<ChecklistTemplateItem>(e =>
        {
            e.ToTable("ChecklistTemplateItems");
            e.Property(x => x.Text).HasMaxLength(300).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.TemplateId, x.Position });
            e.HasOne<ChecklistTemplate>().WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<WorkOrderChecklistItem>(e =>
        {
            e.ToTable("WorkOrderChecklistItems");
            e.Property(x => x.Text).HasMaxLength(300).IsRequired();
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasIndex(x => new { x.TenantId, x.WorkOrderId, x.Position });
            e.HasOne<WorkOrder>().WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<WorkOrderTimeEntry>(e =>
        {
            e.ToTable("WorkOrderTimeEntries");
            e.Property(x => x.HourlyRate).HasPrecision(12, 2);
            e.Property(x => x.Note).HasMaxLength(500);
            e.HasIndex(x => new { x.TenantId, x.WorkOrderId });
            e.HasOne<WorkOrder>().WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<WorkOrderCostLine>(e =>
        {
            e.ToTable("WorkOrderCostLines");
            e.Property(x => x.Description).HasMaxLength(300).IsRequired();
            e.Property(x => x.Reference).HasMaxLength(100);
            e.Property(x => x.Quantity).HasPrecision(12, 3);
            e.Property(x => x.UnitCost).HasPrecision(14, 4);
            e.HasIndex(x => new { x.TenantId, x.WorkOrderId });
            e.HasOne<WorkOrder>().WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<WorkOrderVoid>(e =>
        {
            e.ToTable("WorkOrderVoids");
            e.Property(x => x.Reason).HasMaxLength(500).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.EntryType, x.EntryId }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.WorkOrderId });
            e.HasOne<WorkOrder>().WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<WorkOrderSignature>(e =>
        {
            e.ToTable("WorkOrderSignatures");
            e.Property(x => x.SignerName).HasMaxLength(200).IsRequired();
            e.Property(x => x.StorageKey).HasMaxLength(300).IsRequired();
            e.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.WorkOrderId });
            e.HasOne<WorkOrder>().WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<WorkOrderAttachment>(e =>
        {
            e.ToTable("WorkOrderAttachments");
            e.Property(x => x.FileName).HasMaxLength(200).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
            e.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            e.Property(x => x.StorageKey).HasMaxLength(300).IsRequired();
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.DeleteReason).HasMaxLength(500);
            e.HasIndex(x => new { x.TenantId, x.WorkOrderId, x.DeletedAt });
            e.HasOne<WorkOrder>().WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<PmTemplate>(e =>
        {
            e.ToTable("PmTemplates");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.NameAr).HasMaxLength(200);
            e.Property(x => x.Instructions).HasMaxLength(4000);
            e.HasIndex(x => new { x.TenantId, x.Name }).IsUnique().HasFilter("[IsArchived] = 0");
            e.HasOne<WorkCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ChecklistTemplate>().WithMany().HasForeignKey(x => x.ChecklistTemplateId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<PmSchedule>(e =>
        {
            e.ToTable("PmSchedules");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.MeterName).HasMaxLength(200);
            e.Property(x => x.MeterInterval).HasPrecision(18, 4);
            e.HasIndex(x => new { x.TenantId, x.TemplateId });
            e.HasIndex(x => new { x.TenantId, x.AssetId });
            e.HasIndex(x => new { x.TenantId, x.AssetClassId });
            e.HasOne<PmTemplate>().WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<PmPlan>(e =>
        {
            e.ToTable("PmPlans");
            e.Property(x => x.LastServiceMeterValue).HasPrecision(18, 4);
            e.HasIndex(x => new { x.TenantId, x.ScheduleId, x.AssetId }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.IsActive, x.NextDueOn });
            e.HasIndex(x => new { x.TenantId, x.OpenWorkOrderId });
            e.HasOne<PmSchedule>().WithMany().HasForeignKey(x => x.ScheduleId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<PmOccurrence>(e =>
        {
            e.ToTable("PmOccurrences");
            e.HasIndex(x => new { x.TenantId, x.PlanId, x.DueOn, x.Trigger }).IsUnique().HasFilter("[Trigger] = 1"); // one calendar visit per due date; meter visits may repeat
            e.HasIndex(x => new { x.TenantId, x.WorkOrderId });
            e.HasIndex(x => new { x.TenantId, x.DueOn });
            e.HasOne<PmPlan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<WorkCalendar>(e =>
        {
            e.ToTable("Calendars");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.TimeZoneId).HasMaxLength(64).IsRequired();
            e.Property(x => x.HoursJson).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.Name }).IsUnique().HasFilter("[IsArchived] = 0");
            e.HasIndex(x => x.TenantId).IsUnique().HasFilter("[IsDefault] = 1 AND [IsArchived] = 0").HasDatabaseName("IX_Calendars_OneDefault");
        });

        b.Entity<WorkCalendarHoliday>(e =>
        {
            e.ToTable("CalendarHolidays");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.CalendarId, x.Date }).IsUnique();
            e.HasOne<WorkCalendar>().WithMany().HasForeignKey(x => x.CalendarId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<WorkCalendarSite>(e =>
        {
            e.ToTable("CalendarSites");
            e.HasIndex(x => new { x.TenantId, x.SiteId }).IsUnique();
            e.HasOne<WorkCalendar>().WithMany().HasForeignKey(x => x.CalendarId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<SlaPolicy>(e =>
        {
            e.ToTable("SlaPolicies");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.TenantId).IsUnique().HasFilter("[IsDefault] = 1 AND [IsArchived] = 0").HasDatabaseName("IX_SlaPolicies_OneDefault");
            e.HasIndex(x => new { x.TenantId, x.CategoryId }).IsUnique().HasFilter("[CategoryId] IS NOT NULL AND [IsArchived] = 0").HasDatabaseName("IX_SlaPolicies_OnePerCategory");
            e.HasOne<WorkCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<SlaTarget>(e =>
        {
            e.ToTable("SlaTargets");
            e.HasIndex(x => new { x.TenantId, x.PolicyId, x.Priority }).IsUnique();
            e.HasOne<SlaPolicy>().WithMany().HasForeignKey(x => x.PolicyId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<WorkOrder>(e =>
        {
            e.ToTable("WorkOrders");
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(4000);
            e.Property(x => x.StatusReason).HasMaxLength(500);
            e.Property(x => x.ClosureNotes).HasMaxLength(2000);
            e.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Status });
            e.HasIndex(x => new { x.TenantId, x.RequestedBy });
            e.HasIndex(x => new { x.TenantId, x.AssignedTo });
            e.HasIndex(x => new { x.TenantId, x.AssetId });
            e.HasIndex(x => new { x.TenantId, x.LocationId });
            e.HasIndex(x => new { x.TenantId, x.ResolutionDueAt });
            e.HasOne<WorkCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<WorkOrderEvent>(e =>
        {
            e.ToTable("WorkOrderEvents");
            e.Property(x => x.Note).HasMaxLength(2000);
            e.HasIndex(x => new { x.TenantId, x.WorkOrderId, x.At });
            e.HasOne<WorkOrder>().WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<WorkOrderComment>(e =>
        {
            e.ToTable("WorkOrderComments");
            e.Property(x => x.Body).HasMaxLength(2000).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.WorkOrderId, x.At });
            e.HasOne<WorkOrder>().WithMany().HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Restrict);
        });

        base.OnModelCreating(b);
    }
}
