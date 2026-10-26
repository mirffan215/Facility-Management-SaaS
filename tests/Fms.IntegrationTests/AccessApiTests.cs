using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Fms.Api;
using Fms.BuildingBlocks.Security;
using Fms.Platform.Domain;
using Fms.Platform.Security;
using Fms.Platform.Services;
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

[Collection(SqlCollection.Name)]
public sealed class AccessApiTests(SqlServerFixture sql) : IDisposable
{
    private const string Audience = "api://fms";
    private const string Prefix = "https://login.test/";
    private readonly RSA _key = RSA.Create(2048);

    public void Dispose() => _key.Dispose();

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Default", sql.AppConnectionString);
            b.UseSetting("Authentication:Providers:0:Name", "entra");
            b.UseSetting("Authentication:Providers:0:Authority", "https://login.test/common/v2.0");
            b.UseSetting("Authentication:Providers:0:Audience", Audience);
            b.UseSetting("Authentication:Providers:0:IssuerPrefix", Prefix);
            b.UseSetting("Authentication:Providers:0:ExternalTenantClaim", "tid");
            b.ConfigureServices(s => s.PostConfigure<JwtBearerOptions>("idp:entra", o =>
            {
                var config = new OpenIdConnectConfiguration();
                config.SigningKeys.Add(new RsaSecurityKey(_key.ExportParameters(false)) { KeyId = "k1" });
                o.Configuration = config;
                o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
            }));
        });

    private async Task<string> MapAsync(Guid tenant)
    {
        var external = Guid.NewGuid().ToString();
        await using var platform = sql.App(null, platform: true);
        platform.TenantIdentityMappings.Add(new TenantIdentityMapping { TenantId = tenant, Provider = "entra", ExternalTenantId = external });
        await platform.SaveChangesAsync();
        return external;
    }

    private HttpClient Client(WebApplicationFactory<Program> f, string externalTenant, string subject)
    {
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = $"{Prefix}{externalTenant}/v2.0",
            Audience = Audience,
            Claims = new Dictionary<string, object> { ["sub"] = subject, ["tid"] = externalTenant },
            Expires = DateTime.UtcNow.AddMinutes(30),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_key) { KeyId = "k1" }, SecurityAlgorithms.RsaSha256),
        });
        var client = f.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task MakeAdminAsync(Guid tenant, string subject)
    {
        await using var platform = sql.App(null, platform: true);
        await InitialAdmin.EnsureAsync(platform, tenant, "entra", subject);
    }

    [Fact]
    public async Task A_user_without_roles_has_no_permissions_and_is_denied_admin_endpoints()
    {
        var external = await MapAsync(TenantA);
        await using var f = Factory();
        var client = Client(f, external, "plain-" + Guid.NewGuid().ToString("N"));

        var me = await client.GetFromJsonAsync<MeInfo>("/api/v1/me");
        Assert.Empty(me!.Permissions!);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/roles")).StatusCode);
    }

    [Fact]
    public async Task The_initial_administrator_has_every_permission_and_can_list_users_and_roles()
    {
        var external = await MapAsync(TenantA);
        var subject = "admin-" + Guid.NewGuid().ToString("N");
        await MakeAdminAsync(TenantA, subject);
        await using var f = Factory();
        var client = Client(f, external, subject);

        var me = await client.GetFromJsonAsync<MeInfo>("/api/v1/me");
        Assert.Equal(Permissions.All.Order(), me!.Permissions);

        var users = await client.GetFromJsonAsync<UserInfo[]>("/api/v1/users");
        Assert.Contains(users!, u => u.Id == me.UserId);
        var roles = await client.GetFromJsonAsync<RoleInfo[]>("/api/v1/roles");
        Assert.Equal(SystemRoles.Definitions.Count, roles!.Length);
    }

    [Fact]
    public async Task An_administrator_grants_a_role_and_the_user_gains_its_permissions()
    {
        var external = await MapAsync(TenantA);
        var adminSubject = "admin-" + Guid.NewGuid().ToString("N");
        var staffSubject = "staff-" + Guid.NewGuid().ToString("N");
        await MakeAdminAsync(TenantA, adminSubject);
        await using var f = Factory();
        var admin = Client(f, external, adminSubject);
        var staff = Client(f, external, staffSubject);
        var staffMe = await staff.GetFromJsonAsync<MeInfo>("/api/v1/me");
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync("/api/v1/users")).StatusCode);

        var roles = await admin.GetFromJsonAsync<RoleInfo[]>("/api/v1/roles");
        var manager = roles!.Single(r => r.Name == SystemRoles.FacilityManager);
        var put = await admin.PutAsJsonAsync($"/api/v1/users/{staffMe!.UserId}/roles", new SetRolesRequest([manager.Id]));

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync("/api/v1/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PutAsJsonAsync($"/api/v1/users/{staffMe.UserId}/roles", new SetRolesRequest([]))).StatusCode);
    }

    [Fact]
    public async Task An_administrator_cannot_change_their_own_roles_or_use_another_tenants_roles()
    {
        var external = await MapAsync(TenantA);
        var adminSubject = "admin-" + Guid.NewGuid().ToString("N");
        await MakeAdminAsync(TenantA, adminSubject);
        await using var f = Factory();
        var admin = Client(f, external, adminSubject);
        var me = await admin.GetFromJsonAsync<MeInfo>("/api/v1/me");
        var staff = await Client(f, external, "s-" + Guid.NewGuid().ToString("N")).GetFromJsonAsync<MeInfo>("/api/v1/me");

        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PutAsJsonAsync($"/api/v1/users/{me!.UserId}/roles", new SetRolesRequest([]))).StatusCode);

        Guid foreignRole;
        await using (var b = sql.App(TenantB)) foreignRole = await b.Roles.Select(r => r.Id).FirstAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/v1/users/{staff!.UserId}/roles", new SetRolesRequest([foreignRole]))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync($"/api/v1/users/{Guid.NewGuid()}/roles", new SetRolesRequest([]))).StatusCode);
    }

    [Fact]
    public async Task An_administrator_of_one_tenant_sees_nothing_of_another()
    {
        var extA = await MapAsync(TenantA);
        var extB = await MapAsync(TenantB);
        var adminSubject = "admin-" + Guid.NewGuid().ToString("N");
        await MakeAdminAsync(TenantA, adminSubject);
        await using var f = Factory();
        var bUser = await Client(f, extB, "b-" + Guid.NewGuid().ToString("N")).GetFromJsonAsync<MeInfo>("/api/v1/me");

        var users = await Client(f, extA, adminSubject).GetFromJsonAsync<UserInfo[]>("/api/v1/users");

        Assert.DoesNotContain(users!, u => u.Id == bUser!.UserId);
    }
}

