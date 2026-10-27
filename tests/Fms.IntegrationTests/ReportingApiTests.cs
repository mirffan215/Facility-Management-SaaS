using System.Net;
using System.Net.Http.Json;
using Fms.BuildingBlocks.Reporting;
using Fms.Facilities.Services;
using Fms.Platform.Security;
using Fms.Platform.Services;
using Fms.Reporting.Services;
using Fms.Work.Services;
using static Fms.IntegrationTests.ApiHarness;

namespace Fms.IntegrationTests;

[Collection(SqlCollection.Name)]
public sealed class ReportingApiTests(SqlServerFixture sql) : IDisposable
{
    private readonly ApiHarness _api = new(sql);
    public void Dispose() => _api.Dispose();

    private sealed record Rig(Guid Tenant, HttpClient Manager, HttpClient Technician, HttpClient Viewer, HttpClient Requester, LocationDto SiteA, LocationDto BuildingA, LocationDto SiteB, Guid TechnicianId);

    private async Task<Rig> NewRigAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var platform = sql.App(null, platform: true))
        {
            platform.Tenants.Add(new Fms.Platform.Domain.Tenant { Id = tenant, Name = "Rep " + tenant.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
            await platform.SaveChangesAsync();
            await SystemRoleSync.EnsureAsync(platform, tenant);
        }

        var manager = await _api.ClientAsync(tenant, SystemRoles.FacilityManager);
        var technician = await _api.ClientAsync(tenant, SystemRoles.Technician);
        var siteA = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", new { type = "Site", code = FacilitiesScope.Code("A"), name = "Site A" }));
        var buildingA = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", new { type = "Building", parentId = siteA.Id, code = FacilitiesScope.Code("BA"), name = "Tower A", areaSquareMetres = 500 }));
        var siteB = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", new { type = "Site", code = FacilitiesScope.Code("B"), name = "Site B" }));
        var me = (await technician.GetFromJsonAsync<Fms.Api.MeInfo>("/api/v1/me"))!.UserId!.Value;
        return new Rig(tenant, manager, technician, await _api.ClientAsync(tenant, SystemRoles.Viewer), await _api.ClientAsync(tenant, SystemRoles.Requester), siteA, buildingA, siteB, me);
    }

    private static async Task<WorkOrderDto> OrderAsync(Rig rig, LocationDto where, bool complete)
    {
        var wo = await ReadAsync<WorkOrderDto>(await PostAsync(rig.Manager, "/api/v1/work-orders", new { title = "Report me", type = "Emergency", locationId = where.Id }));
        if (!complete) return wo;
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Manager, $"/api/v1/work-orders/{wo.Id}/assign", new { assigneeId = rig.TechnicianId })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Technician, $"/api/v1/work-orders/{wo.Id}/start", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Technician, $"/api/v1/work-orders/{wo.Id}/complete", new { code = "Fixed", notes = "done" })).StatusCode);
        return wo;
    }

    private static async Task<DashboardDto> DashboardAsync(HttpClient c, string query = "")
    {
        var response = await c.GetAsync("/api/v1/reports/dashboard" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<DashboardDto>(response);
    }

    private static decimal? Kpi(DashboardDto d, string key) => d.Groups.SelectMany(g => g.Kpis).Single(k => k.Key == key).Value;

    [Fact]
    public async Task Each_person_sees_only_the_groups_their_rights_allow()
    {
        var rig = await NewRigAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Requester.GetAsync("/api/v1/reports/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Technician.GetAsync("/api/v1/reports/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Requester.GetAsync("/api/v1/reports/export.csv")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GuestClient().GetAsync("/api/v1/reports/dashboard")).StatusCode);

        var manager = await DashboardAsync(rig.Manager);
        Assert.Equal(["work", "facilities", "inventory", "purchasing", "vendors", "spaces", "bookings", "visitors"], manager.Groups.Select(g => g.Group).ToArray());
        Assert.All(manager.Groups, g => Assert.False(g.Failed));

        var viewer = await DashboardAsync(rig.Viewer);
        Assert.Equal(["work", "facilities", "inventory", "vendors", "spaces", "bookings"], viewer.Groups.Select(g => g.Group).ToArray()); // no purchasing, no visitors
    }

    [Fact]
    public async Task Work_figures_count_what_happened_and_follow_the_site_filter()
    {
        var rig = await NewRigAsync();
        await OrderAsync(rig, rig.SiteA, complete: true);
        await OrderAsync(rig, rig.SiteA, complete: true);
        await OrderAsync(rig, rig.SiteA, complete: true);
        await OrderAsync(rig, rig.SiteA, complete: false);
        await OrderAsync(rig, rig.SiteB, complete: false);

        var all = await DashboardAsync(rig.Manager);
        Assert.Equal((5m, 3m, 2m), (Kpi(all, "work.created"), Kpi(all, "work.completed"), Kpi(all, "work.open")));
        Assert.NotNull(Kpi(all, "work.mttr")); // some hours, however small
        Assert.Null(Kpi(all, "work.slaResponse")); // no service levels are set: nothing to measure, shown as a dash and not as 0%
        Assert.Null(Kpi(all, "work.pmCompliance"));
        var statuses = all.Groups.Single(g => g.Group == "work").Series.Single(s => s.Key == "work.openByStatus").Points;
        Assert.Equal(2m, statuses.Sum(p => p.Value));
        Assert.Equal(5m, all.Groups.Single(g => g.Group == "work").Series.Single(s => s.Key == "work.createdByWeek").Points.Sum(p => p.Value));

        var a = await DashboardAsync(rig.Manager, $"?siteId={rig.SiteA.Id}");
        Assert.Equal(("Site A", 4m, 1m), (a.SiteName, Kpi(a, "work.created"), Kpi(a, "work.open")));
        var b = await DashboardAsync(rig.Manager, $"?siteId={rig.SiteB.Id}");
        Assert.Equal((1m, 0m), (Kpi(b, "work.created"), Kpi(b, "work.completed")));

        // A period in the past has none of it.
        var old = await DashboardAsync(rig.Manager, "?from=2020-01-01&to=2020-01-31");
        Assert.Equal((0m, 0m), (Kpi(old, "work.created"), Kpi(old, "work.completed")));
        Assert.Equal(2m, Kpi(old, "work.open")); // open now is not a matter of period
    }

    [Fact]
    public async Task Cost_and_cost_per_square_metre_use_the_estates_area()
    {
        var rig = await NewRigAsync();
        var wo = await OrderAsync(rig, rig.SiteA, complete: false);
        await PostAsync(rig.Manager, $"/api/v1/work-orders/{wo.Id}/assign", new { assigneeId = rig.TechnicianId });
        await PostAsync(rig.Technician, $"/api/v1/work-orders/{wo.Id}/start", new { }); // costs are recorded while the work is under way
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Technician, $"/api/v1/work-orders/{wo.Id}/costs", new { kind = "Material", description = "Seal", quantity = 4, unitCost = 25 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Technician, $"/api/v1/work-orders/{wo.Id}/costs", new { kind = "Contractor", description = "Crane", quantity = 1, unitCost = 150 })).StatusCode);

        var a = await DashboardAsync(rig.Manager, $"?siteId={rig.SiteA.Id}");
        Assert.Equal(250m, Kpi(a, "work.cost"));
        Assert.Equal(500m, Kpi(a, "facilities.area"));
        Assert.Equal(0.5m, Kpi(a, "work.costPerArea"));
        var byKind = a.Groups.Single(g => g.Group == "work").Series.Single(s => s.Key == "work.costByKind").Points.ToDictionary(p => p.Label, p => p.Value);
        Assert.Equal((100m, 150m), (byKind["Material"], byKind["Contractor"]));

        var b = await DashboardAsync(rig.Manager, $"?siteId={rig.SiteB.Id}");
        Assert.Equal(0m, Kpi(b, "work.cost"));
        Assert.Null(Kpi(b, "work.costPerArea")); // no area known for this site: no figure rather than a division by zero
    }

    [Fact]
    public async Task The_period_and_the_site_are_validated()
    {
        var rig = await NewRigAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await rig.Manager.GetAsync("/api/v1/reports/dashboard?from=2027-03-10&to=2027-03-01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await rig.Manager.GetAsync("/api/v1/reports/dashboard?from=2020-01-01&to=2027-03-01")).StatusCode); // too long
        Assert.Equal(HttpStatusCode.BadRequest, (await rig.Manager.GetAsync($"/api/v1/reports/dashboard?siteId={Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await rig.Manager.GetAsync($"/api/v1/reports/dashboard?siteId={rig.BuildingA.Id}")).StatusCode); // a building is not a site
        Assert.Equal(HttpStatusCode.BadRequest, (await rig.Manager.GetAsync("/api/v1/reports/dashboard?from=not-a-date")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rig.Manager.GetAsync("/api/v1/reports/dashboard?from=2027-03-01&to=2027-03-01")).StatusCode); // one day is fine
    }

    [Fact]
    public async Task The_pack_exports_as_a_spreadsheet_with_the_same_numbers()
    {
        var rig = await NewRigAsync();
        await OrderAsync(rig, rig.SiteA, complete: true);
        var response = await rig.Manager.GetAsync("/api/v1/reports/export.csv");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("kpi-", response.Content.Headers.ContentDisposition!.FileName);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var csv = await response.Content.ReadAsStringAsync();
        Assert.Contains("work,kpi,work.created,,1,count,", csv);
        Assert.Contains("work,kpi,work.completed,,1,count,", csv);
        Assert.Contains("work,series,work.createdByWeek,", csv);
        Assert.Equal(HttpStatusCode.BadRequest, (await rig.Manager.GetAsync("/api/v1/reports/export.csv?from=2027-03-10&to=2027-03-01")).StatusCode);
        var viewerCsv = await (await rig.Viewer.GetAsync("/api/v1/reports/export.csv")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("purchasing,", viewerCsv); // the export holds only what the person may see
    }

    [Fact]
    public async Task Another_organisation_sees_only_its_own_figures()
    {
        var rig = await NewRigAsync();
        var other = await NewRigAsync();
        await OrderAsync(rig, rig.SiteA, complete: true);
        Assert.Equal(1m, Kpi(await DashboardAsync(rig.Manager), "work.created"));
        Assert.Equal(0m, Kpi(await DashboardAsync(other.Manager), "work.created"));
        Assert.Equal(HttpStatusCode.BadRequest, (await other.Manager.GetAsync($"/api/v1/reports/dashboard?siteId={rig.SiteA.Id}")).StatusCode);
    }

    [Fact]
    public async Task Space_booking_and_visitor_figures_are_part_of_the_pack()
    {
        var rig = await NewRigAsync();
        var floor = await ReadAsync<LocationDto>(await PostAsync(rig.Manager, "/api/v1/locations", new { type = "Floor", parentId = rig.BuildingA.Id, code = FacilitiesScope.Code("F"), name = "F1" }));
        var room = await ReadAsync<LocationDto>(await PostAsync(rig.Manager, "/api/v1/locations", new { type = "Space", parentId = floor.Id, code = FacilitiesScope.Code("R"), name = "Open plan" }));
        await PutAsync(rig.Manager, $"/api/v1/spaces/{room.Id}", new { kind = "Office", capacity = 10 });
        await PostAsync(rig.Manager, "/api/v1/allocations", new { spaceId = room.Id, kind = "Team", teamName = "Sales", headcount = 4, startOn = DateOnly.FromDateTime(DateTime.UtcNow) });

        var a = await DashboardAsync(rig.Manager, $"?siteId={rig.SiteA.Id}");
        Assert.Equal((10m, 4m, 40.0m), (Kpi(a, "spaces.capacity"), Kpi(a, "spaces.allocated"), Kpi(a, "spaces.utilisation")));
        var b = await DashboardAsync(rig.Manager, $"?siteId={rig.SiteB.Id}");
        Assert.Equal(0m, Kpi(b, "spaces.capacity"));
        Assert.Null(Kpi(b, "spaces.utilisation"));
        Assert.Equal(0m, Kpi(a, "bookings.count"));
        Assert.Null(Kpi(a, "bookings.noShowRate"));
        Assert.Equal(0m, Kpi(a, "visitors.onSite"));
    }
}
