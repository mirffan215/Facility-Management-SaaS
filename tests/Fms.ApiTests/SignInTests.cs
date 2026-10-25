using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Fms.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Serilog.Core;
using Serilog.Events;

namespace Fms.ApiTests;

public sealed class SignInTests : IDisposable
{
    private const string EntraPrefix = "https://login.test/";
    private const string Audience = "api://fms";
    private const string SingleIssuer = "https://corp-idp.test";

    private static readonly Guid FmsTenantA = Guid.NewGuid();
    private const string EntraTenantA = "11111111-1111-1111-1111-111111111111";
    private const string EntraUnmapped = "99999999-9999-9999-9999-999999999999";

    private readonly RSA _key = RSA.Create(2048);
    private readonly RSA _otherKey = RSA.Create(2048);

    public void Dispose()
    {
        _key.Dispose();
        _otherKey.Dispose();
    }

    private sealed class CapturingSink : ILogEventSink
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<LogEvent> Events = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }

    private WebApplicationFactory<Program> Factory(bool onPrem = false, Guid? singleTenant = null, ILogEventSink? sink = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Deployment:Mode", onPrem ? "OnPrem" : "SaaS");
            if (onPrem)
            {
                b.UseSetting("Deployment:SingleTenantId", (singleTenant ?? Guid.NewGuid()).ToString());
                b.UseSetting("Deployment:LocalStoragePath", Path.GetTempPath());
            }

            b.UseSetting("Authentication:Providers:0:Name", "entra");
            b.UseSetting("Authentication:Providers:0:Authority", "https://login.test/common/v2.0");
            b.UseSetting("Authentication:Providers:0:Audience", Audience);
            b.UseSetting("Authentication:Providers:0:IssuerPrefix", EntraPrefix);
            b.UseSetting("Authentication:Providers:0:ExternalTenantClaim", "tid");
            b.UseSetting("Authentication:Providers:1:Name", "corp");
            b.UseSetting("Authentication:Providers:1:Authority", SingleIssuer);
            b.UseSetting("Authentication:Providers:1:Audience", Audience);
            b.UseSetting("Authentication:TenantMappings:0:Provider", "entra");
            b.UseSetting("Authentication:TenantMappings:0:ExternalTenantId", EntraTenantA);
            b.UseSetting("Authentication:TenantMappings:0:TenantId", FmsTenantA.ToString());

            b.ConfigureServices(services =>
            {
                if (sink is not null) services.AddSingleton(sink);
                // No network: hand each scheme the test signing key instead of fetching provider metadata.
                var parameters = new RsaSecurityKey(_key.ExportParameters(false)) { KeyId = "k1" };
                foreach (var scheme in new[] { "idp:entra", "idp:corp" })
                {
                    services.PostConfigure<JwtBearerOptions>(scheme, o =>
                    {
                        var config = new OpenIdConnectConfiguration();
                        config.SigningKeys.Add(parameters);
                        o.Configuration = config;
                        o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
                    });
                }
            });
        });

    private string Token(string issuer, string? tid = null, string audience = Audience, RSA? signWith = null,
        DateTime? expires = null, IDictionary<string, object>? extra = null)
    {
        var claims = new Dictionary<string, object> { ["sub"] = "user-1" };
        if (tid is not null) claims["tid"] = tid;
        if (extra is not null) foreach (var (k, v) in extra) claims[k] = v;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            NotBefore = DateTime.UtcNow.AddMinutes(-10),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(30),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(signWith ?? _key) { KeyId = "k1" }, SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static HttpClient Client(WebApplicationFactory<Program> f, string? token)
    {
        var c = f.CreateClient();
        if (token is not null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static string EntraIssuer(string tid) => $"{EntraPrefix}{tid}/v2.0";

    [Fact]
    public async Task No_token_is_401()
    {
        await using var f = Factory();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(f, null).GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Mapped_multi_tenant_token_resolves_the_fms_tenant()
    {
        await using var f = Factory();
        var me = await Client(f, Token(EntraIssuer(EntraTenantA), EntraTenantA)).GetFromJsonAsync<MeInfo>("/api/v1/me");

        Assert.Equal(new MeInfo("user-1", "entra", FmsTenantA), me);
    }

    [Fact]
    public async Task Unmapped_provider_tenant_is_authenticated_but_forbidden()
    {
        await using var f = Factory();
        var response = await Client(f, Token(EntraIssuer(EntraUnmapped), EntraUnmapped)).GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Fms_claims_inside_a_token_are_ignored()
    {
        await using var f = Factory();
        var forged = new Dictionary<string, object> { ["fms_tid"] = Guid.NewGuid().ToString(), ["fms_idp"] = "other" };

        var unmapped = await Client(f, Token(EntraIssuer(EntraUnmapped), EntraUnmapped, extra: forged)).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Forbidden, unmapped.StatusCode);

        var mapped = await Client(f, Token(EntraIssuer(EntraTenantA), EntraTenantA, extra: forged)).GetFromJsonAsync<MeInfo>("/api/v1/me");
        Assert.Equal(FmsTenantA, mapped!.TenantId);
        Assert.Equal("entra", mapped.Provider);
    }

    [Fact]
    public async Task Provider_without_a_tenant_claim_never_resolves_a_tenant_in_saas()
    {
        await using var f = Factory();
        var response = await Client(f, Token(SingleIssuer)).GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Bad_tokens_are_401()
    {
        await using var f = Factory();
        var issuer = EntraIssuer(EntraTenantA);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(f, Token(issuer, EntraTenantA, audience: "api://other")).GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(f, Token(issuer, EntraTenantA, signWith: _otherKey)).GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(f, Token(issuer, EntraTenantA, expires: DateTime.UtcNow.AddMinutes(-5))).GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(f, Token("https://evil.test/x", EntraTenantA)).GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(f, "not-a-jwt").GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Unsigned_tokens_are_rejected()
    {
        await using var f = Factory();
        var unsigned = new JsonWebTokenHandler().CreateToken(
            $$"""{"iss":"{{EntraIssuer(EntraTenantA)}}","aud":"{{Audience}}","sub":"x","tid":"{{EntraTenantA}}","exp":{{DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds()}}}""");

        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(f, unsigned).GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task OnPrem_maps_every_signed_in_user_to_the_single_tenant()
    {
        var single = Guid.NewGuid();
        await using var f = Factory(onPrem: true, singleTenant: single);

        var me = await Client(f, Token(SingleIssuer)).GetFromJsonAsync<MeInfo>("/api/v1/me");
        Assert.Equal(single, me!.TenantId);

        var entraUnmapped = await Client(f, Token(EntraIssuer(EntraUnmapped), EntraUnmapped)).GetFromJsonAsync<MeInfo>("/api/v1/me");
        Assert.Equal(single, entraUnmapped!.TenantId);
    }

    [Fact]
    public async Task System_info_shows_tenant_resolved_for_a_signed_in_user()
    {
        await using var f = Factory();
        var info = await Client(f, Token(EntraIssuer(EntraTenantA), EntraTenantA)).GetFromJsonAsync<SystemInfo>("/api/v1/system/info");

        Assert.True(info!.TenantResolved);
    }

    [Fact]
    public async Task Public_endpoints_stay_anonymous_and_list_providers()
    {
        await using var f = Factory();
        var client = Client(f, null);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        var providers = await client.GetFromJsonAsync<AuthProviderInfo[]>("/api/v1/auth/providers");
        Assert.Equal(["entra", "corp"], providers!.Select(p => p.Name));
    }

    [Fact]
    public async Task Request_logs_carry_the_tenant_and_never_the_token()
    {
        var sink = new CapturingSink();
        await using var f = Factory(sink: sink);
        var token = Token(EntraIssuer(EntraTenantA), EntraTenantA);

        (await Client(f, token).GetAsync("/api/v1/me")).EnsureSuccessStatusCode();

        var requestLog = Assert.Single(sink.Events, e => e.MessageTemplate.Text.StartsWith("HTTP ", StringComparison.Ordinal) && e.Properties.ContainsKey("RequestPath"));
        Assert.Equal(FmsTenantA.ToString(), requestLog.Properties["TenantId"].ToString().Trim('"'));
        Assert.Equal("fms-api", requestLog.Properties["Service"].ToString().Trim('"'));
        Assert.All(sink.Events, e =>
        {
            Assert.DoesNotContain(token, e.RenderMessage(), StringComparison.Ordinal);
            Assert.All(e.Properties.Values, v => Assert.DoesNotContain(token, v.ToString(), StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task Anonymous_request_logs_have_no_tenant()
    {
        var sink = new CapturingSink();
        await using var f = Factory(sink: sink);

        await f.CreateClient().GetAsync("/api/v1/system/info");

        var requestLog = Assert.Single(sink.Events, e => e.MessageTemplate.Text.StartsWith("HTTP ", StringComparison.Ordinal) && e.Properties["RequestPath"].ToString().Contains("system/info"));
        Assert.False(requestLog.Properties.ContainsKey("TenantId"));
    }
}
