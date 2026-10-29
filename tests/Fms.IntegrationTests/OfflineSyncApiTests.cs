using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Fms.Facilities.Services;
using Fms.Platform.Security;
using Fms.Work.Domain;
using Fms.Work.Services;
using static Fms.IntegrationTests.ApiHarness;

namespace Fms.IntegrationTests;

/// <summary>What a phone that works offline relies on: safe retries, honest times, and a one-call download of its jobs.</summary>
[Collection(SqlCollection.Name)]
public sealed class OfflineSyncApiTests(SqlServerFixture sql) : IDisposable
{
    private readonly ApiHarness _api = new(sql, settings: new Dictionary<string, string?> { ["Storage:Provider"] = "InMemory" });
    public void Dispose() => _api.Dispose();

    private sealed record Me(Guid? UserId);

    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private async Task<Guid> NewTenantAsync()
    {
        var tenant = Guid.NewGuid();
        await using var platform = sql.App(null, platform: true);
        platform.Tenants.Add(new Fms.Platform.Domain.Tenant { Id = tenant, Name = "Offline " + tenant.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
        await platform.SaveChangesAsync();
        await Fms.Platform.Services.SystemRoleSync.EnsureAsync(platform, tenant);
        return tenant;
    }

    private sealed record Job(Guid Tenant, HttpClient Manager, HttpClient Technician, HttpClient Requester, Guid TechId, WorkOrderDto Order);

    private async Task<Job> NewJobAsync(bool start = false)
    {
        var tenant = await NewTenantAsync();
        var manager = await _api.ClientAsync(tenant, SystemRoles.FacilityManager);
        var approver = await _api.ClientAsync(tenant, SystemRoles.FacilityManager);
        var technician = await _api.ClientAsync(tenant, SystemRoles.Technician);
        var requester = await _api.ClientAsync(tenant, SystemRoles.Requester);
        var techId = (await technician.GetFromJsonAsync<Me>("/api/v1/me", Json))!.UserId!.Value;
        var site = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", new { type = "Site", code = FacilitiesScope.Code("S"), name = "HQ" }));
        var order = await ReadAsync<WorkOrderDto>(await PostAsync(requester, "/api/v1/work-orders", new { title = "Fan noise", locationId = site.Id }));
        await PostAsync(approver, $"/api/v1/work-orders/{order.Id}/approve", new { });
        order = await ReadAsync<WorkOrderDto>(await PostAsync(approver, $"/api/v1/work-orders/{order.Id}/assign", new { assigneeId = techId }));
        if (start) order = await ReadAsync<WorkOrderDto>(await PostAsync(technician, $"/api/v1/work-orders/{order.Id}/start", new { }));
        return new Job(tenant, manager, technician, requester, techId, order);
    }

    private static Task<HttpResponseMessage> Send(HttpClient c, HttpMethod method, string url, object? body, string? key = null, DateTimeOffset? performedAt = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        if (performedAt is { } at) request.Headers.Add("X-Performed-At", at.ToString("O"));
        return c.SendAsync(request);
    }

    private static string NewKey() => "t-" + Guid.NewGuid().ToString("N");

    // ---------------- idempotency ----------------

    [Fact]
    public async Task Repeating_a_write_with_the_same_key_does_the_work_once_and_returns_the_first_answer()
    {
        var job = await NewJobAsync(start: true);
        var url = $"/api/v1/work-orders/{job.Order.Id}/time";
        var key = NewKey();
        var body = new { workedOn = DateOnly.FromDateTime(DateTime.UtcNow), minutes = 30, note = "first try" };

        var first = await Send(job.Technician, HttpMethod.Post, url, body, key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.False(first.Headers.Contains("Idempotency-Replayed"));
        var firstBody = await first.Content.ReadAsStringAsync();

        var again = await Send(job.Technician, HttpMethod.Post, url, body, key); // the phone never saw the answer and tries again
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("true", again.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(firstBody, await again.Content.ReadAsStringAsync());

        var entries = await job.Technician.GetFromJsonAsync<TimeEntryDto[]>(url, Json);
        Assert.Single(entries!); // recorded once

        // The same key with a different request is a mistake, not a retry.
        var changed = await Send(job.Technician, HttpMethod.Post, url, new { workedOn = body.workedOn, minutes = 45 }, key);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, changed.StatusCode);
        Assert.Single((await job.Technician.GetFromJsonAsync<TimeEntryDto[]>(url, Json))!);

        // Without a key nothing is remembered: each call is its own.
        await Send(job.Technician, HttpMethod.Post, url, body);
        await Send(job.Technician, HttpMethod.Post, url, body);
        Assert.Equal(3, (await job.Technician.GetFromJsonAsync<TimeEntryDto[]>(url, Json))!.Length);
    }

    [Fact]
    public async Task Failed_requests_are_not_remembered_and_keys_belong_to_one_person()
    {
        var job = await NewJobAsync(start: true);
        var otherTech = await _api.ClientAsync(job.Tenant, SystemRoles.Technician);
        var url = $"/api/v1/work-orders/{job.Order.Id}/time";
        var key = NewKey();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // A refused request leaves the key free: fixing the request and retrying with it works.
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(job.Technician, HttpMethod.Post, url, new { workedOn = today, minutes = 0 }, key)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(job.Technician, HttpMethod.Post, url, new { workedOn = today, minutes = 20 }, key)).StatusCode);

        // The same key from somebody else is a different key; it cannot replay or collide with another person's.
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(otherTech, HttpMethod.Post, url, new { workedOn = today, minutes = 20 }, key)).StatusCode);
        var manager = await Send(job.Manager, HttpMethod.Post, url, new { workedOn = today, minutes = 20 }, key);
        Assert.Equal(HttpStatusCode.OK, manager.StatusCode);
        Assert.False(manager.Headers.Contains("Idempotency-Replayed"));
        Assert.Equal(2, (await job.Technician.GetFromJsonAsync<TimeEntryDto[]>(url, Json))!.Length);
    }

    [Fact]
    public async Task Parallel_duplicates_of_one_request_do_the_work_once()
    {
        var job = await NewJobAsync(start: true);
        var url = $"/api/v1/work-orders/{job.Order.Id}/costs";
        var key = NewKey();
        var body = new { kind = "Material", description = "Fan belt", quantity = 1, unitCost = 30 };

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Send(job.Technician, HttpMethod.Post, url, body, key)));

        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, r.StatusCode.ToString()));
        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(responses.Where(r => r.StatusCode == HttpStatusCode.Conflict), r => Assert.True(r.Headers.Contains("Retry-After"))); // "still working on it, ask again"
        Assert.Single((await job.Technician.GetFromJsonAsync<CostLineDto[]>(url, Json))!);
    }

    [Fact]
    public async Task Uploads_are_retry_safe_too_and_bad_keys_are_refused()
    {
        var job = await NewJobAsync(start: true);
        var url = $"/api/v1/work-orders/{job.Order.Id}/attachments";
        var key = NewKey();

        HttpRequestMessage Upload()
        {
            var form = new MultipartFormDataContent { { new ByteArrayContent(Png), "file", "p.png" }, { new StringContent("Before"), "kind" } };
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
            request.Headers.Add("Idempotency-Key", key);
            return request;
        }

        Assert.Equal(HttpStatusCode.Created, (await job.Technician.SendAsync(Upload())).StatusCode);
        var again = await job.Technician.SendAsync(Upload());
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.Equal("true", again.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Single((await job.Technician.GetFromJsonAsync<AttachmentDto[]>(url, Json))!);

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(job.Technician, HttpMethod.Post, $"/api/v1/work-orders/{job.Order.Id}/comments", new { body = "hi" }, "short")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(job.Technician, HttpMethod.Post, $"/api/v1/work-orders/{job.Order.Id}/comments", new { body = "hi" }, "has spaces and <markup>")).StatusCode);
    }

    [Fact]
    public async Task A_key_does_not_let_anyone_do_what_they_may_not()
    {
        var job = await NewJobAsync(start: true);
        var key = NewKey();
        // The requester cannot approve, with or without a key; and a refused call is not remembered.
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(job.Requester, HttpMethod.Post, $"/api/v1/work-orders/{job.Order.Id}/approve", new { }, key)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(job.Requester, HttpMethod.Post, $"/api/v1/work-orders/{job.Order.Id}/approve", new { }, key)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(_api.Factory.CreateClient(), HttpMethod.Post, $"/api/v1/work-orders/{job.Order.Id}/start", new { }, NewKey())).StatusCode);
    }

    // ---------------- honest times ----------------

    [Fact]
    public async Task An_action_done_offline_counts_from_when_it_was_done_within_limits()
    {
        var job = await NewJobAsync();
        var url = $"/api/v1/work-orders/{job.Order.Id}";
        var floor = await ReadAsync<WorkOrderDetailDto>(await job.Manager.GetAsync(url));
        var lastChange = floor.Events.Max(e => e.At);

        // A claim before the last change is lifted to it (history cannot run backwards).
        var started = await ReadAsync<WorkOrderDto>(await Send(job.Technician, HttpMethod.Post, $"{url}/start", new { }, performedAt: lastChange.AddHours(-3)));
        var startedEvent = (await ReadAsync<WorkOrderDetailDto>(await job.Manager.GetAsync(url))).Events.Single(e => e.Action == WorkOrderAction.Started);
        Assert.True(startedEvent.At >= lastChange, "never before the previous event");
        Assert.True(started.Sla.RespondedAt is null || started.Sla.RespondedAt >= lastChange);

        // A claim in the future is lifted down to now.
        var held = await Send(job.Technician, HttpMethod.Post, $"{url}/hold", new { reason = "waiting" }, performedAt: DateTimeOffset.UtcNow.AddDays(3));
        Assert.Equal(HttpStatusCode.OK, held.StatusCode);
        var holdEvent = (await ReadAsync<WorkOrderDetailDto>(await job.Manager.GetAsync(url))).Events.Single(e => e.Action == WorkOrderAction.Held);
        Assert.True(holdEvent.At <= DateTimeOffset.UtcNow.AddSeconds(5));

        // Nonsense in the header is ignored (server time), not an error.
        var request = new HttpRequestMessage(HttpMethod.Post, $"{url}/resume") { Content = JsonContent.Create(new { }) };
        request.Headers.Add("X-Performed-At", "yesterday-ish");
        Assert.Equal(HttpStatusCode.OK, (await job.Technician.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task A_sensible_offline_start_time_is_recorded_as_claimed()
    {
        var job = await NewJobAsync();
        // Make the last change old enough that a recent claim is inside the window.
        await using (var platform = sql.Work(null, platform: true))
        {
            await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRawAsync(platform.Database,
                "UPDATE work.WorkOrders SET UpdatedAt = DATEADD(hour, -2, SYSDATETIMEOFFSET()) WHERE Id = {0}", job.Order.Id);
        }

        var claimed = DateTimeOffset.UtcNow.AddMinutes(-20);
        var started = await ReadAsync<WorkOrderDto>(await Send(job.Technician, HttpMethod.Post, $"/api/v1/work-orders/{job.Order.Id}/start", new { }, performedAt: claimed));
        var startedEvent = (await ReadAsync<WorkOrderDetailDto>(await job.Manager.GetAsync($"/api/v1/work-orders/{job.Order.Id}"))).Events.Single(e => e.Action == WorkOrderAction.Started);
        Assert.InRange(Math.Abs((startedEvent.At - claimed).TotalSeconds), 0, 2);
        Assert.NotNull(started.Sla);
    }

    // ---------------- the offline bundle ----------------

    [Fact]
    public async Task The_bundle_holds_exactly_the_open_jobs_assigned_to_the_caller_with_what_they_need()
    {
        var job = await NewJobAsync(start: true);
        var otherTech = await _api.ClientAsync(job.Tenant, SystemRoles.Technician);
        var template = await ReadAsync<ChecklistTemplateDto>(await PostAsync(job.Manager, "/api/v1/work-orders/checklists", new { name = "Fan check", items = new[] { new { text = "Isolate", required = true } } }));
        await PostAsync(job.Manager, $"/api/v1/work-orders/{job.Order.Id}/checklist", new { templateId = template.Id });
        await PostAsync(job.Technician, $"/api/v1/work-orders/{job.Order.Id}/costs", new { kind = "Material", description = "Belt", quantity = 1, unitCost = 30 });
        await PostAsync(job.Technician, $"/api/v1/work-orders/{job.Order.Id}/comments", new { body = "On site" });

        var bundle = await ReadAsync<OfflineJobsDto>(await job.Technician.GetAsync("/api/v1/work-orders/offline/jobs"));
        Assert.InRange((DateTimeOffset.UtcNow - bundle.ServerTime).TotalSeconds, -5, 30);
        var mine = Assert.Single(bundle.Jobs);
        Assert.Equal(job.Order.Id, mine.Order.Id);
        Assert.Equal("Isolate", Assert.Single(mine.Checklist).Text);
        Assert.Single(mine.Costs);
        Assert.Contains(mine.Comments, c => c.Body == "On site");
        Assert.Contains(WorkOrderAction.Completed, mine.Order.Allowed);
        Assert.Equal("SAR", mine.CurrencyCode);

        // Somebody else's phone gets nothing of this; the requester may not use the endpoint at all.
        Assert.Empty((await ReadAsync<OfflineJobsDto>(await otherTech.GetAsync("/api/v1/work-orders/offline/jobs"))).Jobs);
        Assert.Equal(HttpStatusCode.Forbidden, (await job.Requester.GetAsync("/api/v1/work-orders/offline/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.Factory.CreateClient().GetAsync("/api/v1/work-orders/offline/jobs")).StatusCode);

        // Finished work drops out of the bundle.
        await PostAsync(job.Technician, $"/api/v1/work-orders/{job.Order.Id}/checklist/{mine.Checklist[0].Id}", new { result = "Done" });
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(job.Technician, $"/api/v1/work-orders/{job.Order.Id}/complete", new { code = "Fixed", notes = "done" })).StatusCode);
        Assert.Empty((await ReadAsync<OfflineJobsDto>(await job.Technician.GetAsync("/api/v1/work-orders/offline/jobs"))).Jobs);
    }

    [Fact]
    public async Task Another_tenants_technician_never_sees_these_jobs_in_a_bundle()
    {
        var job = await NewJobAsync(start: true);
        var foreign = await _api.ClientAsync(SqlServerFixture.TenantB, SystemRoles.Technician);
        Assert.Empty((await ReadAsync<OfflineJobsDto>(await foreign.GetAsync("/api/v1/work-orders/offline/jobs"))).Jobs);
        Assert.Single((await ReadAsync<OfflineJobsDto>(await job.Technician.GetAsync("/api/v1/work-orders/offline/jobs"))).Jobs);
    }

    // ---------------- permits and alert-raised jobs in the bundle ----------------

    private static async Task<Fms.Compliance.Services.PermitDto> PermitForAsync(Job job, string code, Guid? workOrderId)
    {
        var type = await ReadAsync<Fms.Compliance.Services.PermitTypeDto>(await PostAsync(job.Manager, "/api/v1/compliance/permit-types", new { code, name = "Hot work " + code, maxDurationHours = 8, exclusive = false, requiresIsolation = false, precautions = new[] { "Fire watch posted" } }));
        var start = DateTimeOffset.UtcNow.AddHours(1);
        return await ReadAsync<Fms.Compliance.Services.PermitDto>(await PostAsync(job.Technician, "/api/v1/permits", new { typeId = type.Id, locationId = job.Order.LocationId, workOrderId, description = "Weld the bracket", plannedStart = start, plannedEnd = start.AddHours(2) }));
    }

    [Fact]
    public async Task The_bundle_carries_the_permits_in_play_for_each_job_and_drops_them_once_they_are_cancelled()
    {
        var job = await NewJobAsync(start: true);
        var other = await NewJobAsync(start: true);
        var permit = await PermitForAsync(job, "WLD", job.Order.Id);
        await PermitForAsync(job, "UNL", null); // a permit not tied to any job is not in the bundle

        var bundle = await ReadAsync<OfflineJobsDto>(await job.Technician.GetAsync("/api/v1/work-orders/offline/jobs"));
        var carried = Assert.Single(Assert.Single(bundle.Jobs).Permits!);
        Assert.Equal((permit.Id, permit.Number, "Requested", job.Order.Id), (carried.Id, carried.Number, carried.Status, carried.WorkOrderId));
        Assert.Equal("Hot work WLD", carried.TypeName);

        // Another organisation's technician never sees it, and a job without a permit carries an empty list.
        Assert.Empty(Assert.Single((await ReadAsync<OfflineJobsDto>(await other.Technician.GetAsync("/api/v1/work-orders/offline/jobs"))).Jobs).Permits!);

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(job.Technician, $"/api/v1/permits/{permit.Id}/cancel", new { reason = "Not needed", rowVersion = permit.RowVersion })).StatusCode);
        Assert.Empty(Assert.Single((await ReadAsync<OfflineJobsDto>(await job.Technician.GetAsync("/api/v1/work-orders/offline/jobs"))).Jobs).Permits!);
    }

    [Fact]
    public async Task A_job_raised_by_an_alert_says_so_in_the_bundle_and_an_ordinary_one_does_not()
    {
        var job = await NewJobAsync(start: true);
        await using (var platform = sql.Work(null, platform: true))
        {
            await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRawAsync(platform.Database,
                "UPDATE work.WorkOrders SET SourceKind = N'energy.alarm', SourceId = {1} WHERE Id = {0}", job.Order.Id, Guid.NewGuid());
        }

        var raised = Assert.Single((await ReadAsync<OfflineJobsDto>(await job.Technician.GetAsync("/api/v1/work-orders/offline/jobs"))).Jobs);
        Assert.Equal("energy.alarm", raised.Order.SourceKind);

        var ordinary = await NewJobAsync(start: true);
        Assert.Null(Assert.Single((await ReadAsync<OfflineJobsDto>(await ordinary.Technician.GetAsync("/api/v1/work-orders/offline/jobs"))).Jobs).Order.SourceKind);
    }
}
