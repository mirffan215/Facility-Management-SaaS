using System.Net;
using System.Net.Http.Json;
using Fms.BuildingBlocks.Results;
using Fms.Facilities.Domain;
using Fms.Facilities.Services;
using Fms.Platform.Security;
using Microsoft.EntityFrameworkCore;
using static Fms.IntegrationTests.ApiHarness;
using static Fms.IntegrationTests.SqlServerFixture;

namespace Fms.IntegrationTests;

[Collection(SqlCollection.Name)]
public sealed class LocationApiTests(SqlServerFixture sql) : IDisposable
{
    private readonly ApiHarness _api = new(sql);
    public void Dispose() => _api.Dispose();

    private static object NewSite(string? code = null) => new { type = "Site", code = code ?? FacilitiesScope.Code("S"), name = "Riyadh HQ", nameAr = "المقر", timeZone = "Asia/Riyadh" };

    [Fact]
    public async Task A_facility_manager_manages_locations_end_to_end()
    {
        var manager = await _api.ClientAsync(TenantA, SystemRoles.FacilityManager);

        var created = await PostAsync(manager, "/api/v1/locations", NewSite());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var site = await ReadAsync<LocationDto>(created);
        Assert.Equal($"/api/v1/locations/{site.Id}", created.Headers.Location!.OriginalString);

        var building = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", new { type = "Building", parentId = site.Id, code = FacilitiesScope.Code("B"), name = "Tower" }));
        var renamed = await PutAsync(manager, $"/api/v1/locations/{building.Id}", new { code = building.Code, name = "Tower A", rowVersion = building.RowVersion });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var updated = await ReadAsync<LocationDto>(renamed);

        var tree = await manager.GetFromJsonAsync<LocationNode[]>("/api/v1/locations/tree", Json);
        Assert.Contains(tree!, n => n.Id == building.Id && n.ParentId == site.Id);

        var archived = await PostAsync(manager, $"/api/v1/locations/{updated.Id}/archive", new { rowVersion = updated.RowVersion });
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        Assert.True((await ReadAsync<LocationDto>(archived)).IsArchived);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/v1/locations/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Failures_are_problem_details_with_the_right_status()
    {
        var manager = await _api.ClientAsync(TenantA, SystemRoles.FacilityManager);
        var code = FacilitiesScope.Code("S");
        var site = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", NewSite(code)));

        var invalid = await PostAsync(manager, "/api/v1/locations", new { type = "Floor", code = "bad code", name = "" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType?.MediaType);
        Assert.Contains("\"errors\"", await invalid.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(manager, "/api/v1/locations", NewSite(code))).StatusCode);
        var stale = await PutAsync(manager, $"/api/v1/locations/{site.Id}", new { code = site.Code, name = "x", rowVersion = Convert.ToBase64String([1, 2, 3, 4, 5, 6, 7, 8]) });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(manager, $"/api/v1/locations/{site.Id}", new { code = site.Code, name = "x" })).StatusCode);
    }

    [Fact]
    public async Task Permissions_decide_who_can_read_and_who_can_change()
    {
        var manager = await _api.ClientAsync(TenantA, SystemRoles.FacilityManager);
        var site = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", NewSite()));

        var technician = await _api.ClientAsync(TenantA, SystemRoles.Technician);
        var requester = await _api.ClientAsync(TenantA, SystemRoles.Requester);
        var nobody = await _api.ClientAsync(TenantA, null);

        Assert.Equal(HttpStatusCode.OK, (await technician.GetAsync($"/api/v1/locations/{site.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await requester.GetAsync("/api/v1/locations/tree")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.GetAsync("/api/v1/locations/tree")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await technician.PostAsJsonAsync("/api/v1/locations", NewSite(), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await technician.PutAsJsonAsync($"/api/v1/locations/{site.Id}", new { code = site.Code, name = "x", rowVersion = site.RowVersion }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.PostAsJsonAsync($"/api/v1/locations/{site.Id}/archive", new { rowVersion = site.RowVersion }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.Factory.CreateClient().GetAsync("/api/v1/locations")).StatusCode);
    }

    [Fact]
    public async Task One_tenant_never_sees_or_touches_another_tenants_locations_through_the_api()
    {
        var managerA = await _api.ClientAsync(TenantA, SystemRoles.FacilityManager);
        var managerB = await _api.ClientAsync(TenantB, SystemRoles.FacilityManager);
        var siteA = await ReadAsync<LocationDto>(await PostAsync(managerA, "/api/v1/locations", NewSite()));

        Assert.Equal(HttpStatusCode.NotFound, (await managerB.GetAsync($"/api/v1/locations/{siteA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(managerB, $"/api/v1/locations/{siteA.Id}", new { code = "X", name = "hacked", rowVersion = siteA.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(managerB, $"/api/v1/locations/{siteA.Id}/archive", new { rowVersion = siteA.RowVersion })).StatusCode);
        var listB = await ReadAsync<Page<LocationDto>>(await managerB.GetAsync("/api/v1/locations?pageSize=100"));
        Assert.DoesNotContain(siteA.Id, listB.Items.Select(l => l.Id));
        var childUnderA = await PostAsync(managerB, "/api/v1/locations", new { type = "Building", parentId = siteA.Id, code = FacilitiesScope.Code("B"), name = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, childUnderA.StatusCode);
    }

    [Fact]
    public async Task Custom_fields_are_defined_by_managers_and_readable_by_location_readers()
    {
        var manager = await _api.ClientAsync(TenantA, SystemRoles.FacilityManager);
        var technician = await _api.ClientAsync(TenantA, SystemRoles.Technician);
        var nobody = await _api.ClientAsync(TenantA, null);
        var key = "cf" + Guid.NewGuid().ToString("N")[..8];

        var created = await PostAsync(manager, "/api/v1/custom-fields", new { target = "Location", key, labelEn = "Fire zone", labelAr = "منطقة الحريق", dataType = "Text", isRequired = false });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var visible = await technician.GetFromJsonAsync<CustomFieldDto[]>("/api/v1/custom-fields?target=Location", Json);
        Assert.Contains(visible!, d => d.Key == key && d.LabelAr == "منطقة الحريق");
        Assert.Equal(HttpStatusCode.Forbidden, (await technician.PostAsJsonAsync("/api/v1/custom-fields", new { target = "Location", key = "zzz", labelEn = "x", dataType = "Text" }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.GetAsync("/api/v1/custom-fields")).StatusCode);

        var site = await PostAsync(manager, "/api/v1/locations", new { type = "Site", code = FacilitiesScope.Code("S"), name = "S", customFields = new Dictionary<string, object> { [key] = "Zone 4" } });
        Assert.Equal("Zone 4", (await ReadAsync<LocationDto>(site)).CustomFields[key].GetString());
    }

    [Fact]
    public async Task Location_changes_are_audited_and_visible_only_to_their_tenant()
    {
        var manager = await _api.ClientAsync(TenantA, SystemRoles.FacilityManager);
        var site = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", NewSite()));

        await using var a = sql.App(TenantA);
        var events = await a.AuditEvents.Where(e => e.EntityId == site.Id.ToString()).ToListAsync();
        var created = Assert.Single(events);
        Assert.Equal("location.created", created.Action);
        Assert.NotNull(created.ActorId);

        await using var b = sql.App(TenantB);
        Assert.False(await b.AuditEvents.IgnoreQueryFilters().AnyAsync(e => e.EntityId == site.Id.ToString()));
    }
}
