using Fms.BuildingBlocks.Deployment;
using Fms.BuildingBlocks.Identity;
using Fms.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Fms.Platform.Services;

/// <summary>
/// Database-backed tenant directory. SaaS: a provider tenant resolves only through a stored mapping to an active tenant.
/// On-prem: every signed-in user belongs to the single configured tenant, which must exist and be active.
/// Reads only platform tables, which are not tenant-filtered, so it works before a tenant is known.
/// </summary>
public sealed class DbTenantDirectory(PlatformDbContext db, IOptions<DeploymentOptions> deployment) : ITenantDirectory
{
    public async Task<Guid?> ResolveAsync(string provider, string? externalTenantId, CancellationToken ct = default)
    {
        if (deployment.Value.Mode == DeploymentMode.OnPrem)
        {
            var single = deployment.Value.SingleTenantId;
            return single is { } id && await db.Tenants.AnyAsync(t => t.Id == id && t.IsActive, ct) ? id : null;
        }

        if (string.IsNullOrWhiteSpace(externalTenantId))
        {
            return null;
        }

        return await (from m in db.TenantIdentityMappings
                      join t in db.Tenants on m.TenantId equals t.Id
                      where m.Provider == provider && m.ExternalTenantId == externalTenantId && t.IsActive
                      select (Guid?)t.Id).FirstOrDefaultAsync(ct);
    }
}
