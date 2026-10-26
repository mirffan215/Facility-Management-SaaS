using System.Net;
using System.Net.Http.Json;
using Fms.Facilities.Services;
using Fms.Platform.Security;
using Fms.Work.Domain;
using Fms.Work.Services;
using Microsoft.EntityFrameworkCore;
using static Fms.IntegrationTests.ApiHarness;

namespace Fms.IntegrationTests;

[Collection(SqlCollection.Name)]
public sealed class PmApiTests(SqlServerFixture sql) : IDisposable
{
    private readonly ApiHarness _api = new(sql);
    public void Dispose() => _api.Dispose();

    private sealed record Me(Guid? UserId);

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private sealed record Rig(Guid Tenant, HttpClient Manager, HttpClient Technician, HttpClient Viewer, HttpClient Requester, Guid TechId, LocationDto Site, AssetClassDto Class, AssetDto Asset, ChecklistTemplateDto Checklist);

    private async Task<Rig> NewRigAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var platform = sql.App(null, platform: true))
        {
            platform.Tenants.Add(new Fms.Platform.Domain.Tenant { Id = tenant, Name = "Pm " + tenant.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
            await platform.SaveChangesAsync();
            await Fms.Platform.Services.SystemRoleSync.EnsureAsync(platform, tenant);
        }

        var manager = await _api.ClientAsync(tenant, SystemRoles.FacilityManager);
        var technician = await _api.ClientAsync(tenant, SystemRoles.Technician);
        var viewer = await _api.ClientAsync(tenant, SystemRoles.Viewer);
        var requester = await _api.ClientAsync(tenant, SystemRoles.Requester);
        var site = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", new { type = "Site", code = FacilitiesScope.Code("S"), name = "HQ" }));
        var cls = await ReadAsync<AssetClassDto>(await PostAsync(manager, "/api/v1/asset-classes", new { code = FacilitiesScope.Code("C"), name = "Chillers" }));
        var asset = await NewAssetAsync(manager, site, cls);
        var checklist = await ReadAsync<ChecklistTemplateDto>(await PostAsync(manager, "/api/v1/work-orders/checklists", new { name = "Quarterly service", items = new[] { new { text = "Check refrigerant", required = true }, new { text = "Clean coils", required = false } } }));
        return new Rig(tenant, manager, technician, viewer, requester, (await technician.GetFromJsonAsync<Me>("/api/v1/me", Json))!.UserId!.Value, site, cls, asset, checklist);
    }

    private static async Task<AssetDto> NewAssetAsync(HttpClient manager, LocationDto site, AssetClassDto cls) =>
        await ReadAsync<AssetDto>(await PostAsync(manager, "/api/v1/assets", new { code = FacilitiesScope.Code("CH"), name = "Chiller", assetClassId = cls.Id, locationId = site.Id, startActive = true }));

    private static async Task<PmTemplateDto> NewTemplateAsync(Rig rig, string name = "Quarterly service", Guid? checklist = null)
    {
        var response = await PostAsync(rig.Manager, "/api/v1/pm/templates", new { name, instructions = "Follow the OEM sheet", priority = "High", estimatedMinutes = 90, checklistTemplateId = checklist ?? rig.Checklist.Id });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<PmTemplateDto>(response);
    }

    private static async Task<PmScheduleDto> NewScheduleAsync(Rig rig, PmTemplateDto template, object? overrides = null, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var body = overrides ?? new { name = "Chiller service", templateId = template.Id, targetKind = "Asset", assetId = rig.Asset.Id, timeBasis = "Fixed", intervalUnit = "Month", interval = 1, startOn = Today.AddDays(3), leadDays = 7 };
        var response = await PostAsync(rig.Manager, "/api/v1/pm/schedules", body);
        Assert.Equal(expected, response.StatusCode);
        return expected == HttpStatusCode.Created ? await ReadAsync<PmScheduleDto>(response) : null!;
    }

    private static Task<HttpResponseMessage> Run(HttpClient manager) => PostAsync(manager, "/api/v1/pm/run", new { });

    private static async Task<PmRunResult> RunOk(HttpClient manager) => await ReadAsync<PmRunResult>(await Run(manager));

    private static async Task<WorkOrderDto[]> PlannedOrdersAsync(HttpClient c) =>
        (await ReadAsync<Fms.BuildingBlocks.Results.Page<WorkOrderDto>>(await c.GetAsync("/api/v1/work-orders?pageSize=100"))).Items.Where(w => w.Type == WorkOrderType.Planned).OrderBy(w => w.Number).ToArray();

    // ---------------- set-up ----------------

    [Fact]
    public async Task Templates_and_schedules_are_managed_by_managers_read_by_staff_and_validated()
    {
        var rig = await NewRigAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Technician, "/api/v1/pm/templates", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Requester.GetAsync("/api/v1/pm/templates")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/pm/templates", new { name = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/pm/templates", new { name = "x", checklistTemplateId = Guid.NewGuid() })).StatusCode);

        var template = await NewTemplateAsync(rig);
        Assert.Equal("Quarterly service", template.ChecklistName);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, "/api/v1/pm/templates", new { name = "Quarterly service" })).StatusCode);
        Assert.Contains((await rig.Viewer.GetFromJsonAsync<PmTemplateDto[]>("/api/v1/pm/templates", Json))!, t => t.Id == template.Id);

        var edited = await ReadAsync<PmTemplateDto>(await PutAsync(rig.Manager, $"/api/v1/pm/templates/{template.Id}", new { name = "Quarterly service", priority = "Low", rowVersion = template.RowVersion }));
        Assert.Equal(WorkPriority.Low, edited.Priority);
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(rig.Manager, $"/api/v1/pm/templates/{template.Id}", new { name = "Quarterly service", rowVersion = template.RowVersion })).StatusCode);

        var good = new { name = "S", templateId = template.Id, targetKind = "Asset", assetId = rig.Asset.Id, timeBasis = "Fixed", intervalUnit = "Month", interval = 1, startOn = Today.AddDays(30) };
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Technician, "/api/v1/pm/schedules", good)).StatusCode);
        foreach (var bad in new object[]
                 {
                     new { name = "", templateId = template.Id, targetKind = "Asset", assetId = rig.Asset.Id, timeBasis = "Fixed", intervalUnit = "Month", interval = 1, startOn = Today },
                     new { name = "S", templateId = Guid.NewGuid(), targetKind = "Asset", assetId = rig.Asset.Id, timeBasis = "Fixed", intervalUnit = "Month", interval = 1, startOn = Today },
                     new { name = "S", templateId = template.Id, targetKind = "Asset", assetId = Guid.NewGuid(), timeBasis = "Fixed", intervalUnit = "Month", interval = 1, startOn = Today },
                     new { name = "S", templateId = template.Id, targetKind = "AssetClass", assetClassId = Guid.NewGuid(), timeBasis = "Fixed", intervalUnit = "Month", interval = 1, startOn = Today },
                     new { name = "S", templateId = template.Id, targetKind = "Asset", assetId = rig.Asset.Id, timeBasis = "Fixed", intervalUnit = "Month", interval = 0, startOn = Today },
                     new { name = "S", templateId = template.Id, targetKind = "Asset", assetId = rig.Asset.Id, timeBasis = "Fixed", intervalUnit = "Month", interval = 1 },
                     new { name = "S", templateId = template.Id, targetKind = "Asset", assetId = rig.Asset.Id, timeBasis = "None" }, // no time rule and no meter rule
                     new { name = "S", templateId = template.Id, targetKind = "Asset", assetId = rig.Asset.Id, timeBasis = "Fixed", intervalUnit = "Month", interval = 1, startOn = Today, leadDays = 999 },
                     new { name = "S", templateId = template.Id, targetKind = "Asset", assetId = rig.Asset.Id, timeBasis = "Fixed", intervalUnit = "Month", interval = 1, startOn = Today, assignToUserId = rig.TechId == Guid.Empty ? Guid.NewGuid() : Guid.NewGuid() },
                 })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/pm/schedules", bad)).StatusCode);
        }

        var schedule = await NewScheduleAsync(rig, template, good);
        Assert.Equal(1, schedule.Assets); // the plan exists at once
        Assert.Equal(0, schedule.Open);
        var plans = (await rig.Viewer.GetFromJsonAsync<PmPlanDto[]>($"/api/v1/pm/schedules/{schedule.Id}/plans", Json))!;
        Assert.Equal(Today.AddDays(30), Assert.Single(plans).NextDueOn);

        // A template in use cannot be archived; archiving the schedule releases it.
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, $"/api/v1/pm/templates/{template.Id}/archive", new { rowVersion = edited.RowVersion })).StatusCode);
        var archived = await ReadAsync<PmScheduleDto>(await PostAsync(rig.Manager, $"/api/v1/pm/schedules/{schedule.Id}/archive", new { rowVersion = schedule.RowVersion }));
        Assert.True(archived.IsArchived);
        Assert.Equal(0, archived.Assets); // its plans stand down
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Manager, $"/api/v1/pm/templates/{template.Id}/archive", new { rowVersion = edited.RowVersion })).StatusCode);
    }

    // ---------------- generation ----------------

    [Fact]
    public async Task A_schedule_creates_its_work_order_ahead_of_time_once_and_assigns_it()
    {
        var rig = await NewRigAsync();
        var template = await NewTemplateAsync(rig);
        var schedule = await NewScheduleAsync(rig, template, new
        {
            name = "Chiller service", templateId = template.Id, targetKind = "Asset", assetId = rig.Asset.Id, timeBasis = "Fixed", intervalUnit = "Month", interval = 3,
            startOn = Today.AddDays(5), leadDays = 7, assignToUserId = rig.TechId,
        });

        // Not due yet outside the lead window: a schedule starting in 30 days creates nothing.
        var later = await NewScheduleAsync(rig, template, new { name = "Later", templateId = template.Id, targetKind = "Asset", assetId = rig.Asset.Id, timeBasis = "Fixed", intervalUnit = "Month", interval = 1, startOn = Today.AddDays(30), leadDays = 7 });

        var first = await RunOk(rig.Manager);
        Assert.Equal(1, first.WorkOrdersCreated);
        var again = await RunOk(rig.Manager);
        Assert.Equal(0, again.WorkOrdersCreated); // running again changes nothing

        var order = Assert.Single(await PlannedOrdersAsync(rig.Manager));
        Assert.Equal(WorkOrderStatus.Assigned, order.Status); // preventive work needs no approval, and is assigned when the schedule says who
        Assert.Equal(rig.TechId, order.AssignedTo);
        Assert.Equal(WorkPriority.High, order.Priority);
        Assert.Equal(rig.Asset.Id, order.AssetId);
        Assert.Contains(rig.Asset.Code, order.Title);
        Assert.Contains("Quarterly service", order.Title);
        Assert.Equal(Today.AddDays(5), DateOnly.FromDateTime(order.DueAt!.Value.UtcDateTime));
        Assert.Equal(SlaState.None, order.Sla.State); // the SLA is for reported faults, not planned visits

        var checklist = await rig.Technician.GetFromJsonAsync<ChecklistItemDto[]>($"/api/v1/work-orders/{order.Id}/checklist", Json);
        Assert.Equal(["Check refrigerant", "Clean coils"], checklist!.Select(c => c.Text).ToArray());
        Assert.Contains((await ReadAsync<PmScheduleDto[]>(await rig.Viewer.GetAsync("/api/v1/pm/schedules"))), s => s.Id == schedule.Id && s.Open == 1);
        Assert.Contains((await ReadAsync<PmScheduleDto[]>(await rig.Viewer.GetAsync("/api/v1/pm/schedules"))), s => s.Id == later.Id && s.Open == 0);

        // Parallel runs (two workers) make one work order, not two.
        var second = await NewScheduleAsync(rig, template, new { name = "Other", templateId = template.Id, targetKind = "Asset", assetId = rig.Asset.Id, timeBasis = "Fixed", intervalUnit = "Week", interval = 1, startOn = Today.AddDays(1), leadDays = 3 });
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Run(rig.Manager)));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(2, (await PlannedOrdersAsync(rig.Manager)).Length);
        Assert.NotNull(second);

        // The audit trail marks the work order as created by a schedule (the actor is whoever triggered the run; the worker has none).
        await using var platform = sql.App(rig.Tenant);
        Assert.True(await platform.AuditEvents.AnyAsync(a => a.Action == "workorder.created" && a.EntityId == order.Id.ToString() && a.ChangeSummaryJson!.Contains("\"preventive\":true")));
    }

    [Fact]
    public async Task Finishing_the_job_moves_a_fixed_schedule_to_its_next_date_and_records_whether_it_was_on_time()
    {
        var rig = await NewRigAsync();
        var template = await NewTemplateAsync(rig);
        var schedule = await NewScheduleAsync(rig, template, new
        {
            name = "Monthly", templateId = template.Id, targetKind = "Asset", assetId = rig.Asset.Id, timeBasis = "Fixed", intervalUnit = "Month", interval = 1,
            startOn = Today.AddDays(2), leadDays = 7, assignToUserId = rig.TechId,
        });
        await RunOk(rig.Manager);
        var order = Assert.Single(await PlannedOrdersAsync(rig.Manager));

        // Complete it (the required checklist line first).
        await PostAsync(rig.Technician, $"/api/v1/work-orders/{order.Id}/start", new { });
        var checklist = (await rig.Technician.GetFromJsonAsync<ChecklistItemDto[]>($"/api/v1/work-orders/{order.Id}/checklist", Json))!;
        await PostAsync(rig.Technician, $"/api/v1/work-orders/{order.Id}/checklist/{checklist[0].Id}", new { result = "Done" });
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Technician, $"/api/v1/work-orders/{order.Id}/complete", new { code = "Fixed", notes = "serviced" })).StatusCode);

        var plan = Assert.Single((await rig.Viewer.GetFromJsonAsync<PmPlanDto[]>($"/api/v1/pm/schedules/{schedule.Id}/plans", Json))!);
        Assert.Null(plan.OpenWorkOrderId);
        Assert.Equal(Today, plan.LastCompletedOn);
        Assert.Equal(Today.AddDays(2).AddMonths(1), plan.NextDueOn); // the schedule's own date, not "a month from today"
        Assert.Equal(0, (await RunOk(rig.Manager)).WorkOrdersCreated); // the next one is far ahead

        await using var db = sql.Work(rig.Tenant);
        var occurrence = await db.PmOccurrences.SingleAsync(o => o.WorkOrderId == order.Id);
        Assert.Equal((PmOutcome.Completed, true), (occurrence.Outcome, occurrence.OnTime));

        // Reopening and completing again does not move the plan a second time.
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Manager, $"/api/v1/work-orders/{order.Id}/reopen", new { reason = "missed a check" })).StatusCode);
        await PostAsync(rig.Technician, $"/api/v1/work-orders/{order.Id}/start", new { });
        await PostAsync(rig.Technician, $"/api/v1/work-orders/{order.Id}/complete", new { code = "Fixed", notes = "again" });
        Assert.Equal(Today.AddDays(2).AddMonths(1), Assert.Single((await rig.Viewer.GetFromJsonAsync<PmPlanDto[]>($"/api/v1/pm/schedules/{schedule.Id}/plans", Json))!).NextDueOn);
    }

    [Fact]
    public async Task A_floating_schedule_counts_from_the_day_the_job_was_done_and_a_cancelled_job_counts_as_skipped()
    {
        var rig = await NewRigAsync();
        var template = await NewTemplateAsync(rig, "Lubrication", checklist: null);
        var schedule = await NewScheduleAsync(rig, template, new
        {
            name = "Lubricate", templateId = template.Id, targetKind = "Asset", assetId = rig.Asset.Id, timeBasis = "Floating", intervalUnit = "Day", interval = 90,
            startOn = Today, leadDays = 5, assignToUserId = rig.TechId,
        });
        await RunOk(rig.Manager);
        var order = Assert.Single(await PlannedOrdersAsync(rig.Manager));

        await PostAsync(rig.Technician, $"/api/v1/work-orders/{order.Id}/start", new { });
        var items = (await rig.Technician.GetFromJsonAsync<ChecklistItemDto[]>($"/api/v1/work-orders/{order.Id}/checklist", Json))!;
        await PostAsync(rig.Technician, $"/api/v1/work-orders/{order.Id}/checklist/{items[0].Id}", new { result = "Done" });
        var done = await PostAsync(rig.Technician, $"/api/v1/work-orders/{order.Id}/complete", new { code = "Fixed", notes = "greased" });
        Assert.True(done.IsSuccessStatusCode, await done.Content.ReadAsStringAsync());
        Assert.Equal(Today.AddDays(90), Assert.Single((await rig.Viewer.GetFromJsonAsync<PmPlanDto[]>($"/api/v1/pm/schedules/{schedule.Id}/plans", Json))!).NextDueOn);

        // Bring the next one forward, generate it, then cancel it: it is recorded as skipped and the plan moves on from today.
        await using (var platform = sql.Work(null, platform: true))
        {
            await platform.Database.ExecuteSqlRawAsync("UPDATE work.PmPlans SET NextDueOn = {0} WHERE TenantId = {1}", Today.AddDays(1).ToDateTime(TimeOnly.MinValue), rig.Tenant);
        }

        Assert.Equal(1, (await RunOk(rig.Manager)).WorkOrdersCreated);
        var next = (await PlannedOrdersAsync(rig.Manager)).Last();
        Assert.NotEqual(order.Id, next.Id);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Manager, $"/api/v1/work-orders/{next.Id}/cancel", new { reason = "Chiller is being replaced" })).StatusCode);

        await using var db = sql.Work(rig.Tenant);
        var occurrence = await db.PmOccurrences.SingleAsync(o => o.WorkOrderId == next.Id);
        Assert.Equal(PmOutcome.Skipped, occurrence.Outcome);
        Assert.Null(occurrence.OnTime);
        Assert.Equal(Today.AddDays(90), (await rig.Viewer.GetFromJsonAsync<PmPlanDto[]>($"/api/v1/pm/schedules/{schedule.Id}/plans", Json))![0].NextDueOn);
    }

    [Fact]
    public async Task A_schedule_for_an_asset_class_follows_the_assets_in_it()
    {
        var rig = await NewRigAsync();
        var template = await NewTemplateAsync(rig, "Class service", checklist: null);
        var second = await NewAssetAsync(rig.Manager, rig.Site, rig.Class);
        var schedule = await NewScheduleAsync(rig, template, new { name = "All chillers", templateId = template.Id, targetKind = "AssetClass", assetClassId = rig.Class.Id, timeBasis = "Fixed", intervalUnit = "Month", interval = 1, startOn = Today.AddDays(2), leadDays = 7 });
        Assert.Equal(2, schedule.Assets);
        Assert.Equal(2, (await RunOk(rig.Manager)).WorkOrdersCreated);

        // A new chiller is picked up on the next run; a retired one is let go (its open work order stays).
        var third = await NewAssetAsync(rig.Manager, rig.Site, rig.Class);
        var run = await RunOk(rig.Manager);
        Assert.Equal(1, run.PlansCreated);
        Assert.Equal(1, run.WorkOrdersCreated);

        var retire = await PostAsync(rig.Manager, $"/api/v1/assets/{second.Id}/status", new { status = "Retired", reason = "Scrapped", rowVersion = second.RowVersion });
        Assert.Equal(HttpStatusCode.OK, retire.StatusCode);
        Assert.Equal(1, (await RunOk(rig.Manager)).PlansRetired);
        var plans = (await rig.Viewer.GetFromJsonAsync<PmPlanDto[]>($"/api/v1/pm/schedules/{schedule.Id}/plans", Json))!;
        Assert.Equal(3, plans.Length);
        Assert.False(plans.Single(p => p.AssetId == second.Id).IsActive);
        Assert.Equal(3, (await PlannedOrdersAsync(rig.Manager)).Length);
        Assert.NotEqual(third.Id, second.Id);
    }

    // ---------------- forecast ----------------

    [Fact]
    public async Task The_forecast_shows_open_work_projected_dates_and_what_is_overdue()
    {
        var rig = await NewRigAsync();
        var template = await NewTemplateAsync(rig, "Forecast service", checklist: null);
        var schedule = await NewScheduleAsync(rig, template, new { name = "Monthly", templateId = template.Id, targetKind = "Asset", assetId = rig.Asset.Id, timeBasis = "Fixed", intervalUnit = "Month", interval = 1, startOn = Today.AddDays(4), leadDays = 7 });
        await RunOk(rig.Manager);

        var forecast = await ReadAsync<ForecastDto>(await rig.Viewer.GetAsync($"/api/v1/pm/forecast?from={Today:yyyy-MM-dd}&to={Today.AddMonths(6):yyyy-MM-dd}"));
        Assert.Equal(Today, forecast.Today);
        var open = Assert.Single(forecast.Items, i => i.Kind == ForecastKind.Open);
        Assert.Equal(Today.AddDays(4), open.Date);
        Assert.StartsWith("WO-", open.WorkOrderNumber);
        Assert.False(open.Overdue);
        var projected = forecast.Items.Where(i => i.Kind == ForecastKind.Projected).Select(i => i.Date).ToArray();
        Assert.Equal(Enumerable.Range(1, 5).Select(n => Today.AddDays(4).AddMonths(n)).Where(d => d <= Today.AddMonths(6)).ToArray(), projected);
        Assert.All(forecast.Items, i => Assert.Equal(rig.Asset.Id, i.AssetId));

        // Filters narrow it; a class with nothing returns nothing; ranges are limited.
        Assert.Empty((await ReadAsync<ForecastDto>(await rig.Viewer.GetAsync($"/api/v1/pm/forecast?assetClassId={Guid.NewGuid()}"))).Items);
        Assert.Equal(HttpStatusCode.BadRequest, (await rig.Viewer.GetAsync($"/api/v1/pm/forecast?from=2026-01-01&to=2028-01-01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await rig.Viewer.GetAsync($"/api/v1/pm/forecast?from=2026-06-01&to=2026-01-01")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Requester.GetAsync("/api/v1/pm/forecast")).StatusCode);

        // Late work shows as overdue.
        await using (var platform = sql.Work(null, platform: true))
        {
            await platform.Database.ExecuteSqlRawAsync("UPDATE work.PmOccurrences SET DueOn = {0} WHERE TenantId = {1}", Today.AddDays(-3).ToDateTime(TimeOnly.MinValue), rig.Tenant);
        }

        var late = await ReadAsync<ForecastDto>(await rig.Viewer.GetAsync($"/api/v1/pm/forecast?from={Today.AddDays(-10):yyyy-MM-dd}&to={Today.AddDays(10):yyyy-MM-dd}"));
        Assert.True(Assert.Single(late.Items, i => i.Kind == ForecastKind.Open).Overdue);
        Assert.NotNull(schedule);
    }

    // ---------------- isolation ----------------

    [Fact]
    public async Task Another_tenant_sees_none_of_it_and_the_tables_are_protected_by_the_database()
    {
        var rig = await NewRigAsync();
        var template = await NewTemplateAsync(rig, "Isolated", checklist: null);
        var schedule = await NewScheduleAsync(rig, template, new { name = "S", templateId = template.Id, targetKind = "Asset", assetId = rig.Asset.Id, timeBasis = "Fixed", intervalUnit = "Month", interval = 1, startOn = Today.AddDays(1) });
        await RunOk(rig.Manager);

        var foreign = await _api.ClientAsync(SqlServerFixture.TenantB, SystemRoles.FacilityManager);
        Assert.Empty((await foreign.GetFromJsonAsync<PmTemplateDto[]>("/api/v1/pm/templates", Json))!);
        Assert.Empty((await foreign.GetFromJsonAsync<PmScheduleDto[]>("/api/v1/pm/schedules", Json))!);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/api/v1/pm/schedules/{schedule.Id}/plans")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(foreign, $"/api/v1/pm/templates/{template.Id}", new { name = "hijack", rowVersion = template.RowVersion })).StatusCode);
        Assert.Empty((await ReadAsync<ForecastDto>(await foreign.GetAsync("/api/v1/pm/forecast"))).Items);
        Assert.Equal(0, (await RunOk(foreign)).WorkOrdersCreated);

        // A schedule cannot point at another tenant's asset.
        var theirs = await foreign.PostAsJsonAsync("/api/v1/pm/schedules", new { name = "x", templateId = template.Id, targetKind = "Asset", assetId = rig.Asset.Id, timeBasis = "Fixed", intervalUnit = "Month", interval = 1, startOn = Today }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, theirs.StatusCode);

        await using var other = sql.Work(SqlServerFixture.TenantB);
        Assert.Equal(0, await other.PmSchedules.IgnoreQueryFilters().CountAsync(s => s.TenantId == rig.Tenant));
        Assert.Equal(0, await other.PmPlans.IgnoreQueryFilters().CountAsync(s => s.TenantId == rig.Tenant));
        Assert.Equal(0, await other.PmOccurrences.IgnoreQueryFilters().CountAsync(s => s.TenantId == rig.Tenant));
        Assert.Equal(0, await other.PmTemplates.IgnoreQueryFilters().CountAsync(s => s.TenantId == rig.Tenant));
    }
}
