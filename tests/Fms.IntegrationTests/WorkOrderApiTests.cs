using System.Net;
using System.Net.Http.Json;
using Fms.BuildingBlocks.Results;
using Fms.Facilities.Services;
using Fms.Platform.Security;
using Fms.Work.Domain;
using Fms.Work.Services;
using Microsoft.EntityFrameworkCore;
using static Fms.IntegrationTests.ApiHarness;
using static Fms.IntegrationTests.SqlServerFixture;

namespace Fms.IntegrationTests;

[Collection(SqlCollection.Name)]
public sealed class WorkOrderApiTests(SqlServerFixture sql) : IDisposable
{
    private readonly ApiHarness _api = new(sql);
    public void Dispose() => _api.Dispose();

    private sealed record Me(Guid? UserId);

    private static async Task<Guid> UserIdAsync(HttpClient c) => (await c.GetFromJsonAsync<Me>("/api/v1/me", Json))!.UserId!.Value;

    private async Task<(LocationDto Site, AssetDto Asset)> PlaceAsync(HttpClient manager)
    {
        var site = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", new { type = "Site", code = FacilitiesScope.Code("S"), name = "Main site" }));
        var cls = await ReadAsync<AssetClassDto>(await PostAsync(manager, "/api/v1/asset-classes", new { code = FacilitiesScope.Code("C"), name = "HVAC" }));
        var asset = await ReadAsync<AssetDto>(await PostAsync(manager, "/api/v1/assets", new { code = FacilitiesScope.Code("A"), name = "Chiller 1", assetClassId = cls.Id, locationId = site.Id, startActive = true }));
        return (site, asset);
    }

    private static async Task<WorkOrderDto> Ok(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        Assert.Equal(expected, response.StatusCode);
        return await ReadAsync<WorkOrderDto>(response);
    }

    private static Task<HttpResponseMessage> Act(HttpClient c, Guid id, string action, object? body = null) => PostAsync(c, $"/api/v1/work-orders/{id}/{action}", body ?? new { });

