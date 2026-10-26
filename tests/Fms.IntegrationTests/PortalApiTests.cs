using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Fms.BuildingBlocks.Results;
using Fms.Facilities.Services;
using Fms.Platform.Security;
using Fms.Platform.Services;
using Fms.Purchasing.Services;
using Fms.Vendors.Services;
using Fms.Work.Services;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Fms.IntegrationTests.ApiHarness;
using OrderStatus = Fms.Purchasing.Domain.OrderStatus;
using VendorStatus = Fms.Vendors.Domain.VendorStatus;

namespace Fms.IntegrationTests;

[Collection(SqlCollection.Name)]
public sealed class PortalApiTests(SqlServerFixture sql) : IDisposable
{
    private readonly ApiHarness _api = new(sql, settings: new Dictionary<string, string?> { ["Storage:Provider"] = "InMemory" });
    public void Dispose() => _api.Dispose();

    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\n%%EOF");
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private sealed record Rig(Guid Tenant, HttpClient Manager, HttpClient Approver, HttpClient Technician, HttpClient Viewer, LocationDto Site);

    private async Task<Rig> NewRigAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var platform = sql.App(null, platform: true))
        {
            platform.Tenants.Add(new Fms.Platform.Domain.Tenant { Id = tenant, Name = "Portal " + tenant.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
            await platform.SaveChangesAsync();
            await SystemRoleSync.EnsureAsync(platform, tenant);
        }

        var manager = await _api.ClientAsync(tenant, SystemRoles.FacilityManager);
        var site = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", new { type = "Site", code = FacilitiesScope.Code("S"), name = "HQ" }));
        return new Rig(tenant, manager, await _api.ClientAsync(tenant, SystemRoles.FacilityManager), await _api.ClientAsync(tenant, SystemRoles.Technician), await _api.ClientAsync(tenant, SystemRoles.Viewer), site);
    }

    private static async Task<VendorDto> VendorAsync(Rig rig, string name) =>
        await ReadAsync<VendorDto>(await PostAsync(rig.Manager, "/api/v1/vendors", new { code = FacilitiesScope.Code("V"), name }));

    private static Task<HttpResponseMessage> Invite(HttpClient who, VendorDto vendor, string email = "ops@vendor.test") => PostAsync(who, $"/api/v1/vendors/{vendor.Id}/invitations", new { email });

    private static Task<HttpResponseMessage> Accept(HttpClient who, string token) => PostAsync(who, "/api/v1/portal/invitations/accept", new { token });

    /// <summary>A vendor's employee who has been invited and has accepted: a client that is signed in as a portal user.</summary>
    private async Task<HttpClient> PortalUserAsync(Rig rig, VendorDto vendor)
    {
        var created = await ReadAsync<InvitationCreatedDto>(await Invite(rig.Manager, vendor));
        var guest = _api.GuestClient();
        Assert.Equal(HttpStatusCode.OK, (await Accept(guest, created.Token)).StatusCode);
        return guest;
    }

    // ---------------- invitations ----------------

    [Fact]
    public async Task An_invited_outsider_gets_a_tenant_only_by_accepting_and_then_sees_only_the_portal()
    {
        var rig = await NewRigAsync();
        var vendor = await VendorAsync(rig, "CoolAir Services");

        Assert.Equal(HttpStatusCode.Forbidden, (await Invite(rig.Technician, vendor)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Invite(rig.Viewer, vendor)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Invite(rig.Manager, vendor, "not-an-email")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(rig.Manager, $"/api/v1/vendors/{Guid.NewGuid()}/invitations", new { email = "a@b.test" })).StatusCode);

        var created = await ReadAsync<InvitationCreatedDto>(await Invite(rig.Manager, vendor));
        Assert.Equal(("Open", "/portal/accept"), (created.Invitation.State, created.Path));
        Assert.True(created.Token.Length >= 40);

        // The token appears nowhere after this: not in the list, and the database holds only its hash.
        var access = await rig.Manager.GetStringAsync($"/api/v1/vendors/{vendor.Id}/portal-access");
        Assert.DoesNotContain(created.Token, access);
        await using (var db = sql.App(null, platform: true))
        {
            var row = await db.GuestInvitations.IgnoreQueryFilters().SingleAsync(i => i.Id == created.Invitation.Id);
            Assert.NotEqual(created.Token, row.TokenHash);
            Assert.Equal(64, row.TokenHash.Length);
        }

        // Before accepting, the outsider belongs nowhere: nothing works, and the portal says no.
        var guest = _api.GuestClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.GetAsync("/api/v1/portal/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.GetAsync("/api/v1/vendors")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Accept(guest, "not-a-real-token-not-a-real-token")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Accept(guest, "")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Accept(_api.Factory.CreateClient(), created.Token)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Accept(guest, created.Token)).StatusCode);
        var me = await ReadAsync<PortalMeDto>(await guest.GetAsync("/api/v1/portal/me"));
        Assert.Equal(("CoolAir Services", VendorStatus.Active), (me.VendorName, me.Status));

        // They hold one permission and nothing else; staff screens stay closed.
        var identity = await guest.GetFromJsonAsync<MeDto>("/api/v1/me", Json);
        Assert.Equal(["portal.use"], identity!.Permissions);
        Assert.Equal(rig.Tenant, identity.TenantId);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.GetAsync("/api/v1/vendors")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.GetAsync("/api/v1/work-orders")).StatusCode);

        // The same person asking again gets the same answer; anyone else with the token gets nothing.
        Assert.Equal(HttpStatusCode.OK, (await Accept(guest, created.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Accept(_api.GuestClient(), created.Token)).StatusCode);

        var after = await ReadAsync<PortalAccessDto>(await rig.Manager.GetAsync($"/api/v1/vendors/{vendor.Id}/portal-access"));
        Assert.Equal("Accepted", Assert.Single(after.Invitations).State);
        Assert.True(Assert.Single(after.Users).IsActive);
    }

    private sealed record MeDto(Guid? TenantId, string[] Permissions);

    [Fact]
    public async Task Invitations_can_be_revoked_and_they_expire_and_a_sign_in_belongs_to_one_organisation()
    {
        var rig = await NewRigAsync();
        var other = await NewRigAsync();
        var vendor = await VendorAsync(rig, "Vendor A");

        var revoked = await ReadAsync<InvitationCreatedDto>(await Invite(rig.Manager, vendor));
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Viewer, $"/api/v1/vendors/{vendor.Id}/invitations/{revoked.Invitation.Id}/revoke", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(other.Manager, $"/api/v1/vendors/{vendor.Id}/invitations/{revoked.Invitation.Id}/revoke", new { })).StatusCode); // another tenant's
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Manager, $"/api/v1/vendors/{vendor.Id}/invitations/{revoked.Invitation.Id}/revoke", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, $"/api/v1/vendors/{vendor.Id}/invitations/{revoked.Invitation.Id}/revoke", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Accept(_api.GuestClient(), revoked.Token)).StatusCode);

        var expired = await ReadAsync<InvitationCreatedDto>(await Invite(rig.Manager, vendor));
        await using (var db = sql.App(null, platform: true))
            await db.Database.ExecuteSqlRawAsync("UPDATE GuestInvitations SET ExpiresAt = DATEADD(DAY, -1, SYSDATETIMEOFFSET()) WHERE Id = {0}", expired.Invitation.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await Accept(_api.GuestClient(), expired.Token)).StatusCode);

        // One sign-in, one organisation: someone linked to this tenant cannot be pulled into another by an invitation, and staff of one tenant cannot accept another's.
        var member = _api.GuestClient("shared-subject");
        var first = await ReadAsync<InvitationCreatedDto>(await Invite(rig.Manager, vendor));
        Assert.Equal(HttpStatusCode.OK, (await Accept(member, first.Token)).StatusCode);
        var foreignVendor = await VendorAsync(other, "Vendor B");
        var foreignInvite = await ReadAsync<InvitationCreatedDto>(await Invite(other.Manager, foreignVendor));
        Assert.Equal(HttpStatusCode.Conflict, (await Accept(member, foreignInvite.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Accept(rig.Technician, foreignInvite.Token)).StatusCode); // a member of tenant A
        Assert.Equal(HttpStatusCode.OK, (await rig.Manager.GetAsync($"/api/v1/vendors/{vendor.Id}")).StatusCode);

        // Inside one tenant a person belongs to one vendor only.
        var vendorTwo = await VendorAsync(rig, "Vendor C");
        var second = await ReadAsync<InvitationCreatedDto>(await Invite(rig.Manager, vendorTwo));
        Assert.Equal(HttpStatusCode.Conflict, (await Accept(member, second.Token)).StatusCode);
        Assert.Equal("Vendor A", (await ReadAsync<PortalMeDto>(await member.GetAsync("/api/v1/portal/me"))).VendorName);

        // A blocked vendor takes no new invitations.
        var blocked = await ReadAsync<VendorDto>(await PostAsync(rig.Manager, $"/api/v1/vendors/{vendorTwo.Id}/status", new { status = "Blocked", reason = "Fraud", rowVersion = vendorTwo.RowVersion }));
        Assert.Equal(HttpStatusCode.Conflict, (await Invite(rig.Manager, blocked)).StatusCode);

        // The invitations themselves are hidden from other tenants by the database.
        await using var scoped = sql.App(other.Tenant);
        Assert.Equal(0, await scoped.GuestInvitations.IgnoreQueryFilters().CountAsync(i => i.Id == first.Invitation.Id));
    }

    // ---------------- what a vendor user can reach ----------------

    [Fact]
    public async Task A_vendor_user_is_refused_by_every_route_except_the_portal_and_the_few_that_serve_anyone_signed_in()
    {
        var rig = await NewRigAsync();
        var user = await PortalUserAsync(rig, await VendorAsync(rig, "Any Vendor"));

        string[] allowed = ["/api/v1/portal/", "/api/v1/me", "/api/v1/notifications", "/api/v1/system/info", "/api/v1/auth/providers", "/health"];
        var source = _api.Factory.Services.GetRequiredService<EndpointDataSource>();
        var leaks = new List<string>();
        var checkedRoutes = 0;
        foreach (var endpoint in source.Endpoints.OfType<RouteEndpoint>())
        {
            var template = endpoint.RoutePattern.RawText ?? "";
            if (allowed.Any(a => template.StartsWith(a, StringComparison.OrdinalIgnoreCase))) continue;
            var path = System.Text.RegularExpressions.Regex.Replace(template, @"\{[^}]*\}", _ => Guid.NewGuid().ToString());
            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), path);
                if (method is "POST" or "PUT" or "PATCH") request.Content = JsonContent.Create(new { });
                var response = await user.SendAsync(request);
                checkedRoutes++;
                if (response.StatusCode != HttpStatusCode.Forbidden) leaks.Add($"{method} {template} -> {(int)response.StatusCode}");
            }
        }

        Assert.True(checkedRoutes > 150, $"Only {checkedRoutes} routes were checked; the route list could not be read.");
        Assert.Empty(leaks);
    }

    [Fact]
    public async Task Each_vendor_sees_only_its_own_orders_work_and_contracts_and_nothing_internal()
    {
        var rig = await NewRigAsync();
        var a = await VendorAsync(rig, "Vendor A");
        var b = await VendorAsync(rig, "Vendor B");
        var userA = await PortalUserAsync(rig, a);
        var userB = await PortalUserAsync(rig, b);

        // Orders.
        async Task<OrderDto> OrderFor(VendorDto vendor, string title)
        {
            var draft = await ReadAsync<RequestDto>(await PostAsync(rig.Technician, "/api/v1/purchasing/requests", new { title, lines = new[] { new { description = title, quantity = 2, estimatedUnitPrice = 100 } } }));
            await PostAsync(rig.Technician, $"/api/v1/purchasing/requests/{draft.Id}/submit", new { });
            await PostAsync(rig.Approver, $"/api/v1/purchasing/requests/{draft.Id}/approve", new { });
            return await ReadAsync<OrderDto>(await PostAsync(rig.Manager, $"/api/v1/purchasing/requests/{draft.Id}/order", new { vendorId = vendor.Id }));
        }

        var orderA = await OrderFor(a, "Filters for A");
        var orderB = await OrderFor(b, "Pumps for B");
        var listA = await ReadAsync<PortalOrderDto[]>(await userA.GetAsync("/api/v1/portal/orders"));
        Assert.Equal(orderA.Display, Assert.Single(listA).Display);
        Assert.Equal(HttpStatusCode.NotFound, (await userA.GetAsync($"/api/v1/portal/orders/{orderB.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(userA, $"/api/v1/portal/orders/{orderB.Id}/acknowledge", new { })).StatusCode);
        Assert.Equal(OrderStatus.Issued, (await ReadAsync<OrderDto>(await rig.Manager.GetAsync($"/api/v1/purchasing/orders/{orderB.Id}"))).Status);

        // Nothing internal reaches the vendor.
        var raw = await userA.GetStringAsync($"/api/v1/portal/orders/{orderA.Id}");
        foreach (var hidden in new[] { "requestId", "erpReference", "issuedByName", "storeName", "vendorId" }) Assert.DoesNotContain(hidden, raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Filters for A", raw);

        // Acknowledging is the vendor's to do, once.
        var acknowledged = await ReadAsync<PortalOrderDto>(await PostAsync(userA, $"/api/v1/portal/orders/{orderA.Id}/acknowledge", new { }));
        Assert.Equal(OrderStatus.Acknowledged, acknowledged.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(userA, $"/api/v1/portal/orders/{orderA.Id}/acknowledge", new { })).StatusCode);
        Assert.Equal(OrderStatus.Acknowledged, (await ReadAsync<OrderDto>(await rig.Manager.GetAsync($"/api/v1/purchasing/orders/{orderA.Id}"))).Status);

        // Work orders given to the vendor.
        async Task<WorkOrderDto> WorkFor(VendorDto vendor, string title)
        {
            var wo = await ReadAsync<WorkOrderDto>(await PostAsync(rig.Manager, "/api/v1/work-orders", new { title, type = "Emergency", priority = "High", locationId = rig.Site.Id }));
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Manager, $"/api/v1/work-orders/{wo.Id}/vendor", new { vendorId = vendor.Id })).StatusCode);
            return wo;
        }

        var workA = await WorkFor(a, "Chiller down at A");
        var workB = await WorkFor(b, "Lift stuck at B");
        var unassigned = await ReadAsync<WorkOrderDto>(await PostAsync(rig.Manager, "/api/v1/work-orders", new { title = "Not given to anyone", type = "Emergency", locationId = rig.Site.Id }));
        var jobsA = await ReadAsync<PortalWorkDto[]>(await userA.GetAsync("/api/v1/portal/work-orders"));
        Assert.Equal("Chiller down at A", Assert.Single(jobsA).Title);
        Assert.Equal(HttpStatusCode.NotFound, (await userA.GetAsync($"/api/v1/portal/work-orders/{workB.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await userA.GetAsync($"/api/v1/portal/work-orders/{unassigned.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(userA, $"/api/v1/portal/work-orders/{workB.Id}/comments", new { body = "hello" })).StatusCode);

        // A vendor's message reaches staff; staff's internal notes never reach the vendor.
        await PostAsync(rig.Manager, $"/api/v1/work-orders/{workA.Id}/comments", new { body = "Gate code is 4711", isInternal = true });
        await PostAsync(rig.Manager, $"/api/v1/work-orders/{workA.Id}/comments", new { body = "Please come this afternoon", isInternal = false });
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(userA, $"/api/v1/portal/work-orders/{workA.Id}/comments", new { body = " " })).StatusCode);
        var withComment = await ReadAsync<PortalWorkDto>(await PostAsync(userA, $"/api/v1/portal/work-orders/{workA.Id}/comments", new { body = "On our way, 2 pm" }));
        Assert.Equal(["Please come this afternoon", "On our way, 2 pm"], withComment.Comments!.Select(c => c.Body).ToArray());
        Assert.True(withComment.Comments![1].Mine);
        Assert.DoesNotContain("4711", await userA.GetStringAsync($"/api/v1/portal/work-orders/{workA.Id}"));
        var staffView = await ReadAsync<WorkOrderDetailDto>(await rig.Manager.GetAsync($"/api/v1/work-orders/{workA.Id}"));
        Assert.Contains(staffView.Comments, c => c.Body == "On our way, 2 pm" && !c.IsInternal);

        // Finished work takes no more messages.
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Manager, $"/api/v1/work-orders/{workA.Id}/cancel", new { reason = "Fixed in house" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(userA, $"/api/v1/portal/work-orders/{workA.Id}/comments", new { body = "too late" })).StatusCode);

        // Contracts and their files.
        async Task<ContractDto> ContractFor(VendorDto vendor) => await ReadAsync<ContractDto>(await PostAsync(rig.Manager, "/api/v1/contracts", new
        {
            vendorId = vendor.Id, number = FacilitiesScope.Code("CT"), title = "AMC " + vendor.Name, type = "Amc", startOn = Today.AddDays(-10), endOn = Today.AddDays(300), notes = "Internal: we pay too much",
        }));

        var contractA = await ContractFor(a);
        var contractB = await ContractFor(b);
        static MultipartFormDataContent Form() => new() { { new ByteArrayContent(Pdf) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream") } }, "file", "a.pdf" } };
        var docA = await ReadAsync<ContractDocumentDto>(await rig.Manager.PostAsync($"/api/v1/contracts/{contractA.Id}/documents", Form()));
        var docB = await ReadAsync<ContractDocumentDto>(await rig.Manager.PostAsync($"/api/v1/contracts/{contractB.Id}/documents", Form()));

        var contractsA = await ReadAsync<PortalContractDto[]>(await userA.GetAsync("/api/v1/portal/contracts"));
        Assert.Equal(contractA.Number, Assert.Single(contractsA).Number);
        Assert.DoesNotContain("too much", await userA.GetStringAsync("/api/v1/portal/contracts"));
        Assert.Single((await userA.GetFromJsonAsync<ContractDocumentDto[]>($"/api/v1/portal/contracts/{contractA.Id}/documents", Json))!);
        Assert.Equal(HttpStatusCode.NotFound, (await userA.GetAsync($"/api/v1/portal/contracts/{contractB.Id}/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await userA.GetAsync($"/api/v1/portal/contract-documents/{docA.Id}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await userA.GetAsync($"/api/v1/portal/contract-documents/{docB.Id}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await userB.GetAsync($"/api/v1/portal/contract-documents/{docA.Id}/download")).StatusCode);
    }

    [Fact]
    public async Task Access_ends_when_it_is_withdrawn_or_the_vendor_is_blocked_and_returns_when_restored()
    {
        var rig = await NewRigAsync();
        var vendor = await VendorAsync(rig, "Vendor A");
        var user = await PortalUserAsync(rig, vendor);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/v1/portal/me")).StatusCode);

        var person = (await ReadAsync<PortalAccessDto>(await rig.Manager.GetAsync($"/api/v1/vendors/{vendor.Id}/portal-access"))).Users.Single();
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Viewer, $"/api/v1/vendors/{vendor.Id}/portal-users/{person.UserId}/active", new { active = false })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(rig.Manager, $"/api/v1/vendors/{vendor.Id}/portal-users/{Guid.NewGuid()}/active", new { active = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Manager, $"/api/v1/vendors/{vendor.Id}/portal-users/{person.UserId}/active", new { active = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/v1/portal/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/v1/portal/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Manager, $"/api/v1/vendors/{vendor.Id}/portal-users/{person.UserId}/active", new { active = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/v1/portal/me")).StatusCode);

        // On hold: the vendor still sees what it already has. Blocked: nothing.
        var held = await ReadAsync<VendorDto>(await PostAsync(rig.Manager, $"/api/v1/vendors/{vendor.Id}/status", new { status = "OnHold", reason = "Audit", rowVersion = vendor.RowVersion }));
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/v1/portal/me")).StatusCode);
        var blocked = await ReadAsync<VendorDto>(await PostAsync(rig.Manager, $"/api/v1/vendors/{vendor.Id}/status", new { status = "Blocked", reason = "Fraud", rowVersion = held.RowVersion }));
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/v1/portal/me")).StatusCode);
        await PostAsync(rig.Manager, $"/api/v1/vendors/{vendor.Id}/status", new { status = "Active", rowVersion = blocked.RowVersion });
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/v1/portal/me")).StatusCode);

        // A staff member (no vendor link) holding portal.use would still see nothing: the permission alone opens no data.
        var admin = await _api.ClientAsync(rig.Tenant, SystemRoles.Administrator);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/v1/portal/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/v1/portal/work-orders")).StatusCode);
    }
}
