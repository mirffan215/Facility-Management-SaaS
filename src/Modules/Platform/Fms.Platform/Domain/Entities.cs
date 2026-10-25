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
