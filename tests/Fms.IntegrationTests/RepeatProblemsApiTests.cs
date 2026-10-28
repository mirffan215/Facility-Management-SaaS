using System.Net;
using Fms.Api;
using Fms.Facilities.Services;
using Fms.Platform.Security;
using Fms.Platform.Services;
using Fms.Work.Services;
using static Fms.IntegrationTests.ApiHarness;

namespace Fms.IntegrationTests;

[Collection(SqlCollection.Name)]
public sealed class RepeatProblemsApiTests : IDisposable
{
    private readonly SqlServerFixture _sql;
    private readonly ApiHarness _api;

    public RepeatProblemsApiTests(SqlServerFixture sql)
    {
        _sql = sql;
        _api = new ApiHarness(sql);
    }

    public void Dispose() => _api.Dispose();

    private sealed record Rig(Guid Tenant, HttpClient Manager, HttpClient Technician, HttpClient Viewer, LocationDto Site, AssetClassDto Class);

    private async Task<Rig> NewRigAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var platform = _sql.App(null, platform: true))
        {
            platform.Tenants.Add(new Fms.Platform.Domain.Tenant { Id = tenant, Name = "Rep " + tenant.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
            await platform.SaveChangesAsync();
            await SystemRoleSync.EnsureAsync(platform, tenant);
        }

        var manager = await _api.ClientAsync(tenant, SystemRoles.FacilityManager);
        var site = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", new { type = "Site", code = FacilitiesScope.Code("S"), name = "HQ" }));
        var cls = await ReadAsync<AssetClassDto>(await PostAsync(manager, "/api/v1/asset-classes", new { code = FacilitiesScope.Code("C"), name = "HVAC" }));
        return new Rig(tenant, manager, await _api.ClientAsync(tenant, SystemRoles.Technician), await _api.ClientAsync(tenant, SystemRoles.Viewer), site, cls);
    }

    private static async Task<AssetDto> NewAssetAsync(Rig rig, string name) =>
        await ReadAsync<AssetDto>(await PostAsync(rig.Manager, "/api/v1/assets", new { code = FacilitiesScope.Code("A"), name, assetClassId = rig.Class.Id, locationId = rig.Site.Id, startActive = true }));

    private static async Task RequestAsync(Rig rig, AssetDto asset, string title, string type = "Corrective") =>
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(rig.Manager, "/api/v1/work-orders", new { title, type, locationId = rig.Site.Id, assetId = asset.Id })).StatusCode);

    [Fact]
    public async Task An_asset_with_three_repair_requests_is_listed_with_the_reason_and_one_with_two_is_not()
    {
        var rig = await NewRigAsync();
        var bad = await NewAssetAsync(rig, "Chiller 1");
        var fine = await NewAssetAsync(rig, "Chiller 2");
        await RequestAsync(rig, bad, "Not cooling");
        await RequestAsync(rig, bad, "Noise from compressor", "Emergency");
        await RequestAsync(rig, bad, "Tripped again");
        await RequestAsync(rig, fine, "Filter");
        await RequestAsync(rig, fine, "Filter again");

        var list = await ReadAsync<List<RepeatProblemDto>>(await rig.Manager.GetAsync("/api/v1/work-orders/repeat-problems"));
        var item = Assert.Single(list);
        Assert.Equal(bad.Id, item.AssetId);
        Assert.Equal("Chiller 1", item.AssetName);
        Assert.Equal((3, 1), (item.Count, item.Emergencies));
        Assert.Equal("Tripped again", item.LastTitle);
        Assert.NotNull(item.AverageDaysBetween);

        var lower = await ReadAsync<List<RepeatProblemDto>>(await rig.Manager.GetAsync("/api/v1/work-orders/repeat-problems?minimum=2"));
        Assert.Equal(2, lower.Count);
        Assert.Empty(await ReadAsync<List<RepeatProblemDto>>(await rig.Manager.GetAsync("/api/v1/work-orders/repeat-problems?minimum=4")));
    }

    [Fact]
    public async Task Only_people_who_manage_or_dispatch_work_see_it_and_other_organisations_see_nothing()
    {
        var a = await NewRigAsync();
        var b = await NewRigAsync();
        var asset = await NewAssetAsync(a, "Pump");
        for (var i = 0; i < 3; i++) await RequestAsync(a, asset, "Leak " + i);

        Assert.Equal(HttpStatusCode.Forbidden, (await a.Technician.GetAsync("/api/v1/work-orders/repeat-problems")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Viewer.GetAsync("/api/v1/work-orders/repeat-problems")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GuestClient().GetAsync("/api/v1/work-orders/repeat-problems")).StatusCode);
        Assert.Single(await ReadAsync<List<RepeatProblemDto>>(await a.Manager.GetAsync("/api/v1/work-orders/repeat-problems")));
        Assert.Empty(await ReadAsync<List<RepeatProblemDto>>(await b.Manager.GetAsync("/api/v1/work-orders/repeat-problems")));
    }
}
