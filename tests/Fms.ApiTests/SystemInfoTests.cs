using System.Net;
using System.Net.Http.Json;
using Fms.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace Fms.ApiTests;

public sealed class SystemInfoTests
{
    private static WebApplicationFactory<Program> Factory(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            foreach (var (key, value) in settings)
            {
                b.UseSetting(key, value);
            }
        });

    [Fact]
    public async Task SaaS_mode_reports_no_tenant_for_anonymous_callers()
    {
        await using var factory = Factory(("Deployment:Mode", "SaaS"));
        var info = await factory.CreateClient().GetFromJsonAsync<SystemInfo>("/api/v1/system/info");

        Assert.Equal(new SystemInfo("SaaS", "SaaS", true, false), info);
    }

    [Fact]
    public async Task SaaS_mode_ignores_tenant_headers()
    {
        await using var factory = Factory(("Deployment:Mode", "SaaS"));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", Guid.NewGuid().ToString());

        var info = await client.GetFromJsonAsync<SystemInfo>("/api/v1/system/info");

        Assert.False(info!.TenantResolved);
    }

    [Fact]
    public async Task OnPrem_mode_resolves_the_single_tenant()
    {
        await using var factory = Factory(
            ("Deployment:Mode", "OnPrem"),
            ("Deployment:SingleTenantId", Guid.NewGuid().ToString()),
            ("Deployment:LocalStoragePath", Path.GetTempPath()));

        var info = await factory.CreateClient().GetFromJsonAsync<SystemInfo>("/api/v1/system/info");

        Assert.True(info!.TenantResolved);
        Assert.Equal("OnPrem", info.DeploymentMode);
    }

    [Fact]
    public void OnPrem_mode_without_required_settings_fails_at_startup()
    {
        using var factory = Factory(("Deployment:Mode", "OnPrem"));
        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_endpoints_are_available(string path)
    {
        await using var factory = Factory();
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Unknown_routes_return_problem_details()
    {
        await using var factory = Factory();
        var response = await factory.CreateClient().GetAsync("/api/v1/nope");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
