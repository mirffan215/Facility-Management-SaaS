using System.Net;
using System.Net.Http.Json;
using Fms.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Fms.ApiTests;

public sealed class HardeningTests
{
    private static WebApplicationFactory<Program> Factory(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            foreach (var (key, value) in settings) b.UseSetting(key, value);
        });

    [Theory]
    [InlineData("/api/v1/system/info", HttpStatusCode.OK)]
    [InlineData("/api/v1/me", HttpStatusCode.Unauthorized)]
    [InlineData("/api/v1/does-not-exist", HttpStatusCode.Unauthorized)]
    public async Task Every_api_answer_carries_the_strict_headers_and_is_not_cached(string path, HttpStatusCode expected)
    {
        await using var factory = Factory();
        var response = await factory.CreateClient().GetAsync(path);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("same-origin", response.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Too_many_anonymous_requests_get_429_with_retry_after_and_a_problem_document()
    {
        await using var factory = Factory(("Security:RateLimit:AnonymousPerMinute", "3"));
        var client = factory.CreateClient();
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/providers")).StatusCode);

        var limited = await client.GetAsync("/api/v1/auth/providers");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter?.Delta?.TotalSeconds > 0);
        Assert.Equal("application/problem+json", limited.Content.Headers.ContentType?.MediaType);
        Assert.Equal("rate_limited", (await limited.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("code").GetString());
        Assert.Equal("nosniff", limited.Headers.GetValues("X-Content-Type-Options").Single()); // the refusal is hardened too
    }

    [Fact]
    public async Task Health_checks_are_never_rate_limited()
    {
        await using var factory = Factory(("Security:RateLimit:AnonymousPerMinute", "1"));
        var client = factory.CreateClient();
        for (var i = 0; i < 10; i++) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/visits/pass/abcdef")]
    [InlineData("/api/v1/calendar/abcdef")]
    [InlineData("/api/v1/odata/WorkOrders")]
    public async Task Routes_protected_only_by_a_secret_allow_few_guesses_per_address(string path)
    {
        await using var factory = Factory(("Security:RateLimit:SecretRoutesPerMinute", "2"));
        var client = factory.CreateClient();
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await client.GetAsync(path)).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync(path)).StatusCode);

        // Another address's budget is separate, and ordinary routes keep their own larger one.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/providers")).StatusCode);
    }

    /// <summary>The test server has no socket, so give requests a public address to come from.</summary>
    private sealed class FromPublicAddress : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, n) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.7");
                return n(context);
            });
            next(app);
        };
    }

    [Fact]
    public async Task A_client_cannot_dodge_the_limit_by_claiming_another_address()
    {
        await using var factory = Factory(("Security:RateLimit:AnonymousPerMinute", "2")).WithWebHostBuilder(b => b.ConfigureServices(s => s.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, FromPublicAddress>()));
        var client = factory.CreateClient();
        for (var i = 0; i < 2; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/providers");
            request.Headers.Add("X-Forwarded-For", $"203.0.113.{i + 1}");
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
        }

        using var third = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/providers");
        third.Headers.Add("X-Forwarded-For", "203.0.113.99");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(third)).StatusCode); // forwarded headers from an untrusted caller are ignored
    }

    [Fact]
    public async Task A_trusted_proxy_network_makes_the_forwarded_address_count()
    {
        await using var factory = Factory(("Security:RateLimit:AnonymousPerMinute", "2"), ("Security:TrustedProxyCidrs:0", "198.51.100.0/24"))
            .WithWebHostBuilder(b => b.ConfigureServices(s => s.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, FromPublicAddress>()));
        var client = factory.CreateClient();
        async Task<HttpStatusCode> From(string address)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/providers");
            request.Headers.Add("X-Forwarded-For", address);
            return (await client.SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await From("203.0.113.1"));
        Assert.Equal(HttpStatusCode.OK, await From("203.0.113.1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await From("203.0.113.1"));
        Assert.Equal(HttpStatusCode.OK, await From("203.0.113.2")); // a different real client behind the proxy has its own allowance
    }

    [Fact]
    public async Task Rate_limiting_can_be_switched_off_for_a_trusted_gateway_that_does_its_own()
    {
        await using var factory = Factory(("Security:RateLimit:Enabled", "false"), ("Security:RateLimit:AnonymousPerMinute", "1"));
        var client = factory.CreateClient();
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/providers")).StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/odata", true)]
    [InlineData("/api/v1/odata/WorkOrders", true)]
    [InlineData("/API/V1/Visits/Pass/x", true)]
    [InlineData("/api/v1/calendar/x", true)]
    [InlineData("/api/v1/odataX", false)]
    [InlineData("/api/v1/visits", false)]
    [InlineData("/api/v1/visits/mine", false)]
    [InlineData("/api/v1/work-orders", false)]
    [InlineData(null, false)]
    public void Secret_routes_are_recognised_exactly(string? path, bool expected) => Assert.Equal(expected, Hardening.IsSecretRoute(path));

    [Fact]
    public void A_malformed_trusted_network_stops_the_host_instead_of_being_ignored()
    {
        using var factory = Factory(("Security:TrustedProxyCidrs:0", "not-a-network"));
        Assert.ThrowsAny<Exception>(() => factory.CreateClient().GetAsync("/api/v1/system/info").GetAwaiter().GetResult());
    }
}