[Collection(SqlCollection.Name)]
public sealed class UserDeactivationTests(SqlServerFixture sql) : IDisposable
{
    private readonly ApiHarness _api = new(sql);
    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task An_administrator_deactivates_and_reactivates_a_person_and_it_takes_effect_on_their_next_request()
    {
        var admin = await _api.ClientAsync(TenantA, SystemRoles.Administrator);
        var staff = await _api.ClientAsync(TenantA, SystemRoles.Technician);
        var staffId = (await staff.GetFromJsonAsync<MeInfo>("/api/v1/me", ApiHarness.Json))!.UserId;
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync("/api/v1/assets")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/users/{staffId}/deactivate", null)).StatusCode);
        var refused = await staff.GetAsync("/api/v1/assets");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("deactivated", await refused.Content.ReadAsStringAsync());
        var users = await admin.GetFromJsonAsync<Fms.Platform.Services.UserInfo[]>("/api/v1/users", ApiHarness.Json);
        Assert.False(users!.Single(u => u.Id == staffId).IsActive);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/users/{staffId}/activate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync("/api/v1/assets")).StatusCode);

        await using var check = sql.App(TenantA);
        var actions = await check.AuditEvents.Where(e => e.EntityId == staffId.ToString()).OrderBy(e => e.OccurredAt).Select(e => e.Action).ToListAsync();
        Assert.Equal(["user.deactivated", "user.reactivated"], actions);
    }

    [Fact]
    public async Task Nobody_can_deactivate_themselves_other_tenants_users_are_untouchable_and_only_user_managers_may_try()
    {
        var admin = await _api.ClientAsync(TenantA, SystemRoles.Administrator);
        var adminId = (await admin.GetFromJsonAsync<MeInfo>("/api/v1/me", ApiHarness.Json))!.UserId;
        var otherTenant = await _api.ClientAsync(TenantB, SystemRoles.Technician);
        var otherId = (await otherTenant.GetFromJsonAsync<MeInfo>("/api/v1/me", ApiHarness.Json))!.UserId;
        var manager = await _api.ClientAsync(TenantA, SystemRoles.FacilityManager);

        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync($"/api/v1/users/{adminId}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync($"/api/v1/users/{otherId}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync($"/api/v1/users/{Guid.NewGuid()}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsync($"/api/v1/users/{otherId}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await otherTenant.GetAsync("/api/v1/assets")).StatusCode); // still active
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/users/{adminId}/activate", null)).StatusCode); // reactivating yourself is harmless
    }
}
