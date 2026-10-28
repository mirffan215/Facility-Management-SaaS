using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Fms.Api;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Results;
using Fms.Facilities.Services;
using Fms.Platform.Security;
using Fms.Platform.Services;
using Fms.Projects.Domain;
using Fms.Projects.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static Fms.IntegrationTests.ApiHarness;

namespace Fms.IntegrationTests;

/// <summary>The project logic against stand-in work-order and purchase-order sources, so the money arithmetic is exact and the tests need no purchasing flow.</summary>
[Collection(SqlCollection.Name)]
public sealed class ProjectsApiTests : IDisposable
{
    private sealed class FakeWork : IWorkCostLookup
    {
        public ConcurrentDictionary<Guid, WorkCostInfo> Items { get; } = new();
        public Task<IReadOnlyDictionary<Guid, WorkCostInfo>> GetAsync(IEnumerable<Guid> ids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, WorkCostInfo>>(ids.Where(Items.ContainsKey).ToDictionary(i => i, i => Items[i]));
        public Task<WorkCostInfo?> FindByNumberAsync(string number, CancellationToken ct = default) => Task.FromResult(Items.Values.FirstOrDefault(w => w.Number.Equals(number, StringComparison.OrdinalIgnoreCase)));
    }

    private sealed class FakeOrders : IPurchaseOrderLookup
    {
        public ConcurrentDictionary<Guid, PurchaseOrderInfo> Items { get; } = new();
        public Task<IReadOnlyDictionary<Guid, PurchaseOrderInfo>> GetAsync(IEnumerable<Guid> ids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, PurchaseOrderInfo>>(ids.Where(Items.ContainsKey).ToDictionary(i => i, i => Items[i]));
        public Task<PurchaseOrderInfo?> FindByNumberAsync(string number, CancellationToken ct = default) => Task.FromResult(Items.Values.FirstOrDefault(o => o.Number.Equals(number, StringComparison.OrdinalIgnoreCase)));
    }

    private readonly SqlServerFixture _sql;
    private readonly FakeWork _work = new();
    private readonly FakeOrders _orders = new();
    private readonly ApiHarness _api;

    public ProjectsApiTests(SqlServerFixture sql)
    {
        _sql = sql;
        _api = new ApiHarness(sql, services =>
        {
            services.Replace(ServiceDescriptor.Scoped<IWorkCostLookup>(_ => _work));
            services.Replace(ServiceDescriptor.Scoped<IPurchaseOrderLookup>(_ => _orders));
        });
    }

    public void Dispose() => _api.Dispose();

    private sealed record Rig(Guid Tenant, HttpClient Manager, HttpClient Second, HttpClient Viewer, HttpClient Technician, LocationDto Site);

