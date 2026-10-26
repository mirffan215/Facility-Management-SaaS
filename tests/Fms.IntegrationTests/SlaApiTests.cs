using System.Net;
using System.Net.Http.Json;
using Fms.Facilities.Services;
using Fms.Platform.Security;
using Fms.Work.Domain;
using Fms.Work.Services;
using Microsoft.EntityFrameworkCore;
using static Fms.IntegrationTests.ApiHarness;
using static Fms.IntegrationTests.SqlServerFixture;

namespace Fms.IntegrationTests;

[Collection(SqlCollection.Name)]
public sealed class SlaApiTests(SqlServerFixture sql) : IDisposable
{
    private readonly ApiHarness _api = new(sql);
    public void Dispose() => _api.Dispose();

    private sealed record Me(Guid? UserId);

    private static readonly Dictionary<string, DayHoursDto?> SaudiWeek = new()
    {
        ["Sunday"] = new("08:00", "17:00"), ["Monday"] = new("08:00", "17:00"), ["Tuesday"] = new("08:00", "17:00"), ["Wednesday"] = new("08:00", "17:00"),
        ["Thursday"] = new("08:00", "17:00"), ["Friday"] = null, ["Saturday"] = null,
    };

    private static Dictionary<string, DayHoursDto?> AllDay() => Enum.GetNames<DayOfWeek>().ToDictionary(d => d, _ => (DayHoursDto?)new("00:00", "24:00"));

