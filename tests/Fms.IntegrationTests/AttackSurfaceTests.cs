using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Fms.IntegrationTests;

/// <summary>Locks the list of things anyone can call without signing in. A new anonymous route must be added here on purpose, with a reason.</summary>
[Collection(SqlCollection.Name)]
public sealed class AttackSurfaceTests(SqlServerFixture sql) : IDisposable
{
    private readonly ApiHarness _api = new(sql);
    public void Dispose() => _api.Dispose();

    /// <summary>Each is protected by something other than a sign-in, and is rate limited when a guessable secret is all that protects it (see Hardening.IsSecretRoute).</summary>
    private static readonly string[] Anonymous =
    [
        "ANY /health/live",                                // liveness, no data
        "ANY /health/ready",                               // readiness, no data
        "GET /api/v1/system/info",                         // mode, edition, license state: needed by the sign-in page and the banner
        "GET /api/v1/auth/providers",                      // public identity-provider settings the web app needs to start sign-in
        "GET /api/v1/calendar/{token}",                    // secret calendar address, hash stored, same 404 for any wrong secret
        "GET /api/v1/visits/pass/{code}",                  // visitor pass, 128-bit secret, shows only the visitor's own name, place and time
        "GET /api/v1/visits/pass/{code}/qr.svg",           // the same pass as a picture
        "GET /api/v1/odata/",                              // BI feed, revocable token (Basic or X-Api-Key), read only
        "GET /api/v1/odata/$metadata",
        "GET /api/v1/odata/{set}",
    ];

    private static string Describe(RouteEndpoint e) =>
        $"{e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "ANY"} {e.RoutePattern.RawText}";

    [Fact]
    public void Exactly_the_expected_routes_are_open_without_signing_in()
    {
        _ = _api.Factory.CreateClient(); // starts the host
        var endpoints = _api.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        Assert.True(endpoints.Count > 200, "the API should map its whole surface here");

        var open = endpoints.Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null).Select(Describe).Order().ToList();
        Assert.Equal(Anonymous.Order().ToList(), open);
    }

    [Fact]
    public async Task Everything_else_demands_a_sign_in_or_a_named_permission()
    {
        _ = _api.Factory.CreateClient();
        var endpoints = _api.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is null).ToList();

        // The fallback policy (authenticated user) covers any route without its own; a route that explicitly names a policy must name a permission.
        foreach (var endpoint in endpoints)
        {
            foreach (var authorize in endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>())
            {
                if (authorize.Policy is { Length: > 0 } policy) Assert.StartsWith("perm:", policy);
            }
        }

        var fallback = await _api.Factory.Services.GetRequiredService<IAuthorizationPolicyProvider>().GetFallbackPolicyAsync();
        Assert.NotNull(fallback);
        Assert.Contains(fallback!.Requirements, r => r is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
    }
}
