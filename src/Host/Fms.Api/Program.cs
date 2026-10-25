using Fms.Api;
using Fms.BuildingBlocks.Deployment;
using Fms.BuildingBlocks.Tenancy;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddFmsDeployment(o => builder.Configuration.GetSection(DeploymentOptions.SectionName).Bind(o));
builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<TenantResolutionMiddleware>();

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

var v1 = app.MapGroup("/api/v1");
v1.MapGet("/system/info", (IOptions<DeploymentOptions> deployment, ILicenseProvider license, ITenantContext tenant) =>
    Results.Ok(new SystemInfo(
        deployment.Value.Mode.ToString(),
        license.Current.Edition,
        license.Current.IsLicensed,
        tenant.TenantId is not null)));

app.Run();

namespace Fms.Api
{
    public sealed record SystemInfo(string DeploymentMode, string Edition, bool Licensed, bool TenantResolved);

    public partial class Program;
}
