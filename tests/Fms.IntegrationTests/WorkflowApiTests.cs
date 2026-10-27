using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Fms.Facilities.Services;
using Fms.Platform.Security;
using Fms.Platform.Services;
using Fms.Work.Domain;
using Fms.Work.Services;
using Microsoft.EntityFrameworkCore;
using static Fms.IntegrationTests.ApiHarness;

namespace Fms.IntegrationTests;

[Collection(SqlCollection.Name)]
public sealed class WorkflowApiTests(SqlServerFixture sql) : IDisposable
{
    private readonly ApiHarness _api = new(sql, settings: new Dictionary<string, string?> { ["Storage:Provider"] = "InMemory" });
    public void Dispose() => _api.Dispose();

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 1, 0, 0, 0, 1];

    private sealed record Rig(Guid Tenant, HttpClient Manager, HttpClient Technician, HttpClient Requester, HttpClient Viewer, LocationDto Site, Guid TechnicianId);

    private async Task<Rig> NewRigAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var platform = sql.App(null, platform: true))
        {
            platform.Tenants.Add(new Fms.Platform.Domain.Tenant { Id = tenant, Name = "Wf " + tenant.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
            await platform.SaveChangesAsync();
            await SystemRoleSync.EnsureAsync(platform, tenant);
        }

        var manager = await _api.ClientAsync(tenant, SystemRoles.FacilityManager);
        var technician = await _api.ClientAsync(tenant, SystemRoles.Technician);
        var site = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", new { type = "Site", code = FacilitiesScope.Code("S"), name = "HQ" }));
        var me = (await technician.GetFromJsonAsync<Fms.Api.MeInfo>("/api/v1/me"))!.UserId!.Value;
        return new Rig(tenant, manager, technician, await _api.ClientAsync(tenant, SystemRoles.Requester), await _api.ClientAsync(tenant, SystemRoles.Viewer), site, me);
    }

    private static object Rules(bool approval = true, bool verification = true, bool signature = false, string[]? waived = null, string[]? verify = null, bool time = false, bool photo = false) =>
        new { requireApproval = approval, requireVerification = verification, requireCompletionSignature = signature, waivedApprovalPriorities = waived ?? [], verificationPriorities = verify ?? [], requireTimeOnCompletion = time, requirePhotoOnCompletion = photo };

    private static async Task<WorkflowDto> PublishAsync(Rig rig, object rules, string note = "Tuning", int? expected = null)
    {
        var current = expected ?? (await ReadAsync<WorkflowDto>(await rig.Manager.GetAsync("/api/v1/work-orders/workflow"))).Version;
        var response = await PostAsync(rig.Manager, "/api/v1/work-orders/workflow/publish", new { rules, note, expectedVersion = current });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<WorkflowDto>(response);
    }

    private static async Task<WorkOrderDto> RaiseAsync(HttpClient c, Rig rig, string type, string priority) =>
        await ReadAsync<WorkOrderDto>(await PostAsync(c, "/api/v1/work-orders", new { title = "Rule test", type, priority, locationId = rig.Site.Id }));

    private static async Task<WorkOrderDto> Get(HttpClient c, Guid id) => (await ReadAsync<WorkOrderDetailDto>(await c.GetAsync($"/api/v1/work-orders/{id}"))).Order;

    [Fact]
    public async Task Everyone_reads_the_rules_only_managers_change_them_and_each_change_needs_a_reason_and_the_version_it_started_from()
    {
        var rig = await NewRigAsync();
        var start = await ReadAsync<WorkflowDto>(await rig.Requester.GetAsync("/api/v1/work-orders/workflow"));
        Assert.Equal((0, true, true), (start.Version, start.Rules.RequireApproval, start.Rules.RequireVerification));
        Assert.Contains("noSelfApproval", start.FixedRules);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Viewer.GetAsync("/api/v1/work-orders/workflow/history")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Technician, "/api/v1/work-orders/workflow/publish", new { rules = Rules(), note = "x", expectedVersion = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Requester, "/api/v1/work-orders/workflow/restore", new { version = 1, note = "x", expectedVersion = 0 })).StatusCode);

        foreach (var bad in new object[]
        {
            new { rules = Rules(signature: true), note = " ", expectedVersion = 0 }, new { rules = Rules(signature: true), expectedVersion = 0 }, new { note = "x", expectedVersion = 0 },
            new { rules = Rules(signature: true), note = "x" }, new { rules = Rules(approval: false, waived: ["Low"]), note = "x", expectedVersion = 0 },
            new { rules = Rules(verification: false, verify: ["High"]), note = "x", expectedVersion = 0 }, new { rules = Rules(waived: ["Low", "Medium", "High", "Urgent"]), note = "x", expectedVersion = 0 },
            new { rules = Rules(waived: ["Nonsense"]), note = "x", expectedVersion = 0 }, new { rules = Rules(), note = "same as now", expectedVersion = 0 },
        })
        {
            var status = (await PostAsync(rig.Manager, "/api/v1/work-orders/workflow/publish", bad)).StatusCode;
            Assert.True(status == HttpStatusCode.BadRequest, $"{System.Text.Json.JsonSerializer.Serialize(bad)} gave {status}");
        }

        var v1 = await PublishAsync(rig, Rules(signature: true), "Sign-off wanted by the auditors");
        Assert.Equal((1, true, "Sign-off wanted by the auditors"), (v1.Version, v1.Rules.RequireCompletionSignature, v1.Note));
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, "/api/v1/work-orders/workflow/publish", new { rules = Rules(time: true), note = "stale", expectedVersion = 0 })).StatusCode);
    }

    [Fact]
    public async Task Waived_priorities_start_approved_and_the_others_still_wait()
    {
        var rig = await NewRigAsync();
        Assert.Equal(WorkOrderStatus.Requested, (await RaiseAsync(rig.Requester, rig, "Corrective", "Low")).Status);
        await PublishAsync(rig, Rules(waived: ["Low"]), "Small jobs need no approval");
        Assert.Equal(WorkOrderStatus.Approved, (await RaiseAsync(rig.Requester, rig, "Corrective", "Low")).Status);
        Assert.Equal(WorkOrderStatus.Requested, (await RaiseAsync(rig.Requester, rig, "Corrective", "High")).Status);
        Assert.Equal(WorkOrderStatus.Approved, (await RaiseAsync(rig.Manager, rig, "Emergency", "Urgent")).Status); // emergencies never wait, with or without the rule
    }

    [Fact]
    public async Task Verification_can_be_limited_to_some_priorities_but_the_worker_never_verifies_their_own_work()
    {
        var rig = await NewRigAsync();
        await PublishAsync(rig, Rules(verify: ["High", "Urgent"]), "Verify only the big ones");

        async Task<WorkOrderDto> DoneAsync(string priority)
        {
            var wo = await RaiseAsync(rig.Manager, rig, "Emergency", priority);
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Manager, $"/api/v1/work-orders/{wo.Id}/assign", new { assigneeId = rig.TechnicianId })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Technician, $"/api/v1/work-orders/{wo.Id}/start", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Technician, $"/api/v1/work-orders/{wo.Id}/complete", new { code = "Fixed", notes = "done" })).StatusCode);
            return await Get(rig.Manager, wo.Id);
        }

        var low = await DoneAsync("Low");
        Assert.Contains(WorkOrderAction.Closed, low.Allowed);
        Assert.Equal(HttpStatusCode.OK, (await rig.Manager.PostAsync($"/api/v1/work-orders/{low.Id}/close", null)).StatusCode);

        var high = await DoneAsync("High");
        Assert.DoesNotContain(WorkOrderAction.Closed, high.Allowed); // must be verified first
        Assert.Equal(HttpStatusCode.Conflict, (await rig.Manager.PostAsync($"/api/v1/work-orders/{high.Id}/close", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Technician.PostAsync($"/api/v1/work-orders/{high.Id}/verify", null)).StatusCode); // the worker, and no verify right
        Assert.Equal(HttpStatusCode.OK, (await rig.Manager.PostAsync($"/api/v1/work-orders/{high.Id}/verify", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rig.Manager.PostAsync($"/api/v1/work-orders/{high.Id}/close", null)).StatusCode);
    }

    [Fact]
    public async Task Time_and_a_photo_can_be_required_before_work_is_completed()
    {
        var rig = await NewRigAsync();
        await PublishAsync(rig, Rules(time: true, photo: true), "Evidence before completion");
        var wo = await RaiseAsync(rig.Manager, rig, "Emergency", "High");
        await PostAsync(rig.Manager, $"/api/v1/work-orders/{wo.Id}/assign", new { assigneeId = rig.TechnicianId });
        await PostAsync(rig.Technician, $"/api/v1/work-orders/{wo.Id}/start", new { });
        Task<HttpResponseMessage> Complete() => PostAsync(rig.Technician, $"/api/v1/work-orders/{wo.Id}/complete", new { code = "Fixed", notes = "done" });

        var first = await Complete();
        Assert.Equal(HttpStatusCode.BadRequest, first.StatusCode);
        Assert.Contains("Time worked", await first.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Technician, $"/api/v1/work-orders/{wo.Id}/time", new { workedOn = DateOnly.FromDateTime(DateTime.UtcNow), minutes = 45 })).StatusCode);

        var second = await Complete();
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Contains("photo", await second.Content.ReadAsStringAsync());
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Png);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "after.png");
        form.Add(new StringContent("Before"), "kind"); // the wrong kind of photo does not count
        Assert.Equal(HttpStatusCode.Created, (await rig.Technician.PostAsync($"/api/v1/work-orders/{wo.Id}/attachments", form)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Complete()).StatusCode);

        var after = new MultipartFormDataContent();
        var again = new ByteArrayContent(Png);
        again.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        after.Add(again, "file", "after.png");
        after.Add(new StringContent("After"), "kind");
        Assert.Equal(HttpStatusCode.Created, (await rig.Technician.PostAsync($"/api/v1/work-orders/{wo.Id}/attachments", after)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Complete()).StatusCode);
    }

    [Fact]
    public async Task Every_version_is_kept_going_back_is_a_new_version_and_the_history_cannot_be_edited()
    {
        var rig = await NewRigAsync();
        await PublishAsync(rig, Rules(signature: true), "Auditors");
        await PublishAsync(rig, Rules(signature: true, time: true), "Costing");
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, "/api/v1/work-orders/workflow/restore", new { version = 1, note = "back", expectedVersion = 1 })).StatusCode); // stale
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(rig.Manager, "/api/v1/work-orders/workflow/restore", new { version = 99, note = "back", expectedVersion = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/work-orders/workflow/restore", new { version = 1, expectedVersion = 2 })).StatusCode); // needs a reason

        var restored = await ReadAsync<WorkflowDto>(await PostAsync(rig.Manager, "/api/v1/work-orders/workflow/restore", new { version = 1, note = "Costing was too heavy", expectedVersion = 2 }));
        Assert.Equal((3, true, false), (restored.Version, restored.Rules.RequireCompletionSignature, restored.Rules.RequireTimeOnCompletion));
        Assert.Contains("Restored version 1", restored.Note);

        var history = await ReadAsync<List<WorkflowVersionDto>>(await rig.Manager.GetAsync("/api/v1/work-orders/workflow/history"));
        Assert.Equal([3, 2, 1], history.Select(h => h.Version).ToArray());
        Assert.True(history[1].Rules.RequireTimeOnCompletion); // the old rules are still readable
        Assert.NotNull(history[0].ChangedBy);

        await using var db = sql.Work(rig.Tenant);
        (await db.WorkflowVersions.FirstAsync()).Note = "rewritten";
        await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync()); // the application refuses
        db.ChangeTracker.Clear();
        // And so does the database itself, even for someone who bypasses the application.
        var update = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("UPDATE work.WorkflowVersions SET Note = 'rewritten'"));
        Assert.Contains("append-only", update.ToString());
        var delete = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM work.WorkflowVersions"));
        Assert.Contains("append-only", delete.ToString());
    }

    [Fact]
    public async Task Changes_on_the_older_settings_screen_also_appear_in_the_history()
    {
        var rig = await NewRigAsync();
        var settings = await ReadAsync<WorkSettingsDto>(await rig.Manager.GetAsync("/api/v1/work-orders/settings"));
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(rig.Manager, "/api/v1/work-orders/settings", new { requireApproval = false, requireVerification = true, rowVersion = settings.RowVersion })).StatusCode);
        // The first save creates the settings row, so there is nothing to compare with: the next change is the first to be recorded.
        var after = await ReadAsync<WorkSettingsDto>(await rig.Manager.GetAsync("/api/v1/work-orders/settings"));
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(rig.Manager, "/api/v1/work-orders/settings", new { requireApproval = true, requireVerification = false, rowVersion = after.RowVersion })).StatusCode);
        var history = await ReadAsync<List<WorkflowVersionDto>>(await rig.Manager.GetAsync("/api/v1/work-orders/workflow/history"));
        var entry = Assert.Single(history);
        Assert.Equal("Changed on the settings screen", entry.Note);
        Assert.Equal((true, false), (entry.Rules.RequireApproval, entry.Rules.RequireVerification));
        Assert.Equal(1, (await ReadAsync<WorkflowDto>(await rig.Manager.GetAsync("/api/v1/work-orders/workflow"))).Version);
    }

    [Fact]
    public async Task Another_organisation_has_its_own_rules_and_history()
    {
        var a = await NewRigAsync();
        var b = await NewRigAsync();
        await PublishAsync(a, Rules(waived: ["Low"]), "A only");
        Assert.Equal(0, (await ReadAsync<WorkflowDto>(await b.Manager.GetAsync("/api/v1/work-orders/workflow"))).Version);
        Assert.Empty(await ReadAsync<List<WorkflowVersionDto>>(await b.Manager.GetAsync("/api/v1/work-orders/workflow/history")));
        Assert.Equal(WorkOrderStatus.Requested, (await RaiseAsync(b.Requester, b, "Corrective", "Low")).Status); // B's Low requests still wait
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(b.Manager, "/api/v1/work-orders/workflow/restore", new { version = 1, note = "steal", expectedVersion = 0 })).StatusCode);
        await using var db = sql.Work(b.Tenant);
        Assert.Equal(0, await db.WorkflowVersions.IgnoreQueryFilters().CountAsync());
    }
}
