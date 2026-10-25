namespace Fms.BuildingBlocks.Tenancy;

/// <summary>Row belongs to exactly one tenant. Every implementer gets a global query filter.</summary>
public interface ITenantOwned
{
    Guid TenantId { get; set; }
}

public interface ITenantContext
{
    /// <summary>Current tenant, or null when none has been resolved (nothing is visible then).</summary>
    Guid? TenantId { get; }

    /// <summary>True only inside an explicit platform scope (see <see cref="TenantContext.BeginPlatformScope"/>).</summary>
    bool IsPlatform { get; }

    Guid RequireTenantId();
}

public sealed class TenantRequiredException()
    : InvalidOperationException("No tenant is resolved for this operation.");

public sealed class TenantViolationException(string message) : InvalidOperationException(message);

/// <summary>Scoped holder for the tenant of the current request or job.</summary>
public sealed class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }
    public bool IsPlatform { get; private set; }

    public Guid RequireTenantId() => TenantId ?? throw new TenantRequiredException();

    public void SetTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be empty.", nameof(tenantId));
        }

        if (TenantId is { } existing && existing != tenantId)
        {
            throw new TenantViolationException("The tenant of the current scope cannot be changed.");
        }

        TenantId = tenantId;
    }

    /// <summary>Cross-tenant access for platform operations (migrations, schedulers). The only way to bypass tenant filters.</summary>
    public IDisposable BeginPlatformScope()
    {
        IsPlatform = true;
        return new Scope(this);
    }

    private sealed class Scope(TenantContext owner) : IDisposable
    {
        public void Dispose() => owner.IsPlatform = false;
    }
}
