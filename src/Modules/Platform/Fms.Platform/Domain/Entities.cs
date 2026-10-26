using Fms.BuildingBlocks.Tenancy;

namespace Fms.Platform.Domain;

/// <summary>A customer (SaaS) or the single installation owner (on-prem). Platform-level: not itself tenant-filtered.</summary>
public sealed class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Links an identity provider's tenant (e.g. an Entra directory id) to one FMS tenant. One mapping per provider tenant.</summary>
public sealed class TenantIdentityMapping
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Provider { get; set; } = "";
    public string ExternalTenantId { get; set; } = "";
}

/// <summary>A person who signs in. Identified by (provider, subject) within a tenant; the same person in two tenants is two users.</summary>
public sealed class User : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Provider { get; set; } = "";
    public string Subject { get; set; } = "";
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSignInAt { get; set; }
}

/// <summary>A named set of permissions within a tenant. System roles are defined in code and kept in sync; they cannot be edited.</summary>
public sealed class Role : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public bool IsSystem { get; set; }
}

public sealed class RolePermission : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid RoleId { get; set; }
    public string Permission { get; set; } = "";
}

public sealed class UserRole : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
    public Guid? GrantedBy { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
}

/// <summary>An in-app notice for one person. Type and data are rendered in the reader's language by the client.</summary>
public sealed class Notification : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }

    /// <summary>What happened, e.g. "workorder.Assigned".</summary>
    public string Type { get; set; } = "";

    public string DataJson { get; set; } = "{}";

    /// <summary>Where to go in the app, e.g. "/work-orders/{id}".</summary>
    public string? LinkPath { get; set; }

    /// <summary>The event that caused it (an outbox message id); with the person, unique, so a retried event cannot notify twice.</summary>
    public Guid SourceId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}
