using System.Net;
using System.Net.Http.Json;
using Fms.Api;
using Fms.BuildingBlocks.Results;
using Fms.Facilities.Services;
using Fms.Platform.Notifications;
using Fms.Platform.Security;
using Fms.Platform.Services;
using Fms.Visitors.Domain;
using Fms.Visitors.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Fms.IntegrationTests.ApiHarness;

namespace Fms.IntegrationTests;

[Collection(SqlCollection.Name)]
public sealed class VisitorsApiTests(SqlServerFixture sql) : IDisposable
{
    private readonly ApiHarness _api = new(sql, settings: new Dictionary<string, string?> { ["Visitors:PublicBaseUrl"] = "https://fms.example.test" });
    public void Dispose() => _api.Dispose();

    private sealed record Rig(Guid Tenant, HttpClient Manager, HttpClient Host, HttpClient Reception, HttpClient Viewer, LocationDto Site);

    private async Task<Rig> NewRigAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var platform = sql.App(null, platform: true))
        {
            platform.Tenants.Add(new Fms.Platform.Domain.Tenant { Id = tenant, Name = "Vis " + tenant.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
            await platform.SaveChangesAsync();
            await SystemRoleSync.EnsureAsync(platform, tenant);
        }

        var manager = await _api.ClientAsync(tenant, SystemRoles.FacilityManager);
        var site = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", new { type = "Site", code = FacilitiesScope.Code("S"), name = "HQ" }));
        return new Rig(tenant, manager, await _api.ClientAsync(tenant, SystemRoles.Requester), await _api.ClientAsync(tenant, SystemRoles.Reception), await _api.ClientAsync(tenant, SystemRoles.Viewer), site);
    }

    private static Task<HttpResponseMessage> ExpectAsync(HttpClient client, Rig rig, string name = "Jose Garcia", object? more = null, DateTimeOffset? at = null)
    {
        var body = new Dictionary<string, object?> { ["siteId"] = rig.Site.Id, ["kind"] = "Guest", ["visitorName"] = name, ["visitorCompany"] = "Acme", ["purpose"] = "Quarterly review", ["expectedAt"] = at ?? DateTimeOffset.UtcNow.AddHours(1) };
        if (more is not null) foreach (var p in more.GetType().GetProperties()) body[p.Name] = p.GetValue(more);
        return PostAsync(client, "/api/v1/visits", body);
    }

    private static async Task<CreatedVisitDto> ExpectedAsync(HttpClient client, Rig rig, string name = "Jose Garcia", object? more = null, DateTimeOffset? at = null)
    {
        var response = await ExpectAsync(client, rig, name, more, at);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<CreatedVisitDto>(response);
    }

    private async Task DeliverAsync()
    {
        var processor = _api.Factory.Services.GetRequiredService<Fms.Platform.Outbox.OutboxProcessor>();
        for (var i = 0; i < 20 && await processor.ProcessBatchAsync() > 0; i++) { }
    }

    private static Task<HttpResponseMessage> CheckInAsync(HttpClient client, VisitDto v, object? more = null)
    {
        var body = new Dictionary<string, object?> { ["rowVersion"] = v.RowVersion };
        if (more is not null) foreach (var p in more.GetType().GetProperties()) body[p.Name] = p.GetValue(more);
        return PostAsync(client, $"/api/v1/visits/{v.Id}/check-in", body);
    }

    private static string CodeOf(CreatedVisitDto c) => c.PassPath.Split('/').Last();

    private static async Task<Guid> UserIdAsync(HttpClient client) => (await client.GetFromJsonAsync<MeInfo>("/api/v1/me"))!.UserId!.Value;

    // ---------------- expecting ----------------

    [Fact]
    public async Task Hosts_expect_their_own_visitors_reception_sees_everyones_and_viewers_see_nothing()
    {
        var rig = await NewRigAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await ExpectAsync(rig.Viewer, rig)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Viewer.GetAsync("/api/v1/visits/mine")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Host.GetAsync("/api/v1/visits")).StatusCode); // a host cannot read the whole list
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Host.GetAsync("/api/v1/visits/on-site")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Host.GetAsync("/api/v1/visits/watchlist")).StatusCode);

        var created = await ExpectedAsync(rig.Host, rig);
        Assert.Equal((VisitStatus.Expected, "Jose Garcia"), (created.Visit.Status, created.Visit.VisitorName));
        Assert.StartsWith("/visit/", created.PassPath);
        Assert.StartsWith("https://fms.example.test/visit/", created.PassUrl);
        Assert.Contains("<svg", created.QrSvg);

        var mine = await ReadAsync<List<VisitDto>>(await rig.Host.GetAsync("/api/v1/visits/mine"));
        Assert.Equal(created.Visit.Id, Assert.Single(mine).Id);
        Assert.Empty(await ReadAsync<List<VisitDto>>(await (await _api.ClientAsync(rig.Tenant, SystemRoles.Technician)).GetAsync("/api/v1/visits/mine"))); // someone else's visitor is not mine
        var all = await ReadAsync<List<VisitDto>>(await rig.Reception.GetAsync("/api/v1/visits"));
        Assert.Contains(all, v => v.Id == created.Visit.Id);

        // Reception may expect a visitor for someone else; a host may not.
        var hostId = await UserIdAsync(rig.Host);
        var colleague = await _api.ClientAsync(rig.Tenant, SystemRoles.Technician);
        Assert.Equal(HttpStatusCode.Forbidden, (await ExpectAsync(colleague, rig, "Someone", new { hostId })).StatusCode); // a host cannot expect a visitor for someone else
        var onBehalf = await ExpectedAsync(rig.Reception, rig, "Walk In", new { hostId });
        Assert.Equal(hostId, onBehalf.Visit.HostId);
        Assert.Equal(2, (await ReadAsync<List<VisitDto>>(await rig.Host.GetAsync("/api/v1/visits/mine"))).Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await ExpectAsync(rig.Reception, rig, "Ghost Host", new { hostId = Guid.NewGuid() })).StatusCode);
    }

    [Fact]
    public async Task Pre_registration_is_validated()
    {
        var rig = await NewRigAsync();
        var building = await ReadAsync<LocationDto>(await PostAsync(rig.Manager, "/api/v1/locations", new { type = "Building", parentId = rig.Site.Id, code = FacilitiesScope.Code("B"), name = "Tower" }));
        foreach (var bad in new object[]
        {
            new { visitorName = "" }, new { visitorName = new string('x', 201) }, new { visitorEmail = "not-an-email" }, new { visitorPhone = new string('1', 41) }, new { purpose = new string('p', 501) },
            new { kind = "Alien" }, new { expectedAt = DateTimeOffset.UtcNow.AddDays(-2) }, new { expectedAt = DateTimeOffset.UtcNow.AddDays(400) },
            new { expectedUntil = DateTimeOffset.UtcNow.AddHours(-3) }, new { expectedUntil = DateTimeOffset.UtcNow.AddDays(30) }, new { siteId = building.Id }, new { siteId = Guid.NewGuid() },
        })
        {
            var status = (await ExpectAsync(rig.Host, rig, more: bad)).StatusCode;
            Assert.True(status == HttpStatusCode.BadRequest, $"{System.Text.Json.JsonSerializer.Serialize(bad)} gave {status}");
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Host, "/api/v1/visits", new { })).StatusCode);

        // A body the server cannot read is the caller's mistake (400), never a server fault (500).
        foreach (var broken in new[] { "{ not json", "{\"kind\": \"Alien\"}", "{\"expectedAt\": \"yesterday-ish\"}", "{\"siteId\": 12}", "[]" })
        {
            var response = await rig.Host.PostAsync("/api/v1/visits", new StringContent(broken, System.Text.Encoding.UTF8, "application/json"));
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{broken} gave {response.StatusCode}");
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        }
    }

    [Fact]
    public async Task A_visitor_opens_their_pass_without_signing_in_and_sees_only_what_is_theirs()
    {
        var rig = await NewRigAsync();
        var created = await ExpectedAsync(rig.Host, rig, "Layla Hassan", new { purpose = "Confidential contract talks", visitorEmail = "layla@acme.test" });
        var code = CodeOf(created);
        var anonymous = _api.Factory.CreateClient();

        var response = await anonymous.GetAsync($"/api/v1/visits/pass/{code}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        var pass = await response.Content.ReadFromJsonAsync<PassDto>(Json);
        Assert.Equal(("Layla Hassan", "HQ"), (pass!.VisitorName, pass.SiteName));
        Assert.DoesNotContain("Confidential", text); // not why they are visiting
        Assert.DoesNotContain("layla@acme.test", text);

        var qr = await anonymous.GetAsync($"/api/v1/visits/pass/{code}/qr.svg");
        Assert.Equal(HttpStatusCode.OK, qr.StatusCode);
        Assert.Equal("image/svg+xml", qr.Content.Headers.ContentType!.MediaType);
        Assert.True(qr.Headers.CacheControl!.NoStore);

        foreach (var wrong in new[] { "AAAAAAAAAAAAAAAAAAAAAA", "short", new string('x', 90) })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/v1/visits/pass/{wrong}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/v1/visits/pass/{wrong}/qr.svg")).StatusCode);
        }

        // Only a hash is kept, and a new pass stops the old one.
        await using (var db = sql.Visitors(rig.Tenant))
        {
            var stored = await db.Visits.SingleAsync(v => v.Id == created.Visit.Id);
            Assert.NotEqual(code, stored.PassHash);
            Assert.DoesNotContain(code, stored.PassHash);
        }

        var colleague = await _api.ClientAsync(rig.Tenant, SystemRoles.Technician);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(colleague, $"/api/v1/visits/{created.Visit.Id}/pass", new { })).StatusCode); // not theirs
        var renewed = await ReadAsync<CreatedVisitDto>(await PostAsync(rig.Host, $"/api/v1/visits/{created.Visit.Id}/pass", new { }));
        Assert.NotEqual(code, CodeOf(renewed));
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/v1/visits/pass/{code}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/v1/visits/pass/{CodeOf(renewed)}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Viewer.PostAsJsonAsync($"/api/v1/visits/{created.Visit.Id}/pass", new { }, Json)).StatusCode);
    }

    // ---------------- at the desk ----------------

    [Fact]
    public async Task Reception_finds_the_visitor_from_their_pass_checks_them_in_and_out_and_the_host_is_told()
    {
        var rig = await NewRigAsync();
        var created = await ExpectedAsync(rig.Host, rig, "Layla Hassan");

        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Host.GetAsync($"/api/v1/visits/lookup?code={CodeOf(created)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.Reception.GetAsync("/api/v1/visits/lookup?code=nonsense")).StatusCode);
        var scanned = await ReadAsync<VisitDto>(await rig.Reception.GetAsync($"/api/v1/visits/lookup?code={Uri.EscapeDataString(created.PassUrl)}")); // what a QR scanner types
        Assert.Equal(created.Visit.Id, scanned.Id);
        Assert.True(scanned.CanCheckInNow);

        Assert.Equal(HttpStatusCode.Forbidden, (await CheckInAsync(rig.Host, scanned)).StatusCode); // hosts do not run the desk
        Assert.Equal(HttpStatusCode.BadRequest, (await CheckInAsync(rig.Reception, scanned, new { badgeNumber = new string('9', 21) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Reception, $"/api/v1/visits/{scanned.Id}/check-in", new { })).StatusCode); // needs the version
        var arrived = await ReadAsync<VisitDto>(await CheckInAsync(rig.Reception, scanned, new { badgeNumber = "B-17" }));
        Assert.Equal((VisitStatus.CheckedIn, "B-17"), (arrived.Status, arrived.BadgeNumber));
        Assert.Equal(HttpStatusCode.Conflict, (await CheckInAsync(rig.Reception, arrived)).StatusCode); // already in

        var onSite = await ReadAsync<List<VisitDto>>(await rig.Reception.GetAsync($"/api/v1/visits/on-site?siteId={rig.Site.Id}"));
        Assert.Equal(arrived.Id, Assert.Single(onSite).Id);

        await DeliverAsync();
        var notices = (await ReadAsync<Page<NotificationDto>>(await rig.Host.GetAsync("/api/v1/notifications?pageSize=50"))).Items.Where(n => n.Type == "visitor.arrived").ToList();
        Assert.Single(notices);

        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Reception, $"/api/v1/visits/{arrived.Id}/check-out", new { rowVersion = scanned.RowVersion })).StatusCode); // stale version
        var left = await ReadAsync<VisitDto>(await PostAsync(rig.Reception, $"/api/v1/visits/{arrived.Id}/check-out", new { rowVersion = arrived.RowVersion }));
        Assert.Equal(VisitStatus.CheckedOut, left.Status);
        Assert.Empty(await ReadAsync<List<VisitDto>>(await rig.Reception.GetAsync("/api/v1/visits/on-site")));
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Reception, $"/api/v1/visits/{arrived.Id}/check-out", new { rowVersion = left.RowVersion })).StatusCode); // not in
    }

    [Fact]
    public async Task A_visit_is_checked_in_only_while_it_is_due_and_not_after_it_is_cancelled()
    {
        var rig = await NewRigAsync();
        var later = await ExpectedAsync(rig.Host, rig, "Next Week", at: DateTimeOffset.UtcNow.AddDays(3));
        Assert.False(later.Visit.CanCheckInNow);
        Assert.Equal(HttpStatusCode.Conflict, (await CheckInAsync(rig.Reception, later.Visit)).StatusCode);

        var today = await ExpectedAsync(rig.Host, rig, "Cancelled Visitor");
        var colleague = await _api.ClientAsync(rig.Tenant, SystemRoles.Technician);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(colleague, $"/api/v1/visits/{today.Visit.Id}/cancel", new { rowVersion = today.Visit.RowVersion })).StatusCode); // not their visitor, and no desk rights
        var cancelled = await ReadAsync<VisitDto>(await PostAsync(rig.Host, $"/api/v1/visits/{today.Visit.Id}/cancel", new { reason = "Meeting moved", rowVersion = today.Visit.RowVersion }));
        Assert.Equal((VisitStatus.Cancelled, "Meeting moved"), (cancelled.Status, cancelled.CancelReason));
        Assert.Equal(HttpStatusCode.Conflict, (await CheckInAsync(rig.Reception, cancelled)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Host, $"/api/v1/visits/{today.Visit.Id}/cancel", new { rowVersion = cancelled.RowVersion })).StatusCode);

        // Reception can cancel anyone's.
        var other = await ExpectedAsync(rig.Host, rig, "Reception Cancels");
        Assert.Equal(VisitStatus.Cancelled, (await ReadAsync<VisitDto>(await PostAsync(rig.Reception, $"/api/v1/visits/{other.Visit.Id}/cancel", new { rowVersion = other.Visit.RowVersion }))).Status);
    }

    // ---------------- watchlist ----------------

    [Fact]
    public async Task A_watchlisted_name_stops_the_desk_until_security_lets_the_visitor_in_with_a_reason()
    {
        var rig = await NewRigAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Reception, "/api/v1/visits/watchlist", new { name = "Jose Garcia", reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/visits/watchlist", new { name = "", reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/visits/watchlist", new { name = "Jose Garcia", reason = " " })).StatusCode);
        var entry = await ReadAsync<WatchlistDto>(await PostAsync(rig.Manager, "/api/v1/visits/watchlist", new { name = "Jose Garcia", reason = "Trespassed in 2025" }));
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, "/api/v1/visits/watchlist", new { name = "  JOSÉ   garcía ", reason = "again" })).StatusCode); // the same person, spelled differently
        Assert.Single(await ReadAsync<List<WatchlistDto>>(await rig.Manager.GetAsync("/api/v1/visits/watchlist")));

        var spelled = await ExpectedAsync(rig.Host, rig, "José García");
        var refused = await CheckInAsync(rig.Reception, spelled.Visit);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var problem = await refused.Content.ReadAsStringAsync();
        Assert.Contains("call security", problem);
        Assert.DoesNotContain("Trespassed", problem); // reception is not told why

        Assert.Equal(HttpStatusCode.Forbidden, (await CheckInAsync(rig.Reception, spelled.Visit, new { overrideReason = "I know him" })).StatusCode); // reception cannot override
        Assert.Equal(HttpStatusCode.Forbidden, (await CheckInAsync(rig.Manager, spelled.Visit)).StatusCode); // security needs to give a reason
        var let = await ReadAsync<VisitDto>(await CheckInAsync(rig.Manager, spelled.Visit, new { overrideReason = "Cleared by the site manager, escorted" }));
        Assert.Equal(VisitStatus.CheckedIn, let.Status);
        await using (var db = sql.Visitors(rig.Tenant))
        {
            var stored = await db.Visits.SingleAsync(v => v.Id == let.Id);
            Assert.True(stored.WatchlistMatch);
            Assert.Equal("Cleared by the site manager, escorted", stored.OverrideReason);
        }

        // Removing someone needs a reason and keeps the record; afterwards the desk lets the name through.
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, $"/api/v1/visits/watchlist/{entry.Id}/remove", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Manager, $"/api/v1/visits/watchlist/{entry.Id}/remove", new { reason = "Mistaken identity" })).StatusCode);
        Assert.Empty(await ReadAsync<List<WatchlistDto>>(await rig.Manager.GetAsync("/api/v1/visits/watchlist")));
        await using (var db = sql.Visitors(rig.Tenant)) Assert.Equal("Mistaken identity", (await db.Watchlist.SingleAsync()).RemoveReason);
        var again = await ExpectedAsync(rig.Host, rig, "Jose Garcia");
        Assert.Equal(HttpStatusCode.OK, (await CheckInAsync(rig.Reception, again.Visit)).StatusCode);
    }

    [Fact]
    public async Task Arabic_spellings_on_the_watchlist_are_caught()
    {
        var rig = await NewRigAsync();
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(rig.Manager, "/api/v1/visits/watchlist", new { name = "أحمد إبراهيم", reason = "منع" })).StatusCode);
        var visit = await ExpectedAsync(rig.Host, rig, "احمد ابراهيم");
        Assert.Equal(HttpStatusCode.Forbidden, (await CheckInAsync(rig.Reception, visit.Visit)).StatusCode);
    }

    // ---------------- induction and settings ----------------

    [Fact]
    public async Task Contractors_must_acknowledge_the_induction_when_the_rules_say_so()
    {
        var rig = await NewRigAsync();
        Assert.Equal(HttpStatusCode.OK, (await rig.Reception.GetAsync("/api/v1/visits/settings")).StatusCode); // reception reads the induction it must show
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Viewer.GetAsync("/api/v1/visits/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(rig.Reception, "/api/v1/visits/settings", new { requireInduction = true, inductionText = "Wear a helmet", retentionDays = 90 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(rig.Manager, "/api/v1/visits/settings", new { requireInduction = true, retentionDays = 90 })).StatusCode); // needs the text
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(rig.Manager, "/api/v1/visits/settings", new { retentionDays = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(rig.Manager, "/api/v1/visits/settings", new { retentionDays = 99999 })).StatusCode);
        var saved = await ReadAsync<VisitorSettingsDto>(await PutAsync(rig.Manager, "/api/v1/visits/settings", new { requireInduction = true, inductionText = "Wear a helmet. Sign in and out.", retentionDays = 90 }));
        Assert.Equal((true, 90), (saved.RequireInduction, saved.RetentionDays));
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(rig.Manager, "/api/v1/visits/settings", new { requireInduction = false, retentionDays = 90 })).StatusCode); // changing needs the version

        var contractor = await ExpectedAsync(rig.Host, rig, "Plumber Pete", new { kind = "Contractor" });
        Assert.Equal(HttpStatusCode.BadRequest, (await CheckInAsync(rig.Reception, contractor.Visit)).StatusCode);
        var arrived = await ReadAsync<VisitDto>(await CheckInAsync(rig.Reception, contractor.Visit, new { inductionAcknowledged = true }));
        Assert.NotNull(arrived.InductionAt);

        var guest = await ExpectedAsync(rig.Host, rig, "Ordinary Guest");
        Assert.Null((await ReadAsync<VisitDto>(await CheckInAsync(rig.Reception, guest.Visit))).InductionAt); // guests are not asked
    }

    // ---------------- housekeeping ----------------

    [Fact]
    public async Task Visits_never_attended_are_marked_and_old_personal_details_are_removed()
    {
        var rig = await NewRigAsync();
        var host = await UserIdAsync(rig.Host);
        var now = DateTimeOffset.UtcNow;
        Visit Make(string name, VisitStatus status, int daysAgo) => new()
        {
            TenantId = rig.Tenant, HostId = host, HostName = "Host", SiteId = rig.Site.Id, SiteName = "HQ", Kind = VisitKind.Guest, VisitorName = name, VisitorEmail = name + "@x.test", VisitorPhone = "123", Purpose = "p",
            ExpectedAt = now.AddDays(-daysAgo), Status = status, PassHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), CreatedAt = now.AddDays(-daysAgo - 1),
        };
        var missed = Make("Never Came", VisitStatus.Expected, 3);
        var upcoming = Make("Still Coming", VisitStatus.Expected, -2);
        var oldDone = Make("Old Visitor", VisitStatus.CheckedOut, 400);
        var oldCancelled = Make("Old Cancelled", VisitStatus.Cancelled, 500);
        var recentDone = Make("Recent Visitor", VisitStatus.CheckedOut, 40);
        var stillIn = Make("Never Left", VisitStatus.CheckedIn, 800);
        await using (var db = sql.Visitors(rig.Tenant))
        {
            db.Visits.AddRange(missed, upcoming, oldDone, oldCancelled, recentDone, stillIn);
            await db.SaveChangesAsync();
        }

        await using (var scope = _api.Factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<Fms.BuildingBlocks.Tenancy.TenantContext>().SetTenant(rig.Tenant);
            Assert.Equal(3, await scope.ServiceProvider.GetRequiredService<VisitHousekeeping>().RunAsync()); // one missed, two removed
        }

        await using var check = sql.Visitors(rig.Tenant);
        async Task<Visit> Get(Visit v) => await check.Visits.AsNoTracking().SingleAsync(x => x.Id == v.Id);
        Assert.Equal(VisitStatus.NoShow, (await Get(missed)).Status);
        Assert.Equal(VisitStatus.Expected, (await Get(upcoming)).Status);
        var removed = await Get(oldDone);
        Assert.Equal(("—", null, null, null), (removed.VisitorName, removed.VisitorEmail, removed.VisitorPhone, removed.Purpose));
        Assert.NotNull(removed.AnonymisedAt);
        Assert.NotNull((await Get(oldCancelled)).AnonymisedAt);
        Assert.Equal("Recent Visitor", (await Get(recentDone)).VisitorName); // still inside the retention period
        Assert.Equal("Never Left", (await Get(stillIn)).VisitorName); // someone shown as on site is never wiped
        Assert.Equal(HttpStatusCode.NotFound, (await _api.Factory.CreateClient().GetAsync($"/api/v1/visits/pass/{oldDone.PassHash[..22]}")).StatusCode);
    }

    // ---------------- tenants and the database ----------------

    [Fact]
    public async Task Another_organisation_sees_and_changes_nothing()
    {
        var rig = await NewRigAsync();
        var other = await NewRigAsync();
        var created = await ExpectedAsync(rig.Host, rig, "Layla Hassan");
        await PostAsync(rig.Manager, "/api/v1/visits/watchlist", new { name = "Bad Actor", reason = "x" });

        Assert.Equal(HttpStatusCode.NotFound, (await other.Reception.GetAsync($"/api/v1/visits/lookup?code={CodeOf(created)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CheckInAsync(other.Reception, created.Visit)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(other.Reception, $"/api/v1/visits/{created.Visit.Id}/cancel", new { rowVersion = created.Visit.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ExpectAsync(other.Host, rig)).StatusCode); // someone else's site
        Assert.Empty(await ReadAsync<List<VisitDto>>(await other.Reception.GetAsync("/api/v1/visits")));
        Assert.Empty(await ReadAsync<List<WatchlistDto>>(await other.Manager.GetAsync("/api/v1/visits/watchlist")));
        var visitor = await ExpectedAsync(other.Host, other, "Bad Actor");
        Assert.Equal(HttpStatusCode.OK, (await CheckInAsync(other.Reception, visitor.Visit)).StatusCode); // another organisation's watchlist does not apply

        await using var db = sql.Visitors(other.Tenant);
        Assert.Equal(1, await db.Visits.IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await db.Watchlist.IgnoreQueryFilters().CountAsync());
        await using var mine = sql.Visitors(rig.Tenant);
        Assert.Equal(1, await mine.Visits.CountAsync());
    }

    [Fact]
    public async Task The_database_refuses_a_visit_that_ends_before_it_starts_or_impossible_settings()
    {
        var rig = await NewRigAsync();
        await using var db = sql.Visitors(rig.Tenant);
        db.Visits.Add(new Visit
        {
            TenantId = rig.Tenant, HostId = Guid.NewGuid(), HostName = "h", SiteId = rig.Site.Id, SiteName = "s", VisitorName = "x", ExpectedAt = DateTimeOffset.UtcNow, ExpectedUntil = DateTimeOffset.UtcNow.AddHours(-1),
            PassHash = Guid.NewGuid().ToString("N"), CreatedAt = DateTimeOffset.UtcNow,
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.Settings.Add(new VisitorSettings { TenantId = rig.Tenant, RetentionDays = 5 });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
