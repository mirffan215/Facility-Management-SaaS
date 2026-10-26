using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Fms.BuildingBlocks.Results;
using Fms.Facilities.Services;
using Fms.Platform.Notifications;
using Fms.Platform.Security;
using Fms.Platform.Services;
using Fms.Vendors.Domain;
using Fms.Vendors.Services;
using Fms.Work.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Fms.IntegrationTests.ApiHarness;

namespace Fms.IntegrationTests;

[Collection(SqlCollection.Name)]
public sealed class VendorApiTests(SqlServerFixture sql) : IDisposable
{
    private readonly ApiHarness _api = new(sql, settings: new Dictionary<string, string?> { ["Storage:Provider"] = "InMemory" });
    public void Dispose() => _api.Dispose();

    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\n%%EOF");
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private sealed record Rig(Guid Tenant, HttpClient Manager, HttpClient Technician, HttpClient Viewer, HttpClient Requester, LocationDto Site);

    private async Task<Rig> NewRigAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var platform = sql.App(null, platform: true))
        {
            platform.Tenants.Add(new Fms.Platform.Domain.Tenant { Id = tenant, Name = "Ven " + tenant.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
            await platform.SaveChangesAsync();
            await SystemRoleSync.EnsureAsync(platform, tenant);
        }

        var manager = await _api.ClientAsync(tenant, SystemRoles.FacilityManager);
        var site = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", new { type = "Site", code = FacilitiesScope.Code("S"), name = "HQ" }));
        return new Rig(tenant, manager, await _api.ClientAsync(tenant, SystemRoles.Technician), await _api.ClientAsync(tenant, SystemRoles.Viewer), await _api.ClientAsync(tenant, SystemRoles.Requester), site);
    }

    private static async Task<VendorDto> VendorAsync(Rig rig, string name = "CoolAir Services", string? tax = null) =>
        await ReadAsync<VendorDto>(await PostAsync(rig.Manager, "/api/v1/vendors", new { code = FacilitiesScope.Code("V"), name, taxNumber = tax, email = "ops@coolair.test", country = "sa" }));

    private static async Task<ContractDto> ContractAsync(Rig rig, VendorDto vendor, int startOffset = -30, int endOffset = 300, int notice = 60, string title = "Chiller AMC") =>
        await ReadAsync<ContractDto>(await PostAsync(rig.Manager, "/api/v1/contracts", new
        {
            vendorId = vendor.Id, number = FacilitiesScope.Code("CT"), title, type = "Amc", startOn = Today.AddDays(startOffset), endOn = Today.AddDays(endOffset), value = 120000, currencyCode = "sar", renewalNoticeDays = notice,
        }));

    private async Task DeliverAsync()
    {
        var processor = _api.Factory.Services.GetRequiredService<Fms.Platform.Outbox.OutboxProcessor>();
        for (var i = 0; i < 20 && await processor.ProcessBatchAsync() > 0; i++) { }
    }

    // ---------------- vendors ----------------

    [Fact]
    public async Task Vendors_are_managed_by_managers_read_by_viewers_validated_and_unique()
    {
        var rig = await NewRigAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Viewer, "/api/v1/vendors", new { code = "X1", name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Technician.GetAsync("/api/v1/vendors")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Requester.GetAsync("/api/v1/vendors")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/vendors", new { code = "bad code!", name = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/vendors", new { code = FacilitiesScope.Code("V"), name = "X", email = "not-an-email" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/vendors", new { code = FacilitiesScope.Code("V"), name = "X", country = "KSA" })).StatusCode);

        var vendor = await VendorAsync(rig, tax: "300012345600003");
        Assert.Equal(("SA", VendorStatus.Active), (vendor.Country, vendor.Status));
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, "/api/v1/vendors", new { code = vendor.Code.ToLowerInvariant(), name = "Again" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, "/api/v1/vendors", new { code = FacilitiesScope.Code("V"), name = "Other", taxNumber = "300012345600003" })).StatusCode);
        Assert.Equal(vendor.Id, (await ReadAsync<Page<VendorDto>>(await rig.Viewer.GetAsync("/api/v1/vendors?q=CoolAir"))).Items.Single().Id);

        var renamed = await ReadAsync<VendorDto>(await PutAsync(rig.Manager, $"/api/v1/vendors/{vendor.Id}", new { code = vendor.Code, name = "CoolAir Co", taxNumber = vendor.TaxNumber, rowVersion = vendor.RowVersion }));
        Assert.Equal("CoolAir Co", renamed.Name);
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(rig.Manager, $"/api/v1/vendors/{vendor.Id}", new { code = vendor.Code, name = "Stale", taxNumber = vendor.TaxNumber, rowVersion = vendor.RowVersion })).StatusCode);

        // Contacts are replaced as a list; only one is the main contact.
        var contacts = await ReadAsync<VendorContactDto[]>(await PutAsync(rig.Manager, $"/api/v1/vendors/{vendor.Id}/contacts", new { contacts = new object[] { new { name = "Omar", email = "omar@coolair.test", isPrimary = true }, new { name = "Sara", role = "Dispatcher", isPrimary = false } } }));
        Assert.Equal("Omar", contacts[0].Name);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(rig.Manager, $"/api/v1/vendors/{vendor.Id}/contacts", new { contacts = new[] { new { name = "A", isPrimary = true }, new { name = "B", isPrimary = true } } })).StatusCode);
        Assert.Equal(2, (await rig.Viewer.GetFromJsonAsync<VendorContactDto[]>($"/api/v1/vendors/{vendor.Id}/contacts", Json))!.Length);

        // Holding or blocking a vendor needs a reason; an active one needs none.
        var current = await ReadAsync<VendorDto>(await rig.Manager.GetAsync($"/api/v1/vendors/{vendor.Id}"));
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, $"/api/v1/vendors/{vendor.Id}/status", new { status = "OnHold", rowVersion = current.RowVersion })).StatusCode);
        var held = await ReadAsync<VendorDto>(await PostAsync(rig.Manager, $"/api/v1/vendors/{vendor.Id}/status", new { status = "OnHold", reason = "Insurance lapsed", rowVersion = current.RowVersion }));
        Assert.Equal((VendorStatus.OnHold, "Insurance lapsed"), (held.Status, held.StatusReason));
        var active = await ReadAsync<VendorDto>(await PostAsync(rig.Manager, $"/api/v1/vendors/{vendor.Id}/status", new { status = "Active", rowVersion = held.RowVersion }));
        Assert.Null(active.StatusReason);

        // A vendor with a contract in force cannot be archived.
        var contract = await ContractAsync(rig, active);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, $"/api/v1/vendors/{vendor.Id}/archive", new { rowVersion = active.RowVersion })).StatusCode);
        var terminated = await PostAsync(rig.Manager, $"/api/v1/contracts/{contract.Id}/terminate", new { reason = "Replaced", rowVersion = contract.RowVersion });
        Assert.Equal(HttpStatusCode.OK, terminated.StatusCode);
        var fresh = await ReadAsync<VendorDto>(await rig.Manager.GetAsync($"/api/v1/vendors/{vendor.Id}"));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Manager, $"/api/v1/vendors/{vendor.Id}/archive", new { rowVersion = fresh.RowVersion })).StatusCode);
    }

    // ---------------- contracts ----------------

    [Fact]
    public async Task Contracts_have_dates_a_status_coverage_service_targets_and_end_when_terminated()
    {
        var rig = await NewRigAsync();
        var other = await NewRigAsync();
        var vendor = await VendorAsync(rig);

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/contracts", new { vendorId = vendor.Id, number = "C1", title = "x", type = "Amc", startOn = Today, endOn = Today.AddDays(-1) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/contracts", new { vendorId = Guid.NewGuid(), number = "C1", title = "x", type = "Amc", startOn = Today, endOn = Today.AddDays(5) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/contracts", new { vendorId = vendor.Id, number = "C1", title = "x", type = "Amc", startOn = Today, endOn = Today.AddDays(5), currencyCode = "SARR" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Viewer, "/api/v1/contracts", new { vendorId = vendor.Id, number = "C1", title = "x", type = "Amc", startOn = Today, endOn = Today.AddDays(5) })).StatusCode);

        var active = await ContractAsync(rig, vendor);
        Assert.Equal((ContractStatus.Active, "SAR"), (active.Status, active.CurrencyCode));
        Assert.Equal(300, active.DaysLeft);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, "/api/v1/contracts", new { vendorId = vendor.Id, number = active.Number.ToLowerInvariant(), title = "dup", type = "Amc", startOn = Today, endOn = Today.AddDays(5) })).StatusCode);
        var soon = await ContractAsync(rig, vendor, endOffset: 20, title: "Lifts");
        var ended = await ContractAsync(rig, vendor, startOffset: -400, endOffset: -5, title: "Old");
        var upcoming = await ContractAsync(rig, vendor, startOffset: 10, endOffset: 400, title: "Next year");
        Assert.Equal((ContractStatus.Expiring, ContractStatus.Expired, ContractStatus.Upcoming), (soon.Status, ended.Status, upcoming.Status));

        async Task<string[]> Titles(string status) =>
            (await ReadAsync<Page<ContractDto>>(await rig.Viewer.GetAsync($"/api/v1/contracts?vendorId={vendor.Id}&status={status}"))).Items.Select(c => c.Title).ToArray();
        Assert.Equal(["Chiller AMC"], await Titles("Active"));
        Assert.Equal(["Lifts"], await Titles("Expiring"));
        Assert.Equal(["Old"], await Titles("Expired"));
        Assert.Equal(["Next year"], await Titles("Upcoming"));

        // Coverage names real things of this tenant: a site, an asset class or an asset.
        var cls = await ReadAsync<AssetClassDto>(await PostAsync(rig.Manager, "/api/v1/asset-classes", new { code = FacilitiesScope.Code("C"), name = "Chillers" }));
        var foreignSite = await ReadAsync<LocationDto>(await PostAsync(other.Manager, "/api/v1/locations", new { type = "Site", code = FacilitiesScope.Code("S"), name = "Elsewhere" }));
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(rig.Manager, $"/api/v1/contracts/{active.Id}/coverage", new { coverage = new[] { new { kind = "Site", targetId = foreignSite.Id } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(rig.Manager, $"/api/v1/contracts/{active.Id}/coverage", new { coverage = new[] { new { kind = "Asset", targetId = Guid.NewGuid() } } })).StatusCode);
        var covered = await ReadAsync<ContractDto>(await PutAsync(rig.Manager, $"/api/v1/contracts/{active.Id}/coverage", new { coverage = new[] { new { kind = "Site", targetId = rig.Site.Id }, new { kind = "AssetClass", targetId = cls.Id } } }));
        Assert.Equal(["Chillers", "HQ"], covered.Coverage.Select(c => c.Name!).OrderBy(n => n).ToArray());

        // Service targets: one per priority, resolution never sooner than response.
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(rig.Manager, $"/api/v1/contracts/{active.Id}/sla", new { targets = new[] { new { priority = 3, responseMinutes = 120, resolutionMinutes = 60 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(rig.Manager, $"/api/v1/contracts/{active.Id}/sla", new { targets = new[] { new { priority = 3, responseMinutes = 60, resolutionMinutes = 120 }, new { priority = 3, responseMinutes = 30, resolutionMinutes = 90 } } })).StatusCode);
        var withSla = await ReadAsync<ContractDto>(await PutAsync(rig.Manager, $"/api/v1/contracts/{active.Id}/sla", new { targets = new[] { new { priority = 3, responseMinutes = 60, resolutionMinutes = 240 } } }));
        Assert.Equal(new ContractSlaDto(3, 60, 240), Assert.Single(withSla.Sla));

        // Dates can be changed (a renewal), the vendor cannot; a terminated contract is closed.
        var renewed = await ReadAsync<ContractDto>(await PutAsync(rig.Manager, $"/api/v1/contracts/{active.Id}", new { number = active.Number, title = active.Title, type = "Amc", startOn = active.StartOn, endOn = active.EndOn.AddDays(365), value = 130000, currencyCode = "SAR", renewalNoticeDays = 90, rowVersion = withSla.RowVersion }));
        Assert.Equal(active.EndOn.AddDays(365), renewed.EndOn);
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(rig.Manager, $"/api/v1/contracts/{active.Id}", new { number = active.Number, title = "Stale", type = "Amc", startOn = active.StartOn, endOn = active.EndOn, rowVersion = withSla.RowVersion })).StatusCode);
        var second = await VendorAsync(rig, "Other vendor");
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(rig.Manager, $"/api/v1/contracts/{active.Id}", new { vendorId = second.Id, number = active.Number, title = active.Title, type = "Amc", startOn = active.StartOn, endOn = active.EndOn, rowVersion = renewed.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, $"/api/v1/contracts/{active.Id}/terminate", new { rowVersion = renewed.RowVersion })).StatusCode); // reason needed
        var done = await ReadAsync<ContractDto>(await PostAsync(rig.Manager, $"/api/v1/contracts/{active.Id}/terminate", new { reason = "Vendor went out of business", rowVersion = renewed.RowVersion }));
        Assert.Equal(ContractStatus.Terminated, done.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(rig.Manager, $"/api/v1/contracts/{active.Id}/coverage", new { coverage = Array.Empty<object>() })).StatusCode);

        // Another tenant sees nothing.
        Assert.Empty((await ReadAsync<Page<ContractDto>>(await other.Manager.GetAsync("/api/v1/contracts"))).Items);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Manager.GetAsync($"/api/v1/contracts/{active.Id}")).StatusCode);
    }

    [Fact]
    public async Task Contract_files_are_checked_by_content_downloaded_with_a_record_and_removed_only_with_a_reason()
    {
        var rig = await NewRigAsync();
        var contract = await ContractAsync(rig, await VendorAsync(rig));

        static MultipartFormDataContent Form(byte[] bytes, string name) =>
            new() { { new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } }, "file", name }, { new StringContent("Amendment"), "kind" } };

        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Viewer.PostAsync($"/api/v1/contracts/{contract.Id}/documents", Form(Pdf, "a.pdf"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await rig.Manager.PostAsync($"/api/v1/contracts/{contract.Id}/documents", Form([0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00], "virus.pdf"))).StatusCode);
        var created = await rig.Manager.PostAsync($"/api/v1/contracts/{contract.Id}/documents", Form(Pdf, "../signed contract.pdf"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var doc = await ReadAsync<ContractDocumentDto>(created);
        Assert.Equal(("application/pdf", ContractDocumentKind.Amendment), (doc.ContentType, doc.Kind));
        Assert.DoesNotContain("..", doc.FileName);

        Assert.Single((await rig.Viewer.GetFromJsonAsync<ContractDocumentDto[]>($"/api/v1/contracts/{contract.Id}/documents", Json))!);
        var download = await rig.Viewer.GetAsync($"/api/v1/contract-documents/{doc.Id}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(Pdf, await download.Content.ReadAsByteArrayAsync());
        await using (var audit = sql.App(rig.Tenant))
            Assert.True(await audit.AuditEvents.AnyAsync(a => a.Action == "contract.document_downloaded" && a.EntityId == doc.Id.ToString()));

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, $"/api/v1/contract-documents/{doc.Id}/delete", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Manager, $"/api/v1/contract-documents/{doc.Id}/delete", new { reason = "Uploaded to the wrong contract" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.Viewer.GetAsync($"/api/v1/contract-documents/{doc.Id}/download")).StatusCode);
        Assert.Empty((await rig.Viewer.GetFromJsonAsync<ContractDocumentDto[]>($"/api/v1/contracts/{contract.Id}/documents", Json))!);
    }

    [Fact]
    public async Task Managers_are_reminded_once_when_a_contract_nears_its_end_and_once_when_it_ends_and_again_after_an_extension()
    {
        var rig = await NewRigAsync();
        var vendor = await VendorAsync(rig);
        var soon = await ContractAsync(rig, vendor, endOffset: 10, notice: 30, title: "Lifts");
        var ended = await ContractAsync(rig, vendor, startOffset: -100, endOffset: -2, title: "Old pumps");
        var far = await ContractAsync(rig, vendor, endOffset: 300, title: "Far away");

        var monitor = _api.Factory.Services.GetRequiredService<ContractMonitor>();
        await monitor.RunOnceAsync();
        await DeliverAsync();
        async Task<NotificationDto[]> Notices() => (await ReadAsync<Page<NotificationDto>>(await rig.Manager.GetAsync("/api/v1/notifications?pageSize=100"))).Items.Where(n => n.Type.StartsWith("contract.")).ToArray();
        var notices = await Notices();
        Assert.Equal(2, notices.Length);
        Assert.Contains(notices, n => n.Type == "contract.Expiring" && n.Data.GetProperty("number").GetString() == soon.Number);
        Assert.Contains(notices, n => n.Type == "contract.Expired" && n.Data.GetProperty("number").GetString() == ended.Number);
        Assert.DoesNotContain(notices, n => n.Data.GetProperty("number").GetString() == far.Number);
        Assert.Empty((await ReadAsync<Page<NotificationDto>>(await rig.Viewer.GetAsync("/api/v1/notifications"))).Items); // viewers do not manage vendors

        // Running again, even many times, reminds nobody twice.
        await monitor.RunOnceAsync();
        await monitor.RunOnceAsync();
        await DeliverAsync();
        Assert.Equal(2, (await Notices()).Length);

        // Extending the contract and then nearing the new end reminds again.
        var current = await ReadAsync<ContractDto>(await rig.Manager.GetAsync($"/api/v1/contracts/{soon.Id}"));
        var extended = await ReadAsync<ContractDto>(await PutAsync(rig.Manager, $"/api/v1/contracts/{soon.Id}", new { number = soon.Number, title = soon.Title, type = "Amc", startOn = soon.StartOn, endOn = Today.AddDays(200), renewalNoticeDays = 30, rowVersion = current.RowVersion }));
        Assert.Equal(ContractStatus.Active, extended.Status);
        var shorter = await ReadAsync<ContractDto>(await PutAsync(rig.Manager, $"/api/v1/contracts/{soon.Id}", new { number = soon.Number, title = soon.Title, type = "Amc", startOn = soon.StartOn, endOn = Today.AddDays(5), renewalNoticeDays = 30, rowVersion = extended.RowVersion }));
        Assert.Equal(ContractStatus.Expiring, shorter.Status);
        await monitor.RunOnceAsync();
        await DeliverAsync();
        Assert.Equal(3, (await Notices()).Length);
    }

    // ---------------- work orders given to vendors ----------------

    private static Task<HttpResponseMessage> AssignVendor(HttpClient c, Guid wo, Guid? vendor, Guid? contract = null) => PostAsync(c, $"/api/v1/work-orders/{wo}/vendor", new { vendorId = vendor, contractId = contract });

    [Fact]
    public async Task A_work_order_can_be_given_to_a_vendor_under_a_contract_and_shows_the_contracts_terms()
    {
        var rig = await NewRigAsync();
        var vendor = await VendorAsync(rig);
        var contract = await ContractAsync(rig, vendor);
        await PutAsync(rig.Manager, $"/api/v1/contracts/{contract.Id}/coverage", new { coverage = new[] { new { kind = "Site", targetId = rig.Site.Id } } });
        await PutAsync(rig.Manager, $"/api/v1/contracts/{contract.Id}/sla", new { targets = new[] { new { priority = 3, responseMinutes = 60, resolutionMinutes = 240 } } });
        var wo = await ReadAsync<WorkOrderDto>(await PostAsync(rig.Manager, "/api/v1/work-orders", new { title = "Chiller down", type = "Emergency", priority = "High", locationId = rig.Site.Id }));
        Assert.Equal(Fms.Work.Domain.WorkOrderStatus.Approved, wo.Status);

        Assert.Equal(HttpStatusCode.Forbidden, (await AssignVendor(rig.Technician, wo.Id, vendor.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AssignVendor(rig.Manager, wo.Id, null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AssignVendor(rig.Manager, wo.Id, Guid.NewGuid())).StatusCode);

        var other = await VendorAsync(rig, "Someone else");
        Assert.Equal(HttpStatusCode.BadRequest, (await AssignVendor(rig.Manager, wo.Id, other.Id, contract.Id)).StatusCode); // the contract is not theirs
        var expired = await ContractAsync(rig, vendor, startOffset: -400, endOffset: -5, title: "Old");
        Assert.Equal(HttpStatusCode.BadRequest, (await AssignVendor(rig.Manager, wo.Id, vendor.Id, expired.Id)).StatusCode); // not in force

        var held = await ReadAsync<VendorDto>(await PostAsync(rig.Manager, $"/api/v1/vendors/{other.Id}/status", new { status = "Blocked", reason = "Fraud investigation", rowVersion = other.RowVersion }));
        Assert.Equal(HttpStatusCode.BadRequest, (await AssignVendor(rig.Manager, wo.Id, held.Id)).StatusCode); // cannot receive work

        var assigned = await ReadAsync<WorkOrderDto>(await AssignVendor(rig.Manager, wo.Id, vendor.Id, contract.Id));
        Assert.Equal(Fms.Work.Domain.WorkOrderStatus.Assigned, assigned.Status);
        Assert.Null(assigned.AssignedTo);
        var terms = (await rig.Viewer.GetFromJsonAsync<VendorAssignmentDto>($"/api/v1/work-orders/{wo.Id}/vendor", Json))!;
        Assert.Equal((vendor.Name, contract.Number, true, true, 60, 240), (terms.VendorName, terms.ContractNumber, terms.ContractInForce, terms.Covered, terms.ResponseMinutes, terms.ResolutionMinutes));
        var history = (await ReadAsync<WorkOrderDetailDto>(await rig.Manager.GetAsync($"/api/v1/work-orders/{wo.Id}"))).Events;
        Assert.Contains(history, e => e.Action == Fms.Work.Domain.WorkOrderAction.Assigned && e.Note == $"Vendor: {vendor.Name}");

        // A contract that does not cover this place says so; one with no targets for the priority gives none.
        var elsewhere = await ReadAsync<LocationDto>(await PostAsync(rig.Manager, "/api/v1/locations", new { type = "Site", code = FacilitiesScope.Code("S"), name = "Annex" }));
        var narrow = await ContractAsync(rig, vendor, title: "Annex only");
        await PutAsync(rig.Manager, $"/api/v1/contracts/{narrow.Id}/coverage", new { coverage = new[] { new { kind = "Site", targetId = elsewhere.Id } } });
        Assert.Equal(HttpStatusCode.OK, (await AssignVendor(rig.Manager, wo.Id, vendor.Id, narrow.Id)).StatusCode);
        var narrowTerms = (await rig.Viewer.GetFromJsonAsync<VendorAssignmentDto>($"/api/v1/work-orders/{wo.Id}/vendor", Json))!;
        Assert.Equal((false, (int?)null), (narrowTerms.Covered, narrowTerms.ResponseMinutes));

        // Giving the work to a person takes it back from the vendor; removing the vendor returns it to the queue.
        var techId = (await rig.Technician.GetFromJsonAsync<MeDto>("/api/v1/me", Json))!.UserId!.Value;
        var toPerson = await ReadAsync<WorkOrderDto>(await PostAsync(rig.Manager, $"/api/v1/work-orders/{wo.Id}/assign", new { assigneeId = techId }));
        Assert.Equal(techId, toPerson.AssignedTo);
        Assert.Equal(HttpStatusCode.NoContent, (await rig.Viewer.GetAsync($"/api/v1/work-orders/{wo.Id}/vendor")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AssignVendor(rig.Manager, wo.Id, vendor.Id)).StatusCode);
        var back = await ReadAsync<WorkOrderDto>(await PostAsync(rig.Manager, $"/api/v1/work-orders/{wo.Id}/vendor/clear", new { }));
        Assert.Equal(Fms.Work.Domain.WorkOrderStatus.Approved, back.Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, $"/api/v1/work-orders/{wo.Id}/vendor/clear", new { })).StatusCode);
    }

    private sealed record MeDto(Guid? UserId);

    [Fact]
    public async Task Vendors_are_isolated_by_tenant_in_the_database_too()
    {
        var rig = await NewRigAsync();
        var other = await NewRigAsync();
        var vendor = await VendorAsync(rig);
        await ContractAsync(rig, vendor);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Manager.GetAsync($"/api/v1/vendors/{vendor.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(other.Manager, "/api/v1/contracts", new { vendorId = vendor.Id, number = "X", title = "x", type = "Amc", startOn = Today, endOn = Today.AddDays(5) })).StatusCode);
        await using var db = sql.Vendors(other.Tenant);
        Assert.Equal(0, await db.Vendors.IgnoreQueryFilters().CountAsync(v => v.Id == vendor.Id));
        Assert.Equal(0, await db.Contracts.IgnoreQueryFilters().CountAsync());
        await using var mine = sql.Vendors(rig.Tenant);
        Assert.Equal(1, await mine.Vendors.CountAsync(v => v.Id == vendor.Id));
    }
}
