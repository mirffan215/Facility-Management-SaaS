using System.Net;
using System.Net.Http.Json;
using Fms.BuildingBlocks.Deployment;
using Fms.BuildingBlocks.Licensing;
using Fms.Platform.Security;
using Fms.Platform.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static Fms.IntegrationTests.ApiHarness;

namespace Fms.IntegrationTests;

[Collection(SqlCollection.Name)]
public sealed class LicenseEnforcementTests : IDisposable
{
    private sealed class SwitchableLicense : ILicenseProvider
    {
        public LicenseInfo Current { get; set; } = new(true, "Test", new HashSet<string> { "*" }, null);
    }

    private readonly SqlServerFixture _sql;
    private readonly SwitchableLicense _license = new();
    private readonly ApiHarness _api;

    public LicenseEnforcementTests(SqlServerFixture sql)
    {
        _sql = sql;
        _api = new ApiHarness(sql, services => services.Replace(ServiceDescriptor.Singleton<ILicenseProvider>(_license)));
    }

    public void Dispose() => _api.Dispose();

    private static LicenseInfo Info(LicenseState state, bool licensed, string[]? modules = null, int? maxUsers = null) =>
        new(licensed, "Test", new HashSet<string>(modules ?? ["*"]), DateTimeOffset.UtcNow.AddDays(-1)) { State = state, MaxUsers = maxUsers, Detail = "d" };

    private async Task<(Guid Tenant, HttpClient Admin)> NewTenantAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var platform = _sql.App(null, platform: true))
        {
            platform.Tenants.Add(new Fms.Platform.Domain.Tenant { Id = tenant, Name = "Lic " + tenant.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
            await platform.SaveChangesAsync();
            await SystemRoleSync.EnsureAsync(platform, tenant);
        }

        return (tenant, await _api.ClientAsync(tenant, SystemRoles.Administrator));
    }

    private static object Site() => new { type = "Site", code = FacilitiesScope.Code("S"), name = "Main" };

    [Fact]
    public async Task Without_a_valid_license_data_can_be_read_but_nothing_can_be_changed()
    {
        var (_, admin) = await NewTenantAsync();
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(admin, "/api/v1/locations", Site())).StatusCode);

        foreach (var state in new[] { LicenseState.Expired, LicenseState.Missing, LicenseState.Invalid, LicenseState.WrongInstallation, LicenseState.NotYetValid })
        {
            _license.Current = Info(state, licensed: false);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/locations")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/reports/dashboard")).StatusCode);

            var blocked = await PostAsync(admin, "/api/v1/locations", Site());
            Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
            Assert.Equal("license.read_only", (await blocked.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("code").GetString());
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.DeleteAsync($"/api/v1/locations/{Guid.NewGuid()}")).StatusCode);
        }

        _license.Current = Info(LicenseState.Valid, licensed: true);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(admin, "/api/v1/locations", Site())).StatusCode);
    }

    [Fact]
    public async Task The_system_and_sign_in_endpoints_still_answer_without_a_license()
    {
        var (_, admin) = await NewTenantAsync();
        _license.Current = Info(LicenseState.Missing, licensed: false);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/me")).StatusCode);
        var info = await ReadAsync<Fms.Api.SystemInfo>(await _api.Factory.CreateClient().GetAsync("/api/v1/system/info"));
        Assert.Equal("Missing", info.LicenseState);
        Assert.False(info.Licensed);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/license")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _api.Factory.CreateClient().GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task A_module_outside_the_license_is_closed_even_for_reading_and_others_keep_working()
    {
        var (_, admin) = await NewTenantAsync();
        _license.Current = Info(LicenseState.Valid, licensed: true, modules: ["work"]);

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/work-orders")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/locations")).StatusCode); // core is always there
        foreach (var path in new[] { "/api/v1/inventory/items", "/api/v1/vendors", "/api/v1/purchasing/requests", "/api/v1/spaces/occupancy/" + Guid.NewGuid(), "/api/v1/bookings/mine", "/api/v1/visits", "/api/v1/reports/dashboard", "/api/v1/pm/schedules", "/api/v1/integration/erp/settings" })
        {
            var response = await admin.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{path} gave {response.StatusCode}");
            Assert.Equal("license.module_not_licensed", (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("code").GetString());
        }

        // Even without any sign-in, a closed module does not answer (it is not "found", it is "not licensed").
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.Factory.CreateClient().GetAsync("/api/v1/odata/WorkOrders")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.Factory.CreateClient().GetAsync("/api/v1/visits/pass/abc")).StatusCode);
    }

    [Fact]
    public async Task Grace_keeps_everything_working_and_says_so()
    {
        var (_, admin) = await NewTenantAsync();
        _license.Current = Info(LicenseState.Grace, licensed: true);
        var response = await PostAsync(admin, "/api/v1/locations", Site());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("grace", response.Headers.GetValues("X-License-State").Single());
    }

    [Fact]
    public async Task Only_people_with_the_license_right_see_the_license_page_and_it_counts_active_users()
    {
        var (tenant, admin) = await NewTenantAsync();
        var viewer = await _api.ClientAsync(tenant, SystemRoles.Viewer);
        _license.Current = Info(LicenseState.Valid, licensed: true, modules: ["work", "inventory"], maxUsers: 10);

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/v1/license")).StatusCode);
        var status = await ReadAsync<Fms.Api.LicenseStatus>(await admin.GetAsync("/api/v1/license"));
        Assert.Equal("Valid", status.State);
        Assert.Equal(10, status.MaxUsers);
        Assert.Equal(2, status.ActiveUsers);
        Assert.Equal(["inventory", "work"], status.Modules);
        Assert.Contains("visitors", status.AllModules);
    }

    [Fact]
    public async Task The_user_limit_stops_new_people_and_reactivation_but_never_existing_users()
    {
        var (tenant, admin) = await NewTenantAsync();
        var second = await _api.ClientAsync(tenant, SystemRoles.Viewer);
        var secondId = (await second.GetFromJsonAsync<Fms.Api.MeInfo>("/api/v1/me"))!.UserId!.Value;

        _license.Current = Info(LicenseState.Valid, licensed: true, maxUsers: 2);

        // Two active users exist: a newcomer is turned away, and is not created.
        var newcomer = await _api.FirstTimeClientAsync(tenant);
        var refused = await newcomer.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("license.user_limit", (await refused.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/v1/me")).StatusCode);

        // Deactivate one: the newcomer can now come in, which fills the license again.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/users/{secondId}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await newcomer.GetAsync("/api/v1/me")).StatusCode);

        // The deactivated user cannot be switched back on while the license is full.
        var again = await admin.PostAsync($"/api/v1/users/{secondId}/activate", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        // Raising the license lets it through.
        _license.Current = Info(LicenseState.Valid, licensed: true, maxUsers: 3);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/users/{secondId}/activate", null)).StatusCode);
    }

    [Fact]
    public async Task Another_organisations_users_do_not_count_against_this_ones_limit()
    {
        var (tenantA, adminA) = await NewTenantAsync();
        var (tenantB, adminB) = await NewTenantAsync();
        _ = await _api.ClientAsync(tenantB, SystemRoles.Viewer);
        _ = await _api.ClientAsync(tenantB, SystemRoles.Viewer);
        _license.Current = Info(LicenseState.Valid, licensed: true, maxUsers: 2);

        var newcomer = await _api.FirstTimeClientAsync(tenantA); // A has one user; B's three do not count
        Assert.Equal(HttpStatusCode.OK, (await newcomer.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await adminA.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await adminB.GetAsync("/api/v1/me")).StatusCode);
    }
}
