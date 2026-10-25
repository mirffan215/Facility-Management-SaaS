using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Fms.Api;
using Fms.Platform.Domain;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using static Fms.IntegrationTests.SqlServerFixture;

namespace Fms.IntegrationTests;

/// <summary>Whole stack: signed token -> database tenant directory -> user provisioning -> row-level security, on SQL Server.</summary>
[Collection(SqlCollection.Name)]
public sealed class SignInEndToEndTests(SqlServerFixture sql) : IDisposable
{
    private const string Audience = "api://fms";
    private const string Prefix = "https://login.test/";
    private readonly RSA _key = RSA.Create(2048);

    public void Dispose() => _key.Dispose();

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Default", sql.AppConnectionString);
            b.UseSetting("Deployment:Mode", "SaaS");
            b.UseSetting("Authentication:Providers:0:Name", "entra");
            b.UseSetting("Authentication:Providers:0:Authority", "https://login.test/common/v2.0");
            b.UseSetting("Authentication:Providers:0:Audience", Audience);
            b.UseSetting("Authentication:Providers:0:IssuerPrefix", Prefix);
            b.UseSetting("Authentication:Providers:0:ExternalTenantClaim", "tid");
            b.ConfigureServices(services => services.PostConfigure<JwtBearerOptions>("idp:entra", o =>
            {
                var config = new OpenIdConnectConfiguration();
                config.SigningKeys.Add(new RsaSecurityKey(_key.ExportParameters(false)) { KeyId = "k1" });
                o.Configuration = config;
                o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
            }));
        });

    private HttpClient Client(WebApplicationFactory<Program> f, string externalTenant, string subject)
    {
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = $"{Prefix}{externalTenant}/v2.0",
            Audience = Audience,
            Claims = new Dictionary<string, object> { ["sub"] = subject, ["tid"] = externalTenant, ["email"] = $"{subject}@x.test", ["name"] = "Test User" },
            Expires = DateTime.UtcNow.AddMinutes(30),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_key) { KeyId = "k1" }, SecurityAlgorithms.RsaSha256),
        });
        var client = f.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<string> MapAsync(Guid tenant)
    {
        var external = Guid.NewGuid().ToString();
        await using var platform = sql.App(null, platform: true);
        platform.TenantIdentityMappings.Add(new TenantIdentityMapping { TenantId = tenant, Provider = "entra", ExternalTenantId = external });
        await platform.SaveChangesAsync();
        return external;
    }

    [Fact]
    public async Task First_sign_in_provisions_a_user_in_the_mapped_tenant()
    {
        var external = await MapAsync(TenantA);
        var subject = "e2e-" + Guid.NewGuid().ToString("N");
        await using var f = Factory();

        var me = await Client(f, external, subject).GetFromJsonAsync<MeInfo>("/api/v1/me");

        Assert.Equal(TenantA, me!.TenantId);
        Assert.NotNull(me.UserId);
        await using var check = sql.App(TenantA);
        var user = await check.Users.SingleAsync(u => u.Subject == subject);
        Assert.Equal(me.UserId, user.Id);
        Assert.Equal($"{subject}@x.test", user.Email);
    }

    [Fact]
    public async Task A_tenant_without_a_stored_mapping_is_forbidden_and_creates_no_user()
    {
        var subject = "nomap-" + Guid.NewGuid().ToString("N");
        await using var f = Factory();

        var response = await Client(f, Guid.NewGuid().ToString(), subject).GetAsync("/api/v1/me");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var platform = sql.App(null, platform: true);
        Assert.False(await platform.Users.AnyAsync(u => u.Subject == subject));
    }

    [Fact]
    public async Task A_deactivated_user_is_forbidden()
    {
        var external = await MapAsync(TenantB);
        var subject = "off-" + Guid.NewGuid().ToString("N");
        await using var f = Factory();
        var client = Client(f, external, subject);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/me")).StatusCode);

        await using (var db = sql.App(TenantB))
        {
            (await db.Users.SingleAsync(u => u.Subject == subject)).IsActive = false;
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Users_of_two_tenants_get_their_own_records()
    {
        var extA = await MapAsync(TenantA);
        var extB = await MapAsync(TenantB);
        var subject = "same-" + Guid.NewGuid().ToString("N");
        await using var f = Factory();

        var a = await Client(f, extA, subject).GetFromJsonAsync<MeInfo>("/api/v1/me");
        var b = await Client(f, extB, subject).GetFromJsonAsync<MeInfo>("/api/v1/me");

        Assert.Equal(TenantA, a!.TenantId);
        Assert.Equal(TenantB, b!.TenantId);
        Assert.NotEqual(a.UserId, b.UserId);
    }

    [Fact]
    public async Task Ready_health_check_includes_the_database()
    {
        await using var f = Factory();
        var response = await f.CreateClient().GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Healthy", await response.Content.ReadAsStringAsync());
    }
}
