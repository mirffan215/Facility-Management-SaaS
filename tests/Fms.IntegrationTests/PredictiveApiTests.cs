using System.Net;
using System.Net.Http.Json;
using Fms.Api;
using Fms.Facilities.Services;
using Fms.Platform.Security;
using Fms.Platform.Services;
using Fms.Work.Services;
using Microsoft.EntityFrameworkCore;
using static Fms.IntegrationTests.ApiHarness;

namespace Fms.IntegrationTests;

[Collection(SqlCollection.Name)]
public sealed class PredictiveApiTests : IDisposable
{
    private readonly SqlServerFixture _sql;
    private readonly ApiHarness _api;

    public PredictiveApiTests(SqlServerFixture sql)
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
            platform.Tenants.Add(new Fms.Platform.Domain.Tenant { Id = tenant, Name = "Pred " + tenant.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
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

    /// <summary>Raises a repair request and back-dates it.</summary>
    private async Task RepairAsync(Rig rig, AssetDto asset, int daysAgo)
    {
        var order = await ReadAsync<WorkOrderDto>(await PostAsync(rig.Manager, "/api/v1/work-orders", new { title = "Repair " + daysAgo, type = "Corrective", locationId = rig.Site.Id, assetId = asset.Id }));
        await using var db = _sql.Work(rig.Tenant);
        var row = await db.WorkOrders.SingleAsync(w => w.Id == order.Id);
        row.RequestedAt = DateTimeOffset.UtcNow.AddDays(-daysAgo);
        await db.SaveChangesAsync();
    }

    private static async Task ReadingAsync(Rig rig, MeterDto meter, decimal value, double daysAgo) =>
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(rig.Technician, $"/api/v1/meters/{meter.Id}/readings", new { value, readAt = DateTimeOffset.UtcNow.AddDays(-daysAgo) })).StatusCode);

    private static async Task<List<PredictiveHintDto>> HintsAsync(HttpClient c, string query = "") =>
        await ReadAsync<List<PredictiveHintDto>>(await c.GetAsync("/api/v1/work-orders/predictions" + query));

    [Fact]
    public async Task Repairs_coming_closer_together_are_a_hint_with_the_gaps_and_steady_assets_are_not()
    {
        var rig = await NewRigAsync();
        var worsening = await NewAssetAsync(rig, "Chiller 1");
        var steady = await NewAssetAsync(rig, "Chiller 2");
        foreach (var d in new[] { 150, 90, 30, 10, 0 }) await RepairAsync(rig, worsening, d); // gaps 60, 60, 20, 10
        foreach (var d in new[] { 120, 90, 60, 30, 0 }) await RepairAsync(rig, steady, d);

        var hint = Assert.Single(await HintsAsync(rig.Manager));
        Assert.Equal(HintKind.FailuresSpeedingUp, hint.Kind);
        Assert.Equal(worsening.Id, hint.AssetId);
        Assert.Equal("Chiller 1", hint.AssetName);
        Assert.Equal(5, hint.Events);
        Assert.Equal(60, hint.EarlierGapDays);
        Assert.Equal(15, hint.RecentGapDays);
        Assert.NotNull(hint.ExpectedBy);

        Assert.Single(await HintsAsync(rig.Manager, $"?assetId={worsening.Id}"));
        Assert.Empty(await HintsAsync(rig.Manager, $"?assetId={steady.Id}"));
    }

    [Fact]
    public async Task Cancelled_duplicate_and_planned_work_is_not_counted_as_a_repair()
    {
        var rig = await NewRigAsync();
        var asset = await NewAssetAsync(rig, "Pump");
        foreach (var d in new[] { 150, 90, 30, 10, 0 }) await RepairAsync(rig, asset, d);
        Assert.Single(await HintsAsync(rig.Manager));

        await using (var db = _sql.Work(rig.Tenant))
        {
            var rows = await db.WorkOrders.Where(w => w.AssetId == asset.Id).OrderBy(w => w.RequestedAt).ToListAsync();
            rows[3].Status = Fms.Work.Domain.WorkOrderStatus.Cancelled; // 4 left: gaps 60, 60, 30 -> recent (60+30)/2 = 45 of 60: no hint
            await db.SaveChangesAsync();
        }

        Assert.Empty(await HintsAsync(rig.Manager));
    }