    [Fact]
    public async Task A_request_travels_the_whole_lifecycle_with_each_role_doing_only_its_part()
    {
        var manager = await _api.ClientAsync(TenantA, SystemRoles.FacilityManager);
        var approver = await _api.ClientAsync(TenantA, SystemRoles.FacilityManager);
        var checker = await _api.ClientAsync(TenantA, SystemRoles.FacilityManager);
        var technician = await _api.ClientAsync(TenantA, SystemRoles.Technician);
        var requester = await _api.ClientAsync(TenantA, SystemRoles.Requester);
        var (site, asset) = await PlaceAsync(manager);

        // A requester reports a fault at an asset; the location is taken from the asset.
        // The requester cannot browse the register, but can find the asset by typing or by what its label encodes.
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.GetAsync("/api/v1/assets")).StatusCode);
        var found = await requester.GetFromJsonAsync<AssetChoiceDto[]>($"/api/v1/work-orders/assets?q={asset.Code}", Json);
        Assert.Equal(asset.Id, Assert.Single(found!).Id);
        Assert.Equal(asset.Id, Assert.Single((await requester.GetFromJsonAsync<AssetChoiceDto[]>($"/api/v1/work-orders/assets?tag={asset.TagCode.ToLowerInvariant()}", Json))!).Id);
        Assert.Empty((await requester.GetFromJsonAsync<AssetChoiceDto[]>("/api/v1/work-orders/assets?tag=nonsense", Json))!);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await _api.ClientAsync(TenantA, SystemRoles.Viewer)).GetAsync("/api/v1/work-orders/assets")).StatusCode);
        Assert.Empty((await (await _api.ClientAsync(TenantB, SystemRoles.Requester)).GetFromJsonAsync<AssetChoiceDto[]>($"/api/v1/work-orders/assets?q={asset.Code}", Json))!);

        var created = await Ok(PostAsync(requester, "/api/v1/work-orders", new { title = "Chiller is warm", description = "Second floor is hot", assetId = asset.Id }), HttpStatusCode.Created);
        Assert.Equal(WorkOrderStatus.Requested, created.Status);
        Assert.Equal(site.Id, created.LocationId);
        Assert.StartsWith("WO-", created.DisplayNumber);
        Assert.Contains(WorkOrderAction.Cancelled, created.Allowed);
        Assert.DoesNotContain(WorkOrderAction.Approved, created.Allowed);

        // Maker-checker: the requester cannot approve (no permission); a manager cannot approve their own request.
        Assert.Equal(HttpStatusCode.Forbidden, (await Act(requester, created.Id, "approve")).StatusCode);
        var managersOwn = await Ok(PostAsync(manager, "/api/v1/work-orders", new { title = "Manager's own", locationId = site.Id }), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.Conflict, (await Act(manager, managersOwn.Id, "approve")).StatusCode);
        Assert.Equal(WorkOrderStatus.Approved, (await Ok(Act(approver, managersOwn.Id, "approve"))).Status);

        Assert.Equal(WorkOrderStatus.Approved, (await Ok(Act(approver, created.Id, "approve"))).Status);

        // Assignment: only someone who can do work orders, and only a dispatcher assigns.
        var techId = await UserIdAsync(technician);
        var requesterId = await UserIdAsync(requester);
        Assert.Equal(HttpStatusCode.BadRequest, (await Act(approver, created.Id, "assign", new { assigneeId = requesterId })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Act(approver, created.Id, "assign", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Act(technician, created.Id, "assign", new { assigneeId = techId })).StatusCode);
        var assigned = await Ok(Act(approver, created.Id, "assign", new { assigneeId = techId }));
        Assert.Equal(WorkOrderStatus.Assigned, assigned.Status);
        Assert.Equal(techId, assigned.AssignedTo);

        // The assignee works the job; someone else's technician cannot.
        var otherTech = await _api.ClientAsync(TenantA, SystemRoles.Technician);
        Assert.Equal(HttpStatusCode.Conflict, (await Act(otherTech, created.Id, "start")).StatusCode);
        Assert.Equal(WorkOrderStatus.InProgress, (await Ok(Act(technician, created.Id, "start"))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Act(technician, created.Id, "hold", new { reason = "" })).StatusCode);
        var held = await Ok(Act(technician, created.Id, "hold", new { reason = "Waiting for a compressor" }));
        Assert.Equal(WorkOrderStatus.OnHold, held.Status);
        Assert.Equal("Waiting for a compressor", held.StatusReason);
        Assert.Equal(WorkOrderStatus.InProgress, (await Ok(Act(technician, created.Id, "resume"))).Status);

        Assert.Equal(HttpStatusCode.BadRequest, (await Act(technician, created.Id, "complete", new { notes = "done" })).StatusCode); // no closure code
        Assert.Equal(HttpStatusCode.BadRequest, (await Act(technician, created.Id, "complete", new { code = "Fixed" })).StatusCode); // no notes
        var completed = await Ok(Act(technician, created.Id, "complete", new { code = "Fixed", notes = "Replaced the compressor" }));
        Assert.Equal(WorkOrderStatus.Completed, completed.Status);
        Assert.Equal(ClosureCode.Fixed, completed.ClosureCode);

        // Segregation of duties: the technician has no verify permission, and cannot close until it is verified.
        Assert.Equal(HttpStatusCode.Forbidden, (await Act(technician, created.Id, "verify")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Act(checker, created.Id, "close")).StatusCode);
        Assert.Equal(WorkOrderStatus.Verified, (await Ok(Act(checker, created.Id, "verify"))).Status);
        var closed = await Ok(Act(checker, created.Id, "close"));
        Assert.Equal(WorkOrderStatus.Closed, closed.Status);
        Assert.NotNull(closed.ClosedAt);

        // Closed work is locked; only a manager can reopen it, with a reason.
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(manager, $"/api/v1/work-orders/{created.Id}", new { title = "x", locationId = site.Id, rowVersion = closed.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Act(technician, created.Id, "reopen", new { reason = "no" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Act(manager, created.Id, "reopen", new { })).StatusCode);
        var reopened = await Ok(Act(manager, created.Id, "reopen", new { reason = "Still warm" }));
        Assert.Equal(WorkOrderStatus.Assigned, reopened.Status);
        Assert.Null(reopened.ClosedAt);

        // The history tells the whole story, in order, and names who did what.
        var detail = await ReadAsync<WorkOrderDetailDto>(await manager.GetAsync($"/api/v1/work-orders/{created.Id}"));
        Assert.Equal(
            [WorkOrderAction.Created, WorkOrderAction.Approved, WorkOrderAction.Assigned, WorkOrderAction.Started, WorkOrderAction.Held, WorkOrderAction.Resumed,
             WorkOrderAction.Completed, WorkOrderAction.Verified, WorkOrderAction.Closed, WorkOrderAction.Reopened],
            detail.Events.Select(e => e.Action).ToArray());
        Assert.Equal(WorkOrderStatus.Verified, detail.Events.Single(e => e.Action == WorkOrderAction.Closed).FromStatus);
        Assert.Equal(techId, detail.Events.Single(e => e.Action == WorkOrderAction.Assigned).SubjectId);
    }

    [Fact]
    public async Task Requesters_see_only_their_own_requests_and_not_internal_comments()
    {
        var manager = await _api.ClientAsync(TenantA, SystemRoles.FacilityManager);
        var alice = await _api.ClientAsync(TenantA, SystemRoles.Requester);
        var bob = await _api.ClientAsync(TenantA, SystemRoles.Requester);
        var viewer = await _api.ClientAsync(TenantA, SystemRoles.Viewer);
        var (site, _) = await PlaceAsync(manager);

        var mine = await Ok(PostAsync(alice, "/api/v1/work-orders", new { title = "Alice's leak", locationId = site.Id }), HttpStatusCode.Created);

        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/v1/work-orders/{mine.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Act(bob, mine.Id, "cancel", new { reason = "mine now" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(bob, $"/api/v1/work-orders/{mine.Id}/comments", new { body = "hi" })).StatusCode);
        Assert.DoesNotContain((await ReadAsync<Page<WorkOrderDto>>(await bob.GetAsync("/api/v1/work-orders"))).Items, w => w.Id == mine.Id);
        Assert.Contains((await ReadAsync<Page<WorkOrderDto>>(await alice.GetAsync("/api/v1/work-orders"))).Items, w => w.Id == mine.Id);
        Assert.Contains((await ReadAsync<Page<WorkOrderDto>>(await viewer.GetAsync("/api/v1/work-orders"))).Items, w => w.Id == mine.Id);

        Assert.Equal(HttpStatusCode.Created, (await PostAsync(alice, $"/api/v1/work-orders/{mine.Id}/comments", new { body = "It is getting worse" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(alice, $"/api/v1/work-orders/{mine.Id}/comments", new { body = "secret", isInternal = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(manager, $"/api/v1/work-orders/{mine.Id}/comments", new { body = "Customer is exaggerating", isInternal = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(manager, $"/api/v1/work-orders/{mine.Id}/comments", new { body = "We are on it" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(manager, $"/api/v1/work-orders/{mine.Id}/comments", new { body = " " })).StatusCode);

        var seenByAlice = await ReadAsync<WorkOrderDetailDto>(await alice.GetAsync($"/api/v1/work-orders/{mine.Id}"));
        Assert.Equal(["It is getting worse", "We are on it"], seenByAlice.Comments.Select(c => c.Body).ToArray());
        var seenByManager = await ReadAsync<WorkOrderDetailDto>(await manager.GetAsync($"/api/v1/work-orders/{mine.Id}"));
        Assert.Equal(3, seenByManager.Comments.Count);

        // A requester can cancel their own request while it waits, with a reason, and then it is closed to everyone but a reopen.
        Assert.Equal(HttpStatusCode.BadRequest, (await Act(alice, mine.Id, "cancel", new { })).StatusCode);
        Assert.Equal(WorkOrderStatus.Cancelled, (await Ok(Act(alice, mine.Id, "cancel", new { reason = "Fixed itself" }))).Status);
    }

    [Fact]
    public async Task A_tenant_can_switch_off_approval_and_verification()
    {
        var tenant = Guid.NewGuid();
        await using (var platform = sql.App(null, platform: true))
        {
            platform.Tenants.Add(new Fms.Platform.Domain.Tenant { Id = tenant, Name = "Relaxed", CreatedAt = DateTimeOffset.UtcNow });
            await platform.SaveChangesAsync();
            await Fms.Platform.Services.SystemRoleSync.EnsureAsync(platform, tenant);
        }

        var manager = await _api.ClientAsync(tenant, SystemRoles.FacilityManager);
        var technician = await _api.ClientAsync(tenant, SystemRoles.Technician);
        var requester = await _api.ClientAsync(tenant, SystemRoles.Requester);
        var (site, _) = await PlaceAsync(manager);

        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(requester, "/api/v1/work-orders/settings", new { requireApproval = false, requireVerification = false })).StatusCode);
        var settings = await ReadAsync<WorkSettingsDto>(await PutAsync(manager, "/api/v1/work-orders/settings", new { requireApproval = false, requireVerification = false }));
        Assert.False(settings.RequireApproval);
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(manager, "/api/v1/work-orders/settings", new { requireApproval = true, requireVerification = true, rowVersion = "AAAAAAAAAAA=" })).StatusCode);

        var wo = await Ok(PostAsync(requester, "/api/v1/work-orders", new { title = "Light out", locationId = site.Id }), HttpStatusCode.Created);
        Assert.Equal(WorkOrderStatus.Approved, wo.Status); // no approval step

        var techId = await UserIdAsync(technician);
        await Ok(Act(manager, wo.Id, "assign", new { assigneeId = techId }));
        await Ok(Act(technician, wo.Id, "start"));
        Assert.Equal(WorkOrderStatus.Completed, (await Ok(Act(technician, wo.Id, "complete", new { code = "Fixed", notes = "Bulb" }))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Act(technician, wo.Id, "close")).StatusCode); // can finish, not sign off
        Assert.Equal(WorkOrderStatus.Closed, (await Ok(Act(manager, wo.Id, "close"))).Status); // no verification step

        // Turning it back on applies to work from then on.
        var current = await ReadAsync<WorkSettingsDto>(await manager.GetAsync("/api/v1/work-orders/settings"));
        await ReadAsync<WorkSettingsDto>(await PutAsync(manager, "/api/v1/work-orders/settings", new { requireApproval = true, requireVerification = true, rowVersion = current.RowVersion }));
        Assert.Equal(WorkOrderStatus.Requested, (await Ok(PostAsync(requester, "/api/v1/work-orders", new { title = "Again", locationId = site.Id }), HttpStatusCode.Created)).Status);
    }

    [Fact]
    public async Task Requests_are_validated()
    {
        var manager = await _api.ClientAsync(TenantA, SystemRoles.FacilityManager);
        var requester = await _api.ClientAsync(TenantA, SystemRoles.Requester);
        var viewer = await _api.ClientAsync(TenantA, SystemRoles.Viewer);
        var (site, _) = await PlaceAsync(manager);

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(requester, "/api/v1/work-orders", new { title = "", locationId = site.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(requester, "/api/v1/work-orders", new { title = "No place" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(requester, "/api/v1/work-orders", new { title = "Ghost place", locationId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(requester, "/api/v1/work-orders", new { title = "Ghost asset", assetId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(requester, "/api/v1/work-orders", new { title = "x", locationId = site.Id, categoryId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(requester, "/api/v1/work-orders", new { title = new string('x', 201), locationId = site.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(viewer, "/api/v1/work-orders", new { title = "x", locationId = site.Id })).StatusCode);

        // Emergency and planned work belong to dispatchers; an emergency is never held for approval.
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(requester, "/api/v1/work-orders", new { title = "Flood", type = "Emergency", locationId = site.Id })).StatusCode);
        var emergency = await Ok(PostAsync(manager, "/api/v1/work-orders", new { title = "Flood", type = "Emergency", priority = "Urgent", locationId = site.Id }), HttpStatusCode.Created);
        Assert.Equal(WorkOrderStatus.Approved, emergency.Status);
        Assert.Equal(WorkPriority.Urgent, emergency.Priority);

        // Editing needs the current version; a stale one is a conflict.
        var wo = await Ok(PostAsync(requester, "/api/v1/work-orders", new { title = "Dripping tap", locationId = site.Id }), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(requester, $"/api/v1/work-orders/{wo.Id}", new { title = "Dripping tap", locationId = site.Id })).StatusCode);
        var edited = await Ok(PutAsync(requester, $"/api/v1/work-orders/{wo.Id}", new { title = "Dripping tap in kitchen", locationId = site.Id, priority = "High", rowVersion = wo.RowVersion }));
        Assert.Equal("Dripping tap in kitchen", edited.Title);
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(requester, $"/api/v1/work-orders/{wo.Id}", new { title = "stale", locationId = site.Id, rowVersion = wo.RowVersion })).StatusCode);

        // Once it is approved the requester can no longer edit it.
        var approver = await _api.ClientAsync(TenantA, SystemRoles.FacilityManager);
        var approved = await Ok(Act(approver, wo.Id, "approve"));
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(requester, $"/api/v1/work-orders/{wo.Id}", new { title = "late", locationId = site.Id, rowVersion = approved.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Act(approver, (await Ok(PostAsync(requester, "/api/v1/work-orders", new { title = "Reject me", locationId = site.Id }), HttpStatusCode.Created)).Id, "reject", new { })).StatusCode);
    }

    [Fact]
    public async Task Categories_set_the_default_priority_and_are_managed_by_managers()
    {
        var manager = await _api.ClientAsync(TenantA, SystemRoles.FacilityManager);
        var requester = await _api.ClientAsync(TenantA, SystemRoles.Requester);
        var (site, _) = await PlaceAsync(manager);
        var code = FacilitiesScope.Code("CAT");

        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(requester, "/api/v1/work-orders/categories", new { code, name = "Plumbing" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(manager, "/api/v1/work-orders/categories", new { code = "bad code!", name = "x" })).StatusCode);
        var created = await manager.PostAsJsonAsync("/api/v1/work-orders/categories", new { code, name = "Plumbing", nameAr = "سباكة", defaultPriority = "High" }, Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var category = await ReadAsync<CategoryDto>(created);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(manager, "/api/v1/work-orders/categories", new { code, name = "Duplicate" })).StatusCode);

        Assert.Contains((await requester.GetFromJsonAsync<CategoryDto[]>("/api/v1/work-orders/categories", Json))!, c => c.Id == category.Id);
        var wo = await Ok(PostAsync(requester, "/api/v1/work-orders", new { title = "Burst pipe", locationId = site.Id, categoryId = category.Id }), HttpStatusCode.Created);
        Assert.Equal(WorkPriority.High, wo.Priority);
        Assert.Equal("Plumbing", wo.CategoryName);

        var archived = await ReadAsync<CategoryDto>(await PostAsync(manager, $"/api/v1/work-orders/categories/{category.Id}/archive", new { rowVersion = category.RowVersion }));
        Assert.True(archived.IsArchived);
        Assert.DoesNotContain((await requester.GetFromJsonAsync<CategoryDto[]>("/api/v1/work-orders/categories", Json))!, c => c.Id == category.Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(requester, "/api/v1/work-orders", new { title = "Again", locationId = site.Id, categoryId = category.Id })).StatusCode);
    }

    [Fact]
    public async Task List_filters_and_search_work_and_numbers_are_unique_under_concurrency()
    {
        var tenant = Guid.NewGuid();
        await using (var platform = sql.App(null, platform: true))
        {
            platform.Tenants.Add(new Fms.Platform.Domain.Tenant { Id = tenant, Name = "Busy", CreatedAt = DateTimeOffset.UtcNow });
            await platform.SaveChangesAsync();
            await Fms.Platform.Services.SystemRoleSync.EnsureAsync(platform, tenant);
        }

        var manager = await _api.ClientAsync(tenant, SystemRoles.FacilityManager);
        var (site, asset) = await PlaceAsync(manager);

        var created = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            Ok(PostAsync(manager, "/api/v1/work-orders", new { title = $"Parallel {i}", locationId = site.Id, assetId = i % 2 == 0 ? asset.Id : (Guid?)null }), HttpStatusCode.Created)));
        Assert.Equal(12, created.Select(c => c.Number).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 12), created.Select(c => c.Number).Order());

        var all = await ReadAsync<Page<WorkOrderDto>>(await manager.GetAsync("/api/v1/work-orders?pageSize=5&page=2"));
        Assert.Equal(12, all.Total);
        Assert.Equal(5, all.Items.Count);
        Assert.Equal(7, all.Items[0].Number); // newest first

        Assert.Equal(6, (await ReadAsync<Page<WorkOrderDto>>(await manager.GetAsync($"/api/v1/work-orders?assetId={asset.Id}"))).Total);
        Assert.Equal(12, (await ReadAsync<Page<WorkOrderDto>>(await manager.GetAsync("/api/v1/work-orders?status=Requested,Approved"))).Total);
        Assert.Equal(0, (await ReadAsync<Page<WorkOrderDto>>(await manager.GetAsync("/api/v1/work-orders?status=Closed"))).Total);
        Assert.Equal(1, (await ReadAsync<Page<WorkOrderDto>>(await manager.GetAsync("/api/v1/work-orders?q=Parallel 3"))).Total);
        Assert.Equal(1, (await ReadAsync<Page<WorkOrderDto>>(await manager.GetAsync("/api/v1/work-orders?q=WO-000005"))).Total);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.GetAsync("/api/v1/work-orders?status=Nonsense")).StatusCode);

        var overdue = await Ok(PostAsync(manager, "/api/v1/work-orders", new { title = "Late", locationId = site.Id, dueAt = DateTimeOffset.UtcNow.AddDays(-2) }), HttpStatusCode.Created);
        Assert.True(overdue.IsOverdue);
        Assert.Equal([overdue.Id], (await ReadAsync<Page<WorkOrderDto>>(await manager.GetAsync("/api/v1/work-orders?overdue=true"))).Items.Select(w => w.Id).ToArray());
        Assert.Equal(13, (await ReadAsync<Page<WorkOrderDto>>(await manager.GetAsync("/api/v1/work-orders?mine=true"))).Total);
    }

    [Fact]
    public async Task Another_tenant_cannot_see_touch_or_assign_into_a_work_order()
    {
        var managerA = await _api.ClientAsync(TenantA, SystemRoles.FacilityManager);
        var managerB = await _api.ClientAsync(TenantB, SystemRoles.FacilityManager);
        var techB = await _api.ClientAsync(TenantB, SystemRoles.Technician);
        var techA = await _api.ClientAsync(TenantA, SystemRoles.Technician);
        var approverA = await _api.ClientAsync(TenantA, SystemRoles.FacilityManager);
        var (siteA, assetA) = await PlaceAsync(managerA);
        var (siteB, _) = await PlaceAsync(managerB);

        var wo = await Ok(PostAsync(managerA, "/api/v1/work-orders", new { title = "Tenant A only", locationId = siteA.Id }), HttpStatusCode.Created);

        Assert.Equal(HttpStatusCode.NotFound, (await managerB.GetAsync($"/api/v1/work-orders/{wo.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Act(managerB, wo.Id, "approve")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Act(managerB, wo.Id, "cancel", new { reason = "x" })).StatusCode);
        Assert.DoesNotContain((await ReadAsync<Page<WorkOrderDto>>(await managerB.GetAsync("/api/v1/work-orders"))).Items, w => w.Id == wo.Id);

        // Tenant B cannot raise work against tenant A's location or asset...
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(managerB, "/api/v1/work-orders", new { title = "x", locationId = siteA.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(managerB, "/api/v1/work-orders", new { title = "x", assetId = assetA.Id, locationId = siteB.Id })).StatusCode);

        // ...and tenant A cannot assign one of its work orders to a person of tenant B.
        await Ok(Act(approverA, wo.Id, "approve"));
        var foreign = await UserIdAsync(techB);
        Assert.Equal(HttpStatusCode.BadRequest, (await Act(approverA, wo.Id, "assign", new { assigneeId = foreign })).StatusCode);
        Assert.Equal(WorkOrderStatus.Assigned, (await Ok(Act(approverA, wo.Id, "assign", new { assigneeId = await UserIdAsync(techA) }))).Status);

        // Assignee list only shows the tenant's own people.
        var assignees = await managerB.GetFromJsonAsync<AssigneeDto[]>("/api/v1/work-orders/assignees", Json);
        Assert.Contains(assignees!, a => a.Id == foreign);
        var techAId = await UserIdAsync(techA);
        Assert.DoesNotContain(assignees!, a => a.Id == techAId);
    }

    [Fact]
    public async Task The_database_itself_enforces_isolation_and_keeps_history_append_only()
    {
        var manager = await _api.ClientAsync(TenantA, SystemRoles.FacilityManager);
        var (site, _) = await PlaceAsync(manager);
        var wo = await Ok(PostAsync(manager, "/api/v1/work-orders", new { title = "Evidence", locationId = site.Id }), HttpStatusCode.Created);
        await PostAsync(manager, $"/api/v1/work-orders/{wo.Id}/comments", new { body = "note" });

        // Row-level security: another tenant's session sees nothing, even bypassing the EF filter, and cannot update.
        await using (var other = sql.Work(TenantB))
        {
            Assert.Equal(0, await other.WorkOrders.IgnoreQueryFilters().CountAsync(w => w.Id == wo.Id));
            Assert.Equal(0, await other.WorkOrderEvents.IgnoreQueryFilters().CountAsync(e => e.WorkOrderId == wo.Id));
            Assert.Equal(0, await other.Database.ExecuteSqlRawAsync("UPDATE work.WorkOrders SET Title = 'hacked' WHERE Id = {0}", wo.Id));
        }

        // Append-only: even the right tenant, even with raw SQL, cannot change or remove history.
        await using var own = sql.Work(TenantA);
        await Assert.ThrowsAnyAsync<Exception>(() => own.Database.ExecuteSqlRawAsync("UPDATE work.WorkOrderEvents SET Note = 'rewritten' WHERE WorkOrderId = {0}", wo.Id));
        await Assert.ThrowsAnyAsync<Exception>(() => own.Database.ExecuteSqlRawAsync("DELETE FROM work.WorkOrderEvents WHERE WorkOrderId = {0}", wo.Id));
        await Assert.ThrowsAnyAsync<Exception>(() => own.Database.ExecuteSqlRawAsync("UPDATE work.WorkOrderComments SET Body = 'rewritten' WHERE WorkOrderId = {0}", wo.Id));
        await Assert.ThrowsAnyAsync<Exception>(() => own.Database.ExecuteSqlRawAsync("DELETE FROM work.WorkOrderComments WHERE WorkOrderId = {0}", wo.Id));
        Assert.Equal(1, await own.WorkOrderEvents.CountAsync(e => e.WorkOrderId == wo.Id));

        // Every lifecycle step also lands in the audit trail.
        await using var platform = sql.App(TenantA);
        Assert.True(await platform.AuditEvents.AnyAsync(a => a.Action == "workorder.created" && a.EntityId == wo.Id.ToString()));
    }
}