    private async Task<Rig> NewRigAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var platform = _sql.App(null, platform: true))
        {
            platform.Tenants.Add(new Fms.Platform.Domain.Tenant { Id = tenant, Name = "Prj " + tenant.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
            await platform.SaveChangesAsync();
            await SystemRoleSync.EnsureAsync(platform, tenant);
        }

        var manager = await _api.ClientAsync(tenant, SystemRoles.FacilityManager);
        var site = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", new { type = "Site", code = FacilitiesScope.Code("S"), name = "HQ" }));
        return new Rig(tenant, manager, await _api.ClientAsync(tenant, SystemRoles.FacilityManager), await _api.ClientAsync(tenant, SystemRoles.Viewer), await _api.ClientAsync(tenant, SystemRoles.Technician), site);
    }

    private static string D(int offset) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(offset).ToString("yyyy-MM-dd");

    private static async Task<ProjectDetailDto> NewProjectAsync(HttpClient c, Rig rig, string? code = null, object? more = null)
    {
        var body = new Dictionary<string, object?> { ["code"] = code ?? "P" + Guid.NewGuid().ToString("N")[..6], ["name"] = "Level 3 fit-out", ["startOn"] = D(-5), ["endOn"] = D(60), ["locationId"] = rig.Site.Id, ["currencyCode"] = "SAR" };
        if (more is not null) foreach (var p in more.GetType().GetProperties()) body[p.Name] = p.GetValue(more);
        var response = await PostAsync(c, "/api/v1/projects", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<ProjectDetailDto>(response);
    }

    private static async Task<ProjectDetailDto> GetAsync(HttpClient c, Guid id) => await ReadAsync<ProjectDetailDto>(await c.GetAsync($"/api/v1/projects/{id}"));

    private static Task<HttpResponseMessage> Budget(HttpClient c, Guid id, string category, decimal amount, string description = "Line") =>
        PostAsync(c, $"/api/v1/projects/{id}/budget", new { category, description, amount });

    // ------------------------------------------------------------------ access and basics

    [Fact]
    public async Task Roles_decide_who_reads_and_who_changes_projects()
    {
        var rig = await NewRigAsync();
        var p = await NewProjectAsync(rig.Manager, rig);
        Assert.Equal(HttpStatusCode.OK, (await rig.Viewer.GetAsync("/api/v1/projects")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rig.Viewer.GetAsync($"/api/v1/projects/{p.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Technician.GetAsync("/api/v1/projects")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Viewer, "/api/v1/projects", new { code = "X", name = "x", startOn = D(0), endOn = D(1) })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Budget(rig.Viewer, p.Id, "Design", 5)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GuestClient().GetAsync("/api/v1/projects")).StatusCode);
    }

    [Fact]
    public async Task A_project_is_validated_unique_by_code_and_edited_with_a_current_version()
    {
        var rig = await NewRigAsync();
        var p = await NewProjectAsync(rig.Manager, rig, "LVL3");
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, "/api/v1/projects", new { code = "lvl3", name = "dup", startOn = D(0), endOn = D(5) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/projects", new { code = "bad code!", name = "x", startOn = D(0), endOn = D(5) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/projects", new { code = "OK1", name = "", startOn = D(0), endOn = D(5) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/projects", new { code = "OK2", name = "x", startOn = D(5), endOn = D(0) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/projects", new { code = "OK3", name = "x", startOn = D(0), endOn = D(5), locationId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/projects", new { code = "OK4", name = "x", startOn = D(0), endOn = D(5), managerId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/projects", new { code = "OK5", name = "x", startOn = D(0), endOn = D(5), currencyCode = "RIYAL" })).StatusCode);

        var managerId = (await rig.Second.GetFromJsonAsync<MeInfo>("/api/v1/me"))!.UserId!.Value;
        var ok = await PutAsync(rig.Manager, $"/api/v1/projects/{p.Id}", new { code = "LVL3", name = "Level 3 fit-out v2", startOn = D(-5), endOn = D(90), managerId, locationId = rig.Site.Id, currencyCode = "SAR", rowVersion = p.RowVersion });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(rig.Manager, $"/api/v1/projects/{p.Id}", new { code = "LVL3", name = "Stale", startOn = D(-5), endOn = D(90), rowVersion = p.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(rig.Manager, $"/api/v1/projects/{p.Id}", new { code = "LVL3", name = "x", startOn = D(0), endOn = D(5) })).StatusCode); // no version
    }

    // ------------------------------------------------------------------ plan

    [Fact]
    public async Task Phases_tasks_and_milestones_make_up_the_plan_and_progress_follows_the_tasks()
    {
        var rig = await NewRigAsync();
        var p = await NewProjectAsync(rig.Manager, rig);
        var phase = await ReadAsync<PhaseDto>(await PostAsync(rig.Manager, $"/api/v1/projects/{p.Id}/phases", new { name = "Design" }));
        await PostAsync(rig.Manager, $"/api/v1/projects/{p.Id}/phases", new { name = "Build" });
        var assignee = (await rig.Second.GetFromJsonAsync<MeInfo>("/api/v1/me"))!.UserId!.Value;

        var t1 = await ReadAsync<TaskDto>(await PostAsync(rig.Manager, $"/api/v1/projects/{p.Id}/tasks", new { title = "Survey", phaseId = phase.Id, assigneeId = assignee, dueOn = D(-2) }));
        var t2 = await ReadAsync<TaskDto>(await PostAsync(rig.Manager, $"/api/v1/projects/{p.Id}/tasks", new { title = "Drawings", phaseId = phase.Id, dueOn = D(10) }));
        Assert.True(t1.IsOverdue);
        Assert.False(t2.IsOverdue);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, $"/api/v1/projects/{p.Id}/tasks", new { title = "Bad phase", phaseId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, $"/api/v1/projects/{p.Id}/tasks", new { title = "", dueOn = D(1) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, $"/api/v1/projects/{p.Id}/tasks", new { title = "Ghost", assigneeId = Guid.NewGuid() })).StatusCode);

        var done = await PutAsync(rig.Manager, $"/api/v1/projects/tasks/{t1.Id}", new { title = "Survey", phaseId = phase.Id, assigneeId = assignee, dueOn = D(-2), status = "Done", rowVersion = t1.RowVersion });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var doneDto = await ReadAsync<TaskDto>(done);
        Assert.Equal(DateOnly.Parse(D(0)), doneDto.DoneOn);
        Assert.False(doneDto.IsOverdue);
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(rig.Second, $"/api/v1/projects/tasks/{t1.Id}", new { title = "Stale", status = "ToDo", rowVersion = t1.RowVersion })).StatusCode);
        Assert.Equal(50, (await GetAsync(rig.Viewer, p.Id)).PercentDone);

        // A phase that still has tasks cannot be removed; one without can, with a reason.
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, $"/api/v1/projects/phases/{phase.Id}/delete", new { reason = "x" })).StatusCode);
        var build = (await GetAsync(rig.Viewer, p.Id)).Phases.Single(x => x.Name == "Build");
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, $"/api/v1/projects/phases/{build.Id}/delete", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Manager, $"/api/v1/projects/phases/{build.Id}/delete", new { reason = "Merged into design" })).StatusCode);

        var m = await ReadAsync<MilestoneDto>(await PostAsync(rig.Manager, $"/api/v1/projects/{p.Id}/milestones", new { name = "Handover", dueOn = D(-1) }));
        Assert.True(m.IsLate);
        var achieved = await ReadAsync<MilestoneDto>(await PostAsync(rig.Manager, $"/api/v1/projects/milestones/{m.Id}/achieve", new { rowVersion = m.RowVersion }));
        Assert.NotNull(achieved.AchievedOn);
        Assert.False(achieved.IsLate);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, $"/api/v1/projects/milestones/{m.Id}/achieve", new { rowVersion = achieved.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, $"/api/v1/projects/milestones/{m.Id}/delete", new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, $"/api/v1/projects/{p.Id}/milestones", new { name = "No date" })).StatusCode);
    }

    [Fact]
    public async Task A_project_can_only_be_completed_when_every_task_and_milestone_is_done_and_closing_needs_the_right_reasons()
    {
        var rig = await NewRigAsync();
        var p = await NewProjectAsync(rig.Manager, rig);
        Task<HttpResponseMessage> Status(string to, string? reason = null) => PostAsync(rig.Manager, $"/api/v1/projects/{p.Id}/status", new { status = to, reason, rowVersion = GetAsync(rig.Manager, p.Id).Result.RowVersion });

        Assert.Equal(HttpStatusCode.Conflict, (await Status("Completed")).StatusCode); // not even started
        Assert.Equal(HttpStatusCode.OK, (await Status("Active")).StatusCode);
        var task = await ReadAsync<TaskDto>(await PostAsync(rig.Manager, $"/api/v1/projects/{p.Id}/tasks", new { title = "Last job" }));
        var ms = await ReadAsync<MilestoneDto>(await PostAsync(rig.Manager, $"/api/v1/projects/{p.Id}/milestones", new { name = "Sign-off", dueOn = D(5) }));
        var open = await Status("Completed");
        Assert.Equal(HttpStatusCode.Conflict, open.StatusCode);
        Assert.Contains("1 open task", await open.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, (await Status("OnHold")).StatusCode); // reason needed
        Assert.Equal(HttpStatusCode.OK, (await Status("OnHold", "Waiting for permit")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Status("Active")).StatusCode);
        await PutAsync(rig.Manager, $"/api/v1/projects/tasks/{task.Id}", new { title = "Last job", status = "Done", rowVersion = task.RowVersion });
        await PostAsync(rig.Manager, $"/api/v1/projects/milestones/{ms.Id}/achieve", new { rowVersion = ms.RowVersion });
        Assert.Equal(HttpStatusCode.OK, (await Status("Completed")).StatusCode);

        var closed = await GetAsync(rig.Viewer, p.Id);
        Assert.Equal(ProjectStatus.Completed, closed.Status);
        Assert.NotNull(closed.ClosedOn);
        Assert.Empty(closed.NextStatuses);

        // A closed project keeps its plan, money and links as they were.
        Assert.Equal(HttpStatusCode.Conflict, (await Budget(rig.Manager, p.Id, "Design", 1)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, $"/api/v1/projects/{p.Id}/tasks", new { title = "Too late" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(rig.Manager, $"/api/v1/projects/{p.Id}", new { code = closed.Code, name = "Renamed", startOn = D(-5), endOn = D(60), rowVersion = closed.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Status("Active")).StatusCode);
    }

    [Fact]
    public async Task Cancelling_needs_a_reason_and_is_final()
    {
        var rig = await NewRigAsync();
        var p = await NewProjectAsync(rig.Manager, rig);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, $"/api/v1/projects/{p.Id}/status", new { status = "Cancelled", rowVersion = p.RowVersion })).StatusCode);
        var response = await PostAsync(rig.Manager, $"/api/v1/projects/{p.Id}/status", new { status = "Cancelled", reason = "Funding withdrawn", rowVersion = p.RowVersion });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cancelled = await ReadAsync<ProjectDetailDto>(response);
        Assert.Equal("Funding withdrawn", cancelled.CloseReason);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, $"/api/v1/projects/{p.Id}/status", new { status = "Active", rowVersion = cancelled.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, $"/api/v1/projects/{p.Id}/status", new { status = "Bogus", rowVersion = cancelled.RowVersion })).StatusCode);
    }

    // ------------------------------------------------------------------ budget, links and costs

    [Fact]
    public async Task Budget_is_summed_per_category_and_every_change_is_audited_with_before_and_after()
    {
        var rig = await NewRigAsync();
        var p = await NewProjectAsync(rig.Manager, rig);
        Assert.Equal(HttpStatusCode.BadRequest, (await Budget(rig.Manager, p.Id, "Design", -1)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, $"/api/v1/projects/{p.Id}/budget", new { category = "Design", description = "", amount = 1 })).StatusCode);
        var line = await ReadAsync<BudgetLineDto>(await Budget(rig.Manager, p.Id, "Construction", 1000.456m, "Partitions"));
        Assert.Equal(1000.46m, line.Amount);
        await Budget(rig.Manager, p.Id, "Design", 250, "Architect");
        var changed = await PutAsync(rig.Manager, $"/api/v1/projects/budget/{line.Id}", new { category = "Construction", description = "Partitions", amount = 1200, rowVersion = line.RowVersion });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(rig.Second, $"/api/v1/projects/budget/{line.Id}", new { category = "Construction", description = "x", amount = 1, rowVersion = line.RowVersion })).StatusCode);

        var costs = await ReadAsync<ProjectCostsDto>(await rig.Viewer.GetAsync($"/api/v1/projects/{p.Id}/costs"));
        Assert.Equal(1450m, costs.Budget);
        Assert.Equal(1450m, costs.Remaining);
        Assert.False(costs.OverBudget);

        var admin = await _api.ClientAsync(rig.Tenant, SystemRoles.Administrator);
        var trail = await admin.GetStringAsync("/api/v1/audit?pageSize=100&action=project.budget_changed");
        Assert.Contains("1000.46", trail);
        Assert.Contains("1200", trail);

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, $"/api/v1/projects/budget/{line.Id}/delete", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Manager, $"/api/v1/projects/budget/{line.Id}/delete", new { reason = "Moved to the main contract" })).StatusCode);
        Assert.Equal(250m, (await ReadAsync<ProjectCostsDto>(await rig.Viewer.GetAsync($"/api/v1/projects/{p.Id}/costs"))).Budget);
    }

    private (Guid Id, string Number) AddWorkOrder(decimal total, string number, string currency = "SAR")
    {
        var id = Guid.NewGuid();
        _work.Items[id] = new WorkCostInfo(id, number, "Install partitions", "InProgress", total, currency);
        return (id, number);
    }

    private (Guid Id, string Number) AddOrder(decimal ordered, decimal received, string number, bool cancelled = false, string currency = "SAR")
    {
        var id = Guid.NewGuid();
        _orders.Items[id] = new PurchaseOrderInfo(id, number, "Acme Supplies", cancelled ? "Cancelled" : "PartiallyReceived", ordered, received, currency, cancelled);
        return (id, number);
    }

    [Fact]
    public async Task Budget_against_spent_and_committed_is_worked_out_from_the_linked_work_and_orders()
    {
        var rig = await NewRigAsync();
        var p = await NewProjectAsync(rig.Manager, rig);
        await Budget(rig.Manager, p.Id, "Construction", 1000);
        await Budget(rig.Manager, p.Id, "Equipment", 500);

        var wo = AddWorkOrder(300, "WO-900001");
        var po = AddOrder(600, 200, "PO-900001");
        var cancelled = AddOrder(999, 0, "PO-900002", cancelled: true);
        var foreign = AddOrder(100, 100, "PO-900003", currency: "USD");
        async Task<HttpResponseMessage> Link(string kind, string number, string category) => await PostAsync(rig.Manager, $"/api/v1/projects/{p.Id}/links", new { kind, number, category });

        Assert.Equal(HttpStatusCode.Created, (await Link("WorkOrder", wo.Number, "Construction")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Link("PurchaseOrder", po.Number.ToLowerInvariant(), "Equipment")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Link("PurchaseOrder", cancelled.Number, "Equipment")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Link("PurchaseOrder", foreign.Number, "Equipment")).StatusCode);

        var costs = await ReadAsync<ProjectCostsDto>(await rig.Viewer.GetAsync($"/api/v1/projects/{p.Id}/costs"));
        Assert.Equal(1500m, costs.Budget);
        Assert.Equal(500m, costs.Spent);       // 300 of work + 200 received
        Assert.Equal(400m, costs.Committed);   // 600 ordered, 200 arrived
        Assert.Equal(600m, costs.Remaining);
        Assert.False(costs.OverBudget);
        var equipment = costs.ByCategory.Single(r => r.Category == BudgetCategory.Equipment);
        Assert.Equal((500m, 200m, 400m, -100m), (equipment.Budget, equipment.Spent, equipment.Committed, equipment.Remaining));
        Assert.Equal(1000m - 300m, costs.ByCategory.Single(r => r.Category == BudgetCategory.Construction).Remaining);
        var excluded = costs.Items.Single(i => i.Excluded);
        Assert.Equal(foreign.Number, excluded.Number);
        Assert.Contains("USD", excluded.ExcludedBecause);

        // More arrives and the money moves from committed to spent; costs growing past the budget is flagged.
        _orders.Items[po.Id] = _orders.Items[po.Id] with { Received = 600 };
        _work.Items[wo.Id] = _work.Items[wo.Id] with { Total = 1200 };
        var later = await ReadAsync<ProjectCostsDto>(await rig.Viewer.GetAsync($"/api/v1/projects/{p.Id}/costs"));
        Assert.Equal((1800m, 0m, true), (later.Spent, later.Committed, later.OverBudget));
    }

    [Fact]
    public async Task A_work_order_or_order_belongs_to_one_project_at_a_time_and_unlinking_keeps_the_history()
    {
        var rig = await NewRigAsync();
        var a = await NewProjectAsync(rig.Manager, rig);
        var b = await NewProjectAsync(rig.Manager, rig);
        var wo = AddWorkOrder(10, "WO-900010");
        Task<HttpResponseMessage> Link(Guid project, string number, string kind = "WorkOrder") => PostAsync(rig.Manager, $"/api/v1/projects/{project}/links", new { kind, number, category = "Other" });

        var link = await ReadAsync<LinkDto>(await Link(a.Id, wo.Number));
        Assert.Equal(HttpStatusCode.Conflict, (await Link(a.Id, wo.Number)).StatusCode);
        var other = await Link(b.Id, wo.Number);
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
        Assert.Contains("another project", await other.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await Link(a.Id, "WO-000000")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Link(a.Id, wo.Number, "PurchaseOrder")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, $"/api/v1/projects/{a.Id}/links", new { kind = "WorkOrder", number = wo.Number })).StatusCode); // no category

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, $"/api/v1/projects/links/{link.Id}/unlink", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Manager, $"/api/v1/projects/links/{link.Id}/unlink", new { reason = "Charged to the wrong project" })).StatusCode);
        Assert.Empty((await GetAsync(rig.Viewer, a.Id)).Links);
        Assert.Equal(HttpStatusCode.Created, (await Link(b.Id, wo.Number)).StatusCode); // free to move

        await using var db = _sql.Projects(rig.Tenant);
        var history = await db.Links.Where(l => l.RefId == wo.Id).OrderBy(l => l.LinkedAt).ToListAsync();
        Assert.Equal(2, history.Count);
        Assert.Equal("Charged to the wrong project", history[0].UnlinkReason);
    }

    [Fact]
    public async Task The_list_shows_progress_budget_and_lateness_and_filters_by_status_and_text()
    {
        var rig = await NewRigAsync();
        var late = await NewProjectAsync(rig.Manager, rig, "LATE1", new { name = "Overrunning", startOn = D(-60), endOn = D(-1) });
        await Budget(rig.Manager, late.Id, "Other", 400);
        await PostAsync(rig.Manager, $"/api/v1/projects/{late.Id}/milestones", new { name = "Missed", dueOn = D(-10) });
        await PostAsync(rig.Manager, $"/api/v1/projects/{late.Id}/tasks", new { title = "Done one", status = "Done" });
        await PostAsync(rig.Manager, $"/api/v1/projects/{late.Id}/tasks", new { title = "Open one" });
        await NewProjectAsync(rig.Manager, rig, "FINE1", new { name = "Tidy" });

        var all = await ReadAsync<Page<ProjectSummaryDto>>(await rig.Viewer.GetAsync("/api/v1/projects"));
        Assert.Equal(2, all.Total);
        var row = all.Items.Single(x => x.Code == "LATE1");
        Assert.Equal((400m, 1, 2, 50, 1, true), (row.Budget, row.TasksDone, row.TasksTotal, row.PercentDone, row.MilestonesLate, row.EndOverdue));
        Assert.Single((await ReadAsync<Page<ProjectSummaryDto>>(await rig.Viewer.GetAsync("/api/v1/projects?q=tidy"))).Items);
        Assert.Equal(2, (await ReadAsync<Page<ProjectSummaryDto>>(await rig.Viewer.GetAsync("/api/v1/projects?status=Planning"))).Total);
        Assert.Empty((await ReadAsync<Page<ProjectSummaryDto>>(await rig.Viewer.GetAsync("/api/v1/projects?status=Active"))).Items);
    }

    [Fact]
    public async Task Another_organisation_sees_none_of_it()
    {
        var a = await NewRigAsync();
        var b = await NewRigAsync();
        var p = await NewProjectAsync(a.Manager, a);
        var line = await ReadAsync<BudgetLineDto>(await Budget(a.Manager, p.Id, "Design", 5));
        var task = await ReadAsync<TaskDto>(await PostAsync(a.Manager, $"/api/v1/projects/{p.Id}/tasks", new { title = "Secret plan" }));

        Assert.Equal(HttpStatusCode.NotFound, (await b.Manager.GetAsync($"/api/v1/projects/{p.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Manager.GetAsync($"/api/v1/projects/{p.Id}/costs")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Budget(b.Manager, p.Id, "Design", 1)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(b.Manager, $"/api/v1/projects/budget/{line.Id}", new { category = "Design", description = "x", amount = 1, rowVersion = line.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(b.Manager, $"/api/v1/projects/tasks/{task.Id}", new { title = "x", rowVersion = task.RowVersion })).StatusCode);
        Assert.Empty((await ReadAsync<Page<ProjectSummaryDto>>(await b.Manager.GetAsync("/api/v1/projects"))).Items);

        await using var raw = _sql.Projects(b.Tenant);
        Assert.Equal(0, await raw.Projects.IgnoreQueryFilters().CountAsync(x => x.Id == p.Id));
        Assert.Equal(0, await raw.Tasks.IgnoreQueryFilters().CountAsync(x => x.Id == task.Id));
        Assert.Equal(0, await raw.BudgetLines.IgnoreQueryFilters().CountAsync(x => x.Id == line.Id));
    }

    [Fact]
    public async Task The_dashboard_and_the_data_feed_include_projects_without_names()
    {
        var rig = await NewRigAsync();
        var p = await NewProjectAsync(rig.Manager, rig, "FEED1", new { name = "Secret refurbishment", startOn = D(-30), endOn = D(-2) });
        var text = await (await rig.Manager.GetAsync("/api/v1/reports/dashboard")).Content.ReadAsStringAsync();
        Assert.True(text.Contains("projects.open", StringComparison.Ordinal), text);
        Assert.Contains("projects.late", text);

        var admin = await _api.ClientAsync(rig.Tenant, SystemRoles.Administrator);
        var token = await ReadAsync<Fms.Reporting.Services.CreatedFeedTokenDto>(await PostAsync(admin, "/api/v1/reports/feed/tokens", new { name = "BI" }));
        var feed = _api.Factory.CreateClient();
        feed.DefaultRequestHeaders.Add("X-Api-Key", token.Secret);
        var projects = await feed.GetStringAsync("/api/v1/odata/Projects");
        Assert.Contains("FEED1", projects);
        Assert.DoesNotContain("Secret refurbishment", projects);
        _ = p;
    }
}
