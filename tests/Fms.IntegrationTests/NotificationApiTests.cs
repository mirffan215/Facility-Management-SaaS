using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fms.BuildingBlocks.Results;
using Fms.Facilities.Services;
using Fms.Platform.Notifications;
using Fms.Platform.Outbox;
using Fms.Platform.Security;
using Fms.Work.Services;
using Microsoft.Extensions.DependencyInjection;
using static Fms.IntegrationTests.ApiHarness;
using static Fms.IntegrationTests.SqlServerFixture;

namespace Fms.IntegrationTests;

[Collection(SqlCollection.Name)]
public sealed class NotificationApiTests(SqlServerFixture sql) : IDisposable
{
    private readonly ApiHarness _api = new(sql);
    public void Dispose() => _api.Dispose();

    private sealed record UnreadCount(int Count);

    private async Task<Guid> NewTenantAsync()
    {
        var tenant = Guid.NewGuid();
        await using var platform = sql.App(null, platform: true);
        platform.Tenants.Add(new Fms.Platform.Domain.Tenant { Id = tenant, Name = "Notify " + tenant.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
        await platform.SaveChangesAsync();
        await Fms.Platform.Services.SystemRoleSync.EnsureAsync(platform, tenant);
        return tenant;
    }

    private async Task DeliverAsync()
    {
        // The API process does not run the worker; do what it would.
        var processor = _api.Factory.Services.GetRequiredService<OutboxProcessor>();
        for (var i = 0; i < 20 && await processor.ProcessBatchAsync() > 0; i++) { }
    }

    private static async Task<int> UnreadAsync(HttpClient c) => (await c.GetFromJsonAsync<UnreadCount>("/api/v1/notifications/unread-count", Json))!.Count;

    [Fact]
    public async Task A_request_notifies_approvers_not_the_requester_and_people_manage_only_their_own_notices()
    {
        var tenant = await NewTenantAsync(); // its own tenant, so other tests' events do not count here
        var manager = await _api.ClientAsync(tenant, SystemRoles.FacilityManager);
        var requester = await _api.ClientAsync(tenant, SystemRoles.Requester);
        var viewer = await _api.ClientAsync(tenant, SystemRoles.Viewer);
        var foreign = await _api.ClientAsync(TenantB, SystemRoles.FacilityManager);
        var site = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", new { type = "Site", code = FacilitiesScope.Code("S"), name = "HQ" }));

        var before = await UnreadAsync(manager);
        var created = await ReadAsync<WorkOrderDto>(await PostAsync(requester, "/api/v1/work-orders", new { title = "Door will not close", locationId = site.Id }));
        await DeliverAsync();

        // Every approver of the tenant is told; the requester (the actor) and other tenants are not.
        Assert.Equal(before + 1, await UnreadAsync(manager));
        Assert.Equal(0, await UnreadAsync(requester));
        Assert.Equal(0, await UnreadAsync(viewer));
        Assert.Equal(0, await UnreadAsync(foreign));

        var page = await ReadAsync<Page<NotificationDto>>(await manager.GetAsync("/api/v1/notifications?unreadOnly=true"));
        var notice = page.Items.Single(n => n.Data.GetProperty("workOrderId").GetGuid() == created.Id);
        Assert.Equal("workorder.Created", notice.Type);
        Assert.Equal($"/work-orders/{created.Id}", notice.LinkPath);
        Assert.Equal("Door will not close", notice.Data.GetProperty("title").GetString());
        Assert.False(notice.IsRead);

        // Nobody else can read or clear it.
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(requester, $"/api/v1/notifications/{notice.Id}/read", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(foreign, $"/api/v1/notifications/{notice.Id}/read", new { })).StatusCode);
        Assert.Equal(before + 1, await UnreadAsync(manager));

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(manager, $"/api/v1/notifications/{notice.Id}/read", new { })).StatusCode);
        Assert.Equal(before, await UnreadAsync(manager));
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(manager, $"/api/v1/notifications/{notice.Id}/read", new { })).StatusCode); // idempotent

        // Approval tells the requester; marking all read clears the rest.
        var approver = await _api.ClientAsync(tenant, SystemRoles.FacilityManager);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(approver, $"/api/v1/work-orders/{created.Id}/approve", new { })).StatusCode);
        await DeliverAsync();
        Assert.Equal(1, await UnreadAsync(requester));
        var told = (await ReadAsync<Page<NotificationDto>>(await requester.GetAsync("/api/v1/notifications"))).Items.Single();
        Assert.Equal("workorder.Approved", told.Type);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(requester, "/api/v1/notifications/read-all", new { })).StatusCode);
        Assert.Equal(0, await UnreadAsync(requester));
        Assert.True((await ReadAsync<Page<NotificationDto>>(await requester.GetAsync("/api/v1/notifications"))).Items.Single().IsRead);
    }

    [Fact]
    public async Task Notices_need_a_signed_in_user()
    {
        var anonymous = _api.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/notifications")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/notifications/unread-count")).StatusCode);
        await Task.CompletedTask;
    }
}
