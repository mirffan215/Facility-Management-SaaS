using System.Net;
using System.Net.Http.Json;
using Fms.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task OnPrem_mode_reports_its_mode_and_no_tenant_for_anonymous_callers()
    {
        await using var factory = Factory(
            ("Deployment:Mode", "OnPrem"),
            ("Deployment:SingleTenantId", Guid.NewGuid().ToString()),
            ("Deployment:LocalStoragePath", Path.GetTempPath()));

        var info = await factory.CreateClient().GetFromJsonAsync<SystemInfo>("/api/v1/system/info");

        Assert.False(info!.TenantResolved);
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
    public async Task Anonymous_calls_to_unknown_routes_get_401_problem_details()
    {
        await using var factory = Factory();
        var response = await factory.CreateClient().GetAsync("/api/v1/nope");

        // The fallback policy requires sign-in, so anonymous callers cannot probe which routes exist.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}

public sealed class LoggingIsolationTests
{
    private sealed class Sink : Serilog.Core.ILogEventSink
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Paths = new();

        public void Emit(Serilog.Events.LogEvent e)
        {
            if (e.MessageTemplate.Text.StartsWith("HTTP ", StringComparison.Ordinal) && e.Properties.TryGetValue("RequestPath", out var path))
                Paths.Enqueue(path.ToString().Trim('"'));
        }
    }

    private static WebApplicationFactory<Program> Host(Sink sink) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<Serilog.Core.ILogEventSink>(sink)));

    [Fact]
    public async Task Hosts_in_one_process_never_receive_each_others_request_logs()
    {
        var sinkA = new Sink();
        var sinkB = new Sink();
        await using var a = Host(sinkA);
        var clientA = a.CreateClient();
        await using var b = Host(sinkB); // built last: with a process-wide logger it would take over A's request logs
        var clientB = b.CreateClient();

        await clientA.GetAsync("/api/v1/system/info");
        await clientB.GetAsync("/api/v1/auth/providers");
        await clientA.GetAsync("/api/v1/system/info");

        Assert.Equal(["/api/v1/system/info", "/api/v1/system/info"], sinkA.Paths);
        Assert.Equal(["/api/v1/auth/providers"], sinkB.Paths);
    }
}
