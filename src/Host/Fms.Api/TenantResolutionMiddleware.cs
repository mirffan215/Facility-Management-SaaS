using Fms.BuildingBlocks.Deployment;
using Fms.BuildingBlocks.Tenancy;
using Microsoft.Extensions.Options;

namespace Fms.Api;

/// <summary>
/// Resolves the tenant for the request. On-prem uses the single configured tenant. SaaS takes it ONLY from the
/// authenticated principal's "tid" claim, never from a header or query string; until authentication exists (roadmap phase 0,
/// identity step) no SaaS request resolves a tenant, so nothing tenant-owned is visible.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next, IOptions<DeploymentOptions> options)
{
    public Task InvokeAsync(HttpContext context, TenantContext tenant)
    {
        var deployment = options.Value;
        if (deployment.Mode == DeploymentMode.OnPrem)
        {
            tenant.SetTenant(deployment.SingleTenantId!.Value);
        }
        else if (context.User.Identity?.IsAuthenticated == true
                 && Guid.TryParse(context.User.FindFirst("tid")?.Value, out var tid))
        {
            tenant.SetTenant(tid);
        }

        return next(context);
    }
}