    private async Task<Guid> NewTenantAsync()
    {
        var tenant = Guid.NewGuid();
        await using var platform = sql.App(null, platform: true);
        platform.Tenants.Add(new Fms.Platform.Domain.Tenant { Id = tenant, Name = "Sla " + tenant.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
        await platform.SaveChangesAsync();
        await Fms.Platform.Services.SystemRoleSync.EnsureAsync(platform, tenant);
        return tenant;
    }

    private static SaveSlaPolicyRequest DefaultPolicy(string name = "Standard", int scale = 1) => new(name, null, true,
    [
        new(WorkPriority.Low, 480 * scale, 2400 * scale), new(WorkPriority.Medium, 240 * scale, 1440 * scale),
        new(WorkPriority.High, 60 * scale, 480 * scale), new(WorkPriority.Urgent, 15 * scale, 120 * scale),
    ], null);

    private async Task<(LocationDto Site, LocationDto Building)> PlaceAsync(HttpClient manager)
    {
        var site = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", new { type = "Site", code = FacilitiesScope.Code("S"), name = "Riyadh HQ" }));
        var building = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", new { type = "Building", parentId = site.Id, code = FacilitiesScope.Code("B"), name = "Tower" }));
        return (site, building);
    }

    private static async Task<WorkOrderDto> Ok(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        Assert.Equal(expected, response.StatusCode);
        return await ReadAsync<WorkOrderDto>(response);
    }

    private static Task<HttpResponseMessage> Act(HttpClient c, Guid id, string action, object? body = null) => PostAsync(c, $"/api/v1/work-orders/{id}/{action}", body ?? new { });

    [Fact]
    public async Task Only_managers_set_up_calendars_and_policies_and_bad_input_is_explained()
    {
        var tenant = await NewTenantAsync();
        var manager = await _api.ClientAsync(tenant, SystemRoles.FacilityManager);
        var technician = await _api.ClientAsync(tenant, SystemRoles.Technician);
        var (site, building) = await PlaceAsync(manager);

        Assert.Equal(HttpStatusCode.Forbidden, (await technician.GetAsync("/api/v1/work-orders/sla/calendars")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(technician, "/api/v1/work-orders/sla/policies", DefaultPolicy())).StatusCode);

        // Calendars
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(manager, "/api/v1/work-orders/sla/calendars", new { name = "", timeZoneId = "Nowhere/Land", hours = SaudiWeek })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(manager, "/api/v1/work-orders/sla/calendars", new { name = "Closed", timeZoneId = "Asia/Riyadh", hours = new Dictionary<string, DayHoursDto?>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(manager, "/api/v1/work-orders/sla/calendars",
            new { name = "Backwards", timeZoneId = "Asia/Riyadh", hours = new Dictionary<string, DayHoursDto?> { ["Sunday"] = new("17:00", "08:00") } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(manager, "/api/v1/work-orders/sla/calendars",
            new { name = "Twice", timeZoneId = "Asia/Riyadh", hours = SaudiWeek, holidays = new[] { new { date = "2026-11-10", name = "a" }, new { date = "2026-11-10", name = "b" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(manager, "/api/v1/work-orders/sla/calendars",
            new { name = "Wrong kind", timeZoneId = "Asia/Riyadh", hours = SaudiWeek, siteIds = new[] { building.Id } })).StatusCode); // a building is not a site

        var created = await PostAsync(manager, "/api/v1/work-orders/sla/calendars", new
        {
            name = "Saudi week", timeZoneId = "Asia/Riyadh", hours = SaudiWeek, isDefault = true, siteIds = new[] { site.Id },
            holidays = new[] { new { date = "2026-09-23", name = "Saudi National Day" } },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var calendar = await ReadAsync<CalendarDto>(created);
        Assert.Null(calendar.Hours["Friday"]);
        Assert.Equal("08:00", calendar.Hours["Sunday"]!.Start);
        Assert.Equal(site.Id, Assert.Single(calendar.SiteIds));
        Assert.Equal("Saudi National Day", Assert.Single(calendar.Holidays).Name);

        // A site follows only one calendar, names are unique, and only one calendar is the default.
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(manager, "/api/v1/work-orders/sla/calendars", new { name = "Other", timeZoneId = "UTC", hours = AllDay(), siteIds = new[] { site.Id } })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(manager, "/api/v1/work-orders/sla/calendars", new { name = "Saudi week", timeZoneId = "UTC", hours = AllDay() })).StatusCode);
        var second = await ReadAsync<CalendarDto>(await PostAsync(manager, "/api/v1/work-orders/sla/calendars", new { name = "24x7", timeZoneId = "UTC", hours = AllDay(), isDefault = true }));
        var all = await manager.GetFromJsonAsync<CalendarDto[]>("/api/v1/work-orders/sla/calendars", Json);
        Assert.Equal(["24x7"], all!.Where(c => c.IsDefault).Select(c => c.Name).ToArray());

        // Updating replaces holidays and sites; a stale version conflicts.
        var current = all!.Single(c => c.Id == calendar.Id); // making another calendar the default changed this one's version
        var staleVersion = current.RowVersion;
        var updateResponse = await PutAsync(manager, $"/api/v1/work-orders/sla/calendars/{calendar.Id}",
            new { name = "Saudi week", timeZoneId = "Asia/Riyadh", hours = SaudiWeek, isDefault = false, rowVersion = current.RowVersion, siteIds = Array.Empty<Guid>(), holidays = Array.Empty<object>() });
        Assert.True(updateResponse.IsSuccessStatusCode, await updateResponse.Content.ReadAsStringAsync());
        var updated = await ReadAsync<CalendarDto>(updateResponse);
        Assert.Empty(updated.SiteIds);
        Assert.Empty(updated.Holidays);
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(manager, $"/api/v1/work-orders/sla/calendars/{calendar.Id}",
            new { name = "Saudi week", timeZoneId = "Asia/Riyadh", hours = SaudiWeek, rowVersion = staleVersion })).StatusCode);

        // Policies
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(manager, "/api/v1/work-orders/sla/policies", new { name = "", isDefault = true, targets = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(manager, "/api/v1/work-orders/sla/policies", DefaultPolicy() with { IsDefault = false })).StatusCode); // neither default nor category
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(manager, "/api/v1/work-orders/sla/policies", DefaultPolicy() with { CategoryId = Guid.NewGuid() })).StatusCode); // both
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(manager, "/api/v1/work-orders/sla/policies", DefaultPolicy() with { Targets = DefaultPolicy().Targets!.Take(3).ToList() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(manager, "/api/v1/work-orders/sla/policies",
            DefaultPolicy() with { Targets = DefaultPolicy().Targets!.Select(t => t.Priority == WorkPriority.Low ? t with { ResolutionMinutes = 1 } : t).ToList() })).StatusCode); // resolution before response

        var policy = await ReadAsync<SlaPolicyDto>(await PostAsync(manager, "/api/v1/work-orders/sla/policies", DefaultPolicy()));
        Assert.Equal(4, policy.Targets.Count);
        var another = await ReadAsync<SlaPolicyDto>(await PostAsync(manager, "/api/v1/work-orders/sla/policies", DefaultPolicy("Newer standard")));
        Assert.Equal(["Newer standard"], (await manager.GetFromJsonAsync<SlaPolicyDto[]>("/api/v1/work-orders/sla/policies", Json))!.Where(p => p.IsDefault).Select(p => p.Name).ToArray());

        var category = await ReadAsync<CategoryDto>(await PostAsync(manager, "/api/v1/work-orders/categories", new { code = FacilitiesScope.Code("CAT"), name = "HVAC" }));
        var forCategory = await PostAsync(manager, "/api/v1/work-orders/sla/policies", DefaultPolicy("HVAC only") with { IsDefault = false, CategoryId = category.Id });
        Assert.Equal(HttpStatusCode.Created, forCategory.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(manager, "/api/v1/work-orders/sla/policies", DefaultPolicy("Second for HVAC") with { IsDefault = false, CategoryId = category.Id })).StatusCode);

        var archived = await ReadAsync<SlaPolicyDto>(await PostAsync(manager, $"/api/v1/work-orders/sla/policies/{another.Id}/archive", new { rowVersion = another.RowVersion }));
        Assert.True(archived.IsArchived);
        Assert.DoesNotContain((await manager.GetFromJsonAsync<SlaPolicyDto[]>("/api/v1/work-orders/sla/policies", Json))!, p => p.Id == another.Id);
    }

    [Fact]
    public async Task The_clocks_start_at_approval_run_on_the_sites_calendar_and_pause_while_on_hold()
    {
        var tenant = await NewTenantAsync();
        var manager = await _api.ClientAsync(tenant, SystemRoles.FacilityManager);
        var approver = await _api.ClientAsync(tenant, SystemRoles.FacilityManager);
        var technician = await _api.ClientAsync(tenant, SystemRoles.Technician);
        var requester = await _api.ClientAsync(tenant, SystemRoles.Requester);
        var (site, building) = await PlaceAsync(manager);
        var techId = (await technician.GetFromJsonAsync<Me>("/api/v1/me", Json))!.UserId!.Value;

        await ReadAsync<CalendarDto>(await PostAsync(manager, "/api/v1/work-orders/sla/calendars", new { name = "Saudi week", timeZoneId = "Asia/Riyadh", hours = SaudiWeek, siteIds = new[] { site.Id } }));
        await ReadAsync<CalendarDto>(await PostAsync(manager, "/api/v1/work-orders/sla/calendars", new { name = "24x7", timeZoneId = "UTC", hours = AllDay(), isDefault = true }));
        await ReadAsync<SlaPolicyDto>(await PostAsync(manager, "/api/v1/work-orders/sla/policies", DefaultPolicy()));
        var saudi = new BusinessCalendar("Asia/Riyadh", WorkingHours.TryParse(Newtonsoft(SaudiWeek), out _)!);

        // Waiting for approval: no clocks yet.
        var before = DateTimeOffset.UtcNow;
        var wo = await Ok(PostAsync(requester, "/api/v1/work-orders", new { title = "Chiller warm", locationId = building.Id, priority = "High" }), HttpStatusCode.Created);
        Assert.Equal(SlaState.None, wo.Sla.State);
        Assert.Null(wo.Sla.ResolutionDueAt);

        // Approval starts them; the site's calendar (not the 24x7 default) decides the due times.
        var approved = await Ok(Act(approver, wo.Id, "approve"));
        var after = DateTimeOffset.UtcNow;
        Assert.Equal(SlaState.OnTrack, approved.Sla.State);
        Assert.InRange(approved.Sla.ResponseDueAt!.Value, saudi.AddBusinessMinutes(before, 60), saudi.AddBusinessMinutes(after, 60));
        Assert.InRange(approved.Sla.ResolutionDueAt!.Value, saudi.AddBusinessMinutes(before, 480), saudi.AddBusinessMinutes(after, 480));

        // Starting work is the response.
        await Ok(Act(approver, wo.Id, "assign", new { assigneeId = techId }));
        Assert.Null((await Ok(Act(approver, wo.Id, "start"))).Sla.RespondedAt is null ? null : (object?)null);
        var started = await ReadAsync<WorkOrderDetailDto>(await manager.GetAsync($"/api/v1/work-orders/{wo.Id}"));
        Assert.NotNull(started.Order.Sla.RespondedAt);
        Assert.False(started.Order.Sla.ResponseBreached);

        // On hold the resolution clock stops; resuming restores the working time that was left.
        var due = started.Order.Sla.ResolutionDueAt!.Value;
        var held = await Ok(Act(technician, wo.Id, "hold", new { reason = "Waiting for parts" }));
        Assert.True(held.Sla.Paused);
        Assert.Equal(SlaState.Paused, held.Sla.State);
        var resumed = await Ok(Act(technician, wo.Id, "resume"));
        Assert.False(resumed.Sla.Paused);
        Assert.True(resumed.Sla.ResolutionDueAt >= due.AddSeconds(-1), "resuming never makes the deadline earlier");
        Assert.True(resumed.Sla.ResolutionDueAt - due < TimeSpan.FromMinutes(5), "a hold of a few seconds moves the deadline by about as much");

        // Changing the priority while the clocks run moves the due times to the new targets, from the same start.
        var urgent = await Ok(PutAsync(manager, $"/api/v1/work-orders/{wo.Id}", new { title = resumed.Title, locationId = building.Id, priority = "Urgent", rowVersion = resumed.RowVersion }));
        Assert.True(urgent.Sla.ResolutionDueAt < resumed.Sla.ResolutionDueAt);

        // Finishing before the deadline meets the SLA.
        await Ok(Act(technician, wo.Id, "complete", new { code = "Fixed", notes = "done" }));
        Assert.Equal(SlaState.Met, (await ReadAsync<WorkOrderDetailDto>(await manager.GetAsync($"/api/v1/work-orders/{wo.Id}"))).Order.Sla.State);
    }

    [Fact]
    public async Task Emergencies_start_their_clocks_at_once_and_work_without_a_policy_has_none()
    {
        var tenant = await NewTenantAsync();
        var manager = await _api.ClientAsync(tenant, SystemRoles.FacilityManager);
        var (_, building) = await PlaceAsync(manager);

        var noPolicy = await Ok(PostAsync(manager, "/api/v1/work-orders", new { title = "Before any policy", type = "Emergency", locationId = building.Id }), HttpStatusCode.Created);
        Assert.Equal(SlaState.None, noPolicy.Sla.State);

        await ReadAsync<SlaPolicyDto>(await PostAsync(manager, "/api/v1/work-orders/sla/policies", DefaultPolicy()));
        var emergency = await Ok(PostAsync(manager, "/api/v1/work-orders", new { title = "Flood", type = "Emergency", priority = "Urgent", locationId = building.Id }), HttpStatusCode.Created);
        Assert.Equal(WorkOrderStatus.Approved, emergency.Status);
        Assert.Equal(SlaState.OnTrack, emergency.Sla.State); // no calendar configured: every minute counts
        Assert.InRange((emergency.Sla.ResponseDueAt!.Value - DateTimeOffset.UtcNow).TotalMinutes, 14, 15.1);

        // Policies apply from then on; the earlier order keeps having none.
        Assert.Equal(SlaState.None, (await ReadAsync<WorkOrderDetailDto>(await manager.GetAsync($"/api/v1/work-orders/{noPolicy.Id}"))).Order.Sla.State);
    }

    [Fact]
    public async Task A_category_policy_wins_over_the_default_and_policies_are_per_tenant()
    {
        var tenant = await NewTenantAsync();
        var other = await NewTenantAsync();
        var manager = await _api.ClientAsync(tenant, SystemRoles.FacilityManager);
        var otherManager = await _api.ClientAsync(other, SystemRoles.FacilityManager);
        var (_, building) = await PlaceAsync(manager);
        var category = await ReadAsync<CategoryDto>(await PostAsync(manager, "/api/v1/work-orders/categories", new { code = FacilitiesScope.Code("CAT"), name = "Lifts" }));

        await ReadAsync<SlaPolicyDto>(await PostAsync(manager, "/api/v1/work-orders/sla/policies", DefaultPolicy()));
        await ReadAsync<SlaPolicyDto>(await PostAsync(manager, "/api/v1/work-orders/sla/policies", DefaultPolicy("Lifts are urgent") with { IsDefault = false, CategoryId = category.Id,
            Targets = [new(WorkPriority.Low, 5, 10), new(WorkPriority.Medium, 5, 10), new(WorkPriority.High, 5, 10), new(WorkPriority.Urgent, 5, 10)] }));

        var lift = await Ok(PostAsync(manager, "/api/v1/work-orders", new { title = "Lift stuck", type = "Emergency", locationId = building.Id, categoryId = category.Id }), HttpStatusCode.Created);
        var other1 = await Ok(PostAsync(manager, "/api/v1/work-orders", new { title = "Light", type = "Emergency", locationId = building.Id }), HttpStatusCode.Created);
        Assert.InRange((lift.Sla.ResolutionDueAt!.Value - DateTimeOffset.UtcNow).TotalMinutes, 9, 10.1);
        Assert.True((other1.Sla.ResolutionDueAt!.Value - DateTimeOffset.UtcNow).TotalMinutes > 100); // the default's 2400 minutes

        Assert.Empty((await otherManager.GetFromJsonAsync<SlaPolicyDto[]>("/api/v1/work-orders/sla/policies", Json))!);
        Assert.Empty((await otherManager.GetFromJsonAsync<CalendarDto[]>("/api/v1/work-orders/sla/calendars", Json))!);
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(otherManager, $"/api/v1/work-orders/sla/policies/{lift.Id}", DefaultPolicy())).StatusCode);
    }

    [Fact]
    public async Task The_breached_filter_lists_work_past_its_sla_and_row_level_security_covers_the_new_tables()
    {
        var tenant = await NewTenantAsync();
        var manager = await _api.ClientAsync(tenant, SystemRoles.FacilityManager);
        var (_, building) = await PlaceAsync(manager);
        await ReadAsync<SlaPolicyDto>(await PostAsync(manager, "/api/v1/work-orders/sla/policies", DefaultPolicy()));
        var late = await Ok(PostAsync(manager, "/api/v1/work-orders", new { title = "Late", type = "Emergency", locationId = building.Id }), HttpStatusCode.Created);
        var fine = await Ok(PostAsync(manager, "/api/v1/work-orders", new { title = "Fine", type = "Emergency", locationId = building.Id }), HttpStatusCode.Created);

        // Move one order's deadlines into the past, as the platform (no API can do this).
        await using (var platform = sql.Work(null, platform: true))
        {
            await platform.Database.ExecuteSqlRawAsync("UPDATE work.WorkOrders SET ResponseDueAt = DATEADD(hour, -2, SYSDATETIMEOFFSET()), ResolutionDueAt = DATEADD(hour, -1, SYSDATETIMEOFFSET()) WHERE Id = {0}", late.Id);
        }

        var breached = await ReadAsync<Fms.BuildingBlocks.Results.Page<WorkOrderDto>>(await manager.GetAsync("/api/v1/work-orders?slaBreached=true"));
        Assert.Equal([late.Id], breached.Items.Select(w => w.Id).ToArray());
        Assert.Equal(SlaState.Breached, breached.Items[0].Sla.State);
        Assert.True(breached.Items[0].Sla.ResponseBreached);
        Assert.DoesNotContain(breached.Items, w => w.Id == fine.Id);

        await using var wrongTenant = sql.Work(TenantB);
        Assert.Equal(0, await wrongTenant.SlaPolicies.IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await wrongTenant.SlaTargets.IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await wrongTenant.Calendars.IgnoreQueryFilters().CountAsync());
    }

    private static string Newtonsoft(Dictionary<string, DayHoursDto?> hours) =>
        System.Text.Json.JsonSerializer.Serialize(hours.ToDictionary(h => h.Key, h => h.Value is null ? null : new { start = h.Value.Start, end = h.Value.End }));
}
