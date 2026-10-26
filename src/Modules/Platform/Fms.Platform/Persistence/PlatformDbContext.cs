using Fms.BuildingBlocks.Audit;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Fms.Platform.Domain;
using Fms.BuildingBlocks.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Fms.Platform.Persistence;

public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options, ITenantContext tenant, PendingWrites? pending = null)
    : TenantDbContext(options, tenant, pending)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantIdentityMapping> TenantIdentityMappings => Set<TenantIdentityMapping>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();
    public DbSet<GuestIdentity> GuestIdentities => Set<GuestIdentity>();
    public DbSet<GuestInvitation> GuestInvitations => Set<GuestInvitation>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<IdempotencyKey>(e =>
        {
            e.ToTable("IdempotencyKeys");
            e.Property(x => x.Key).HasMaxLength(100).IsRequired();
            e.Property(x => x.Method).HasMaxLength(10).IsRequired();
            e.Property(x => x.Path).HasMaxLength(300).IsRequired();
            e.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(100);
            e.HasIndex(x => new { x.TenantId, x.UserId, x.Key }).IsUnique();
            e.HasIndex(x => x.CreatedAt);
        });

        b.Entity<Notification>(e =>
        {
            e.ToTable("Notifications");
            e.Property(x => x.Type).HasMaxLength(100).IsRequired();
            e.Property(x => x.DataJson).IsRequired();
            e.Property(x => x.LinkPath).HasMaxLength(300);
            e.HasIndex(x => new { x.TenantId, x.UserId, x.ReadAt, x.CreatedAt });
            e.HasIndex(x => new { x.TenantId, x.SourceId, x.UserId }).IsUnique();
        });

        b.Entity<GuestIdentity>(e =>
        {
            e.ToTable("GuestIdentities");
            e.Property(x => x.Provider).HasMaxLength(64).IsRequired();
            e.Property(x => x.Subject).HasMaxLength(200).IsRequired();
            e.HasIndex(x => new { x.Provider, x.Subject }).IsUnique();
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<GuestInvitation>(e =>
        {
            e.ToTable("GuestInvitations");
            e.Property(x => x.Kind).HasMaxLength(30).IsRequired();
            e.Property(x => x.Email).HasMaxLength(320).IsRequired();
            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.AcceptedProvider).HasMaxLength(64);
            e.Property(x => x.AcceptedSubject).HasMaxLength(200);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Kind, x.TargetId });
        });

        b.Entity<Tenant>(e =>
        {
            e.ToTable("Tenants");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        });

        b.Entity<TenantIdentityMapping>(e =>
        {
            e.ToTable("TenantIdentityMappings");
            e.Property(x => x.Provider).HasMaxLength(64).IsRequired();
            e.Property(x => x.ExternalTenantId).HasMaxLength(200).IsRequired();
            e.HasIndex(x => new { x.Provider, x.ExternalTenantId }).IsUnique();
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<User>(e =>
        {
            e.ToTable("Users");
            e.Property(x => x.Provider).HasMaxLength(64).IsRequired();
            e.Property(x => x.Subject).HasMaxLength(256).IsRequired();
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.HasIndex(x => new { x.TenantId, x.Provider, x.Subject }).IsUnique();
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Role>(e =>
        {
            e.ToTable("Roles");
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<RolePermission>(e =>
        {
            e.ToTable("RolePermissions");
            e.Property(x => x.Permission).HasMaxLength(100).IsRequired();
            e.HasIndex(x => new { x.RoleId, x.Permission }).IsUnique();
            e.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<UserRole>(e =>
        {
            e.ToTable("UserRoles");
            e.HasIndex(x => new { x.UserId, x.RoleId }).IsUnique();
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("OutboxMessages");
            e.Property(x => x.Type).HasMaxLength(100).IsRequired();
            e.Property(x => x.PayloadJson).IsRequired();
            e.Property(x => x.LockedBy).HasMaxLength(100);
            e.Property(x => x.LastError).HasMaxLength(2000);
            e.HasIndex(x => new { x.ProcessedAt, x.DeadLetteredAt, x.AvailableAt });
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<AuditEvent>(e =>
        {
            e.ToTable("AuditEvents");
            e.Property(x => x.Action).HasMaxLength(100).IsRequired();
            e.Property(x => x.EntityType).HasMaxLength(100).IsRequired();
            e.Property(x => x.EntityId).HasMaxLength(100);
            e.Property(x => x.CorrelationId).HasMaxLength(100);
            e.HasIndex(x => new { x.TenantId, x.OccurredAt });
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        base.OnModelCreating(b);
    }
}