    [Fact]
    public async Task A_service_meter_that_runs_faster_than_before_shows_its_pace_and_the_days_left_to_the_next_service()
    {
        var rig = await NewRigAsync();
        var asset = await NewAssetAsync(rig, "Generator");
        var meter = await ReadAsync<MeterDto>(await PostAsync(rig.Manager, $"/api/v1/assets/{asset.Id}/meters", new { name = "Run hours", unit = "h", kind = "Cumulative" }));
        await ReadingAsync(rig, meter, 0, 43);
        await ReadingAsync(rig, meter, 285, 14.5); // 10 a day before
        await ReadingAsync(rig, meter, 1000, 10);
        await ReadingAsync(rig, meter, 1200, 0); // 20 a day lately
        var template = await ReadAsync<PmTemplateDto>(await PostAsync(rig.Manager, "/api/v1/pm/templates", new { name = "Oil change", priority = "Medium" }));
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(rig.Manager, "/api/v1/pm/schedules", new
        {
            name = "Every 500 h", templateId = template.Id, targetKind = "Asset", assetId = asset.Id, timeBasis = "None", intervalUnit = "Day", interval = 1,
            startOn = DateOnly.FromDateTime(DateTime.UtcNow), leadDays = 0, meterName = "Run hours", meterInterval = 500,
        })).StatusCode);

        var hint = Assert.Single(await HintsAsync(rig.Manager));
        Assert.Equal(HintKind.MeterRunningFaster, hint.Kind);
        Assert.Equal("Run hours", hint.MeterName);
        Assert.Equal("h", hint.Unit);
        Assert.Equal(20, hint.RecentPerDay);
        Assert.Equal(10, hint.PriorPerDay);
        Assert.Equal(2, hint.Ratio);
        Assert.Equal(25, hint.DaysToService); // 500 hours to the next service at 20 a day
    }

    [Fact]
    public async Task Only_people_who_manage_or_dispatch_work_see_hints_and_other_organisations_see_none()
    {
        var a = await NewRigAsync();
        var b = await NewRigAsync();
        var asset = await NewAssetAsync(a, "Lift");
        foreach (var d in new[] { 150, 90, 30, 10, 0 }) await RepairAsync(a, asset, d);

        Assert.Equal(HttpStatusCode.Forbidden, (await a.Technician.GetAsync("/api/v1/work-orders/predictions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Viewer.GetAsync("/api/v1/work-orders/predictions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GuestClient().GetAsync("/api/v1/work-orders/predictions")).StatusCode);
        Assert.Single(await HintsAsync(a.Manager));
        Assert.Empty(await HintsAsync(b.Manager));
        Assert.Empty(await HintsAsync(b.Manager, $"?assetId={asset.Id}"));
    }

    [Fact]
    public async Task The_thresholds_start_at_the_defaults_are_checked_saved_and_change_what_is_flagged()
    {
        var rig = await NewRigAsync();
        var asset = await NewAssetAsync(rig, "Chiller 1");
        foreach (var d in new[] { 150, 90, 30, 10, 0 }) await RepairAsync(rig, asset, d);
        Assert.Single(await HintsAsync(rig.Manager));

        var start = await ReadAsync<HintSettingsDto>(await rig.Manager.GetAsync("/api/v1/work-orders/hint-settings"));
        Assert.Equal((180, 4, 50, 150), (start.HistoryDays, start.MinFailures, start.ShrinkPercent, start.PaceRisePercent));

        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(rig.Manager, "/api/v1/work-orders/hint-settings", new { historyDays = 10, minFailures = 4, shrinkPercent = 50, paceRisePercent = 150, rowVersion = start.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(rig.Manager, "/api/v1/work-orders/hint-settings", new { historyDays = 180, minFailures = 2, shrinkPercent = 50, paceRisePercent = 150, rowVersion = start.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(rig.Manager, "/api/v1/work-orders/hint-settings", new { historyDays = 180, minFailures = 4, shrinkPercent = 50, rowVersion = start.RowVersion })).StatusCode);

        // Asking for six repairs before speaking silences the asset that has five.
        var saved = await ReadAsync<HintSettingsDto>(await PutAsync(rig.Manager, "/api/v1/work-orders/hint-settings", new { historyDays = 365, minFailures = 6, shrinkPercent = 50, paceRisePercent = 150, rowVersion = start.RowVersion }));
        Assert.Equal((365, 6), (saved.HistoryDays, saved.MinFailures));
        Assert.NotNull(saved.RowVersion);
        Assert.Empty(await HintsAsync(rig.Manager));

        var work = await ReadAsync<WorkSettingsDto>(await rig.Manager.GetAsync("/api/v1/work-orders/settings"));
        Assert.True(work.RequireApproval); // the lifecycle switches of the same row are untouched
        Assert.True(work.RequireVerification);

        var restored = await ReadAsync<HintSettingsDto>(await PutAsync(rig.Manager, "/api/v1/work-orders/hint-settings", new { historyDays = 180, minFailures = 4, shrinkPercent = 50, paceRisePercent = 150, rowVersion = saved.RowVersion }));
        Assert.Single(await HintsAsync(rig.Manager));
        Assert.Equal(4, restored.MinFailures);

        // A stale copy is refused.
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(rig.Manager, "/api/v1/work-orders/hint-settings", new { historyDays = 200, minFailures = 4, shrinkPercent = 50, paceRisePercent = 150, rowVersion = saved.RowVersion })).StatusCode);
    }

    [Fact]
    public async Task Only_managers_change_the_thresholds_dispatchers_read_them_and_each_organisation_has_its_own()
    {
        var a = await NewRigAsync();
        var b = await NewRigAsync();
        var start = await ReadAsync<HintSettingsDto>(await a.Manager.GetAsync("/api/v1/work-orders/hint-settings"));
        var body = new { historyDays = 90, minFailures = 5, shrinkPercent = 40, paceRisePercent = 200, rowVersion = start.RowVersion };

        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(a.Technician, "/api/v1/work-orders/hint-settings", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(a.Viewer, "/api/v1/work-orders/hint-settings", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Technician.GetAsync("/api/v1/work-orders/hint-settings")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(a.Manager, "/api/v1/work-orders/hint-settings", body)).StatusCode);

        Assert.Equal(90, (await ReadAsync<HintSettingsDto>(await a.Manager.GetAsync("/api/v1/work-orders/hint-settings"))).HistoryDays);
        Assert.Equal(180, (await ReadAsync<HintSettingsDto>(await b.Manager.GetAsync("/api/v1/work-orders/hint-settings"))).HistoryDays);
    }
}
