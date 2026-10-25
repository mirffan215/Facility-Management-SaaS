using Fms.BuildingBlocks.Deployment;
using Fms.BuildingBlocks.Identity;
using Microsoft.Extensions.Options;

namespace Fms.Api.Authentication;

/// <summary>
/// Configuration-backed directory. On-prem every signed-in user belongs to the single tenant; in SaaS only explicitly mapped
/// provider tenants resolve. A database-backed directory replaces this once tenant onboarding exists.
/// </summary>
public sealed class ConfiguredTenantDirectory(IOptions<AuthOptions> auth, IOptions<DeploymentOptions> deployment) : ITenantDirectory
{
    public Task<Guid?> ResolveAsync(string provider, string? externalTenantId, CancellationToken ct = default)
    {
        if (deployment.Value.Mode == DeploymentMode.OnPrem)
        {
            return Task.FromResult(deployment.Value.SingleTenantId);
        }

        if (string.IsNullOrWhiteSpace(externalTenantId))
        {
            return Task.FromResult<Guid?>(null);
        }

        var mapping = auth.Value.TenantMappings.FirstOrDefault(m =>
            string.Equals(m.Provider, provider, StringComparison.OrdinalIgnoreCase)
            && string.Equals(m.ExternalTenantId, externalTenantId, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(mapping?.TenantId);
    }
}
