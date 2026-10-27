using System.Net;
using System.Net.Http.Json;
using Fms.Api;
using Fms.BuildingBlocks.Results;
using Fms.Bookings.Domain;
using Fms.Bookings.Services;
using Fms.Facilities.Services;
using Fms.Platform.Notifications;
using Fms.Platform.Security;
using Fms.Platform.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Fms.IntegrationTests.ApiHarness;

namespace Fms.IntegrationTests;

[Collection(SqlCollection.Name)]
public sealed class BookingsApiTests(SqlServerFixture sql) : IDisposable
{
    private readonly ApiHarness _api = new(sql, settings: new Dictionary<string, string?> { ["Calendar:AllowFakeAdapter"] = "true" });
    public void Dispose() => _api.Dispose();

    private sealed record Rig(Guid Tenant, HttpClient Manager, HttpClient Technician, HttpClient Requester, HttpClient Viewer);

    private async Task<Rig> NewRigAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var platform = sql.App(null, platform: true))
        {
            platform.Tenants.Add(new Fms.Platform.Domain.Tenant { Id = tenant, Name = "Bkg " + tenant.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
            await platform.SaveChangesAsync();
            await SystemRoleSync.EnsureAsync(platform, tenant);
        }

        return new Rig(tenant, await _api.ClientAsync(tenant, SystemRoles.FacilityManager), await _api.ClientAsync(tenant, SystemRoles.Technician), await _api.ClientAsync(tenant, SystemRoles.Requester), await _api.ClientAsync(tenant, SystemRoles.Viewer));
    }

    /// <summary>A UTC instant on a day some days ahead, always on a weekday-agnostic resource (all days open, UTC hours).</summary>
    private static DateTimeOffset At(int daysAhead, int hour, int minute = 0) => new(DateTime.UtcNow.Date.AddDays(daysAhead).AddHours(hour).AddMinutes(minute), TimeSpan.Zero);

    private static async Task<ResourceDto> ResourceAsync(Rig rig, object? overrides = null, string name = "Boardroom")
    {
        var body = new Dictionary<string, object?>
        {
            ["kind"] = "Room", ["code"] = FacilitiesScope.Code("RM"), ["name"] = name, ["capacity"] = 8, ["timeZone"] = "UTC", ["opensAt"] = "00:00", ["closesAt"] = "24:00", ["minMinutes"] = 15, ["maxMinutes"] = 480,
            ["maxAdvanceDays"] = 90, ["bufferMinutes"] = 0,
        };
        if (overrides is not null) foreach (var p in overrides.GetType().GetProperties()) body[p.Name] = p.GetValue(overrides);
        var response = await PostAsync(rig.Manager, "/api/v1/resources", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<ResourceDto>(response);
    }

    private static Task<HttpResponseMessage> BookAsync(HttpClient client, ResourceDto resource, DateTimeOffset start, int minutes = 60, string title = "Planning", int? attendees = null, int? repeatWeeks = null) =>
        PostAsync(client, "/api/v1/bookings", new { resourceId = resource.Id, title, startsAt = start, endsAt = start.AddMinutes(minutes), attendees, repeatWeeks });

    private static async Task<BookingDto> BookedAsync(HttpClient client, ResourceDto resource, DateTimeOffset start, int minutes = 60, string title = "Planning")
    {
        var response = await BookAsync(client, resource, start, minutes, title);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<CreatedBookingsDto>(response)).Bookings.Single();
    }

    private async Task DeliverAsync()
    {
        var processor = _api.Factory.Services.GetRequiredService<Fms.Platform.Outbox.OutboxProcessor>();
        for (var i = 0; i < 20 && await processor.ProcessBatchAsync() > 0; i++) { }
    }

    private static async Task<Guid> UserIdAsync(HttpClient client) => (await client.GetFromJsonAsync<MeInfo>("/api/v1/me"))!.UserId!.Value;

    // ---------------- resources ----------------

    [Fact]
    public async Task Resources_are_set_up_by_managers_seen_by_staff_and_validated()
    {
        var rig = await NewRigAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Technician, "/api/v1/resources", new { kind = "Room", code = "X1", name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Viewer, "/api/v1/resources", new { kind = "Room", code = "X1", name = "x" })).StatusCode);
        foreach (var bad in new object[]
        {
            new { kind = "Room", code = "bad code", name = "x" }, new { kind = "Room", code = "OK1", name = "" }, new { code = "OK1", name = "x" },
            new { kind = "Room", code = "OK1", name = "x", timeZone = "Mars/Olympus" }, new { kind = "Room", code = "OK1", name = "x", opensAt = "9am" },
            new { kind = "Room", code = "OK1", name = "x", opensAt = "18:00", closesAt = "09:00" }, new { kind = "Room", code = "OK1", name = "x", minMinutes = 7 },
            new { kind = "Room", code = "OK1", name = "x", minMinutes = 60, maxMinutes = 30 }, new { kind = "Room", code = "OK1", name = "x", capacity = 0 },
            new { kind = "Room", code = "OK1", name = "x", days = Array.Empty<string>() }, new { kind = "Room", code = "OK1", name = "x", spaceId = Guid.NewGuid() },
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/resources", bad)).StatusCode);

        var room = await ResourceAsync(rig, new { days = new[] { "Monday", "Tuesday" }, requiresApproval = true, bufferMinutes = 10 });
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Tuesday], room.Days.ToArray());
        Assert.Equal(("24:00", true), (room.ClosesAt, room.RequiresApproval));
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, "/api/v1/resources", new { kind = "Room", code = room.Code.ToLowerInvariant(), name = "Again" })).StatusCode);

        Assert.Single((await rig.Requester.GetFromJsonAsync<List<ResourceDto>>("/api/v1/resources", Json))!, r => r.Id == room.Id);
        Assert.Equal(HttpStatusCode.OK, (await rig.Viewer.GetAsync($"/api/v1/resources/{room.Id}")).StatusCode);

        var updated = await ReadAsync<ResourceDto>(await PutAsync(rig.Manager, $"/api/v1/resources/{room.Id}", new { kind = "Room", code = room.Code, name = "Boardroom 2", capacity = 10, timeZone = "Asia/Riyadh", opensAt = "08:00", closesAt = "18:00", rowVersion = room.RowVersion }));
        Assert.Equal(("Boardroom 2", 10, "08:00"), (updated.Name, updated.Capacity, updated.OpensAt));
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(rig.Manager, $"/api/v1/resources/{room.Id}", new { kind = "Room", code = room.Code, name = "Stale", rowVersion = room.RowVersion })).StatusCode);

        var archived = await ReadAsync<ResourceDto>(await PostAsync(rig.Manager, $"/api/v1/resources/{room.Id}/archive", new { rowVersion = updated.RowVersion }));
        Assert.True(archived.IsArchived);
        Assert.DoesNotContain(await rig.Requester.GetFromJsonAsync<List<ResourceDto>>("/api/v1/resources", Json) ?? [], r => r.Id == room.Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await BookAsync(rig.Requester, archived, At(3, 9))).StatusCode);
    }

    // ---------------- booking ----------------

    [Fact]
    public async Task Booking_follows_the_resources_rules_and_never_double_books()
    {
        var rig = await NewRigAsync();
        var room = await ResourceAsync(rig, new { opensAt = "08:00", closesAt = "18:00", maxMinutes = 120, bufferMinutes = 15, capacity = 6 });

        Assert.Equal(HttpStatusCode.Forbidden, (await BookAsync(rig.Viewer, room, At(3, 9))).StatusCode); // viewers look, they do not book
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Requester, "/api/v1/bookings", new { resourceId = room.Id, title = "", startsAt = At(3, 9), endsAt = At(3, 10) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await BookAsync(rig.Requester, room, At(3, 7))).StatusCode); // before opening
        Assert.Equal(HttpStatusCode.BadRequest, (await BookAsync(rig.Requester, room, At(3, 17), 120)).StatusCode); // after closing
        Assert.Equal(HttpStatusCode.BadRequest, (await BookAsync(rig.Requester, room, At(3, 9), 180)).StatusCode); // too long
        Assert.Equal(HttpStatusCode.BadRequest, (await BookAsync(rig.Requester, room, At(-1, 9))).StatusCode); // the past
        Assert.Equal(HttpStatusCode.BadRequest, (await BookAsync(rig.Requester, room, At(200, 9))).StatusCode); // too far ahead
        Assert.Equal(HttpStatusCode.BadRequest, (await BookAsync(rig.Requester, room, At(3, 9, 3))).StatusCode); // not a 5 minute step
        Assert.Equal(HttpStatusCode.BadRequest, (await BookAsync(rig.Requester, room, At(3, 9), attendees: 7)).StatusCode); // more than it holds
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Requester, "/api/v1/bookings", new { resourceId = Guid.NewGuid(), title = "x", startsAt = At(3, 9), endsAt = At(3, 10) })).StatusCode);

        var first = await BookedAsync(rig.Requester, room, At(3, 9));
        Assert.Equal((BookingStatus.Confirmed, true), (first.Status, first.Mine));

        Assert.Equal(HttpStatusCode.Conflict, (await BookAsync(rig.Technician, room, At(3, 9))).StatusCode); // the same time
        Assert.Equal(HttpStatusCode.Conflict, (await BookAsync(rig.Technician, room, At(3, 9, 30), 60)).StatusCode); // overlapping
        Assert.Equal(HttpStatusCode.Conflict, (await BookAsync(rig.Technician, room, At(3, 10, 5), 30)).StatusCode); // inside the 15 minute buffer
        Assert.Equal(HttpStatusCode.Conflict, (await BookAsync(rig.Technician, room, At(3, 8, 5), 45)).StatusCode); // ends inside the buffer of the next
        Assert.Equal(HttpStatusCode.Created, (await BookAsync(rig.Technician, room, At(3, 10, 15), 30)).StatusCode); // after the buffer
        Assert.Equal(HttpStatusCode.Created, (await BookAsync(rig.Technician, room, At(4, 9))).StatusCode); // another day

        // Availability shows what is taken, and which slots are the caller's, without saying who has the others.
        var busy = await ReadAsync<AvailabilityDto>(await rig.Technician.GetAsync($"/api/v1/bookings/availability?resourceId={room.Id}&from={Uri.EscapeDataString(At(3, 0).ToString("O"))}&to={Uri.EscapeDataString(At(4, 0).ToString("O"))}"));
        Assert.Equal([false, true], busy.Busy.Select(b => b.Mine).ToArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GuestClient().GetAsync($"/api/v1/bookings/availability?resourceId={room.Id}&from={At(3, 0):O}&to={At(4, 0):O}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await rig.Technician.GetAsync($"/api/v1/bookings/availability?resourceId={room.Id}&from={Uri.EscapeDataString(At(3, 0).ToString("O"))}&to={Uri.EscapeDataString(At(90, 0).ToString("O"))}")).StatusCode);
    }

    [Fact]
    public async Task Many_people_asking_for_the_same_slot_at_once_get_exactly_one_booking()
    {
        var rig = await NewRigAsync();
        var room = await ResourceAsync(rig);
        var people = new List<HttpClient>();
        for (var i = 0; i < 8; i++) people.Add(await _api.ClientAsync(rig.Tenant, SystemRoles.Requester));

        var results = await Task.WhenAll(people.Select(p => BookAsync(p, room, At(5, 11))));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(7, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        var all = await ReadAsync<List<BookingDto>>(await rig.Manager.GetAsync($"/api/v1/bookings?resourceId={room.Id}"));
        Assert.Single(all);
    }

    [Fact]
    public async Task A_weekly_series_is_all_or_nothing()
    {
        var rig = await NewRigAsync();
        var room = await ResourceAsync(rig);
        var blocked = await BookedAsync(rig.Technician, room, At(3 + 14, 9)); // week three of the series would clash
        Assert.NotNull(blocked);

        var refused = await BookAsync(rig.Requester, room, At(3, 9), repeatWeeks: 4);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Empty(await ReadAsync<List<BookingDto>>(await rig.Requester.GetAsync("/api/v1/bookings/mine")) is { } mine ? mine.Where(b => b.SeriesId is not null) : []);

        var series = (await ReadAsync<CreatedBookingsDto>(await BookAsync(rig.Requester, room, At(4, 9), repeatWeeks: 3))).Bookings;
        Assert.Equal(3, series.Count);
        Assert.Single(series.Select(s => s.SeriesId).Distinct());
        Assert.Equal([0, 7, 14], series.Select(s => (s.StartsAt - series[0].StartsAt).Days).ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await BookAsync(rig.Requester, room, At(5, 9), repeatWeeks: 27)).StatusCode);
    }

    // ---------------- approval ----------------

    [Fact]
    public async Task A_booking_that_needs_approval_holds_the_slot_and_is_decided_by_someone_else()
    {
        var rig = await NewRigAsync();
        var hall = await ResourceAsync(rig, new { requiresApproval = true }, "Event hall");

        var mine = await BookedAsync(rig.Requester, hall, At(6, 14));
        Assert.Equal(BookingStatus.Pending, mine.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await BookAsync(rig.Technician, hall, At(6, 14))).StatusCode); // a pending request holds the slot

        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Requester, $"/api/v1/bookings/{mine.Id}/approve", new { rowVersion = mine.RowVersion })).StatusCode); // no approval right
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Technician, $"/api/v1/bookings/{mine.Id}/approve", new { rowVersion = mine.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Requester.GetAsync("/api/v1/bookings?pendingOnly=true")).StatusCode); // and cannot list everyone's

        // A manager who asks for something cannot approve it, however many rights they hold.
        var managers = await BookedAsync(rig.Manager, hall, At(7, 14));
        Assert.Equal(BookingStatus.Pending, managers.Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Manager, $"/api/v1/bookings/{managers.Id}/approve", new { rowVersion = managers.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Manager, $"/api/v1/bookings/{managers.Id}/reject", new { note = "no", rowVersion = managers.RowVersion })).StatusCode);

        // Someone else with approval rights decides.
        var other = await _api.ClientAsync(rig.Tenant, SystemRoles.FacilityManager);
        var pending = await ReadAsync<List<BookingDto>>(await other.GetAsync("/api/v1/bookings?pendingOnly=true"));
        Assert.Equal(2, pending.Count);
        var approved = await ReadAsync<BookingDto>(await PostAsync(other, $"/api/v1/bookings/{mine.Id}/approve", new { rowVersion = mine.RowVersion }));
        Assert.Equal(BookingStatus.Confirmed, approved.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(other, $"/api/v1/bookings/{mine.Id}/approve", new { rowVersion = approved.RowVersion })).StatusCode); // already decided

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(other, $"/api/v1/bookings/{managers.Id}/reject", new { note = " ", rowVersion = managers.RowVersion })).StatusCode);
        var rejected = await ReadAsync<BookingDto>(await PostAsync(other, $"/api/v1/bookings/{managers.Id}/reject", new { note = "Closed for cleaning", rowVersion = managers.RowVersion }));
        Assert.Equal((BookingStatus.Rejected, "Closed for cleaning"), (rejected.Status, rejected.DecisionNote));
        Assert.Equal(HttpStatusCode.Created, (await BookAsync(rig.Technician, hall, At(7, 14))).StatusCode); // a rejected request frees the slot

        // Notices: approvers (not the person asking) hear of a request; the organizer hears of the decision.
        await DeliverAsync();
        async Task<string[]> Types(HttpClient c) => (await ReadAsync<Page<NotificationDto>>(await c.GetAsync("/api/v1/notifications?pageSize=100"))).Items.Select(n => n.Type).Where(t => t.StartsWith("booking.")).ToArray();
        Assert.Contains("booking.pending", await Types(other));
        Assert.DoesNotContain("booking.pending", await Types(rig.Requester));
        Assert.Equal(["booking.approved"], await Types(rig.Requester));
        Assert.Contains("booking.rejected", await Types(rig.Manager));
    }

    // ---------------- cancel, check in, no-show ----------------

    [Fact]
    public async Task Cancelling_is_for_the_organizer_or_a_manager_with_a_reason_and_frees_the_slot()
    {
        var rig = await NewRigAsync();
        var room = await ResourceAsync(rig);
        var b = await BookedAsync(rig.Requester, room, At(8, 9));

        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(rig.Technician, $"/api/v1/bookings/{b.Id}/cancel", new { reason = "x", rowVersion = b.RowVersion })).StatusCode); // not theirs, and they cannot even see it
        Assert.Equal(HttpStatusCode.NotFound, (await rig.Technician.GetAsync($"/api/v1/bookings/{b.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, $"/api/v1/bookings/{b.Id}/cancel", new { rowVersion = b.RowVersion })).StatusCode); // a manager must say why
        var cancelled = await ReadAsync<BookingDto>(await PostAsync(rig.Manager, $"/api/v1/bookings/{b.Id}/cancel", new { reason = "Room needed for the audit", rowVersion = b.RowVersion }));
        Assert.Equal((BookingStatus.Cancelled, "Room needed for the audit"), (cancelled.Status, cancelled.CancelReason));
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, $"/api/v1/bookings/{b.Id}/cancel", new { reason = "again", rowVersion = cancelled.RowVersion })).StatusCode);

        var mine = await BookedAsync(rig.Requester, room, At(8, 9)); // the slot is free again
        var own = await ReadAsync<BookingDto>(await PostAsync(rig.Requester, $"/api/v1/bookings/{mine.Id}/cancel", new { rowVersion = mine.RowVersion })); // the organizer needs no reason
        Assert.Equal(BookingStatus.Cancelled, own.Status);
        await DeliverAsync();
        Assert.Contains((await ReadAsync<Page<NotificationDto>>(await rig.Requester.GetAsync("/api/v1/notifications"))).Items, n => n.Type == "booking.cancelled");

        var listed = await ReadAsync<List<BookingDto>>(await rig.Requester.GetAsync($"/api/v1/bookings/mine?status=Cancelled"));
        Assert.Equal(2, listed.Count);
    }

    [Fact]
    public async Task Checking_in_opens_just_before_the_start_and_is_for_the_organizer_only()
    {
        var rig = await NewRigAsync();
        var desk = await ResourceAsync(rig, new { kind = "Desk", checkInRequired = true, checkInGraceMinutes = 15, capacity = 1 }, "Hot desk 4");

        var later = await BookedAsync(rig.Requester, desk, At(3, 9));
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Requester, $"/api/v1/bookings/{later.Id}/check-in", new { rowVersion = later.RowVersion })).StatusCode); // far too early

        var now = DateTimeOffset.UtcNow;
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute / 5 * 5, 0, TimeSpan.Zero); // the current five-minute mark: just started
        var soon = await BookedAsync(rig.Technician, desk, start.AddHours(-0), 60);
        Assert.True(soon.CanCheckInNow);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(rig.Requester, $"/api/v1/bookings/{soon.Id}/check-in", new { rowVersion = soon.RowVersion })).StatusCode);
        var arrived = await ReadAsync<BookingDto>(await PostAsync(rig.Technician, $"/api/v1/bookings/{soon.Id}/check-in", new { rowVersion = soon.RowVersion }));
        Assert.Equal((BookingStatus.CheckedIn, true), (arrived.Status, arrived.CheckedInAt is not null));
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Technician, $"/api/v1/bookings/{soon.Id}/check-in", new { rowVersion = arrived.RowVersion })).StatusCode);
    }

    [Fact]
    public async Task A_booking_that_needed_a_check_in_and_did_not_get_one_is_freed()
    {
        var rig = await NewRigAsync();
        var needs = await ResourceAsync(rig, new { checkInRequired = true, checkInGraceMinutes = 15 }, "Needs check-in");
        var doesnt = await ResourceAsync(rig, name: "Does not");
        var hourAgo = DateTimeOffset.UtcNow.AddHours(-1);
        var organizer = await UserIdAsync(rig.Requester);
        Booking Make(ResourceDto r, BookingStatus status) => new() { TenantId = rig.Tenant, ResourceId = r.Id, OrganizerId = organizer, Title = "Missed", StartsAt = hourAgo, EndsAt = hourAgo.AddHours(2), Status = status, CreatedAt = hourAgo.AddDays(-1) };
        var missed = Make(needs, BookingStatus.Confirmed);
        var arrived = Make(needs, BookingStatus.CheckedIn);
        var notRequired = Make(doesnt, BookingStatus.Confirmed);
        await using (var db = sql.Bookings(rig.Tenant))
        {
            db.Bookings.AddRange(missed, arrived, notRequired);
            await db.SaveChangesAsync();
        }

        await using (var scope = _api.Factory.Services.CreateAsyncScope())
        {
            var monitor = scope.ServiceProvider.GetRequiredService<BookingMonitor>();
            Assert.True(await monitor.RunOnceAsync() >= 1);
        }

        await using var check = sql.Bookings(rig.Tenant);
        Assert.Equal(BookingStatus.NoShow, (await check.Bookings.SingleAsync(b => b.Id == missed.Id)).Status);
        Assert.Equal(BookingStatus.CheckedIn, (await check.Bookings.SingleAsync(b => b.Id == arrived.Id)).Status);
        Assert.Equal(BookingStatus.Confirmed, (await check.Bookings.SingleAsync(b => b.Id == notRequired.Id)).Status);
    }

    // ---------------- calendar feed and sync ----------------

    [Fact]
    public async Task A_calendar_app_can_follow_ones_own_bookings_through_a_secret_address()
    {
        var rig = await NewRigAsync();
        var other = await NewRigAsync();
        var room = await ResourceAsync(rig);
        var theirs = await ResourceAsync(other);
        await BookedAsync(rig.Requester, room, At(3, 9), title: "Kick-off; part 1, budget");
        await BookedAsync(rig.Technician, room, At(3, 12), title: "Somebody else's meeting");
        await BookedAsync(other.Requester, theirs, At(3, 9), title: "Other organisation");

        Assert.False((await ReadAsync<CalendarFeedStatusDto>(await rig.Requester.GetAsync("/api/v1/bookings/calendar-feed"))).Exists);
        var feed = await ReadAsync<CalendarFeedDto>(await PostAsync(rig.Requester, "/api/v1/bookings/calendar-feed", new { }));
        Assert.StartsWith("/api/v1/calendar/", feed.Path);
        Assert.True((await ReadAsync<CalendarFeedStatusDto>(await rig.Requester.GetAsync("/api/v1/bookings/calendar-feed"))).Exists);

        var anonymous = _api.Factory.CreateClient();
        var response = await anonymous.GetAsync(feed.Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/calendar", response.Content.Headers.ContentType!.ToString());
        Assert.True(response.Headers.CacheControl!.NoStore && response.Headers.CacheControl.Private);
        var ics = await response.Content.ReadAsStringAsync();
        Assert.Contains("SUMMARY:Kick-off\\; part 1\\, budget", ics);
        Assert.DoesNotContain("Somebody else's meeting", ics);
        Assert.DoesNotContain("Other organisation", ics);

        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/v1/calendar/not-a-real-address-at-all-123")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/v1/calendar/x")).StatusCode);

        // A new address stops the old one; revoking stops them all.
        var next = await ReadAsync<CalendarFeedDto>(await PostAsync(rig.Requester, "/api/v1/bookings/calendar-feed", new { }));
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(feed.Path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(next.Path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rig.Requester.DeleteAsync("/api/v1/bookings/calendar-feed")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(next.Path)).StatusCode);

        // Only the hash is kept.
        await using var db = sql.Bookings(rig.Tenant);
        Assert.Empty(await db.FeedTokens.Where(t => feed.Path.EndsWith(t.TokenHash) || next.Path.EndsWith(t.TokenHash)).ToListAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GuestClient().PostAsync("/api/v1/bookings/calendar-feed", null)).StatusCode);
    }

    [Fact]
    public async Task Bookings_reach_a_connected_calendar_and_leave_it_when_cancelled()
    {
        var rig = await NewRigAsync();
        var room = await ResourceAsync(rig);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Requester.GetAsync("/api/v1/bookings/calendar-sync")).StatusCode);
        var before = await ReadAsync<CalendarSyncDto>(await rig.Manager.GetAsync("/api/v1/bookings/calendar-sync"));
        Assert.False(before.Enabled);
        Assert.Equal(["fake"], before.AvailableAdapters.ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(rig.Manager, "/api/v1/bookings/calendar-sync", new { enabled = true, adapter = "outlook" })).StatusCode);

        var early = await BookedAsync(rig.Requester, room, At(3, 8), title: "Before connection");
        await DeliverAsync();
        await using (var d = sql.Bookings(rig.Tenant)) Assert.Null(await d.CalendarLinks.SingleOrDefaultAsync(l => l.BookingId == early.Id)); // not connected: nothing sent

        var saved = await ReadAsync<CalendarSyncDto>(await PutAsync(rig.Manager, "/api/v1/bookings/calendar-sync", new { enabled = true, adapter = "fake" }));
        Assert.True(saved.Enabled);

        var b = await BookedAsync(rig.Requester, room, At(3, 10), title: "Design review");
        await DeliverAsync();
        string? reference;
        await using (var d = sql.Bookings(rig.Tenant)) reference = (await d.CalendarLinks.SingleAsync(l => l.BookingId == b.Id)).ExternalEventId;
        Assert.StartsWith("FAKE-EVT-", reference);
        Assert.Equal("Design review", FakeCalendarAdapter.Events[reference!].Title);
        Assert.False(FakeCalendarAdapter.Events[reference!].Tentative);

        // Sending the same booking again updates the same event instead of making another.
        var processor = _api.Factory.Services.GetRequiredService<Fms.Platform.Outbox.OutboxProcessor>();
        await using (var scope = _api.Factory.Services.CreateAsyncScope())
        {
            var adapter = scope.ServiceProvider.GetServices<Fms.BuildingBlocks.Integration.ICalendarAdapter>().Single();
            Assert.Equal(reference, await adapter.UpsertAsync(FakeCalendarAdapter.Events[reference!], reference));
        }

        Assert.NotNull(processor);
        await PostAsync(rig.Requester, $"/api/v1/bookings/{b.Id}/cancel", new { rowVersion = b.RowVersion });
        await DeliverAsync();
        Assert.False(FakeCalendarAdapter.Events.ContainsKey(reference!));
        await using (var d = sql.Bookings(rig.Tenant)) Assert.Null(await d.CalendarLinks.SingleOrDefaultAsync(l => l.BookingId == b.Id));
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Requester, $"/api/v1/bookings/{b.Id}/cancel", new { rowVersion = b.RowVersion })).StatusCode); // already cancelled: the first cancel was not refused as stale
    }

    [Fact]
    public async Task The_in_memory_calendar_cannot_be_chosen_unless_the_server_allows_it()
    {
        using var strict = new ApiHarness(sql);
        var tenant = Guid.NewGuid();
        await using (var platform = sql.App(null, platform: true))
        {
            platform.Tenants.Add(new Fms.Platform.Domain.Tenant { Id = tenant, Name = "Strict", CreatedAt = DateTimeOffset.UtcNow });
            await platform.SaveChangesAsync();
            await SystemRoleSync.EnsureAsync(platform, tenant);
        }

        var manager = await strict.ClientAsync(tenant, SystemRoles.FacilityManager);
        Assert.Empty((await ReadAsync<CalendarSyncDto>(await manager.GetAsync("/api/v1/bookings/calendar-sync"))).AvailableAdapters);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(manager, "/api/v1/bookings/calendar-sync", new { enabled = true, adapter = "fake" })).StatusCode);
    }

    // ---------------- tenants and the database ----------------

    [Fact]
    public async Task Another_organisation_sees_nothing_of_it()
    {
        var rig = await NewRigAsync();
        var other = await NewRigAsync();
        var room = await ResourceAsync(rig);
        var b = await BookedAsync(rig.Requester, room, At(3, 9));

        Assert.Equal(HttpStatusCode.NotFound, (await other.Manager.GetAsync($"/api/v1/resources/{room.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Manager.GetAsync($"/api/v1/bookings/{b.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Manager.GetAsync($"/api/v1/bookings/availability?resourceId={room.Id}&from={Uri.EscapeDataString(At(3, 0).ToString("O"))}&to={Uri.EscapeDataString(At(4, 0).ToString("O"))}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await BookAsync(other.Manager, room, At(3, 9))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(other.Manager, $"/api/v1/bookings/{b.Id}/cancel", new { reason = "x", rowVersion = b.RowVersion })).StatusCode);
        Assert.Empty(await ReadAsync<List<BookingDto>>(await other.Manager.GetAsync("/api/v1/bookings")));

        await using var db = sql.Bookings(other.Tenant);
        Assert.Equal(0, await db.Bookings.IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await db.Resources.IgnoreQueryFilters().CountAsync());
        await using var mine = sql.Bookings(rig.Tenant);
        Assert.Equal(1, await mine.Bookings.CountAsync());
    }

    [Fact]
    public async Task The_database_refuses_a_booking_that_ends_before_it_starts_and_an_impossible_resource()
    {
        var rig = await NewRigAsync();
        var room = await ResourceAsync(rig);
        await using var db = sql.Bookings(rig.Tenant);
        db.Bookings.Add(new Booking { TenantId = rig.Tenant, ResourceId = room.Id, OrganizerId = Guid.NewGuid(), Title = "x", StartsAt = At(3, 10), EndsAt = At(3, 9), Status = BookingStatus.Confirmed, CreatedAt = DateTimeOffset.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.Resources.Add(new BookableResource { TenantId = rig.Tenant, Kind = ResourceKind.Room, Code = "BAD", Name = "x", MinMinutes = 60, MaxMinutes = 30 });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
