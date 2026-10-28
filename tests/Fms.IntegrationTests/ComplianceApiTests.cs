using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Fms.Api;
using Fms.BuildingBlocks.Results;
using Fms.Compliance.Domain;
using Fms.Compliance.Services;
using Fms.Facilities.Services;
using Fms.Platform.Notifications;
using Fms.Platform.Security;
using Fms.Platform.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static Fms.IntegrationTests.ApiHarness;

namespace Fms.IntegrationTests;

[Collection(SqlCollection.Name)]
public sealed class ComplianceApiTests(SqlServerFixture sql) : IDisposable
{
    private readonly ApiHarness _api = new(sql, settings: new Dictionary<string, string?> { ["Storage:Provider"] = "InMemory" });
    public void Dispose() => _api.Dispose();

    private sealed record Rig(Guid Tenant, HttpClient Manager, HttpClient Second, HttpClient Technician, HttpClient Viewer, HttpClient Requester, LocationDto Site, LocationDto Plant);

    private async Task<Rig> NewRigAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var platform = sql.App(null, platform: true))
        {
            platform.Tenants.Add(new Fms.Platform.Domain.Tenant { Id = tenant, Name = "Cmp " + tenant.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
            await platform.SaveChangesAsync();
            await SystemRoleSync.EnsureAsync(platform, tenant);
        }

        var manager = await _api.ClientAsync(tenant, SystemRoles.FacilityManager);
        var site = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", new { type = "Site", code = FacilitiesScope.Code("S"), name = "HQ" }));
        var plant = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", new { type = "Building", parentId = site.Id, code = FacilitiesScope.Code("B"), name = "Plant room" }));
        return new Rig(tenant, manager, await _api.ClientAsync(tenant, SystemRoles.FacilityManager), await _api.ClientAsync(tenant, SystemRoles.Technician),
            await _api.ClientAsync(tenant, SystemRoles.Viewer), await _api.ClientAsync(tenant, SystemRoles.Requester), site, plant);
    }

    private static async Task<Guid> MeAsync(HttpClient c) => (await c.GetFromJsonAsync<MeInfo>("/api/v1/me"))!.UserId!.Value;

    private static async Task<ObligationDto> NewObligationAsync(HttpClient c, Rig rig, object? more = null)
    {
        var body = new Dictionary<string, object?> { ["title"] = "Fire alarm inspection", ["category"] = "Fire", ["intervalMonths"] = 12, ["leadDays"] = 30, ["firstDueOn"] = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(120).ToString("yyyy-MM-dd"), ["locationId"] = rig.Site.Id };
        if (more is not null) foreach (var p in more.GetType().GetProperties()) body[p.Name] = p.GetValue(more);
        var response = await PostAsync(c, "/api/v1/compliance/obligations", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<ObligationDto>(response);
    }

    private static string Day(int offset) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(offset).ToString("yyyy-MM-dd");

    // ------------------------------------------------------------------ access

    [Fact]
    public async Task Only_the_right_roles_can_read_or_manage_compliance_and_permits()
    {
        var rig = await NewRigAsync();
        Assert.Equal(HttpStatusCode.OK, (await rig.Viewer.GetAsync("/api/v1/compliance/obligations")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rig.Viewer.GetAsync("/api/v1/permits")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Viewer, "/api/v1/compliance/obligations", new { title = "x", category = "Fire" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Technician, "/api/v1/compliance/obligations", new { title = "x", category = "Fire" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Requester.GetAsync("/api/v1/compliance/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GuestClient().GetAsync("/api/v1/permits")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Viewer, "/api/v1/permits", new { })).StatusCode);
    }

    // ------------------------------------------------------------------ obligations

    [Fact]
    public async Task An_obligation_moves_from_scheduled_to_compliant_and_a_failure_shows_until_it_is_redone()
    {
        var rig = await NewRigAsync();
        var o = await NewObligationAsync(rig.Manager, rig);
        Assert.Equal(ComplianceStatus.Scheduled, o.Status);

        var near = await NewObligationAsync(rig.Manager, rig, new { title = "Lift certificate", firstDueOn = Day(10) });
        Assert.Equal(ComplianceStatus.DueSoon, near.Status);
        var late = await NewObligationAsync(rig.Manager, rig, new { title = "Water hygiene", firstDueOn = Day(-3) });
        Assert.Equal(ComplianceStatus.Overdue, late.Status);

        var list = await ReadAsync<Page<ObligationDto>>(await rig.Viewer.GetAsync("/api/v1/compliance/obligations"));
        Assert.Equal(["Water hygiene", "Lift certificate", "Fire alarm inspection"], list.Items.Select(x => x.Title));
        Assert.Single((await ReadAsync<Page<ObligationDto>>(await rig.Viewer.GetAsync("/api/v1/compliance/obligations?status=Overdue"))).Items);

        // Done and valid for a year.
        var done = await PostAsync(rig.Manager, $"/api/v1/compliance/obligations/{late.Id}/completions", new { performedOn = Day(-1), result = "Passed", reference = "CERT-1", providerName = "SafeCo" });
        Assert.Equal(HttpStatusCode.Created, done.StatusCode);
        var detail = await ReadAsync<ObligationDetailDto>(await rig.Viewer.GetAsync($"/api/v1/compliance/obligations/{late.Id}"));
        Assert.Equal(ComplianceStatus.Compliant, detail.Obligation.Status);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1).AddMonths(12), detail.Obligation.ValidUntil);
        Assert.Single(detail.Completions);

        // A failed inspection needs an explanation and shows as failed even though the old certificate has not run out.
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, $"/api/v1/compliance/obligations/{late.Id}/completions", new { performedOn = Day(0), result = "Failed" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(rig.Manager, $"/api/v1/compliance/obligations/{late.Id}/completions", new { performedOn = Day(0), result = "Failed", notes = "Two detectors dead" })).StatusCode);
        Assert.Equal(ComplianceStatus.Failed, (await ReadAsync<ObligationDetailDto>(await rig.Viewer.GetAsync($"/api/v1/compliance/obligations/{late.Id}"))).Obligation.Status);

        // Redone and passed: back to compliant, with both attempts kept.
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(rig.Manager, $"/api/v1/compliance/obligations/{late.Id}/completions", new { performedOn = Day(0), result = "PassedWithRemarks", notes = "Replaced" })).StatusCode);
        var again = await ReadAsync<ObligationDetailDto>(await rig.Viewer.GetAsync($"/api/v1/compliance/obligations/{late.Id}"));
        Assert.Equal(ComplianceStatus.Compliant, again.Obligation.Status);
        Assert.Equal(3, again.Completions.Count);

        var summary = await ReadAsync<ComplianceSummaryDto>(await rig.Viewer.GetAsync("/api/v1/compliance/summary"));
        Assert.Equal(3, summary.Total);
        Assert.Equal(1, summary.Compliant);
        Assert.Equal(1, summary.DueSoon);
        Assert.Equal(1, summary.Scheduled);
        Assert.Equal(100, summary.PercentCompliant); // compliant and due-soon count; the unscheduled one is not measured
    }

    [Fact]
    public async Task Validation_stops_nonsense_and_an_older_record_does_not_override_a_newer_one()
    {
        var rig = await NewRigAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/compliance/obligations", new { title = "", category = "Fire" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/compliance/obligations", new { title = "x", category = "Fire", intervalMonths = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/compliance/obligations", new { title = "x", category = "Fire", leadDays = 1000 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/compliance/obligations", new { title = "x", category = "Fire", locationId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/compliance/obligations", new { title = "x", category = "Fire", ownerId = Guid.NewGuid() })).StatusCode);

        var o = await NewObligationAsync(rig.Manager, rig);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, $"/api/v1/compliance/obligations/{o.Id}/completions", new { performedOn = Day(5), result = "Passed" })).StatusCode); // future
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, $"/api/v1/compliance/obligations/{o.Id}/completions", new { performedOn = Day(-1), result = "Passed", validUntil = Day(-2) })).StatusCode);

        await PostAsync(rig.Manager, $"/api/v1/compliance/obligations/{o.Id}/completions", new { performedOn = Day(-10), result = "Passed" });
        await PostAsync(rig.Manager, $"/api/v1/compliance/obligations/{o.Id}/completions", new { performedOn = Day(-400), result = "Failed", notes = "Old record entered late" });
        var d = await ReadAsync<ObligationDetailDto>(await rig.Viewer.GetAsync($"/api/v1/compliance/obligations/{o.Id}"));
        Assert.Equal(ComplianceStatus.Compliant, d.Obligation.Status); // the old failure is history, not the present
        Assert.Equal(2, d.Completions.Count);
    }

    [Fact]
    public async Task Archived_obligations_leave_the_lists_and_cannot_be_recorded_against_and_stale_edits_are_refused()
    {
        var rig = await NewRigAsync();
        var o = await NewObligationAsync(rig.Manager, rig);
        var updated = await PutAsync(rig.Manager, $"/api/v1/compliance/obligations/{o.Id}", new { title = "Fire alarm service", category = "Fire", intervalMonths = 6, leadDays = 14, firstDueOn = Day(100), locationId = rig.Site.Id, rowVersion = o.RowVersion });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var stale = await PutAsync(rig.Second, $"/api/v1/compliance/obligations/{o.Id}", new { title = "Other", category = "Fire", rowVersion = o.RowVersion });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var current = await ReadAsync<ObligationDto>(updated);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Manager, $"/api/v1/compliance/obligations/{o.Id}/archive", new { rowVersion = current.RowVersion })).StatusCode);
        Assert.Empty((await ReadAsync<Page<ObligationDto>>(await rig.Viewer.GetAsync("/api/v1/compliance/obligations"))).Items);
        Assert.Single((await ReadAsync<Page<ObligationDto>>(await rig.Viewer.GetAsync("/api/v1/compliance/obligations?includeArchived=true"))).Items);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, $"/api/v1/compliance/obligations/{o.Id}/completions", new { performedOn = Day(-1), result = "Passed" })).StatusCode);
    }

    [Fact]
    public async Task The_history_of_completions_cannot_be_changed_even_by_the_application_login()
    {
        var rig = await NewRigAsync();
        var o = await NewObligationAsync(rig.Manager, rig);
        await PostAsync(rig.Manager, $"/api/v1/compliance/obligations/{o.Id}/completions", new { performedOn = Day(-1), result = "Passed" });

        await using var db = sql.Compliance(rig.Tenant);
        var c = await db.Completions.FirstAsync();
        c.Notes = "rewritten";
        await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync());
        await using var raw = sql.Compliance(rig.Tenant);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => raw.Database.ExecuteSqlRawAsync("UPDATE compliance.Completions SET Notes = N'rewritten'"));
        Assert.Contains("append-only", ex.ToString());
        await Assert.ThrowsAnyAsync<Exception>(() => raw.Database.ExecuteSqlRawAsync("DELETE FROM compliance.Completions"));
    }

    [Fact]
    public async Task Certificates_are_uploaded_by_content_downloaded_and_removed_only_with_a_reason()
    {
        var rig = await NewRigAsync();
        var o = await NewObligationAsync(rig.Manager, rig);
        var completion = await ReadAsync<CompletionDto>(await PostAsync(rig.Manager, $"/api/v1/compliance/obligations/{o.Id}/completions", new { performedOn = Day(-1), result = "Passed" }));

        MultipartFormDataContent Form(byte[] bytes, string name)
        {
            var content = new MultipartFormDataContent();
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(file, "file", name);
            return content;
        }

        var pdf = "%PDF-1.7\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\n%%EOF"u8.ToArray();
        var up = await rig.Manager.PostAsync($"/api/v1/compliance/completions/{completion.Id}/documents", Form(pdf, "cert.pdf"));
        Assert.True(up.StatusCode == HttpStatusCode.Created, await up.Content.ReadAsStringAsync());
        var doc = await ReadAsync<CompletionDocumentDto>(up);
        Assert.Equal("application/pdf", doc.ContentType);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await rig.Manager.PostAsync($"/api/v1/compliance/completions/{completion.Id}/documents", Form([0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00, 0xFF, 0xFF], "evil.pdf"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Viewer.PostAsync($"/api/v1/compliance/completions/{completion.Id}/documents", Form(pdf, "x.pdf"))).StatusCode);

        var download = await rig.Viewer.GetAsync($"/api/v1/compliance/documents/{doc.Id}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(pdf, await download.Content.ReadAsByteArrayAsync());
        Assert.True(download.Headers.CacheControl?.NoStore);

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, $"/api/v1/compliance/documents/{doc.Id}/delete", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Manager, $"/api/v1/compliance/documents/{doc.Id}/delete", new { reason = "Wrong file" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.Viewer.GetAsync($"/api/v1/compliance/documents/{doc.Id}")).StatusCode);
        Assert.Empty((await ReadAsync<ObligationDetailDto>(await rig.Viewer.GetAsync($"/api/v1/compliance/obligations/{o.Id}"))).Completions.Single().Documents);
    }

    [Fact]
    public async Task The_daily_check_warns_the_owner_once_per_change_and_not_again_the_same_day()
    {
        var rig = await NewRigAsync();
        var owner = rig.Second;
        var ownerId = await MeAsync(owner);
        var o = await NewObligationAsync(rig.Manager, rig, new { title = "Boiler certificate", firstDueOn = Day(-2), ownerId });

        var monitor = new ComplianceMonitor(_api.Factory.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ComplianceMonitor>.Instance);
        await monitor.RunOnceAsync();
        await monitor.RunOnceAsync();

        var notices = (await ReadAsync<Page<NotificationDto>>(await owner.GetAsync("/api/v1/notifications?pageSize=50"))).Items.Where(n => n.Type == "compliance.overdue").ToList();
        Assert.Single(notices);
        Assert.Contains("Boiler certificate", notices[0].Data.ToString());
        Assert.DoesNotContain((await ReadAsync<Page<NotificationDto>>(await rig.Viewer.GetAsync("/api/v1/notifications?pageSize=50"))).Items, n => n.Type.StartsWith("compliance.", StringComparison.Ordinal));

        // Doing it clears the warning state; a failure later warns the owner and the managers.
        await PostAsync(rig.Manager, $"/api/v1/compliance/obligations/{o.Id}/completions", new { performedOn = Day(0), result = "Failed", notes = "Leak" });
        await monitor.RunOnceAsync();
        Assert.Contains((await ReadAsync<Page<NotificationDto>>(await owner.GetAsync("/api/v1/notifications?pageSize=50"))).Items, n => n.Type == "compliance.failed");
    }

    [Fact]
    public void The_alert_source_id_is_stable_for_the_same_state_and_day_and_differs_otherwise()
    {
        var id = Guid.NewGuid();
        var day = new DateOnly(2027, 1, 5);
        Assert.Equal(ComplianceAlerts.SourceId(id, ComplianceStatus.Overdue, day), ComplianceAlerts.SourceId(id, ComplianceStatus.Overdue, day));
        Assert.NotEqual(ComplianceAlerts.SourceId(id, ComplianceStatus.Overdue, day), ComplianceAlerts.SourceId(id, ComplianceStatus.Failed, day));
        Assert.NotEqual(ComplianceAlerts.SourceId(id, ComplianceStatus.Overdue, day), ComplianceAlerts.SourceId(id, ComplianceStatus.Overdue, day.AddDays(7)));
    }

    // ------------------------------------------------------------------ permits

    private static async Task<PermitTypeDto> NewTypeAsync(HttpClient c, string code, bool exclusive = true, bool isolation = false, string[]? precautions = null, int hours = 8)
    {
        var response = await PostAsync(c, "/api/v1/compliance/permit-types", new { code, name = "Hot work " + code, nameAr = "عمل ساخن", maxDurationHours = hours, exclusive, requiresIsolation = isolation, precautions = precautions ?? ["Fire watch posted", "Extinguisher at hand"] });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<PermitTypeDto>(response);
    }

    private static async Task<PermitDto> RequestAsync(HttpClient c, Rig rig, PermitTypeDto type, DateTimeOffset? start = null, int hours = 2, Guid? location = null)
    {
        var s = start ?? DateTimeOffset.UtcNow.AddMinutes(10);
        var response = await PostAsync(c, "/api/v1/permits", new { typeId = type.Id, locationId = location ?? rig.Plant.Id, description = "Weld the pipe bracket", contractorName = "Weld & Co", plannedStart = s, plannedEnd = s.AddHours(hours) });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<PermitDto>(response);
    }

    private static Task<HttpResponseMessage> Act(HttpClient c, PermitDto p, string action, object? body = null) =>
        PostAsync(c, $"/api/v1/permits/{p.Id}/{action}", body ?? new { rowVersion = p.RowVersion });

    private static async Task<PermitDto> Ok(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await ReadAsync<PermitDto>(r);
    }

    private static object IssueBody(PermitDto p, string[]? confirmed = null, string? isolation = null) =>
        new { confirmedPrecautions = confirmed ?? p.Precautions.Select(x => x.Text).ToArray(), isolationReference = isolation, rowVersion = p.RowVersion };

    [Fact]
    public async Task A_permit_follows_the_fixed_lifecycle_with_different_people_at_each_gate_and_a_full_trail()
    {
        var rig = await NewRigAsync();
        var type = await NewTypeAsync(rig.Manager, "HOT");
        var requested = await RequestAsync(rig.Technician, rig, type);
        Assert.Equal(PermitStatus.Requested, requested.Status);
        Assert.Equal(1, requested.Number);
        Assert.Equal(2, requested.Precautions.Count);
        Assert.All(requested.Precautions, x => Assert.False(x.Confirmed));

        // The requester cannot take later steps; people who can approve cannot approve their own request.
        Assert.Equal(HttpStatusCode.Forbidden, (await Act(rig.Technician, requested, "approve")).StatusCode);
        var managersOwn = await RequestAsync(rig.Manager, rig, type, start: DateTimeOffset.UtcNow.AddHours(20));
        var own = await Act(rig.Manager, managersOwn, "approve");
        Assert.Equal(HttpStatusCode.Forbidden, own.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Act(rig.Manager, managersOwn, "reject", new { reason = "no", rowVersion = managersOwn.RowVersion })).StatusCode);

        // Cannot issue before approval.
        Assert.Equal(HttpStatusCode.Conflict, (await Act(rig.Second, requested, "issue", IssueBody(requested))).StatusCode);

        var approved = await Ok(await Act(rig.Manager, requested, "approve"));
        Assert.Equal(PermitStatus.Approved, approved.Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Act(rig.Technician, approved, "issue", IssueBody(approved))).StatusCode); // no right to issue

        // Every precaution must be confirmed.
        var partial = await Act(rig.Second, approved, "issue", IssueBody(approved, ["Fire watch posted"]));
        Assert.Equal(HttpStatusCode.BadRequest, partial.StatusCode);
        Assert.Contains("Extinguisher at hand", await partial.Content.ReadAsStringAsync());

        var issued = await Ok(await Act(rig.Second, approved, "issue", IssueBody(approved)));
        Assert.Equal(PermitStatus.Issued, issued.Status);
        Assert.All(issued.Precautions, x => Assert.True(x.Confirmed));

        // Whoever issued it cannot also accept it; someone else with the right can.
        Assert.Equal(HttpStatusCode.Forbidden, (await Act(rig.Second, issued, "start")).StatusCode);
        var started = await Ok(await Act(rig.Technician, issued, "start"));
        Assert.Equal(PermitStatus.Started, started.Status);

        // Closing needs the safety statement.
        Assert.Equal(HttpStatusCode.BadRequest, (await Act(rig.Technician, started, "close", new { areaSafe = false, rowVersion = started.RowVersion })).StatusCode);
        var closed = await Ok(await Act(rig.Technician, started, "close", new { areaSafe = true, note = "Bracket welded, area clear", rowVersion = started.RowVersion }));
        Assert.Equal(PermitStatus.Closed, closed.Status);
        Assert.True(closed.AreaSafe);

        // Nothing moves after the end.
        Assert.Equal(HttpStatusCode.Conflict, (await Act(rig.Manager, closed, "cancel", new { reason = "oops", rowVersion = closed.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Act(rig.Technician, closed, "start")).StatusCode);

        var detail = await ReadAsync<PermitDetailDto>(await rig.Viewer.GetAsync($"/api/v1/permits/{requested.Id}"));
        Assert.Equal(["requested", "approved", "issued", "started", "closed"], detail.Events.Select(e => e.Action));
        Assert.Equal(5, detail.Events.Select(e => e.ByName).Count());

        await using var db = sql.Compliance(rig.Tenant);
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM compliance.PermitEvents"));
    }

    [Fact]
    public async Task A_permit_cannot_be_longer_than_its_type_allows_or_ask_for_a_missing_place_or_the_past()
    {
        var rig = await NewRigAsync();
        var type = await NewTypeAsync(rig.Manager, "SHORT", hours: 4);
        var start = DateTimeOffset.UtcNow.AddHours(1);
        Task<HttpResponseMessage> Ask(object body) => PostAsync(rig.Technician, "/api/v1/permits", body);
        Assert.Equal(HttpStatusCode.BadRequest, (await Ask(new { typeId = type.Id, locationId = rig.Plant.Id, description = "x", plannedStart = start, plannedEnd = start.AddHours(5) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Ask(new { typeId = type.Id, locationId = Guid.NewGuid(), description = "x", plannedStart = start, plannedEnd = start.AddHours(1) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Ask(new { typeId = type.Id, locationId = rig.Plant.Id, description = "x", plannedStart = start.AddHours(-5), plannedEnd = start.AddHours(-3) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Ask(new { typeId = type.Id, locationId = rig.Plant.Id, description = "", plannedStart = start, plannedEnd = start.AddHours(1) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Ask(new { typeId = Guid.NewGuid(), locationId = rig.Plant.Id, description = "x", plannedStart = start, plannedEnd = start.AddHours(1) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Ask(new { typeId = type.Id, locationId = rig.Plant.Id, description = "x", plannedStart = start, plannedEnd = start.AddHours(1), workOrderId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Ask(new { typeId = type.Id, locationId = rig.Plant.Id, description = "x", plannedStart = start, plannedEnd = start.AddHours(4) })).StatusCode);
    }

    [Fact]
    public async Task An_exclusive_permit_stands_alone_in_its_place_until_it_is_closed()
    {
        var rig = await NewRigAsync();
        var exclusive = await NewTypeAsync(rig.Manager, "EXC", exclusive: true);
        var plain = await NewTypeAsync(rig.Manager, "PLAIN", exclusive: false, precautions: []);
        var a = await RequestAsync(rig.Technician, rig, exclusive);
        var b = await RequestAsync(rig.Technician, rig, exclusive);
        var other = await RequestAsync(rig.Technician, rig, exclusive, location: rig.Site.Id);

        a = await Ok(await Act(rig.Manager, a, "approve"));
        b = await Ok(await Act(rig.Manager, b, "approve"));
        other = await Ok(await Act(rig.Manager, other, "approve"));
        a = await Ok(await Act(rig.Second, a, "issue", IssueBody(a)));

        var clash = await Act(rig.Second, b, "issue", IssueBody(b));
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Contains("PTW-0001", await clash.Content.ReadAsStringAsync());

        // A different place is not affected.
        Assert.Equal(PermitStatus.Issued, (await Ok(await Act(rig.Second, other, "issue", IssueBody(other)))).Status);

        // A plain permit in the same place still clashes with an exclusive one in force.
        var c = await RequestAsync(rig.Technician, rig, plain);
        c = await Ok(await Act(rig.Manager, c, "approve"));
        Assert.Equal(HttpStatusCode.Conflict, (await Act(rig.Second, c, "issue", IssueBody(c))).StatusCode);

        a = await Ok(await Act(rig.Technician, a, "close", new { areaSafe = true, rowVersion = a.RowVersion }));
        b = await Ok(await Act(rig.Second, b, "issue", IssueBody(b)));
        Assert.Equal(PermitStatus.Issued, b.Status);

        // Two plain permits do not interfere.
        var d = await RequestAsync(rig.Technician, rig, plain, location: rig.Site.Id);
        var e = await RequestAsync(rig.Technician, rig, plain, location: rig.Site.Id);
        d = await Ok(await Act(rig.Manager, d, "approve"));
        e = await Ok(await Act(rig.Manager, e, "approve"));
        Assert.Equal(HttpStatusCode.Conflict, (await Act(rig.Second, d, "issue", IssueBody(d))).StatusCode); // "other" (exclusive) still stands at the site
    }

    [Fact]
    public async Task Parallel_issues_for_one_place_let_only_one_through()
    {
        var rig = await NewRigAsync();
        var type = await NewTypeAsync(rig.Manager, "RACE");
        var permits = new List<PermitDto>();
        for (var i = 0; i < 4; i++) permits.Add(await Ok(await Act(rig.Manager, await RequestAsync(rig.Technician, rig, type), "approve")));
        var results = await Task.WhenAll(permits.Select(p => Act(rig.Second, p, "issue", IssueBody(p))));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(3, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Isolation_is_demanded_when_the_type_says_so_and_rejection_and_cancellation_need_reasons()
    {
        var rig = await NewRigAsync();
        var live = await NewTypeAsync(rig.Manager, "LIVE", isolation: true, precautions: ["Circuit proved dead"]);
        var p = await Ok(await Act(rig.Manager, await RequestAsync(rig.Technician, rig, live), "approve"));
        Assert.Equal(HttpStatusCode.BadRequest, (await Act(rig.Second, p, "issue", IssueBody(p))).StatusCode);
        var issued = await Ok(await Act(rig.Second, p, "issue", IssueBody(p, isolation: "LOTO-4411")));
        Assert.Equal("LOTO-4411", issued.IsolationReference);

        var r = await RequestAsync(rig.Technician, rig, live, start: DateTimeOffset.UtcNow.AddHours(30));
        Assert.Equal(HttpStatusCode.BadRequest, (await Act(rig.Manager, r, "reject", new { rowVersion = r.RowVersion })).StatusCode);
        var rejected = await Ok(await Act(rig.Manager, r, "reject", new { reason = "Needs a method statement", rowVersion = r.RowVersion }));
        Assert.Equal(PermitStatus.Rejected, rejected.Status);
        Assert.Equal("Needs a method statement", rejected.RejectReason);

        // The person who asked may withdraw; an issued permit may only be withdrawn by someone who can issue.
        var w = await RequestAsync(rig.Technician, rig, live, start: DateTimeOffset.UtcNow.AddHours(40));
        Assert.Equal(HttpStatusCode.BadRequest, (await Act(rig.Technician, w, "cancel", new { rowVersion = w.RowVersion })).StatusCode);
        Assert.Equal(PermitStatus.Cancelled, (await Ok(await Act(rig.Technician, w, "cancel", new { reason = "Postponed", rowVersion = w.RowVersion }))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Act(rig.Technician, issued, "cancel", new { reason = "Changed mind", rowVersion = issued.RowVersion })).StatusCode);
        Assert.Equal(PermitStatus.Cancelled, (await Ok(await Act(rig.Second, issued, "cancel", new { reason = "Unsafe conditions", rowVersion = issued.RowVersion }))).Status);

        // Nobody else's permit can be cancelled by someone with no say.
        var other = await RequestAsync(rig.Manager, rig, live, start: DateTimeOffset.UtcNow.AddHours(50));
        Assert.Equal(HttpStatusCode.Forbidden, (await Act(rig.Technician, other, "cancel", new { reason = "Not mine", rowVersion = other.RowVersion })).StatusCode);
    }

    [Fact]
    public async Task A_stale_screen_cannot_override_what_someone_else_just_did()
    {
        var rig = await NewRigAsync();
        var type = await NewTypeAsync(rig.Manager, "STALE", exclusive: false, precautions: []);
        var p = await RequestAsync(rig.Technician, rig, type);
        var asSeen = p;
        await Ok(await Act(rig.Manager, p, "approve"));
        var late = await Act(rig.Second, asSeen, "reject", new { reason = "Too late", rowVersion = asSeen.RowVersion });
        Assert.Contains(late.StatusCode, new[] { HttpStatusCode.Conflict });
    }

    [Fact]
    public async Task Permit_numbers_are_unique_and_gap_free_even_when_asked_for_at_once()
    {
        var rig = await NewRigAsync();
        var type = await NewTypeAsync(rig.Manager, "NUM", exclusive: false, precautions: []);
        var made = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => RequestAsync(rig.Technician, rig, type)));
        Assert.Equal(Enumerable.Range(1, 8), made.Select(x => x.Number).Order());
    }

    [Fact]
    public async Task A_permit_past_its_end_is_flagged_overdue_listed_and_still_blocks_others_in_its_place()
    {
        var rig = await NewRigAsync();
        var type = await NewTypeAsync(rig.Manager, "OVER");
        var first = await Ok(await Act(rig.Manager, await RequestAsync(rig.Technician, rig, type), "approve"));
        first = await Ok(await Act(rig.Second, first, "issue", IssueBody(first)));
        var next = await Ok(await Act(rig.Manager, await RequestAsync(rig.Technician, rig, type, start: DateTimeOffset.UtcNow.AddHours(12)), "approve"));

        await using (var db = sql.Compliance(rig.Tenant))
        {
            var row = await db.Permits.FirstAsync(x => x.Id == first.Id);
            row.PlannedStart = DateTimeOffset.UtcNow.AddHours(-6);
            row.PlannedEnd = DateTimeOffset.UtcNow.AddHours(-1);
            await db.SaveChangesAsync();
        }

        var overdue = await ReadAsync<Page<PermitDto>>(await rig.Viewer.GetAsync("/api/v1/permits?overdue=true"));
        Assert.Equal(first.Id, Assert.Single(overdue.Items).Id);
        Assert.True(overdue.Items[0].IsOverdue);
        var refused = await Act(rig.Second, next, "issue", IssueBody(next));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("must be closed first", await refused.Content.ReadAsStringAsync());
        first = (await ReadAsync<PermitDetailDto>(await rig.Viewer.GetAsync($"/api/v1/permits/{first.Id}"))).Permit;
        var tooLate = await Act(rig.Technician, first, "start");
        Assert.Equal(HttpStatusCode.Conflict, tooLate.StatusCode);
        Assert.Contains("past its end", await tooLate.Content.ReadAsStringAsync());

        // It can still be closed, after which the next can go ahead.
        var closed = await Ok(await Act(rig.Technician, first, "close", new { areaSafe = true, note = "Closed late", rowVersion = first.RowVersion }));
        Assert.Equal(PermitStatus.Closed, closed.Status);
        Assert.Equal(PermitStatus.Issued, (await Ok(await Act(rig.Second, next, "issue", IssueBody(next)))).Status);
    }

    [Fact]
    public async Task Permit_types_are_validated_unique_per_code_and_archived_not_deleted()
    {
        var rig = await NewRigAsync();
        var t = await NewTypeAsync(rig.Manager, "CONF");
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, "/api/v1/compliance/permit-types", new { code = "conf", name = "Dup" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/compliance/permit-types", new { code = "bad code!", name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/compliance/permit-types", new { code = "OK1", name = "x", maxDurationHours = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/compliance/permit-types", new { code = "OK2", name = "x", precautions = new[] { "a", "A" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Technician, "/api/v1/compliance/permit-types", new { code = "OK3", name = "x" })).StatusCode);
        Assert.Single(await ReadAsync<List<PermitTypeDto>>(await rig.Technician.GetAsync("/api/v1/compliance/permit-types")));

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Manager, $"/api/v1/compliance/permit-types/{t.Id}/archive", new { rowVersion = t.RowVersion })).StatusCode);
        Assert.Empty(await ReadAsync<List<PermitTypeDto>>(await rig.Technician.GetAsync("/api/v1/compliance/permit-types")));
        var ask = await PostAsync(rig.Technician, "/api/v1/permits", new { typeId = t.Id, locationId = rig.Plant.Id, description = "x", plannedStart = DateTimeOffset.UtcNow.AddHours(1), plannedEnd = DateTimeOffset.UtcNow.AddHours(2) });
        Assert.Equal(HttpStatusCode.BadRequest, ask.StatusCode);
    }

    [Fact]
    public async Task Approvers_are_told_of_a_request_and_the_requester_of_the_decision()
    {
        var rig = await NewRigAsync();
        var type = await NewTypeAsync(rig.Manager, "NOTE", exclusive: false, precautions: []);
        var p = await RequestAsync(rig.Technician, rig, type);
        Assert.Contains((await ReadAsync<Page<NotificationDto>>(await rig.Second.GetAsync("/api/v1/notifications?pageSize=50"))).Items, n => n.Type == "permit.requested");
        Assert.DoesNotContain((await ReadAsync<Page<NotificationDto>>(await rig.Technician.GetAsync("/api/v1/notifications?pageSize=50"))).Items, n => n.Type == "permit.requested");
        await Ok(await Act(rig.Manager, p, "approve"));
        Assert.Contains((await ReadAsync<Page<NotificationDto>>(await rig.Second.GetAsync("/api/v1/notifications?pageSize=50"))).Items, n => n.Type == "permit.approved");
    }

    // ------------------------------------------------------------------ isolation and reports

    [Fact]
    public async Task Another_organisation_sees_none_of_it()
    {
        var a = await NewRigAsync();
        var b = await NewRigAsync();
        var o = await NewObligationAsync(a.Manager, a);
        var type = await NewTypeAsync(a.Manager, "ISO");
        var p = await RequestAsync(a.Technician, a, type);

        Assert.Equal(HttpStatusCode.NotFound, (await b.Manager.GetAsync($"/api/v1/compliance/obligations/{o.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Manager.GetAsync($"/api/v1/permits/{p.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Act(b.Manager, p, "approve")).StatusCode);
        Assert.Empty((await ReadAsync<Page<ObligationDto>>(await b.Manager.GetAsync("/api/v1/compliance/obligations"))).Items);
        Assert.Empty((await ReadAsync<Page<PermitDto>>(await b.Manager.GetAsync("/api/v1/permits"))).Items);
        Assert.Empty(await ReadAsync<List<PermitTypeDto>>(await b.Manager.GetAsync("/api/v1/compliance/permit-types")));
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(b.Technician, "/api/v1/permits", new { typeId = type.Id, locationId = b.Plant.Id, description = "x", plannedStart = DateTimeOffset.UtcNow.AddHours(1), plannedEnd = DateTimeOffset.UtcNow.AddHours(2) })).StatusCode);

        // Row-level security holds even for a context that forgets its own filter.
        await using var raw = sql.Compliance(b.Tenant);
        Assert.Equal(0, await raw.Obligations.IgnoreQueryFilters().CountAsync(x => x.Id == o.Id));
        Assert.Equal(0, await raw.Permits.IgnoreQueryFilters().CountAsync(x => x.Id == p.Id));
    }

    [Fact]
    public async Task The_dashboard_and_the_data_feed_include_compliance()
    {
        var rig = await NewRigAsync();
        await NewObligationAsync(rig.Manager, rig, new { title = "Late one", firstDueOn = Day(-5) });
        var text = await (await rig.Manager.GetAsync("/api/v1/reports/dashboard")).Content.ReadAsStringAsync();
        Assert.True(text.Contains("compliance.overdue", StringComparison.Ordinal), text);
        Assert.Contains("compliance.percentCompliant", text);
        var admin = await _api.ClientAsync(rig.Tenant, SystemRoles.Administrator);
        var datasets = await (await admin.GetAsync("/api/v1/reports/feed/datasets")).Content.ReadAsStringAsync();
        Assert.Contains("Obligations", datasets);
        Assert.Contains("Permits", datasets);
    }

    // ------------------------------------------------------------------ work orders from failures, overdue permit warnings

    private static async Task<CompletionDto> FailAsync(HttpClient c, ObligationDto o, string notes = "Detector 4 did not respond")
    {
        var response = await PostAsync(c, $"/api/v1/compliance/obligations/{o.Id}/completions", new { performedOn = Day(0), result = "Failed", notes });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<CompletionDto>(response);
    }

    [Fact]
    public async Task A_failed_inspection_raises_one_work_order_when_the_obligation_says_so()
    {
        var rig = await NewRigAsync();
        var o = await NewObligationAsync(rig.Manager, rig, new { title = "Sprinkler test", createWorkOrderOnFail = true });
        Assert.True(o.CreateWorkOrderOnFail);

        var passed = await PostAsync(rig.Manager, $"/api/v1/compliance/obligations/{o.Id}/completions", new { performedOn = Day(-1), result = "Passed" });
        Assert.Null((await ReadAsync<CompletionDto>(passed)).WorkOrderId);

        var failed = await FailAsync(rig.Manager, o);
        Assert.NotNull(failed.WorkOrderId);
        Assert.StartsWith("WO-", failed.WorkOrderNumber);
        var wo = (await ReadAsync<Fms.Work.Services.WorkOrderDetailDto>(await rig.Manager.GetAsync($"/api/v1/work-orders/{failed.WorkOrderId}"))).Order;
        Assert.Equal("Failed inspection: Sprinkler test", wo.Title);
        Assert.Equal(rig.Site.Id, wo.LocationId);
        Assert.Contains("Detector 4", wo.Description);

        // The obligation's history shows the link, and asking for it by hand again gives the same work order.
        var detail = await ReadAsync<ObligationDetailDto>(await rig.Viewer.GetAsync($"/api/v1/compliance/obligations/{o.Id}"));
        Assert.Equal(failed.WorkOrderId, detail.Completions.Single(c => c.Id == failed.Id).WorkOrderId);
        var again = await ReadAsync<Fms.BuildingBlocks.Identity.WorkFromSourceResult>(await PostAsync(rig.Manager, $"/api/v1/compliance/completions/{failed.Id}/work-order", new { }));
        Assert.False(again.Created);
        Assert.Equal(failed.WorkOrderId, again.WorkOrderId);
    }

    [Fact]
    public async Task Without_the_switch_a_manager_can_still_raise_the_work_order_by_hand_once()
    {
        var rig = await NewRigAsync();
        var o = await NewObligationAsync(rig.Manager, rig, new { title = "Lift inspection" });
        Assert.False(o.CreateWorkOrderOnFail);
        var failed = await FailAsync(rig.Manager, o);
        Assert.Null(failed.WorkOrderId);

        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Viewer, $"/api/v1/compliance/completions/{failed.Id}/work-order", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Technician, $"/api/v1/compliance/completions/{failed.Id}/work-order", new { })).StatusCode);
        var first = await ReadAsync<Fms.BuildingBlocks.Identity.WorkFromSourceResult>(await PostAsync(rig.Manager, $"/api/v1/compliance/completions/{failed.Id}/work-order", new { }));
        Assert.True(first.Created);
        var second = await ReadAsync<Fms.BuildingBlocks.Identity.WorkFromSourceResult>(await PostAsync(rig.Second, $"/api/v1/compliance/completions/{failed.Id}/work-order", new { }));
        Assert.False(second.Created);
        Assert.Equal(first.WorkOrderId, second.WorkOrderId);

        var orders = await ReadAsync<Page<Fms.Work.Services.WorkOrderDto>>(await rig.Manager.GetAsync("/api/v1/work-orders?pageSize=100"));
        Assert.Single(orders.Items, w => w.Title == "Failed inspection: Lift inspection");
    }

    [Fact]
    public async Task A_work_order_needs_somewhere_to_go_and_only_a_failure_gets_one_and_other_organisations_cannot_ask()
    {
        var rig = await NewRigAsync();
        var other = await NewRigAsync();
        var noPlace = await PostAsync(rig.Manager, "/api/v1/compliance/obligations", new { title = "Floating", category = "Fire", firstDueOn = Day(60), createWorkOrderOnFail = true });
        Assert.Equal(HttpStatusCode.BadRequest, noPlace.StatusCode);
        Assert.Contains("place or an asset", await noPlace.Content.ReadAsStringAsync());

        var o = await NewObligationAsync(rig.Manager, rig);
        var passed = await ReadAsync<CompletionDto>(await PostAsync(rig.Manager, $"/api/v1/compliance/obligations/{o.Id}/completions", new { performedOn = Day(-1), result = "Passed" }));
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, $"/api/v1/compliance/completions/{passed.Id}/work-order", new { })).StatusCode);

        var failed = await FailAsync(rig.Manager, o);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(other.Manager, $"/api/v1/compliance/completions/{failed.Id}/work-order", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(rig.Manager, $"/api/v1/compliance/completions/{Guid.NewGuid()}/work-order", new { })).StatusCode);
    }

    [Fact]
    public async Task A_permit_left_in_force_past_its_end_is_warned_about_once_a_day_to_the_issuer_and_the_people_who_can_issue()
    {
        var rig = await NewRigAsync();
        var type = await NewTypeAsync(rig.Manager, "LATE");
        var approved = await Ok(await Act(rig.Manager, await RequestAsync(rig.Technician, rig, type), "approve"));
        var issued = await Ok(await Act(rig.Second, approved, "issue", IssueBody(approved)));
        await using (var db = sql.Compliance(rig.Tenant))
        {
            var row = await db.Permits.FirstAsync(x => x.Id == issued.Id);
            row.PlannedStart = DateTimeOffset.UtcNow.AddHours(-6);
            row.PlannedEnd = DateTimeOffset.UtcNow.AddHours(-1);
            await db.SaveChangesAsync();
        }

        var monitor = new ComplianceMonitor(_api.Factory.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ComplianceMonitor>.Instance);
        await monitor.RunOnceAsync();
        await monitor.RunOnceAsync();

        async Task<List<NotificationDto>> Notices(HttpClient c) => (await ReadAsync<Page<NotificationDto>>(await c.GetAsync("/api/v1/notifications?pageSize=50"))).Items.Where(n => n.Type == "permit.overdue").ToList();
        var forIssuer = await Notices(rig.Second);
        Assert.Single(forIssuer);
        Assert.Contains("PTW-", forIssuer[0].Data.ToString());
        Assert.Single(await Notices(rig.Manager)); // can issue permits too
        Assert.Empty(await Notices(rig.Viewer));
        Assert.Empty(await Notices(rig.Technician));
    }
}
