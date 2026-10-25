using Fms.Api;
using Fms.Api.Authentication;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Security;
using Fms.Platform;
using Fms.Platform.Services;
using Fms.BuildingBlocks.Deployment;
using Fms.BuildingBlocks.Tenancy;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddFmsDeployment(o => builder.Configuration.GetSection(DeploymentOptions.SectionName).Bind(o));
builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
builder.Services.AddFmsAuthentication(builder.Configuration);
builder.Services.AddFmsPermissions();
if (builder.Configuration.GetConnectionString("Default") is { Length: > 0 } connectionString)
{
    builder.Services.AddFmsPlatform(connectionString);
}

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseMiddleware<UserProvisioningMiddleware>();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new() { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready").AllowAnonymous();

var v1 = app.MapGroup("/api/v1");
v1.MapGet("/system/info", (IOptions<DeploymentOptions> deployment, ILicenseProvider license, ITenantContext tenant) =>
    Results.Ok(new SystemInfo(
        deployment.Value.Mode.ToString(),
        license.Current.Edition,
        license.Current.IsLicensed,
        tenant.TenantId is not null))).AllowAnonymous();

// Public: what the web app needs to start an OIDC sign-in (authority and client id are not secrets).
v1.MapGet("/auth/providers", (IOptions<AuthOptions> auth) =>
    Results.Ok(auth.Value.Providers.Select(p => new AuthProviderInfo(p.Name, p.Authority, p.ClientId)))).AllowAnonymous();

v1.MapGet("/me", async (ICurrentUser user, HttpContext http) =>
{
    var permissions = http.RequestServices.GetService<IPermissionService>();
    var granted = permissions is null ? null : (await permissions.GetCurrentAsync(http.RequestAborted)).Order().ToList();
    return Results.Ok(new MeInfo(user.Subject, user.Provider, user.TenantId, user.UserId, granted));
});

// Access administration needs the platform database; without one (local dev, tests) these endpoints do not exist.
if (app.Configuration.GetConnectionString("Default") is { Length: > 0 })
{
    v1.MapGet("/roles", async (AccessAdministration access, CancellationToken ct) => Results.Ok(await access.ListRolesAsync(ct)))
        .RequirePermission(Permissions.RolesRead);

    v1.MapGet("/users", async (AccessAdministration access, CancellationToken ct) => Results.Ok(await access.ListUsersAsync(ct)))
        .RequirePermission(Permissions.UsersRead);

    v1.MapPut("/users/{userId:guid}/roles", async (Guid userId, SetRolesRequest body, AccessAdministration access, CancellationToken ct) =>
        await access.SetUserRolesAsync(userId, body.RoleIds ?? [], ct) switch
        {
            AccessResult.Ok => Results.NoContent(),
            AccessResult.NotFound => Results.Problem(statusCode: 404, title: "User not found"),
            AccessResult.InvalidRole => Results.Problem(statusCode: 400, title: "Unknown role"),
            AccessResult.CannotChangeOwnRoles => Results.Problem(statusCode: 403, title: "You cannot change your own roles"),
            _ => Results.Problem(statusCode: 500),
        })
        .RequirePermission(Permissions.UsersManage);
}

app.Run();

namespace Fms.Api
{
    public sealed record SystemInfo(string DeploymentMode, string Edition, bool Licensed, bool TenantResolved);

    public sealed record AuthProviderInfo(string Name, string Authority, string? ClientId);

    public sealed record MeInfo(string? Subject, string? Provider, Guid? TenantId, Guid? UserId = null, IReadOnlyList<string>? Permissions = null);

    public sealed record SetRolesRequest(IReadOnlyList<Guid>? RoleIds);

    public partial class Program;
}
