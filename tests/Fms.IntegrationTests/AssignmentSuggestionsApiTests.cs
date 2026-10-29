using System.Net;
using System.Net.Http.Json;
using Fms.Api;
using Fms.BuildingBlocks.Results;
using Fms.Facilities.Services;
using Fms.Platform.Security;
using Fms.Platform.Services;
using Fms.Vendors.Services;
using Fms.Work.Domain;
using Fms.Work.Services;
using Microsoft.EntityFrameworkCore;
using static Fms.IntegrationTests.ApiHarness;

namespace Fms.IntegrationTests;

[Collection(SqlCollection.Name)]
public sealed class AssignmentSuggestionsApiTests : IDisposable
{
    private readonly SqlServerFixture _sql;
    private readonly ApiHarness _api;

    public AssignmentSuggestionsApiTests(SqlServerFixture sql)
    {
        _sql = sql;
        _api = new ApiHarness(sql);
    }

    public void Dispose() => _api.Dispose();

    private sealed record Rig(Guid Tenant, HttpClient Manager, HttpClient Viewer, HttpClient Technician, Guid TechnicianId, LocationDto Site, AssetDto Chiller, Guid CategoryId);

    private async Task<(HttpClient Client, Guid Id)> PersonAsync(Guid tenant, string role)
    {
        var client = await _api.ClientAsync(tenant, role);
        return (client, (await client.GetFromJsonAsync<MeInfo>("/api/v1/me"))!.UserId!.Value);
    }

