using Fms.BuildingBlocks.Security;
using Fms.Platform.Services;
using Microsoft.AspNetCore.Authorization;

namespace Fms.Api;

public sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

/// <summary>Grants access when one of the signed-in user's roles carries the permission. No database or no user means no access.</summary>
public sealed class PermissionHandler(IServiceProvider services) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var permissions = services.GetService<IPermissionService>();
        if (permissions is null) return;

        if ((await permissions.GetCurrentAsync()).Contains(requirement.Permission))
        {
            context.Succeed(requirement);
        }
    }
}

public static class AuthorizationExtensions
{
    /// <summary>One policy per known permission, so endpoints say <c>.RequirePermission(Permissions.UsersManage)</c>.</summary>
    public static IServiceCollection AddFmsPermissions(this IServiceCollection services)
    {
        services.AddScoped<IAuthorizationHandler>(sp => new PermissionHandler(sp));
        services.ConfigureOptions<PermissionPolicyOptions>();
        return services;
    }

    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission) where TBuilder : IEndpointConventionBuilder
    {
        if (!Permissions.All.Contains(permission))
        {
            throw new ArgumentException($"Unknown permission '{permission}'. Add it to Permissions.All.", nameof(permission));
        }

        return builder.RequireAuthorization(Permissions.PolicyName(permission));
    }
}

internal sealed class PermissionPolicyOptions : Microsoft.Extensions.Options.IConfigureOptions<AuthorizationOptions>
{
    public void Configure(AuthorizationOptions options)
    {
        foreach (var permission in Permissions.All)
        {
            options.AddPolicy(Permissions.PolicyName(permission), p => p.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(permission)));
        }
    }
}

public static class AnyPermissionExtensions
{
    /// <summary>Allows the request when the user holds at least one of the permissions (e.g. form definitions readable by anyone who can read the entity).</summary>
    public static TBuilder RequireAnyPermission<TBuilder>(this TBuilder builder, params string[] permissions) where TBuilder : IEndpointConventionBuilder
    {
        foreach (var p in permissions)
        {
            if (!Permissions.All.Contains(p)) throw new ArgumentException($"Unknown permission '{p}'.", nameof(permissions));
        }

        return builder.AddEndpointFilter(async (context, next) =>
        {
            var service = context.HttpContext.RequestServices.GetService<IPermissionService>();
            var granted = service is null ? new HashSet<string>() : await service.GetCurrentAsync(context.HttpContext.RequestAborted);
            return permissions.Any(granted.Contains) ? await next(context) : Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden");
        });
    }
}
