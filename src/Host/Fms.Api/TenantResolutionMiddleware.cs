using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Tenancy;
using Serilog;
using Serilog.Context;

namespace Fms.Api;

/// <summary>
/// Sets the request's tenant from the server-assigned <see cref="FmsClaims.TenantId"/> claim, which authentication derives from the
/// tenant directory. Never from headers, query strings, or claims an identity provider sent. Anonymous requests have no tenant.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, TenantContext tenant, IDiagnosticContext diagnostics)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && Guid.TryParse(context.User.FindFirst(FmsClaims.TenantId)?.Value, out var tenantId))
        {
            tenant.SetTenant(tenantId);
            diagnostics.Set("TenantId", tenantId);

            // Every log line written while handling this request carries the tenant (and never the token).
            using (LogContext.PushProperty("TenantId", tenantId))
            {
                await next(context);
            }

            return;
        }

        await next(context);
    }
}
