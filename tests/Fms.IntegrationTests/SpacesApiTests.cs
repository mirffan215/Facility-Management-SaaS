using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Fms.Api;
using Fms.Facilities.Services;
using Fms.Platform.Security;
using Fms.Platform.Services;
using Fms.Spaces.Domain;
using Fms.Spaces.Services;
using Microsoft.EntityFrameworkCore;
using static Fms.IntegrationTests.ApiHarness;

namespace Fms.IntegrationTests;

[Collection(SqlCollection.Name)]
public sealed class SpacesApiTests(SqlServerFixture sql) : IDisposable
{
    private readonly ApiHarness _api = new(sql, settings: new Dictionary<string, string?> { ["Storage:Provider"] = "InMemory" });
    public void Dispose() => _api.Dispose();

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 1, 0, 0, 0, 1];
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private sealed record Rig(Guid Tenant, HttpClient Manager, HttpClient Technician, HttpClient Viewer, HttpClient Requester, LocationDto Site, LocationDto Building, LocationDto Floor);

    private async Task<Rig> NewRigAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var platform = sql.App(null, platform: true))
        {
            platform.Tenants.Add(new Fms.Platform.Domain.Tenant { Id = tenant, Name = "Spc " + tenant.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
            await platform.SaveChangesAsync();
            await SystemRoleSync.EnsureAsync(platform, tenant);
        }

        var manager = await _api.ClientAsync(tenant, SystemRoles.FacilityManager);
        var site = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", new { type = "Site", code = FacilitiesScope.Code("S"), name = "HQ" }));
        var building = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", new { type = "Building", parentId = site.Id, code = FacilitiesScope.Code("B"), name = "Tower" }));
        var floor = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", new { type = "Floor", parentId = building.Id, code = FacilitiesScope.Code("F"), name = "Floor 3" }));
        return new Rig(tenant, manager, await _api.ClientAsync(tenant, SystemRoles.Technician), await _api.ClientAsync(tenant, SystemRoles.Viewer), await _api.ClientAsync(tenant, SystemRoles.Requester), site, building, floor);
    }

    private static async Task<LocationDto> SpaceAsync(Rig rig, LocationDto? floor = null, string name = "Open plan", int? capacity = 4, SpaceKind kind = SpaceKind.Office)
    {
        var space = await ReadAsync<LocationDto>(await PostAsync(rig.Manager, "/api/v1/locations", new { type = "Space", parentId = (floor ?? rig.Floor).Id, code = FacilitiesScope.Code("R"), name }));
        if (capacity is { } c) Assert.Equal(HttpStatusCode.OK, (await PutAsync(rig.Manager, $"/api/v1/spaces/{space.Id}", new { kind, capacity = c })).StatusCode);
        return space;
    }

    private static async Task<Guid> UserIdAsync(HttpClient client) => (await client.GetFromJsonAsync<MeInfo>("/api/v1/me"))!.UserId!.Value;

    private static Task<HttpResponseMessage> AllocateAsync(Rig rig, Guid spaceId, object occupant, DateOnly? start = null, DateOnly? end = null) =>
        PostAsync(rig.Manager, "/api/v1/allocations", new Dictionary<string, object?>(occupant.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(occupant))) { ["spaceId"] = spaceId, ["startOn"] = start ?? Today, ["endOn"] = end });

    private static object Person(Guid userId) => new { kind = "Person", userId };

    private static object Team(string name, int headcount) => new { kind = "Team", teamName = name, headcount };

    private static MultipartFormDataContent File(byte[] bytes, string name = "plan.png")
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", name);
        return form;
    }

    // ---------------- profiles ----------------

    [Fact]
    public async Task Permissions_validation_and_the_one_profile_per_space_rule()
    {
        var rig = await NewRigAsync();
        var space = await SpaceAsync(rig, capacity: null);
        Assert.Equal(HttpStatusCode.OK, (await rig.Viewer.GetAsync($"/api/v1/spaces/{space.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(rig.Viewer, $"/api/v1/spaces/{space.Id}", new { kind = "Office", capacity = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Technician.GetAsync($"/api/v1/spaces/{space.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Requester.GetAsync("/api/v1/allocations")).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(rig.Manager, $"/api/v1/spaces/{space.Id}", new { capacity = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(rig.Manager, $"/api/v1/spaces/{space.Id}", new { kind = "Office", capacity = -1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(rig.Manager, $"/api/v1/spaces/{space.Id}", new { kind = "Office", capacity = 100001 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(rig.Manager, $"/api/v1/spaces/{rig.Floor.Id}", new { kind = "Office", capacity = 3 })).StatusCode); // a floor is not a space

        var saved = await ReadAsync<SpaceDto>(await PutAsync(rig.Manager, $"/api/v1/spaces/{space.Id}", new { kind = "Meeting", capacity = 6, notes = "Glass room" }));
        Assert.Equal((SpaceKind.Meeting, 6), (saved.Kind, saved.Capacity));
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(rig.Manager, $"/api/v1/spaces/{space.Id}", new { kind = "Meeting", capacity = 7 })).StatusCode); // a second save needs the version
        var again = await ReadAsync<SpaceDto>(await PutAsync(rig.Manager, $"/api/v1/spaces/{space.Id}", new { kind = "Office", capacity = 8, rowVersion = saved.RowVersion }));
        Assert.Equal(8, again.Capacity);
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(rig.Manager, $"/api/v1/spaces/{space.Id}", new { kind = "Office", capacity = 9, rowVersion = saved.RowVersion })).StatusCode); // stale
    }

    // ---------------- allocation ----------------

    [Fact]
    public async Task Allocation_never_puts_more_people_in_a_space_than_it_holds()
    {
        var rig = await NewRigAsync();
        var space = await SpaceAsync(rig, capacity: 3);
        var p1 = await UserIdAsync(await _api.ClientAsync(rig.Tenant, SystemRoles.Viewer));
        var p2 = await UserIdAsync(await _api.ClientAsync(rig.Tenant, SystemRoles.Viewer));

        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Viewer, "/api/v1/allocations", new { spaceId = space.Id, kind = "Team", teamName = "X", headcount = 1, startOn = Today })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AllocateAsync(rig, space.Id, new { kind = "Team", teamName = "", headcount = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AllocateAsync(rig, space.Id, Team("Ops", 0))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AllocateAsync(rig, space.Id, new { kind = "Person" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AllocateAsync(rig, space.Id, Person(Guid.NewGuid()))).StatusCode); // unknown person
        Assert.Equal(HttpStatusCode.BadRequest, (await AllocateAsync(rig, space.Id, Team("Ops", 1), Today, Today.AddDays(-1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AllocateAsync(rig, space.Id, Team("Ops", 1), Today.AddDays(-9), Today.AddDays(-2))).StatusCode); // already over

        var team = await ReadAsync<AllocationDto>(await AllocateAsync(rig, space.Id, Team("Finance", 2)));
        Assert.Equal((AllocationState.Current, 2), (team.State, team.Headcount));
        var person = await ReadAsync<AllocationDto>(await AllocateAsync(rig, space.Id, Person(p1)));
        Assert.Equal(AllocationState.Current, person.State);

        var full = await AllocateAsync(rig, space.Id, Person(p2));
        Assert.Equal(HttpStatusCode.Conflict, full.StatusCode); // 2 + 1 = 3 of 3
        Assert.Equal(HttpStatusCode.Conflict, (await AllocateAsync(rig, space.Id, Person(p1), Today.AddDays(40))).StatusCode); // the same person cannot hold two places
        Assert.Equal(HttpStatusCode.Conflict, (await AllocateAsync(rig, space.Id, Team("Late", 2), Today.AddDays(5), Today.AddDays(9))).StatusCode); // the team still fits only if someone leaves

        // Releasing the team frees its places from today; the team is kept as history.
        var released = await ReadAsync<AllocationDto>(await PostAsync(rig.Manager, $"/api/v1/allocations/{team.Id}/release", new { reason = "Moved floors", rowVersion = team.RowVersion, from = Today.AddDays(1) }));
        Assert.Equal(Today, released.EndOn);
        Assert.Equal(HttpStatusCode.Created, (await AllocateAsync(rig, space.Id, Person(p2), Today.AddDays(1))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AllocateAsync(rig, space.Id, Team("Late", 1), Today.AddDays(1))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await AllocateAsync(rig, space.Id, Person(await UserIdAsync(rig.Technician)), Today.AddDays(1))).StatusCode); // full again from tomorrow

        // A place cannot be given to someone inactive or from a space with no capacity.
        var blank = await SpaceAsync(rig, name: "Store", capacity: null);
        Assert.Equal(HttpStatusCode.Conflict, (await AllocateAsync(rig, blank.Id, Team("Ops", 1))).StatusCode);
    }

    [Fact]
    public async Task Releasing_needs_a_reason_and_an_allocation_that_has_not_started_is_cancelled()
    {
        var rig = await NewRigAsync();
        var space = await SpaceAsync(rig, capacity: 5);
        var future = await ReadAsync<AllocationDto>(await AllocateAsync(rig, space.Id, Team("Projects", 5), Today.AddDays(10)));
        Assert.Equal(AllocationState.Upcoming, future.State);

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, $"/api/v1/allocations/{future.Id}/release", new { reason = " ", rowVersion = future.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Viewer, $"/api/v1/allocations/{future.Id}/release", new { reason = "x", rowVersion = future.RowVersion })).StatusCode);
        var cancelled = await ReadAsync<AllocationDto>(await PostAsync(rig.Manager, $"/api/v1/allocations/{future.Id}/release", new { reason = "Project cancelled", rowVersion = future.RowVersion }));
        Assert.Equal((AllocationState.Cancelled, "Project cancelled"), (cancelled.State, cancelled.EndReason));
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, $"/api/v1/allocations/{future.Id}/release", new { reason = "again", rowVersion = cancelled.RowVersion })).StatusCode);

        // Cancelled allocations free the space and stay listed only when asked for.
        Assert.Equal(HttpStatusCode.Created, (await AllocateAsync(rig, space.Id, Team("Other", 5), Today.AddDays(10))).StatusCode);
        var history = await rig.Manager.GetFromJsonAsync<List<AllocationDto>>($"/api/v1/allocations?spaceId={space.Id}&state=Cancelled", Json);
        Assert.Single(history!);
        var ongoing = await rig.Manager.GetFromJsonAsync<List<AllocationDto>>($"/api/v1/allocations?spaceId={space.Id}", Json);
        Assert.Single(ongoing!);
    }

    [Fact]
    public async Task Capacity_cannot_drop_below_the_people_already_placed()
    {
        var rig = await NewRigAsync();
        var space = await SpaceAsync(rig, capacity: 5);
        Assert.Equal(HttpStatusCode.Created, (await AllocateAsync(rig, space.Id, Team("Sales", 4))).StatusCode);
        var current = await ReadAsync<SpaceDto>(await rig.Manager.GetAsync($"/api/v1/spaces/{space.Id}"));
        Assert.Equal(4, current.AllocatedToday);
        var refused = await PutAsync(rig.Manager, $"/api/v1/spaces/{space.Id}", new { kind = "Office", capacity = 3, rowVersion = current.RowVersion });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(rig.Manager, $"/api/v1/spaces/{space.Id}", new { kind = "Office", capacity = 4, rowVersion = current.RowVersion })).StatusCode);
    }

    [Fact]
    public async Task Parallel_requests_for_the_last_places_fill_the_space_exactly()
    {
        var rig = await NewRigAsync();
        var space = await SpaceAsync(rig, capacity: 3);
        var people = new List<Guid>();
        for (var i = 0; i < 8; i++) people.Add(await UserIdAsync(await _api.ClientAsync(rig.Tenant, SystemRoles.Viewer)));

        var results = await Task.WhenAll(people.Select(p => AllocateAsync(rig, space.Id, Person(p))));
        Assert.Equal(3, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(3, (await ReadAsync<SpaceDto>(await rig.Manager.GetAsync($"/api/v1/spaces/{space.Id}"))).AllocatedToday);
    }

    // ---------------- plans and pins ----------------

    [Fact]
    public async Task A_floor_plan_is_an_image_with_pins_that_must_be_on_that_floor()
    {
        var rig = await NewRigAsync();
        var other = await ReadAsync<LocationDto>(await PostAsync(rig.Manager, "/api/v1/locations", new { type = "Floor", parentId = rig.Building.Id, code = FacilitiesScope.Code("F"), name = "Floor 4" }));
        var onFloor = await SpaceAsync(rig, name: "Meeting A", kind: SpaceKind.Meeting);
        var offFloor = await SpaceAsync(rig, other, "Elsewhere");

        // No plan yet: nothing to pin on.
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(rig.Manager, $"/api/v1/spaces/floors/{rig.Floor.Id}/pins", new { pins = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Viewer.PostAsync($"/api/v1/spaces/floors/{rig.Floor.Id}/plan", File(Png))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.Manager.PostAsync($"/api/v1/spaces/floors/{onFloor.Id}/plan", File(Png))).StatusCode); // a space is not a floor
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await rig.Manager.PostAsync($"/api/v1/spaces/floors/{rig.Floor.Id}/plan", File("%PDF-1.7\n%%EOF"u8.ToArray(), "plan.pdf"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await rig.Manager.PostAsync($"/api/v1/spaces/floors/{rig.Floor.Id}/plan", File("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"u8.ToArray(), "plan.svg"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await rig.Manager.PostAsync($"/api/v1/spaces/floors/{rig.Floor.Id}/plan", File([]))).StatusCode);

        var first = await ReadAsync<PlanDto>(await rig.Manager.PostAsync($"/api/v1/spaces/floors/{rig.Floor.Id}/plan", File(Png)));
        Assert.Equal("image/png", first.ContentType);
        var download = await rig.Viewer.GetAsync($"/api/v1/spaces/plans/{first.Id}/image");
        Assert.Equal(Png, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("default-src 'none'; sandbox", download.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Technician.GetAsync($"/api/v1/spaces/plans/{first.Id}/image")).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(rig.Manager, $"/api/v1/spaces/floors/{rig.Floor.Id}/pins", new { pins = new[] { new { kind = "Space", targetId = onFloor.Id, x = 1.2, y = 0.5 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(rig.Manager, $"/api/v1/spaces/floors/{rig.Floor.Id}/pins", new { pins = new[] { new { kind = "Space", targetId = offFloor.Id, x = 0.1, y = 0.5 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(rig.Manager, $"/api/v1/spaces/floors/{rig.Floor.Id}/pins", new { pins = new[] { new { kind = "Space", targetId = onFloor.Id, x = 0.1, y = 0.1 }, new { kind = "Space", targetId = onFloor.Id, x = 0.2, y = 0.2 } } })).StatusCode);

        var view = await ReadAsync<FloorViewDto>(await PutAsync(rig.Manager, $"/api/v1/spaces/floors/{rig.Floor.Id}/pins", new { pins = new[] { new { kind = "Space", targetId = onFloor.Id, x = 0.25, y = 0.75 } } }));
        var pin = Assert.Single(view.Pins);
        Assert.Equal((0.25, 0.75, "Meeting A"), (pin.X, pin.Y, pin.Name));
        Assert.Equal(onFloor.Id, Assert.Single(view.Spaces).LocationId);

        // A newer drawing replaces the image but keeps the pins; the old image stays downloadable.
        var second = await ReadAsync<PlanDto>(await rig.Manager.PostAsync($"/api/v1/spaces/floors/{rig.Floor.Id}/plan", File(Png.Append((byte)1).ToArray(), "v2.png")));
        var after = await ReadAsync<FloorViewDto>(await rig.Viewer.GetAsync($"/api/v1/spaces/floors/{rig.Floor.Id}"));
        Assert.Equal((second.Id, 1), (after.Plan!.Id, after.Pins.Count));
        Assert.Equal(HttpStatusCode.OK, (await rig.Viewer.GetAsync($"/api/v1/spaces/plans/{first.Id}/image")).StatusCode);

        // Pins cleared by an empty list.
        Assert.Empty((await ReadAsync<FloorViewDto>(await PutAsync(rig.Manager, $"/api/v1/spaces/floors/{rig.Floor.Id}/pins", new { pins = Array.Empty<object>() }))).Pins);
    }

    [Fact]
    public async Task An_asset_can_be_pinned_only_on_the_floor_it_is_on()
    {
        var rig = await NewRigAsync();
        var room = await SpaceAsync(rig, capacity: null);
        var elsewhere = await ReadAsync<LocationDto>(await PostAsync(rig.Manager, "/api/v1/locations", new { type = "Floor", parentId = rig.Building.Id, code = FacilitiesScope.Code("F"), name = "Floor 9" }));
        var cls = await ReadAsync<AssetClassDto>(await PostAsync(rig.Manager, "/api/v1/asset-classes", new { code = FacilitiesScope.Code("C"), name = "Printers" }));
        async Task<AssetDto> AssetIn(Guid locationId) => await ReadAsync<AssetDto>(await PostAsync(rig.Manager, "/api/v1/assets", new { code = FacilitiesScope.Code("A"), name = "Printer", assetClassId = cls.Id, locationId, startActive = true }));
        var here = await AssetIn(room.Id);
        var there = await AssetIn(elsewhere.Id);
        Assert.Equal(HttpStatusCode.Created, (await rig.Manager.PostAsync($"/api/v1/spaces/floors/{rig.Floor.Id}/plan", File(Png))).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(rig.Manager, $"/api/v1/spaces/floors/{rig.Floor.Id}/pins", new { pins = new[] { new { kind = "Asset", targetId = there.Id, x = 0.5, y = 0.5 } } })).StatusCode);
        var view = await ReadAsync<FloorViewDto>(await PutAsync(rig.Manager, $"/api/v1/spaces/floors/{rig.Floor.Id}/pins", new { pins = new[] { new { kind = "Asset", targetId = here.Id, x = 0.5, y = 0.5 } } }));
        Assert.Equal(PinKind.Asset, Assert.Single(view.Pins).Kind);
    }

    // ---------------- occupancy ----------------

    [Fact]
    public async Task Occupancy_adds_up_by_floor_building_and_site_and_by_day()
    {
        var rig = await NewRigAsync();
        var a = await SpaceAsync(rig, name: "A", capacity: 10);
        var b = await SpaceAsync(rig, name: "B", capacity: 10);
        var none = await SpaceAsync(rig, name: "No capacity set", capacity: null);
        Assert.Equal(HttpStatusCode.Created, (await AllocateAsync(rig, a.Id, Team("Sales", 5))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AllocateAsync(rig, b.Id, Team("HR", 3), Today.AddDays(30))).StatusCode);

        foreach (var place in new[] { rig.Floor.Id, rig.Building.Id, rig.Site.Id })
        {
            var today = await ReadAsync<OccupancyDto>(await rig.Viewer.GetAsync($"/api/v1/spaces/occupancy/{place}"));
            Assert.Equal((20, 5, 25.0m, 1), (today.Capacity, today.Allocated, today.UtilisationPercent, today.SpacesWithoutCapacity));
            Assert.Equal(3, today.Rows.Count);
            var later = await ReadAsync<OccupancyDto>(await rig.Viewer.GetAsync($"/api/v1/spaces/occupancy/{place}?on={Today.AddDays(31):yyyy-MM-dd}"));
            Assert.Equal((20, 8, 40.0m), (later.Capacity, later.Allocated, later.UtilisationPercent));
        }

        var one = await ReadAsync<OccupancyDto>(await rig.Viewer.GetAsync($"/api/v1/spaces/occupancy/{a.Id}"));
        Assert.Equal((10, 5), (one.Capacity, one.Allocated));
        Assert.Equal(HttpStatusCode.NotFound, (await rig.Viewer.GetAsync($"/api/v1/spaces/occupancy/{Guid.NewGuid()}")).StatusCode);
        Assert.NotNull(none);
    }

    // ---------------- tenants ----------------

    [Fact]
    public async Task Another_organisation_cannot_see_or_change_any_of_it()
    {
        var rig = await NewRigAsync();
        var other = await NewRigAsync();
        var space = await SpaceAsync(rig, capacity: 4);
        var allocation = await ReadAsync<AllocationDto>(await AllocateAsync(rig, space.Id, Team("Ops", 2)));
        Assert.Equal(HttpStatusCode.Created, (await rig.Manager.PostAsync($"/api/v1/spaces/floors/{rig.Floor.Id}/plan", File(Png))).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await other.Manager.GetAsync($"/api/v1/spaces/{space.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Manager.GetAsync($"/api/v1/spaces/floors/{rig.Floor.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Manager.GetAsync($"/api/v1/spaces/occupancy/{rig.Site.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AllocateAsync(other, space.Id, Team("Spy", 1))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(other.Manager, $"/api/v1/allocations/{allocation.Id}/release", new { reason = "x", rowVersion = allocation.RowVersion })).StatusCode);
        Assert.Empty((await other.Manager.GetFromJsonAsync<List<AllocationDto>>("/api/v1/allocations", Json))!);

        await using var db = sql.Spaces(other.Tenant);
        Assert.Equal(0, await db.Allocations.IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await db.Plans.IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await db.Profiles.IgnoreQueryFilters().CountAsync());
        await using var mine = sql.Spaces(rig.Tenant);
        Assert.Equal(1, await mine.Allocations.CountAsync());
    }

    [Fact]
    public async Task The_database_itself_refuses_overfull_allocations_and_out_of_range_pins()
    {
        var rig = await NewRigAsync();
        var space = await SpaceAsync(rig, capacity: 2);
        await using var db = sql.Spaces(rig.Tenant);
        db.Pins.Add(new PlanPin { TenantId = rig.Tenant, FloorId = rig.Floor.Id, Kind = PinKind.Space, TargetId = space.Id, X = 1.5, Y = 0.2 });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.Allocations.Add(new Allocation { TenantId = rig.Tenant, SpaceId = space.Id, Kind = OccupantKind.Team, TeamName = null, Headcount = 1, StartOn = Today, CreatedAt = DateTimeOffset.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
