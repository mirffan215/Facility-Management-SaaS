using Fms.BuildingBlocks.Deployment;
using Fms.BuildingBlocks.Licensing;

namespace Fms.Api;

/// <summary>
/// Applies the installation's license to every request: a module that is not licensed is closed, and without a valid license
/// (expired past grace, missing, damaged, for another installation) data stays readable but nothing can be changed. Reading is kept
/// so a customer can always get their data out. SaaS is not restricted here.
/// </summary>
public sealed class LicenseEnforcementMiddleware(RequestDelegate next)
{
    private static readonly string[] Exempt = ["/health", "/api/v1/system", "/api/v1/auth", "/api/v1/me", "/api/v1/license"];

    public async Task InvokeAsync(HttpContext context, ILicenseProvider licenses)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (Exempt.Any(e => path.StartsWith(e, StringComparison.OrdinalIgnoreCase)) || !path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var license = licenses.Current;
        if (license.State == LicenseState.Grace) context.Response.Headers["X-License-State"] = "grace";

        var safe = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method) || HttpMethods.IsOptions(context.Request.Method);
        if (!license.AllowsChanges && !safe)
        {
            await Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "License does not allow changes",
                detail: license.Detail ?? "The installation has no valid license. Data can be read but not changed.",
                extensions: new Dictionary<string, object?> { ["code"] = "license.read_only", ["licenseState"] = license.State.ToString() }).ExecuteAsync(context);
            return;
        }

        if (license.AllowsChanges && LicenseModules.For(path) is { } module && !license.Allows(module))
        {
            await Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Not part of this license",
                detail: "This area is not included in the installation's license.",
                extensions: new Dictionary<string, object?> { ["code"] = "license.module_not_licensed", ["module"] = module }).ExecuteAsync(context);
            return;
        }

        await next(context);
    }
}
