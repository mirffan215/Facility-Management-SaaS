using System.Net;
using System.Net.Http.Headers;
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
public sealed class WorkEvidenceApiTests(SqlServerFixture sql) : IDisposable
{
    private readonly ApiHarness _api = new(sql, settings: new Dictionary<string, string?> { ["Storage:Provider"] = "InMemory" });
    public void Dispose() => _api.Dispose();

    private sealed record Me(Guid? UserId);

    // A real 1x1 PNG and the first bytes of a JPEG.
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1, 1, 0, 0, 1, 0, 1, 0, 0, 0xFF, 0xD9];
    private static string PngData => "data:image/png;base64," + Convert.ToBase64String(Png);

    private sealed record Job(Guid Tenant, HttpClient Manager, HttpClient Approver, HttpClient Technician, HttpClient Requester, Guid TechId, WorkOrderDto Order, LocationDto Site);

    private async Task<Guid> NewTenantAsync()
    {
        var tenant = Guid.NewGuid();
        await using var platform = sql.App(null, platform: true);
        platform.Tenants.Add(new Fms.Platform.Domain.Tenant { Id = tenant, Name = "Evidence " + tenant.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
        await platform.SaveChangesAsync();
        await Fms.Platform.Services.SystemRoleSync.EnsureAsync(platform, tenant);
        return tenant;
    }

    private static async Task<WorkOrderDto> OrderAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        Assert.Equal(expected, response.StatusCode);
        return await ReadAsync<WorkOrderDto>(response);
    }

    private static Task<HttpResponseMessage> Act(HttpClient c, Guid id, string action, object? body = null) => PostAsync(c, $"/api/v1/work-orders/{id}/{action}", body ?? new { });

    /// <summary>A work order that is approved, assigned to a technician and (optionally) started, in a tenant of its own.</summary>
    private async Task<Job> NewJobAsync(bool start = true, Guid? tenant = null)
    {
        tenant ??= await NewTenantAsync();
        var manager = await _api.ClientAsync(tenant.Value, SystemRoles.FacilityManager);
        var approver = await _api.ClientAsync(tenant.Value, SystemRoles.FacilityManager);
        var technician = await _api.ClientAsync(tenant.Value, SystemRoles.Technician);
        var requester = await _api.ClientAsync(tenant.Value, SystemRoles.Requester);
        var techId = (await technician.GetFromJsonAsync<Me>("/api/v1/me", Json))!.UserId!.Value;
        var site = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", new { type = "Site", code = FacilitiesScope.Code("S"), name = "HQ" }));

        var order = await OrderAsync(PostAsync(requester, "/api/v1/work-orders", new { title = "Pump noise", locationId = site.Id }), HttpStatusCode.Created);
        await OrderAsync(Act(approver, order.Id, "approve"));
        order = await OrderAsync(Act(approver, order.Id, "assign", new { assigneeId = techId }));
        if (start) order = await OrderAsync(Act(technician, order.Id, "start"));
        return new Job(tenant.Value, manager, approver, technician, requester, techId, order, site);
    }

    private static MultipartFormDataContent FileForm(byte[] bytes, string name, string kind = "Other", string? description = null)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", name);
        form.Add(new StringContent(kind), "kind");
        if (description is not null) form.Add(new StringContent(description), "description");
        return form;
    }

    // ---------------- checklists ----------------

    [Fact]
    public async Task A_required_checklist_item_must_be_answered_before_the_work_can_be_completed()
    {
        var job = await NewJobAsync(start: false);
        var requesterB = await _api.ClientAsync(job.Tenant, SystemRoles.Requester);

        // Templates: managers write them, staff read them.
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(job.Technician, "/api/v1/work-orders/checklists", new { name = "x", items = new[] { new { text = "a", required = true } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(job.Manager, "/api/v1/work-orders/checklists", new { name = "Empty", items = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(job.Manager, "/api/v1/work-orders/checklists", new { name = "Blank line", items = new[] { new { text = " ", required = false } } })).StatusCode);
        var created = await PostAsync(job.Manager, "/api/v1/work-orders/checklists", new
        {
            name = "Pump inspection", nameAr = "فحص المضخة",
            items = new[] { new { text = "Isolate power", required = true }, new { text = "Check seals", required = true }, new { text = "Photograph the nameplate", required = false } },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var template = await ReadAsync<ChecklistTemplateDto>(created);
        Assert.Equal(3, template.Items.Count);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(job.Manager, "/api/v1/work-orders/checklists", new { name = "Pump inspection", items = new[] { new { text = "a", required = false } } })).StatusCode);
        Assert.Contains((await job.Technician.GetFromJsonAsync<ChecklistTemplateDto[]>("/api/v1/work-orders/checklists", Json))!, t => t.Id == template.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await job.Requester.GetAsync("/api/v1/work-orders/checklists")).StatusCode);

        // Attaching: a dispatcher (or the worker) can, a bystander cannot; it copies the lines.
        var url = $"/api/v1/work-orders/{job.Order.Id}/checklist";
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(requesterB, url, new { templateId = template.Id })).StatusCode); // a plain requester has no business with checklists
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(job.Approver, url, new { templateId = Guid.NewGuid() })).StatusCode);
        var attached = await ReadAsync<ChecklistItemDto[]>(await PostAsync(job.Approver, url, new { templateId = template.Id }));
        Assert.Equal(["Isolate power", "Check seals", "Photograph the nameplate"], attached.Select(i => i.Text).ToArray());
        Assert.All(attached, i => Assert.Equal(ChecklistResult.Pending, i.Result));

        // Editing the template later does not change the copy on the work order.
        await PutAsync(job.Manager, $"/api/v1/work-orders/checklists/{template.Id}", new { name = "Pump inspection", items = new[] { new { text = "Something else", required = true } }, rowVersion = template.RowVersion });
        Assert.Equal("Isolate power", (await job.Technician.GetFromJsonAsync<ChecklistItemDto[]>(url, Json))![0].Text);

        // Not started yet: nothing to fill in. Then the technician starts.
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(job.Technician, $"{url}/{attached[0].Id}", new { result = "Done" })).StatusCode);
        await OrderAsync(Act(job.Technician, job.Order.Id, "start"));

        // Completion is refused until the required lines are answered, and the reason says so.
        var refused = await Act(job.Technician, job.Order.Id, "complete", new { code = "Fixed", notes = "all done" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("2 required checklist item", await refused.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(job.Technician, $"{url}/{attached[0].Id}", new { result = "Failed" })).StatusCode); // a failure needs a note
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(job.Requester, $"{url}/{attached[0].Id}", new { result = "Done" })).StatusCode);

        var step1 = await ReadAsync<ChecklistItemDto[]>(await PostAsync(job.Technician, $"{url}/{attached[0].Id}", new { result = "Done" }));
        Assert.NotNull(step1[0].ResultAt);
        await ReadAsync<ChecklistItemDto[]>(await PostAsync(job.Technician, $"{url}/{attached[1].Id}", new { result = "Failed", note = "Seal is cracked" }));
        Assert.Equal(HttpStatusCode.OK, (await Act(job.Technician, job.Order.Id, "complete", new { code = "PartsRequired", notes = "Seal must be replaced" })).StatusCode); // answered, even if failed

        // The requester sees the checklist (it is their request); a stranger does not.
        var seen = await job.Requester.GetFromJsonAsync<ChecklistItemDto[]>(url, Json);
        Assert.Equal("Seal is cracked", seen![1].Note);
        Assert.Equal(HttpStatusCode.NotFound, (await requesterB.GetAsync(url)).StatusCode);

        // Once completed the answers are fixed.
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(job.Technician, $"{url}/{attached[2].Id}", new { result = "Done" })).StatusCode);
    }

    // ---------------- time and cost ----------------

    [Fact]
    public async Task Time_and_costs_are_recorded_by_the_worker_voided_with_a_reason_and_summed_with_the_rate_at_the_time()
    {
        var job = await NewJobAsync();
        var otherTech = await _api.ClientAsync(job.Tenant, SystemRoles.Technician);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        string Url(string tail) => $"/api/v1/work-orders/{job.Order.Id}/{tail}";

        // Settings: currency and hourly rate.
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(job.Manager, "/api/v1/work-orders/settings", new { requireApproval = true, requireVerification = true, currencyCode = "riyal" })).StatusCode);
        var settings = await ReadAsync<WorkSettingsDto>(await PutAsync(job.Manager, "/api/v1/work-orders/settings", new { requireApproval = true, requireVerification = true, currencyCode = "sar", labourRatePerHour = 80 }));
        Assert.Equal("SAR", settings.CurrencyCode);

        // Only the worker (or a manager) records; staff read; requesters see neither.
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(otherTech, Url("time"), new { workedOn = today, minutes = 30 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await job.Requester.GetAsync(Url("time"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await job.Requester.GetAsync(Url("costs"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await job.Requester.GetAsync(Url("cost-summary"))).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(job.Technician, Url("time"), new { workedOn = today, minutes = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(job.Technician, Url("time"), new { workedOn = today, minutes = 1441 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(job.Technician, Url("time"), new { workedOn = today.AddDays(5), minutes = 30 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(job.Technician, Url("time"), new { workedOn = today.AddDays(-200), minutes = 30 })).StatusCode);
        var first = await ReadAsync<TimeEntryDto[]>(await PostAsync(job.Technician, Url("time"), new { workedOn = today, minutes = 90, note = "Dismantled the pump" }));
        Assert.Equal(120m, first[0].LabourCost); // 1.5 h at 80
        Assert.Equal(80m, first[0].HourlyRate);

        // The rate changes later: the earlier entry keeps the rate it was recorded at.
        var current = await job.Manager.GetFromJsonAsync<WorkSettingsDto>("/api/v1/work-orders/settings", Json);
        await ReadAsync<WorkSettingsDto>(await PutAsync(job.Manager, "/api/v1/work-orders/settings", new { requireApproval = true, requireVerification = true, labourRatePerHour = 100, rowVersion = current!.RowVersion }));
        var both = await ReadAsync<TimeEntryDto[]>(await PostAsync(job.Technician, Url("time"), new { workedOn = today, minutes = 60 }));
        Assert.Equal([120m, 100m], both.Select(e => e.LabourCost).ToArray());

        // Costs.
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(job.Technician, Url("costs"), new { kind = "Material", description = "", quantity = 1, unitCost = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(job.Technician, Url("costs"), new { kind = "Material", description = "Seal", quantity = 0, unitCost = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(job.Technician, Url("costs"), new { kind = "Material", description = "Seal", quantity = 1, unitCost = -5 })).StatusCode);
        await PostAsync(job.Technician, Url("costs"), new { kind = "Material", description = "Mechanical seal", reference = "PO-77", quantity = 2, unitCost = 45.555 });
        var lines = await ReadAsync<CostLineDto[]>(await PostAsync(job.Technician, Url("costs"), new { kind = "Contractor", description = "Crane hire", reference = "INV-9", quantity = 1, unitCost = 500 }));
        Assert.Equal([91.11m, 500m], lines.Select(l => l.Total).ToArray()); // 2 x 45.555 rounded to the cent

        var summary = await ReadAsync<CostSummaryDto>(await job.Manager.GetAsync(Url("cost-summary")));
        Assert.Equal(new CostSummaryDto("SAR", 150, 220m, 91.11m, 500m, 0m, 811.11m), summary);

        // Voiding: a reason is required; only the recorder or a manager; the totals drop it but the record stays.
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(job.Technician, Url($"costs/{lines[1].Id}/void"), new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(otherTech, Url($"costs/{lines[1].Id}/void"), new { reason = "no" })).StatusCode);
        var voided = await ReadAsync<CostLineDto[]>(await PostAsync(job.Technician, Url($"costs/{lines[1].Id}/void"), new { reason = "Entered on the wrong order" }));
        Assert.True(voided[1].IsVoided);
        Assert.Equal("Entered on the wrong order", voided[1].VoidReason);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(job.Manager, Url($"costs/{lines[1].Id}/void"), new { reason = "again" })).StatusCode);
        Assert.Equal(311.11m, (await ReadAsync<CostSummaryDto>(await job.Manager.GetAsync(Url("cost-summary")))).Total);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(job.Manager, Url($"time/{first[0].Id}/void"), new { reason = "Duplicate" })).StatusCode); // a manager may void anyone's
        Assert.Equal(100m, (await ReadAsync<CostSummaryDto>(await job.Manager.GetAsync(Url("cost-summary")))).LabourCost);

        // Late receipts are fine after completion; not once the work is checked.
        await OrderAsync(Act(job.Technician, job.Order.Id, "complete", new { code = "Fixed", notes = "done" }));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(job.Technician, Url("costs"), new { kind = "Other", description = "Late receipt", quantity = 1, unitCost = 10 })).StatusCode);
        await OrderAsync(Act(job.Manager, job.Order.Id, "verify"));
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(job.Technician, Url("costs"), new { kind = "Other", description = "Too late", quantity = 1, unitCost = 10 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(job.Technician, Url("time"), new { workedOn = today, minutes = 5 })).StatusCode);
    }

    // ---------------- signatures ----------------

    [Fact]
    public async Task A_tenant_can_require_the_technicians_signature_before_work_is_completed()
    {
        var job = await NewJobAsync();
        string Url(string tail) => $"/api/v1/work-orders/{job.Order.Id}/{tail}";
        await ReadAsync<WorkSettingsDto>(await PutAsync(job.Manager, "/api/v1/work-orders/settings", new { requireApproval = true, requireVerification = true, requireCompletionSignature = true }));

        var refused = await Act(job.Technician, job.Order.Id, "complete", new { code = "Fixed", notes = "done" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("signature", await refused.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(job.Technician, Url("signatures"), new { role = "Technician", signerName = "", imageBase64 = PngData })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(job.Technician, Url("signatures"), new { role = "Technician", signerName = "Tina", imageBase64 = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(job.Technician, Url("signatures"), new { role = "Technician", signerName = "Tina", imageBase64 = "not base64!!" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(job.Technician, Url("signatures"), new { role = "Technician", signerName = "Tina", imageBase64 = Convert.ToBase64String(Jpeg) })).StatusCode); // only PNG
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(job.Technician, Url("signatures"), new { role = "Technician", signerName = "Tina", imageBase64 = Convert.ToBase64String(new byte[300_000]) })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(job.Requester, Url("signatures"), new { role = "Customer", signerName = "Rami", imageBase64 = PngData })).StatusCode);

        var signed = await ReadAsync<SignatureDto[]>(await PostAsync(job.Technician, Url("signatures"), new { role = "Technician", signerName = "Tina Technician", imageBase64 = PngData }));
        Assert.Equal(SignatureRole.Technician, signed[0].Role);
        await OrderAsync(Act(job.Technician, job.Order.Id, "complete", new { code = "Fixed", notes = "done" }));

        // The customer signs on the technician's device after completion; both signatures can be seen by the requester, image included.
        var both = await ReadAsync<SignatureDto[]>(await PostAsync(job.Technician, Url("signatures"), new { role = "Customer", signerName = "Rami Requester", imageBase64 = PngData }));
        Assert.Equal(2, both.Length);
        var seen = await job.Requester.GetFromJsonAsync<SignatureDto[]>(Url("signatures"), Json);
        Assert.Equal(2, seen!.Length);
        var image = await job.Requester.GetAsync($"/api/v1/work-orders/signatures/{seen[0].Id}/image");
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/png", image.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Png, await image.Content.ReadAsByteArrayAsync());

        var stranger = await _api.ClientAsync(job.Tenant, SystemRoles.Requester);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(Url("signatures"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/work-orders/signatures/{seen[0].Id}/image")).StatusCode);
    }

    // ---------------- photos and files ----------------

    [Fact]
    public async Task Photos_are_checked_by_content_downloaded_only_by_people_who_can_see_the_work_order_and_removed_with_a_reason()
    {
        var job = await NewJobAsync(start: false);
        var stranger = await _api.ClientAsync(job.Tenant, SystemRoles.Requester);
        var viewer = await _api.ClientAsync(job.Tenant, SystemRoles.Viewer);
        string Url(string tail) => $"/api/v1/work-orders/{job.Order.Id}/{tail}";

        // The requester adds a photo of the problem; the type comes from the bytes, not the name or claimed type.
        var uploaded = await job.Requester.PostAsync(Url("attachments"), FileForm(Png, "leak.html", "Before", "Water on the floor"));
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        var photo = await ReadAsync<AttachmentDto>(uploaded);
        Assert.Equal("image/png", photo.ContentType);
        Assert.Equal("leak.html.png", photo.FileName); // never left looking like an HTML page
        Assert.Equal(AttachmentKind.Before, photo.Kind);

        // Markup is only ever kept as inert plain text, with a name that says so.
        var html = await job.Requester.PostAsync(Url("attachments"), FileForm("<html><script>alert(1)</script></html>"u8.ToArray(), "x.html"));
        var inert = await ReadAsync<AttachmentDto>(html);
        Assert.Equal(("text/plain", "x.html.txt"), (inert.ContentType, inert.FileName));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await job.Requester.PostAsync(Url("attachments"), FileForm([0x4D, 0x5A, 0x90, 0, 3, 0, 0, 0], "tool.exe"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await job.Requester.PostAsync(Url("attachments"), FileForm([], "empty.png"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await job.Requester.PostAsync(Url("attachments"), FileForm(Png, "a.png", "Sideways"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsync(Url("attachments"), FileForm(Png, "a.png"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync(Url("attachments"), FileForm(Png, "a.png"))).StatusCode);

        // Download: the requester, staff and the viewer can; a stranger cannot. Always as an attachment-safe response.
        foreach (var who in new[] { job.Requester, job.Technician, viewer })
        {
            var download = await who.GetAsync($"/api/v1/work-orders/attachments/{photo.Id}/download");
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal(Png, await download.Content.ReadAsByteArrayAsync());
            Assert.Contains("sandbox", download.Headers.GetValues("Content-Security-Policy").Single());
        }

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/work-orders/attachments/{photo.Id}/download")).StatusCode);
        Assert.Equal([photo.Id, inert.Id], (await ReadAsync<AttachmentDto[]>(await job.Requester.GetAsync(Url("attachments")))).Select(a => a.Id).ToArray());
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(Url("attachments"))).StatusCode);

        // Removing: a reason; the uploader or a manager; not someone else.
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(job.Technician, $"/api/v1/work-orders/attachments/{photo.Id}/delete", new { reason = "not mine" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(job.Requester, $"/api/v1/work-orders/attachments/{photo.Id}/delete", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(job.Requester, $"/api/v1/work-orders/attachments/{photo.Id}/delete", new { reason = "Wrong room" })).StatusCode);
        Assert.Equal([inert.Id], (await ReadAsync<AttachmentDto[]>(await job.Requester.GetAsync(Url("attachments")))).Select(a => a.Id).ToArray());
        Assert.Equal(HttpStatusCode.NotFound, (await job.Requester.GetAsync($"/api/v1/work-orders/attachments/{photo.Id}/download")).StatusCode);

        // The technician adds an "after" photo; once the work order is closed nothing more can be added.
        await OrderAsync(Act(job.Technician, job.Order.Id, "start"));
        Assert.Equal(HttpStatusCode.Created, (await job.Technician.PostAsync(Url("attachments"), FileForm(Jpeg, "after.jpg", "After"))).StatusCode);
        await OrderAsync(Act(job.Technician, job.Order.Id, "complete", new { code = "Fixed", notes = "done" }));
        await OrderAsync(Act(job.Manager, job.Order.Id, "verify"));
        await OrderAsync(Act(job.Approver, job.Order.Id, "close"));
        Assert.Equal(HttpStatusCode.Conflict, (await job.Requester.PostAsync(Url("attachments"), FileForm(Png, "late.png"))).StatusCode);

        // Audit trail: upload and download are on record.
        await using var platform = sql.App(job.Tenant);
        Assert.True(await platform.AuditEvents.AnyAsync(a => a.Action == "workattachment.uploaded" && a.EntityId == photo.Id.ToString()));
        Assert.True(await platform.AuditEvents.AnyAsync(a => a.Action == "workattachment.downloaded" && a.EntityId == photo.Id.ToString()));
        Assert.True(await platform.AuditEvents.AnyAsync(a => a.Action == "workattachment.deleted" && a.EntityId == photo.Id.ToString()));
    }

    // ---------------- isolation and evidence ----------------

    [Fact]
    public async Task Records_are_isolated_per_tenant_and_the_ledgers_cannot_be_rewritten_even_with_sql()
    {
        var job = await NewJobAsync();
        var foreign = await _api.ClientAsync(TenantA, SystemRoles.FacilityManager);
        string Url(string tail) => $"/api/v1/work-orders/{job.Order.Id}/{tail}";
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var time = await ReadAsync<TimeEntryDto[]>(await PostAsync(job.Technician, Url("time"), new { workedOn = today, minutes = 45 }));
        var cost = await ReadAsync<CostLineDto[]>(await PostAsync(job.Technician, Url("costs"), new { kind = "Material", description = "Gasket", quantity = 1, unitCost = 12 }));
        await PostAsync(job.Technician, Url("signatures"), new { role = "Technician", signerName = "Tina", imageBase64 = PngData });
        var photo = await ReadAsync<AttachmentDto>(await job.Technician.PostAsync(Url("attachments"), FileForm(Png, "p.png")));
        await PostAsync(job.Technician, Url($"costs/{cost[0].Id}/void"), new { reason = "typo" });

        // Another tenant's manager reaches none of it.
        foreach (var path in new[] { "checklist", "time", "costs", "cost-summary", "signatures", "attachments" })
            Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync(Url(path))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/api/v1/work-orders/attachments/{photo.Id}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(foreign, Url($"time/{time[0].Id}/void"), new { reason = "x" })).StatusCode);

        // Row-level security on every new table.
        await using (var other = sql.Work(TenantA))
        {
            Assert.Equal(0, await other.TimeEntries.IgnoreQueryFilters().CountAsync(t => t.WorkOrderId == job.Order.Id));
            Assert.Equal(0, await other.CostLines.IgnoreQueryFilters().CountAsync(t => t.WorkOrderId == job.Order.Id));
            Assert.Equal(0, await other.Voids.IgnoreQueryFilters().CountAsync(t => t.WorkOrderId == job.Order.Id));
            Assert.Equal(0, await other.Signatures.IgnoreQueryFilters().CountAsync(t => t.WorkOrderId == job.Order.Id));
            Assert.Equal(0, await other.Attachments.IgnoreQueryFilters().CountAsync(t => t.WorkOrderId == job.Order.Id));
            Assert.Equal(0, await other.ChecklistTemplates.IgnoreQueryFilters().CountAsync(t => t.TenantId == job.Tenant));
        }

        // Append-only even for the owning tenant with raw SQL.
        await using var own = sql.Work(job.Tenant);
        await Assert.ThrowsAnyAsync<Exception>(() => own.Database.ExecuteSqlRawAsync("DELETE FROM work.WorkOrderTimeEntries WHERE WorkOrderId = {0}", job.Order.Id));
        await Assert.ThrowsAnyAsync<Exception>(() => own.Database.ExecuteSqlRawAsync("DELETE FROM work.WorkOrderCostLines WHERE WorkOrderId = {0}", job.Order.Id));
        await Assert.ThrowsAnyAsync<Exception>(() => own.Database.ExecuteSqlRawAsync("DELETE FROM work.WorkOrderVoids WHERE WorkOrderId = {0}", job.Order.Id));
        await Assert.ThrowsAnyAsync<Exception>(() => own.Database.ExecuteSqlRawAsync("DELETE FROM work.WorkOrderSignatures WHERE WorkOrderId = {0}", job.Order.Id));
        await Assert.ThrowsAnyAsync<Exception>(() => own.Database.ExecuteSqlRawAsync("UPDATE work.WorkOrderTimeEntries SET Minutes = 1 WHERE WorkOrderId = {0}", job.Order.Id));
        await Assert.ThrowsAnyAsync<Exception>(() => own.Database.ExecuteSqlRawAsync("UPDATE work.WorkOrderCostLines SET UnitCost = 1 WHERE WorkOrderId = {0}", job.Order.Id));
        Assert.Equal(1, await own.TimeEntries.CountAsync(t => t.WorkOrderId == job.Order.Id));
    }
}
