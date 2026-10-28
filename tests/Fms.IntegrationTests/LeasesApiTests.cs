using System.Net;
using Fms.Api;
using Fms.BuildingBlocks.Results;
using Fms.Facilities.Services;
using Fms.Leases.Domain;
using Fms.Leases.Services;
using Fms.Platform.Notifications;
using Fms.Platform.Security;
using Fms.Platform.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static Fms.IntegrationTests.ApiHarness;

namespace Fms.IntegrationTests;

[Collection(SqlCollection.Name)]
public sealed class LeasesApiTests : IDisposable
{
    private readonly SqlServerFixture _sql;
    private readonly ApiHarness _api;

    public LeasesApiTests(SqlServerFixture sql)
    {
        _sql = sql;
        _api = new ApiHarness(sql);
    }

    public void Dispose() => _api.Dispose();

    private sealed record Rig(Guid Tenant, HttpClient Manager, HttpClient Second, HttpClient Viewer, HttpClient Technician, LocationDto Site);

    private async Task<Rig> NewRigAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var platform = _sql.App(null, platform: true))
        {
            platform.Tenants.Add(new Fms.Platform.Domain.Tenant { Id = tenant, Name = "Lease " + tenant.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
            await platform.SaveChangesAsync();
            await SystemRoleSync.EnsureAsync(platform, tenant);
        }

        var manager = await _api.ClientAsync(tenant, SystemRoles.FacilityManager);
        var site = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", new { type = "Site", code = FacilitiesScope.Code("S"), name = "HQ" }));
        return new Rig(tenant, manager, await _api.ClientAsync(tenant, SystemRoles.FacilityManager), await _api.ClientAsync(tenant, SystemRoles.Viewer), await _api.ClientAsync(tenant, SystemRoles.Technician), site);
    }

    private static string D(int offset) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(offset).ToString("yyyy-MM-dd");

    private static Dictionary<string, object?> Body(Rig rig, object? more = null, string? reference = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["reference"] = reference ?? "L" + Guid.NewGuid().ToString("N")[..8], ["title"] = "Head office", ["kind"] = "Occupied", ["counterparty"] = "Al Noor Properties", ["locationId"] = rig.Site.Id,
            ["startOn"] = D(-100), ["endOn"] = D(265), ["noticeDays"] = 60, ["currencyCode"] = "SAR", ["baseRent"] = 10000, ["frequency"] = "Monthly",
        };
        if (more is not null) foreach (var p in more.GetType().GetProperties()) body[p.Name] = p.GetValue(more);
        return body;
    }

    private static async Task<LeaseDto> NewLeaseAsync(HttpClient c, Rig rig, object? more = null, string? reference = null, bool activate = false)
    {
        var response = await PostAsync(c, "/api/v1/leases", Body(rig, more, reference));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var lease = await ReadAsync<LeaseDto>(response);
        return activate ? await ActivateAsync(c, lease) : lease;
    }

    private static async Task<LeaseDto> ActivateAsync(HttpClient c, LeaseDto l)
    {
        var response = await PostAsync(c, $"/api/v1/leases/{l.Id}/activate", new { rowVersion = l.RowVersion });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<LeaseDto>(response);
    }

    private static async Task<LeaseDto> GetAsync(HttpClient c, Guid id) => await ReadAsync<LeaseDto>(await c.GetAsync($"/api/v1/leases/{id}"));

    private static async Task<List<NotificationDto>> NoticesAsync(HttpClient c, string type) =>
        (await ReadAsync<Page<NotificationDto>>(await c.GetAsync("/api/v1/notifications?pageSize=50"))).Items.Where(n => n.Type == type).ToList();

    private LeaseMonitor Monitor() => new(_api.Factory.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<LeaseMonitor>.Instance);

    [Fact]
    public async Task Roles_decide_who_reads_and_who_changes_leases()
    {
        var rig = await NewRigAsync();
        var l = await NewLeaseAsync(rig.Manager, rig);
        Assert.Equal(HttpStatusCode.OK, (await rig.Viewer.GetAsync("/api/v1/leases")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rig.Viewer.GetAsync($"/api/v1/leases/{l.Id}/schedule")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Technician.GetAsync("/api/v1/leases")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Viewer, "/api/v1/leases", Body(rig))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Viewer, $"/api/v1/leases/{l.Id}/activate", new { rowVersion = l.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GuestClient().GetAsync("/api/v1/leases")).StatusCode);
    }

    [Fact]
    public async Task A_lease_is_validated_unique_by_reference_and_edited_with_a_current_version()
    {
        var rig = await NewRigAsync();
        var l = await NewLeaseAsync(rig.Manager, rig, reference: "hq-001");
        Assert.Equal("HQ-001", l.Reference);
        Assert.Equal(LeaseState.Draft, l.State);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, "/api/v1/leases", Body(rig, reference: "HQ-001"))).StatusCode);

        foreach (var bad in new object[]
                 {
                     new { reference = "bad ref!" }, new { title = "" }, new { counterparty = "" }, new { kind = "Nope" }, new { endOn = D(-200) }, new { baseRent = -1 }, new { frequency = "Weekly" }, new { escalationPercent = 150 },
                     new { noticeDays = 5000 }, new { noticeDays = 400, startOn = D(-10), endOn = D(100) }, new { breakOn = D(1000) }, new { currencyCode = "EURO" }, new { alertLeadDays = 999 }, new { rentFreeMonths = 99 },
                     new { endOn = D(40000) }, new { locationId = Guid.NewGuid() },
                 })
            Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/leases", Body(rig, bad))).StatusCode);

        var update = Body(rig, new { title = "Head office, floor 2" }, "HQ-001");
        update["rowVersion"] = l.RowVersion;
        var saved = await ReadAsync<LeaseDto>(await PutAsync(rig.Manager, $"/api/v1/leases/{l.Id}", update));
        Assert.Equal("Head office, floor 2", saved.Title);
        update["title"] = "Head office, floor 3";
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(rig.Manager, $"/api/v1/leases/{l.Id}", update)).StatusCode); // stale version
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(rig.Manager, $"/api/v1/leases/{l.Id}", Body(rig, null, "HQ-001"))).StatusCode); // no version
    }

    [Fact]
    public async Task The_rent_schedule_follows_the_escalation_rent_free_months_and_the_last_part_period()
    {
        var rig = await NewRigAsync();
        var l = await NewLeaseAsync(rig.Manager, rig, new { startOn = "2026-01-01", endOn = "2027-12-31", baseRent = 1000, escalationPercent = 3, escalationEveryMonths = 12, rentFreeMonths = 2, noticeDays = 0 });
        var s = await ReadAsync<ScheduleDto>(await rig.Viewer.GetAsync($"/api/v1/leases/{l.Id}/schedule"));
        Assert.Equal(24, s.Installments.Count);
        Assert.Equal([0m, 0m], s.Installments.Take(2).Select(i => i.Amount));
        Assert.True(s.Installments[0].IsRentFree);
        Assert.All(s.Installments.Skip(2).Take(10), i => Assert.Equal(1000m, i.Amount));
        Assert.All(s.Installments.Skip(12), i => Assert.Equal(1030m, i.Amount));
        Assert.Equal(10 * 1000m + 12 * 1030m, s.Total);
        Assert.Equal(s.Total, l.TotalRent);
        Assert.Equal(new DateOnly(2026, 1, 1), s.Installments[0].DueOn);
        Assert.Equal(new DateOnly(2027, 12, 1), s.Installments[^1].DueOn);

        var odd = await NewLeaseAsync(rig.Manager, rig, new { startOn = "2026-01-01", endOn = "2026-04-15", baseRent = 3000, frequency = "Quarterly", noticeDays = 0 });
        var q = await ReadAsync<ScheduleDto>(await rig.Viewer.GetAsync($"/api/v1/leases/{odd.Id}/schedule"));
        Assert.Equal(2, q.Installments.Count);
        Assert.Equal(3000m, q.Installments[0].Amount);
        Assert.True(q.Installments[1].IsPartial);
        Assert.Equal(new DateOnly(2026, 4, 15), q.Installments[1].PeriodEnd);
        Assert.Equal(Math.Round(3000m * 15 / 91, 2), q.Installments[1].Amount); // 15 of the 91 days of the April-June quarter
    }

    [Fact]
    public async Task Activation_locks_the_money_and_dates_but_not_the_descriptive_fields()
    {
        var rig = await NewRigAsync();
        var l = await NewLeaseAsync(rig.Manager, rig, activate: true);
        Assert.Equal(LeaseState.Active, l.State);
        Assert.True(l.TermsLocked);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, $"/api/v1/leases/{l.Id}/activate", new { rowVersion = l.RowVersion })).StatusCode);

        var rent = Body(rig, new { baseRent = 20000 }, l.Reference);
        rent["rowVersion"] = l.RowVersion;
        var refused = await PutAsync(rig.Manager, $"/api/v1/leases/{l.Id}", rent);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("cannot be edited", await refused.Content.ReadAsStringAsync());
        Assert.Equal(10000m, (await GetAsync(rig.Manager, l.Id)).BaseRent);

        var notes = Body(rig, new { title = "Renamed", notes = "Landlord contact changed", noticeDays = 90, startOn = l.StartOn.ToString("yyyy-MM-dd"), endOn = l.EndOn.ToString("yyyy-MM-dd") }, l.Reference);
        notes["rowVersion"] = l.RowVersion;
        var saved = await ReadAsync<LeaseDto>(await PutAsync(rig.Manager, $"/api/v1/leases/{l.Id}", notes));
        Assert.Equal("Renamed", saved.Title);
        Assert.Equal(90, saved.NoticeDays);
        Assert.Equal(l.EndOn.AddDays(-90), saved.NoticeDeadline);
    }

    [Fact]
    public async Task Extending_and_terminating_need_reasons_and_end_the_edit_rights()
    {
        var rig = await NewRigAsync();
        var l = await NewLeaseAsync(rig.Manager, rig, activate: true);
        var newEnd = l.EndOn.AddYears(1).ToString("yyyy-MM-dd");
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, $"/api/v1/leases/{l.Id}/extend", new { newEndOn = newEnd, rowVersion = l.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, $"/api/v1/leases/{l.Id}/extend", new { newEndOn = D(0), reason = "Backwards", rowVersion = l.RowVersion })).StatusCode);
        var extended = await ReadAsync<LeaseDto>(await PostAsync(rig.Manager, $"/api/v1/leases/{l.Id}/extend", new { newEndOn = newEnd, reason = "Option exercised", rowVersion = l.RowVersion }));
        Assert.Equal(l.EndOn.AddYears(1), extended.EndOn);
        Assert.True(extended.TotalRent > l.TotalRent);

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, $"/api/v1/leases/{l.Id}/terminate", new { rowVersion = extended.RowVersion })).StatusCode);
        var ended = await ReadAsync<LeaseDto>(await PostAsync(rig.Manager, $"/api/v1/leases/{l.Id}/terminate", new { on = D(-1), reason = "Moved out", rowVersion = extended.RowVersion }));
        Assert.Equal(LeaseState.Terminated, ended.State);
        Assert.Equal("Moved out", ended.TerminationReason);
        Assert.Empty(ended.Deadlines);

        var edit = Body(rig, null, l.Reference);
        edit["rowVersion"] = ended.RowVersion;
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(rig.Manager, $"/api/v1/leases/{l.Id}", edit)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, $"/api/v1/leases/{l.Id}/extend", new { newEndOn = D(900), reason = "x", rowVersion = ended.RowVersion })).StatusCode);

        // The audit trail has each step, and a lease that was in force cannot be deleted.
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, $"/api/v1/leases/{l.Id}/delete", new { reason = "Oops" })).StatusCode);
        var admin = await _api.ClientAsync(rig.Tenant, SystemRoles.Administrator);
        var actions = (await ReadAsync<Page<Fms.Platform.Services.AuditEntryDto>>(await admin.GetAsync($"/api/v1/audit?entityType=Lease&pageSize=100"))).Items.Where(a => a.EntityId == l.Id.ToString()).Select(a => a.Action).ToList();
        Assert.Contains("lease.created", actions);
        Assert.Contains("lease.activated", actions);
        Assert.Contains("lease.extended", actions);
        Assert.Contains("lease.terminated", actions);
    }

    [Fact]
    public async Task Only_a_draft_can_be_deleted_and_it_needs_a_reason()
    {
        var rig = await NewRigAsync();
        var l = await NewLeaseAsync(rig.Manager, rig);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, $"/api/v1/leases/{l.Id}/delete", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Manager, $"/api/v1/leases/{l.Id}/delete", new { reason = "Entered twice" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.Viewer.GetAsync($"/api/v1/leases/{l.Id}")).StatusCode);
    }

    [Fact]
    public async Task The_list_filters_by_state_kind_and_text_and_shows_the_next_deadline()
    {
        var rig = await NewRigAsync();
        var draft = await NewLeaseAsync(rig.Manager, rig, new { title = "Warehouse draft" });
        var active = await NewLeaseAsync(rig.Manager, rig, new { title = "Annex", kind = "LetOut", counterparty = "Retail Co" }, activate: true);
        var expired = await NewLeaseAsync(rig.Manager, rig, new { title = "Old kiosk", startOn = D(-400), endOn = D(-5), noticeDays = 0 }, activate: true);
        Assert.Equal(LeaseState.Expired, expired.State);

        async Task<List<LeaseSummaryDto>> List(string query) => (await ReadAsync<Page<LeaseSummaryDto>>(await rig.Viewer.GetAsync("/api/v1/leases" + query))).Items.ToList();
        Assert.Equal(3, (await List("")).Count);
        Assert.Equal(draft.Id, Assert.Single(await List("?state=Draft")).Id);
        Assert.Equal(active.Id, Assert.Single(await List("?state=Active")).Id);
        Assert.Equal(expired.Id, Assert.Single(await List("?state=Expired")).Id);
        Assert.Equal(active.Id, Assert.Single(await List("?kind=LetOut")).Id);
        Assert.Equal(active.Id, Assert.Single(await List("?q=Retail")).Id);

        var summary = (await List("?state=Active")).Single();
        Assert.Equal(DeadlineKind.Notice, summary.NextDeadline!.Kind);
        Assert.Equal(active.EndOn.AddDays(-60), summary.NextDeadline.On);
    }

    [Fact]
    public async Task The_daily_check_warns_the_managers_once_per_deadline_and_again_when_the_lease_has_run_out()
    {
        var rig = await NewRigAsync();
        var near = await NewLeaseAsync(rig.Manager, rig, new { title = "Car park", startOn = D(-300), endOn = D(40), noticeDays = 30 }, activate: true);
        var far = await NewLeaseAsync(rig.Manager, rig, new { title = "Far away", startOn = D(-300), endOn = D(1000), noticeDays = 30 }, activate: true);

        var monitor = Monitor();
        await monitor.RunOnceAsync();
        await monitor.RunOnceAsync();

        var notices = await NoticesAsync(rig.Second, "lease.notice");
        var mine = Assert.Single(notices, n => n.Data.ToString()!.Contains("Car park"));
        Assert.Contains(near.Reference, mine.Data.ToString());
        Assert.DoesNotContain(notices, n => n.Data.ToString()!.Contains("Far away"));
        Assert.DoesNotContain((await ReadAsync<Page<NotificationDto>>(await rig.Viewer.GetAsync("/api/v1/notifications?pageSize=50"))).Items, n => n.Type.StartsWith("lease.", StringComparison.Ordinal));

        // The lease runs out unnoticed: that is a different warning.
        var gone = await NewLeaseAsync(rig.Manager, rig, new { title = "Kiosk", startOn = D(-400), endOn = D(-3), noticeDays = 0 }, activate: true);
        await monitor.RunOnceAsync();
        await monitor.RunOnceAsync();
        Assert.Single(await NoticesAsync(rig.Second, "lease.expired"), n => n.Data.ToString()!.Contains(gone.Reference));

        // Extending it clears the state; the next warning is about the new end when that comes into range.
        gone = await GetAsync(rig.Manager, gone.Id); // the daily check records what it warned about, so the version moved on
        var extended = await ReadAsync<LeaseDto>(await PostAsync(rig.Manager, $"/api/v1/leases/{gone.Id}/extend", new { newEndOn = D(20), reason = "Renewed", rowVersion = gone.RowVersion }));
        await monitor.RunOnceAsync();
        Assert.Contains(await NoticesAsync(rig.Second, "lease.expiry"), n => n.Data.ToString()!.Contains(gone.Reference));
        _ = (far, extended);
    }

    [Fact]
    public void The_alert_source_id_is_stable_for_the_same_deadline_and_day_and_differs_otherwise()
    {
        var id = Guid.NewGuid();
        var day = new DateOnly(2026, 6, 1);
        Assert.Equal(LeaseAlerts.SourceId(id, "notice:2026-07-01", day), LeaseAlerts.SourceId(id, "notice:2026-07-01", day));
        Assert.NotEqual(LeaseAlerts.SourceId(id, "notice:2026-07-01", day), LeaseAlerts.SourceId(id, "expiry:2026-07-01", day));
        Assert.NotEqual(LeaseAlerts.SourceId(id, "notice:2026-07-01", day), LeaseAlerts.SourceId(id, "notice:2026-07-01", day.AddDays(7)));
    }

    [Fact]
    public async Task The_summary_adds_rent_per_kind_and_currency_and_the_dashboard_and_feed_include_leases_without_names()
    {
        var rig = await NewRigAsync();
        await NewLeaseAsync(rig.Manager, rig, new { title = "Secret tower lease", counterparty = "Hidden Landlord", startOn = D(-30), endOn = D(700), baseRent = 1000 }, "FEED-L1", activate: true);
        await NewLeaseAsync(rig.Manager, rig, new { startOn = D(-30), endOn = D(700), baseRent = 500, kind = "LetOut" }, activate: true);
        var summary = await ReadAsync<LeaseSummaryTotalsDto>(await rig.Viewer.GetAsync("/api/v1/leases/summary"));
        Assert.Equal(2, summary.Active);
        Assert.Contains(summary.Rent, r => r.Kind == LeaseKind.Occupied && r.CurrencyCode == "SAR" && r.NextTwelveMonths is > 11000m and < 13000m);
        Assert.Contains(summary.Rent, r => r.Kind == LeaseKind.LetOut && r.NextTwelveMonths is > 5500m and < 6500m);

        var text = await (await rig.Manager.GetAsync("/api/v1/reports/dashboard")).Content.ReadAsStringAsync();
        Assert.Contains("leases.active", text);
        Assert.Contains("leases.rentPayable", text);

        var admin = await _api.ClientAsync(rig.Tenant, SystemRoles.Administrator);
        var token = await ReadAsync<Fms.Reporting.Services.CreatedFeedTokenDto>(await PostAsync(admin, "/api/v1/reports/feed/tokens", new { name = "BI" }));
        var feed = _api.Factory.CreateClient();
        feed.DefaultRequestHeaders.Add("X-Api-Key", token.Secret);
        var leases = await feed.GetStringAsync("/api/v1/odata/Leases");
        Assert.Contains("FEED-L1", leases);
        Assert.DoesNotContain("Secret tower lease", leases);
        Assert.DoesNotContain("Hidden Landlord", leases);
    }

    [Fact]
    public async Task Another_organisation_sees_none_of_it()
    {
        var a = await NewRigAsync();
        var b = await NewRigAsync();
        var l = await NewLeaseAsync(a.Manager, a, activate: true);

        Assert.Equal(HttpStatusCode.NotFound, (await b.Manager.GetAsync($"/api/v1/leases/{l.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Manager.GetAsync($"/api/v1/leases/{l.Id}/schedule")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(b.Manager, $"/api/v1/leases/{l.Id}/extend", new { newEndOn = D(900), reason = "x", rowVersion = l.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(b.Manager, $"/api/v1/leases/{l.Id}/terminate", new { reason = "x", rowVersion = l.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(b.Manager, $"/api/v1/leases/{l.Id}/delete", new { reason = "x" })).StatusCode);
        Assert.Empty((await ReadAsync<Page<LeaseSummaryDto>>(await b.Manager.GetAsync("/api/v1/leases"))).Items);
        Assert.Empty((await ReadAsync<LeaseSummaryTotalsDto>(await b.Manager.GetAsync("/api/v1/leases/summary"))).Rent);

        // The same reference in another organisation is a different lease; a place from another organisation cannot be used.
        await NewLeaseAsync(b.Manager, b, reference: l.Reference);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(b.Manager, "/api/v1/leases", Body(a))).StatusCode);

        await using var raw = _sql.Leases(b.Tenant);
        Assert.Equal(0, await raw.Leases.IgnoreQueryFilters().CountAsync(x => x.Id == l.Id && x.TenantId == b.Tenant));
    }
}