    private async Task<Rig> NewRigAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var platform = _sql.App(null, platform: true))
        {
            platform.Tenants.Add(new Fms.Platform.Domain.Tenant { Id = tenant, Name = "Sug " + tenant.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
            await platform.SaveChangesAsync();
            await SystemRoleSync.EnsureAsync(platform, tenant);
        }

        var manager = await _api.ClientAsync(tenant, SystemRoles.FacilityManager);
        var site = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", new { type = "Site", code = FacilitiesScope.Code("S"), name = "HQ" }));
        var cls = await ReadAsync<AssetClassDto>(await PostAsync(manager, "/api/v1/asset-classes", new { code = FacilitiesScope.Code("C"), name = "HVAC" }));
        var chiller = await ReadAsync<AssetDto>(await PostAsync(manager, "/api/v1/assets", new { code = FacilitiesScope.Code("A"), name = "Chiller 1", assetClassId = cls.Id, locationId = site.Id, startActive = true }));
        var category = await ReadAsync<CategoryDto>(await PostAsync(manager, "/api/v1/work-orders/categories", new { code = "HV" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant(), name = "Cooling" }));
        var (tech, techId) = await PersonAsync(tenant, SystemRoles.Technician);
        return new Rig(tenant, manager, await _api.ClientAsync(tenant, SystemRoles.Viewer), tech, techId, site, chiller, category.Id);
    }

    private static async Task<WorkOrderDto> NewOrderAsync(Rig rig, string title, bool withAsset = true, bool withCategory = true) =>
        await ReadAsync<WorkOrderDto>(await PostAsync(rig.Manager, "/api/v1/work-orders", new { title, type = "Emergency", locationId = rig.Site.Id, assetId = withAsset ? rig.Chiller.Id : (Guid?)null, categoryId = withCategory ? rig.CategoryId : (Guid?)null }));

    /// <summary>Makes it look as if the person fixed this order some weeks ago.</summary>
    private async Task GiveHistoryAsync(Rig rig, Guid orderId, Guid personId, WorkOrderStatus status = WorkOrderStatus.Closed, int daysAgo = 20)
    {
        await using var db = _sql.Work(rig.Tenant);
        var row = await db.WorkOrders.SingleAsync(w => w.Id == orderId);
        row.AssignedTo = personId;
        row.Status = status;
        row.RequestedAt = DateTimeOffset.UtcNow.AddDays(-daysAgo);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Someone_who_fixed_this_asset_before_is_suggested_first_with_the_numbers_behind_it()
    {
        var rig = await NewRigAsync();
        var (_, fixerId) = await PersonAsync(rig.Tenant, SystemRoles.Technician);
        var (_, busyId) = await PersonAsync(rig.Tenant, SystemRoles.Technician);
        for (var i = 0; i < 2; i++) await GiveHistoryAsync(rig, (await NewOrderAsync(rig, "Old chiller job " + i)).Id, fixerId);
        // The busy person has four open jobs and no history.
        for (var i = 0; i < 4; i++) await GiveHistoryAsync(rig, (await NewOrderAsync(rig, "Open job " + i, withAsset: false, withCategory: false)).Id, busyId, WorkOrderStatus.InProgress, 1);

        var target = await NewOrderAsync(rig, "Chiller not cooling");
        var s = await ReadAsync<AssignmentSuggestionsDto>(await rig.Manager.GetAsync($"/api/v1/work-orders/{target.Id}/suggestions"));
        Assert.Equal(target.Id, s.WorkOrderId);
        var first = s.Assignees[0];
        Assert.Equal(fixerId, first.UserId);
        Assert.Equal((2, 2, 2, 0), (first.SameAsset, first.SameCategory, first.SamePlace, first.OpenJobs));
        Assert.Equal(5 * 2 + 3 * 2 + 1 * 2, first.Score);
        Assert.True(s.Assignees.Count <= 3);
        var busy = s.Assignees.SingleOrDefault(a => a.UserId == busyId);
        Assert.True(busy is null || busy.OpenJobs == 4 && busy.Score < 0);
    }

    [Fact]
    public async Task Cancelled_duplicate_and_not_yet_finished_work_does_not_count_as_experience()
    {
        var rig = await NewRigAsync();
        var (_, personId) = await PersonAsync(rig.Tenant, SystemRoles.Technician);
        await GiveHistoryAsync(rig, (await NewOrderAsync(rig, "Cancelled")).Id, personId, WorkOrderStatus.Cancelled);
        await GiveHistoryAsync(rig, (await NewOrderAsync(rig, "Still going")).Id, personId, WorkOrderStatus.InProgress, 2);
        var dup = await NewOrderAsync(rig, "Duplicate");
        await GiveHistoryAsync(rig, dup.Id, personId);
        await using (var db = _sql.Work(rig.Tenant)) { (await db.WorkOrders.SingleAsync(w => w.Id == dup.Id)).ClosureCode = ClosureCode.Duplicate; await db.SaveChangesAsync(); }
        await GiveHistoryAsync(rig, (await NewOrderAsync(rig, "Long ago")).Id, personId, WorkOrderStatus.Closed, 400);

        var target = await NewOrderAsync(rig, "New job");
        var s = await ReadAsync<AssignmentSuggestionsDto>(await rig.Manager.GetAsync($"/api/v1/work-orders/{target.Id}/suggestions"));
        var me = s.Assignees.Single(a => a.UserId == personId);
        Assert.Equal((0, 0, 0, 1), (me.SameAsset, me.SameCategory, me.SamePlace, me.OpenJobs));
    }

    [Fact]
    public async Task Vendors_under_a_contract_in_force_that_covers_the_job_are_suggested_and_others_are_not()
    {
        var rig = await NewRigAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        async Task<(VendorDto Vendor, ContractDto Contract)> Make(string name, int start, int end)
        {
            var vendor = await ReadAsync<VendorDto>(await PostAsync(rig.Manager, "/api/v1/vendors", new { code = FacilitiesScope.Code("V"), name }));
            var contract = await ReadAsync<ContractDto>(await PostAsync(rig.Manager, "/api/v1/contracts", new { vendorId = vendor.Id, number = FacilitiesScope.Code("CT"), title = name + " AMC", type = "Amc", startOn = today.AddDays(start), endOn = today.AddDays(end) }));
            return (vendor, contract);
        }

        var general = await Make("General Cooling Co", -30, 300);
        var elsewhere = await Make("Other Class Co", -30, 300);
        var ended = await Make("Ended Co", -400, -5);
        var otherClass = await ReadAsync<AssetClassDto>(await PostAsync(rig.Manager, "/api/v1/asset-classes", new { code = FacilitiesScope.Code("C"), name = "Lifts" }));
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(rig.Manager, $"/api/v1/contracts/{elsewhere.Contract.Id}/coverage", new { coverage = new[] { new { kind = "AssetClass", targetId = otherClass.Id } } })).StatusCode);

        var target = await NewOrderAsync(rig, "Chiller not cooling");
        var s = await ReadAsync<AssignmentSuggestionsDto>(await rig.Manager.GetAsync($"/api/v1/work-orders/{target.Id}/suggestions"));
        var offered = Assert.Single(s.Vendors);
        Assert.Equal(general.Vendor.Id, offered.VendorId);
        Assert.Equal(general.Contract.Id, offered.ContractId);
        Assert.DoesNotContain(s.Vendors, v => v.VendorId == elsewhere.Vendor.Id || v.VendorId == ended.Vendor.Id);
    }

    [Fact]
    public async Task Only_dispatchers_see_suggestions_only_for_open_orders_and_never_across_organisations()
    {
        var a = await NewRigAsync();
        var b = await NewRigAsync();
        var order = await NewOrderAsync(a, "Job");
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Technician.GetAsync($"/api/v1/work-orders/{order.Id}/suggestions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Viewer.GetAsync($"/api/v1/work-orders/{order.Id}/suggestions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GuestClient().GetAsync($"/api/v1/work-orders/{order.Id}/suggestions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Manager.GetAsync($"/api/v1/work-orders/{order.Id}/suggestions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Manager.GetAsync($"/api/v1/work-orders/{Guid.NewGuid()}/suggestions")).StatusCode);

        await GiveHistoryAsync(a, order.Id, a.TechnicianId, WorkOrderStatus.Closed);
        Assert.Equal(HttpStatusCode.Conflict, (await a.Manager.GetAsync($"/api/v1/work-orders/{order.Id}/suggestions")).StatusCode);

        // People of another organisation never appear.
        var fresh = await NewOrderAsync(a, "Fresh");
        var s = await ReadAsync<AssignmentSuggestionsDto>(await a.Manager.GetAsync($"/api/v1/work-orders/{fresh.Id}/suggestions"));
        Assert.DoesNotContain(s.Assignees, x => x.UserId == b.TechnicianId);
    }
}
