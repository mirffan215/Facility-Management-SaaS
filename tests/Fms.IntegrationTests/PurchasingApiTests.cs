using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Messaging;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Tenancy;
using Fms.Facilities.Services;
using Fms.Inventory.Services;
using Fms.Platform.Notifications;
using Fms.Platform.Security;
using Fms.Platform.Services;
using Fms.Purchasing.Domain;
using Fms.Purchasing.Services;
using Fms.Vendors.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Fms.IntegrationTests.ApiHarness;

namespace Fms.IntegrationTests;

[Collection(SqlCollection.Name)]
public sealed class PurchasingApiTests(SqlServerFixture sql) : IDisposable
{
    private readonly ApiHarness _api = new(sql);
    public void Dispose() => _api.Dispose();

    private sealed record Rig(Guid Tenant, HttpClient Manager, HttpClient Approver, HttpClient Admin, HttpClient Technician, HttpClient Viewer, StoreDto Store, ItemDto Item, VendorDto Vendor);

    private async Task<Rig> NewRigAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var platform = sql.App(null, platform: true))
        {
            platform.Tenants.Add(new Fms.Platform.Domain.Tenant { Id = tenant, Name = "Buy " + tenant.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
            await platform.SaveChangesAsync();
            await SystemRoleSync.EnsureAsync(platform, tenant);
        }

        var manager = await _api.ClientAsync(tenant, SystemRoles.FacilityManager);
        var store = await ReadAsync<StoreDto>(await PostAsync(manager, "/api/v1/inventory/stores", new { code = FacilitiesScope.Code("ST"), name = "Main store" }));
        var item = await ReadAsync<ItemDto>(await PostAsync(manager, "/api/v1/inventory/items", new { code = FacilitiesScope.Code("IT"), name = "Air filter", unit = "pcs" }));
        var vendor = await ReadAsync<VendorDto>(await PostAsync(manager, "/api/v1/vendors", new { code = FacilitiesScope.Code("V"), name = "Filter Supplies" }));
        return new Rig(tenant, manager, await _api.ClientAsync(tenant, SystemRoles.FacilityManager), await _api.ClientAsync(tenant, SystemRoles.Administrator),
            await _api.ClientAsync(tenant, SystemRoles.Technician), await _api.ClientAsync(tenant, SystemRoles.Viewer), store, item, vendor);
    }

    private static object StockLine(Rig rig, decimal quantity, decimal price) => new { itemId = rig.Item.Id, storeId = rig.Store.Id, quantity, estimatedUnitPrice = price };

    private static Task<HttpResponseMessage> NewRequest(HttpClient who, string title, params object[] lines) =>
        PostAsync(who, "/api/v1/purchasing/requests", new { title, justification = "Needed", lines });

    private static async Task<RequestDto> Submitted(HttpClient who, string title, params object[] lines)
    {
        var draft = await ReadAsync<RequestDto>(await NewRequest(who, title, lines));
        return await ReadAsync<RequestDto>(await PostAsync(who, $"/api/v1/purchasing/requests/{draft.Id}/submit", new { }));
    }

    private static Task<HttpResponseMessage> Decide(HttpClient who, Guid id, string action, string? note = null) => PostAsync(who, $"/api/v1/purchasing/requests/{id}/{action}", new { note });

    private async Task DeliverAsync()
    {
        var processor = _api.Factory.Services.GetRequiredService<Fms.Platform.Outbox.OutboxProcessor>();
        for (var i = 0; i < 20 && await processor.ProcessBatchAsync() > 0; i++) { }
    }

    // ---------------- settings and requests ----------------

    [Fact]
    public async Task The_approval_limits_have_defaults_can_be_changed_by_managers_and_are_validated()
    {
        var rig = await NewRigAsync();
        var defaults = await ReadAsync<SettingsDto>(await rig.Technician.GetAsync("/api/v1/purchasing/settings"));
        Assert.Equal((5000m, 50000m, 10m), (defaults.Level1Limit, defaults.Level2Limit, defaults.OverrunTolerancePercent));
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Viewer.GetAsync("/api/v1/purchasing/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(rig.Technician, "/api/v1/purchasing/settings", new { level1Limit = 1, level2Limit = 2, overrunTolerancePercent = 5, currencyCode = "SAR" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(rig.Manager, "/api/v1/purchasing/settings", new { level1Limit = 2000, level2Limit = 1000, overrunTolerancePercent = 5, currencyCode = "SAR" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(rig.Manager, "/api/v1/purchasing/settings", new { level1Limit = 1000, level2Limit = 2000, overrunTolerancePercent = 150, currencyCode = "SAR" })).StatusCode);

        var saved = await ReadAsync<SettingsDto>(await PutAsync(rig.Manager, "/api/v1/purchasing/settings", new { level1Limit = 1000, level2Limit = 10000, overrunTolerancePercent = 5, currencyCode = "usd" }));
        Assert.Equal(("USD", 1000m), (saved.CurrencyCode, saved.Level1Limit));
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(rig.Manager, "/api/v1/purchasing/settings", new { level1Limit = 1, level2Limit = 2, overrunTolerancePercent = 5, currencyCode = "USD", rowVersion = defaults.RowVersion ?? "AAAAAAAAB9E=" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(rig.Manager, "/api/v1/purchasing/settings", new { level1Limit = 1000, level2Limit = 10000, overrunTolerancePercent = 5, currencyCode = "USD" })).StatusCode); // needs the version once saved
    }

    [Fact]
    public async Task A_request_is_drafted_validated_and_seen_only_by_its_maker_and_by_people_who_read_purchasing()
    {
        var rig = await NewRigAsync();
        var other = await NewRigAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await NewRequest(rig.Viewer, "x")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await NewRequest(rig.Technician, "")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await NewRequest(rig.Technician, "No store", new { itemId = rig.Item.Id, quantity = 1, estimatedUnitPrice = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await NewRequest(rig.Technician, "Zero", new { description = "x", quantity = 0, estimatedUnitPrice = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await NewRequest(rig.Technician, "Foreign", new { itemId = other.Item.Id, storeId = other.Store.Id, quantity = 1, estimatedUnitPrice = 5 })).StatusCode);

        var draft = await ReadAsync<RequestDto>(await NewRequest(rig.Technician, "Filters and a crane", StockLine(rig, 10, 12.5m), new { description = "Crane hire", unit = "day", quantity = 2, estimatedUnitPrice = 1500 }));
        Assert.Equal((RequestStatus.Draft, 3125m), (draft.Status, draft.Total));
        Assert.Equal(2, draft.Lines!.Count);
        Assert.Equal("Air filter", draft.Lines[0].Description); // an item's own name when none is given
        Assert.Equal("pcs", draft.Lines[0].Unit);

        // Seen by the maker and by readers; not by other requesters.
        var otherTech = await _api.ClientAsync(rig.Tenant, SystemRoles.Technician);
        Assert.Equal(HttpStatusCode.NotFound, (await otherTech.GetAsync($"/api/v1/purchasing/requests/{draft.Id}")).StatusCode);
        Assert.Empty((await ReadAsync<Page<RequestDto>>(await otherTech.GetAsync("/api/v1/purchasing/requests"))).Items);
        Assert.Equal(draft.Id, (await ReadAsync<Page<RequestDto>>(await rig.Manager.GetAsync("/api/v1/purchasing/requests"))).Items.Single().Id);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Manager.GetAsync($"/api/v1/purchasing/requests/{draft.Id}")).StatusCode);

        // Only the maker edits, only while a draft, and a stale version is refused.
        var edit = new { title = "Filters", lines = new[] { StockLine(rig, 4, 12.5m) }, rowVersion = draft.RowVersion };
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(rig.Manager, $"/api/v1/purchasing/requests/{draft.Id}", edit)).StatusCode);
        var edited = await ReadAsync<RequestDto>(await PutAsync(rig.Technician, $"/api/v1/purchasing/requests/{draft.Id}", edit));
        Assert.Equal((50m, 1), (edited.Total, edited.Lines!.Count));
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(rig.Technician, $"/api/v1/purchasing/requests/{draft.Id}", edit)).StatusCode);

        // Nothing to submit without value.
        var empty = await ReadAsync<RequestDto>(await NewRequest(rig.Technician, "Empty"));
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Technician, $"/api/v1/purchasing/requests/{empty.Id}/submit", new { })).StatusCode);
        var numbers = new[] { draft.Number, empty.Number };
        Assert.Equal(numbers.Distinct().Count(), numbers.Length);
    }

    [Fact]
    public async Task Approval_needs_the_level_the_total_calls_for_from_someone_other_than_the_requester()
    {
        var rig = await NewRigAsync();
        var small = await Submitted(rig.Technician, "Small", StockLine(rig, 10, 100));        // 1,000
        var medium = await Submitted(rig.Technician, "Medium", StockLine(rig, 10, 2000));     // 20,000
        var large = await Submitted(rig.Technician, "Large", StockLine(rig, 10, 6000));       // 60,000
        Assert.Equal((1, 2, 3), (small.RequiredLevel, medium.RequiredLevel, large.RequiredLevel));
        Assert.Equal(RequestStatus.Submitted, small.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Technician, $"/api/v1/purchasing/requests/{small.Id}/submit", new { })).StatusCode); // not a draft any more

        // The maker cannot decide, nor someone without an approval permission.
        var managerRequest = await Submitted(rig.Manager, "Own", StockLine(rig, 1, 100));
        Assert.Equal(HttpStatusCode.Forbidden, (await Decide(rig.Manager, managerRequest.Id, "approve")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Decide(rig.Technician, small.Id, "approve")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Decide(rig.Viewer, small.Id, "approve")).StatusCode);

        // A first/second-level manager cannot approve level 3; the administrator can.
        Assert.Equal(HttpStatusCode.Forbidden, (await Decide(rig.Manager, large.Id, "approve")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Decide(rig.Manager, medium.Id, "approve", "Within budget")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Decide(rig.Admin, large.Id, "approve")).StatusCode);
        var approved = await ReadAsync<RequestDto>(await rig.Manager.GetAsync($"/api/v1/purchasing/requests/{medium.Id}"));
        Assert.Equal((RequestStatus.Approved, "Within budget"), (approved.Status, approved.DecisionNote));
        Assert.Equal(HttpStatusCode.Conflict, (await Decide(rig.Approver, medium.Id, "approve")).StatusCode); // already decided

        // The queue of what is waiting for me shows only what I may decide.
        var waiting = (await ReadAsync<Page<RequestDto>>(await rig.Approver.GetAsync("/api/v1/purchasing/requests?awaitingMe=true"))).Items;
        Assert.Contains(waiting, r => r.Id == small.Id && r.CanApprove);
        Assert.Contains(waiting, r => r.Id == managerRequest.Id);
        Assert.DoesNotContain(waiting, r => r.Id == large.Id);

        // Rejecting needs a reason; cancelling a submitted request does too.
        Assert.Equal(HttpStatusCode.BadRequest, (await Decide(rig.Approver, small.Id, "reject")).StatusCode);
        Assert.Equal(RequestStatus.Rejected, (await ReadAsync<RequestDto>(await Decide(rig.Approver, small.Id, "reject", "Use the existing stock"))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Decide(rig.Manager, managerRequest.Id, "cancel")).StatusCode);
        Assert.Equal(RequestStatus.Cancelled, (await ReadAsync<RequestDto>(await Decide(rig.Manager, managerRequest.Id, "cancel", "No longer needed"))).Status);

        // The people who decide are told, and the requester hears the outcome.
        await DeliverAsync();
        async Task<string[]> Notices(HttpClient c) => (await ReadAsync<Page<NotificationDto>>(await c.GetAsync("/api/v1/notifications?pageSize=100"))).Items.Where(n => n.Type.StartsWith("purchase.")).Select(n => n.Type).ToArray();
        Assert.Contains("purchase.Submitted", await Notices(rig.Approver));
        var techNotices = await Notices(rig.Technician);
        Assert.Contains("purchase.Approved", techNotices);
        Assert.Contains("purchase.Rejected", techNotices);
    }

    [Fact]
    public async Task Tighter_limits_move_a_request_to_a_higher_level()
    {
        var rig = await NewRigAsync();
        var saved = await ReadAsync<SettingsDto>(await PutAsync(rig.Manager, "/api/v1/purchasing/settings", new { level1Limit = 100, level2Limit = 500, overrunTolerancePercent = 10, currencyCode = "SAR" }));
        Assert.Equal(100m, saved.Level1Limit);
        var request = await Submitted(rig.Technician, "Pricey now", StockLine(rig, 10, 60)); // 600
        Assert.Equal(3, request.RequiredLevel);
        Assert.Equal(HttpStatusCode.Forbidden, (await Decide(rig.Approver, request.Id, "approve")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Decide(rig.Admin, request.Id, "approve")).StatusCode);
    }

    // ---------------- orders and receiving ----------------

    private async Task<RequestDto> ApprovedAsync(Rig rig, params object[] lines)
    {
        var request = await Submitted(rig.Technician, "Filters", lines);
        Assert.Equal(HttpStatusCode.OK, (await Decide(rig.Approver, request.Id, "approve")).StatusCode);
        return await ReadAsync<RequestDto>(await rig.Manager.GetAsync($"/api/v1/purchasing/requests/{request.Id}"));
    }

    private static Task<HttpResponseMessage> OrderIt(HttpClient who, RequestDto request, VendorDto vendor, object? prices = null) =>
        PostAsync(who, $"/api/v1/purchasing/requests/{request.Id}/order", new { vendorId = vendor.Id, prices, notes = "Deliver to the dock" });

    [Fact]
    public async Task An_approved_request_becomes_one_order_to_an_active_vendor_within_the_price_tolerance()
    {
        var rig = await NewRigAsync();
        var request = await ApprovedAsync(rig, StockLine(rig, 10, 100)); // 1,000, approved
        var notApproved = await Submitted(rig.Technician, "Waiting", StockLine(rig, 1, 10));

        Assert.Equal(HttpStatusCode.Forbidden, (await OrderIt(rig.Technician, request, rig.Vendor)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await OrderIt(rig.Manager, notApproved, rig.Vendor)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, $"/api/v1/purchasing/requests/{request.Id}/order", new { })).StatusCode);
        var onHold = await ReadAsync<VendorDto>(await PostAsync(rig.Manager, $"/api/v1/vendors/{rig.Vendor.Id}/status", new { status = "OnHold", reason = "Audit", rowVersion = rig.Vendor.RowVersion }));
        Assert.Equal(HttpStatusCode.BadRequest, (await OrderIt(rig.Manager, request, rig.Vendor)).StatusCode);
        await PostAsync(rig.Manager, $"/api/v1/vendors/{rig.Vendor.Id}/status", new { status = "Active", rowVersion = onHold.RowVersion });

        // 1,150 is more than the approved 1,000 plus 10%.
        Assert.Equal(HttpStatusCode.BadRequest, (await OrderIt(rig.Manager, request, rig.Vendor, new[] { new { lineId = request.Lines![0].Id, unitPrice = 115 } })).StatusCode);
        var created = await OrderIt(rig.Manager, request, rig.Vendor, new[] { new { lineId = request.Lines![0].Id, unitPrice = 108 } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var order = await ReadAsync<OrderDto>(created);
        Assert.Equal((OrderStatus.Issued, 1080m, rig.Vendor.Name), (order.Status, order.Total, order.VendorName));
        Assert.Equal(request.Id, order.RequestId);
        Assert.Single(order.Lines!);
        Assert.Equal(HttpStatusCode.Conflict, (await OrderIt(rig.Manager, request, rig.Vendor)).StatusCode); // already ordered
        Assert.Equal(RequestStatus.Ordered, (await ReadAsync<RequestDto>(await rig.Manager.GetAsync($"/api/v1/purchasing/requests/{request.Id}"))).Status);
        Assert.Equal(order.Display, (await ReadAsync<RequestDto>(await rig.Manager.GetAsync($"/api/v1/purchasing/requests/{request.Id}"))).OrderDisplay);

        // Orders are for purchasing readers; the vendor's name on the order does not change when the vendor is renamed.
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Technician.GetAsync($"/api/v1/purchasing/orders/{order.Id}")).StatusCode);
        var current = await ReadAsync<VendorDto>(await rig.Manager.GetAsync($"/api/v1/vendors/{rig.Vendor.Id}"));
        await PutAsync(rig.Manager, $"/api/v1/vendors/{rig.Vendor.Id}", new { code = current.Code, name = "Renamed Ltd", rowVersion = current.RowVersion });
        Assert.Equal("Filter Supplies", (await ReadAsync<OrderDto>(await rig.Manager.GetAsync($"/api/v1/purchasing/orders/{order.Id}"))).VendorName);

        // A cancelled order frees the request to be ordered again.
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, $"/api/v1/purchasing/orders/{order.Id}/cancel", new { })).StatusCode);
        Assert.Equal(OrderStatus.Cancelled, (await ReadAsync<OrderDto>(await PostAsync(rig.Manager, $"/api/v1/purchasing/orders/{order.Id}/cancel", new { reason = "Wrong vendor" }))).Status);
        Assert.Equal(HttpStatusCode.Created, (await OrderIt(rig.Manager, request, rig.Vendor)).StatusCode);
    }

    [Fact]
    public async Task Receiving_goods_records_them_once_and_puts_stock_in_at_the_price_paid()
    {
        var rig = await NewRigAsync();
        var request = await ApprovedAsync(rig, StockLine(rig, 10, 100), new { description = "Delivery fee", unit = "lot", quantity = 1, estimatedUnitPrice = 50 });
        var order = await ReadAsync<OrderDto>(await OrderIt(rig.Manager, request, rig.Vendor, new[] { new { lineId = request.Lines![0].Id, unitPrice = 104 } }));
        var stockLine = order.Lines!.Single(l => l.ItemId != null);
        var feeLine = order.Lines!.Single(l => l.ItemId == null);

        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Technician, $"/api/v1/purchasing/orders/{order.Id}/receive", new { lines = new[] { new { lineId = stockLine.Id, quantity = 1 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, $"/api/v1/purchasing/orders/{order.Id}/receive", new { lines = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, $"/api/v1/purchasing/orders/{order.Id}/receive", new { lines = new[] { new { lineId = stockLine.Id, quantity = 11 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, $"/api/v1/purchasing/orders/{order.Id}/receive", new { lines = new[] { new { lineId = Guid.NewGuid(), quantity = 1 } } })).StatusCode);

        var partial = await ReadAsync<OrderDto>(await PostAsync(rig.Manager, $"/api/v1/purchasing/orders/{order.Id}/receive", new { deliveryNote = "DN-77", lines = new[] { new { lineId = stockLine.Id, quantity = 6 } } }));
        Assert.Equal(OrderStatus.PartiallyReceived, partial.Status);
        Assert.Equal((6m, 4m), (partial.Lines!.Single(l => l.Id == stockLine.Id).ReceivedQty, partial.Lines!.Single(l => l.Id == stockLine.Id).Outstanding));
        Assert.Equal("DN-77", Assert.Single(partial.Receipts!).DeliveryNote);

        await DeliverAsync();
        async Task<StockLevelDto> Level() => (await ReadAsync<Page<StockLevelDto>>(await rig.Manager.GetAsync($"/api/v1/inventory/stock?storeId={rig.Store.Id}&itemId={rig.Item.Id}"))).Items.Single();
        var level = await Level();
        Assert.Equal((6m, 104m), (level.OnHand, level.AverageCost));

        // Over-receiving the rest is refused; receiving it completes the order. Services need no stock.
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, $"/api/v1/purchasing/orders/{order.Id}/receive", new { lines = new[] { new { lineId = stockLine.Id, quantity = 5 } } })).StatusCode);
        var done = await ReadAsync<OrderDto>(await PostAsync(rig.Manager, $"/api/v1/purchasing/orders/{order.Id}/receive", new { lines = new[] { new { lineId = stockLine.Id, quantity = 4 }, new { lineId = feeLine.Id, quantity = 1 } } }));
        Assert.Equal(OrderStatus.Received, done.Status);
        await DeliverAsync();
        Assert.Equal(10m, (await Level()).OnHand);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, $"/api/v1/purchasing/orders/{order.Id}/receive", new { lines = new[] { new { lineId = stockLine.Id, quantity = 1 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, $"/api/v1/purchasing/orders/{order.Id}/cancel", new { reason = "Too late" })).StatusCode);

        // The stock movements say where the goods came from.
        var moves = await ReadAsync<Page<MovementDto>>(await rig.Manager.GetAsync($"/api/v1/inventory/movements?itemId={rig.Item.Id}&type=Receipt"));
        Assert.Equal(2, moves.Total);
        Assert.All(moves.Items, m => Assert.Contains(order.Display, m.Reason));

        // The record of what arrived cannot be rewritten, even by the application's own login.
        await using var db = sql.Purchasing(rig.Tenant);
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("UPDATE purchasing.GoodsReceipts SET Quantity = 99 WHERE OrderId = {0}", order.Id));
    }

    [Fact]
    public async Task A_receipt_delivered_twice_puts_stock_in_once_and_two_people_receiving_at_once_cannot_exceed_the_order()
    {
        var rig = await NewRigAsync();
        var request = await ApprovedAsync(rig, StockLine(rig, 10, 100));
        var order = await ReadAsync<OrderDto>(await OrderIt(rig.Manager, request, rig.Vendor));
        var line = order.Lines!.Single();

        // The same message, handled twice (the outbox delivers at least once).
        var receiptId = Guid.NewGuid();
        var message = new OutboxMessageContext(Guid.NewGuid(), rig.Tenant, GoodsReceived.MessageType, JsonSerializer.Serialize(new GoodsReceivedMessage(receiptId, order.Display, rig.Store.Id, rig.Item.Id, 3m, 100m)), 1);
        for (var i = 0; i < 2; i++)
        {
            await using var scope = _api.Factory.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(rig.Tenant);
            var handler = scope.ServiceProvider.GetServices<IOutboxHandler>().Single(h => h.MessageType == GoodsReceived.MessageType);
            await handler.HandleAsync(message, CancellationToken.None);
        }

        var level = (await ReadAsync<Page<StockLevelDto>>(await rig.Manager.GetAsync($"/api/v1/inventory/stock?storeId={rig.Store.Id}&itemId={rig.Item.Id}"))).Items.Single();
        Assert.Equal(3m, level.OnHand);

        // Eight people receive the whole line at once: the order allows it exactly once.
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => PostAsync(rig.Manager, $"/api/v1/purchasing/orders/{order.Id}/receive", new { lines = new[] { new { lineId = line.Id, quantity = 10 } } })));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Conflict, HttpStatusCode.BadRequest }));
        var final = await ReadAsync<OrderDto>(await rig.Manager.GetAsync($"/api/v1/purchasing/orders/{order.Id}"));
        Assert.Equal((OrderStatus.Received, 10m), (final.Status, final.Lines!.Single().ReceivedQty));
        Assert.Single(final.Receipts!);
    }

    [Fact]
    public async Task Purchasing_is_isolated_by_tenant()
    {
        var rig = await NewRigAsync();
        var other = await NewRigAsync();
        var request = await ApprovedAsync(rig, StockLine(rig, 1, 10));
        var order = await ReadAsync<OrderDto>(await OrderIt(rig.Manager, request, rig.Vendor));
        Assert.Equal(HttpStatusCode.NotFound, (await other.Manager.GetAsync($"/api/v1/purchasing/orders/{order.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Decide(other.Admin, request.Id, "approve")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(other.Manager, $"/api/v1/purchasing/orders/{order.Id}/receive", new { lines = new[] { new { lineId = order.Lines![0].Id, quantity = 1 } } })).StatusCode);
        Assert.Empty((await ReadAsync<Page<OrderDto>>(await other.Manager.GetAsync("/api/v1/purchasing/orders"))).Items);
        await using var db = sql.Purchasing(other.Tenant);
        Assert.Equal(0, await db.Orders.IgnoreQueryFilters().CountAsync(o => o.Id == order.Id));
        Assert.Equal(0, await db.OrderLines.IgnoreQueryFilters().CountAsync());
    }
}
