using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Tenancy;

namespace Fms.Api;

/// <summary>
/// Sets the request's tenant from the server-assigned <see cref="FmsClaims.TenantId"/> claim, which authentication derives from the
/// tenant directory. Never from headers, query strings, or claims an identity provider sent. Anonymous requests have no tenant.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context, TenantContext tenant)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && Guid.TryParse(context.User.FindFirst(FmsClaims.TenantId)?.Value, out var tenantId))
        {
            tenant.SetTenant(tenantId);
        }

        return next(context);
    }
}
