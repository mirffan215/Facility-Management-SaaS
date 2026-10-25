using Fms.Api;
using Fms.Api.Authentication;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Deployment;
using Fms.BuildingBlocks.Tenancy;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddFmsDeployment(o => builder.Configuration.GetSection(DeploymentOptions.SectionName).Bind(o));
builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
builder.Services.AddFmsAuthentication(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthorization();

app.MapHealthChecks("/health/live").AllowAnonymous();
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

v1.MapGet("/me", (ICurrentUser user) => Results.Ok(new MeInfo(user.Subject, user.Provider, user.TenantId)));

app.Run();

namespace Fms.Api
{
    public sealed record SystemInfo(string DeploymentMode, string Edition, bool Licensed, bool TenantResolved);

    public sealed record AuthProviderInfo(string Name, string Authority, string? ClientId);

    public sealed record MeInfo(string? Subject, string? Provider, Guid? TenantId);

    public partial class Program;
}
