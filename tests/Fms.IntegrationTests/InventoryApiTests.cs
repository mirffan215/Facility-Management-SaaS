using System.Net;
using System.Net.Http.Json;
using Fms.BuildingBlocks.Results;
using Fms.Facilities.Services;
using Fms.Inventory.Domain;
using Fms.Inventory.Services;
using Fms.Platform.Notifications;
using Fms.Platform.Security;
using Fms.Platform.Services;
using Fms.Work.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Fms.IntegrationTests.ApiHarness;

namespace Fms.IntegrationTests;

[Collection(SqlCollection.Name)]
public sealed class InventoryApiTests(SqlServerFixture sql) : IDisposable
{
    private readonly ApiHarness _api = new(sql);
    public void Dispose() => _api.Dispose();

    private sealed record Rig(Guid Tenant, HttpClient Manager, HttpClient Technician, HttpClient Viewer, HttpClient Requester, LocationDto Site);

    private async Task<Rig> NewRigAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var platform = sql.App(null, platform: true))
        {
            platform.Tenants.Add(new Fms.Platform.Domain.Tenant { Id = tenant, Name = "Inv " + tenant.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
            await platform.SaveChangesAsync();
            await SystemRoleSync.EnsureAsync(platform, tenant);
        }

        var manager = await _api.ClientAsync(tenant, SystemRoles.FacilityManager);
        var site = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", new { type = "Site", code = FacilitiesScope.Code("S"), name = "HQ" }));
        return new Rig(tenant, manager, await _api.ClientAsync(tenant, SystemRoles.Technician), await _api.ClientAsync(tenant, SystemRoles.Viewer), await _api.ClientAsync(tenant, SystemRoles.Requester), site);
    }

    private static async Task<StoreDto> StoreAsync(Rig rig, string name = "Main store", Guid? siteId = null) =>
        await ReadAsync<StoreDto>(await PostAsync(rig.Manager, "/api/v1/inventory/stores", new { code = FacilitiesScope.Code("ST"), name, siteId }));

    private static async Task<ItemDto> ItemAsync(Rig rig, string name = "Air filter", string unit = "pcs") =>
        await ReadAsync<ItemDto>(await PostAsync(rig.Manager, "/api/v1/inventory/items", new { code = FacilitiesScope.Code("IT"), name, unit }));

    private static async Task<StockLevelDto> ReceiveAsync(Rig rig, StoreDto store, ItemDto item, decimal quantity, decimal cost) =>
        await ReadAsync<StockLevelDto>(await PostAsync(rig.Manager, "/api/v1/inventory/stock/receive", new { storeId = store.Id, itemId = item.Id, quantity, unitCost = cost, reference = "DN-1" }));

    private static async Task<StockLevelDto> LevelAsync(Rig rig, StoreDto store, ItemDto item) =>
        (await ReadAsync<Page<StockLevelDto>>(await rig.Viewer.GetAsync($"/api/v1/inventory/stock?storeId={store.Id}&itemId={item.Id}&includeZero=true"))).Items.Single();

    private static async Task<WorkOrderDto> WorkOrderAsync(Rig rig, string title = "Fan belt snapped") =>
        await ReadAsync<WorkOrderDto>(await PostAsync(rig.Manager, "/api/v1/work-orders", new { title, type = "Emergency", locationId = rig.Site.Id }));

    private static Task<HttpResponseMessage> Parts(HttpClient c, Guid wo, string action, StoreDto? store, ItemDto item, decimal quantity, string? reason = null) =>
        PostAsync(c, $"/api/v1/work-orders/{wo}/parts/{action}", new { itemId = item.Id, storeId = store?.Id, quantity, reason });

    private static async Task<WorkPartsDto> PartsOf(HttpClient c, Guid wo) => (await c.GetFromJsonAsync<WorkPartsDto>($"/api/v1/work-orders/{wo}/parts", Json))!;

    // ---------------- catalog ----------------

    [Fact]
    public async Task Stores_and_items_are_managed_by_managers_read_by_staff_validated_and_unique()
    {
        var rig = await NewRigAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Technician, "/api/v1/inventory/items", new { code = "X1", name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Viewer, "/api/v1/inventory/stores", new { code = "X1", name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Requester.GetAsync("/api/v1/inventory/items")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/inventory/items", new { code = "bad code!", name = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/inventory/stores", new { code = FacilitiesScope.Code("ST"), name = "Annex", siteId = Guid.NewGuid() })).StatusCode);

        var item = await ItemAsync(rig);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, "/api/v1/inventory/items", new { code = item.Code.ToLowerInvariant(), name = "Again" })).StatusCode);
        var withBarcode = await ReadAsync<ItemDto>(await PostAsync(rig.Manager, "/api/v1/inventory/items", new { code = FacilitiesScope.Code("IT"), name = "Belt", barcode = "6281234567890" }));
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, "/api/v1/inventory/items", new { code = FacilitiesScope.Code("IT"), name = "Belt 2", barcode = "6281234567890" })).StatusCode);
        Assert.Equal(withBarcode.Id, (await ReadAsync<Page<ItemDto>>(await rig.Technician.GetAsync("/api/v1/inventory/items?q=6281234567890"))).Items.Single().Id);

        var store = await StoreAsync(rig, siteId: rig.Site.Id);
        Assert.Equal(rig.Site.Id, store.SiteId);

        // Editing needs the current version; a stale one is refused.
        var renamed = await ReadAsync<ItemDto>(await PutAsync(rig.Manager, $"/api/v1/inventory/items/{item.Id}", new { code = item.Code, name = "Air filter G4", unit = "pcs", rowVersion = item.RowVersion }));
        Assert.Equal("Air filter G4", renamed.Name);
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(rig.Manager, $"/api/v1/inventory/items/{item.Id}", new { code = item.Code, name = "Stale", unit = "pcs", rowVersion = item.RowVersion })).StatusCode);

        // Anything that still holds stock cannot be archived.
        await ReceiveAsync(rig, store, renamed, 5, 10);
        var current = await ReadAsync<ItemDto>(await rig.Manager.GetAsync($"/api/v1/inventory/items/{item.Id}"));
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, $"/api/v1/inventory/items/{item.Id}/archive", new { rowVersion = current.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, $"/api/v1/inventory/stores/{store.Id}/archive", new { rowVersion = store.RowVersion })).StatusCode);
        var empty = await StoreAsync(rig, "Empty");
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Manager, $"/api/v1/inventory/stores/{empty.Id}/archive", new { rowVersion = empty.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/inventory/stock/receive", new { storeId = empty.Id, itemId = item.Id, quantity = 1, unitCost = 1 })).StatusCode); // archived store
    }

    // ---------------- quantities and cost ----------------

    [Fact]
    public async Task Receipts_average_the_cost_transfers_carry_it_and_stock_can_never_go_negative()
    {
        var rig = await NewRigAsync();
        var main = await StoreAsync(rig);
        var van = await StoreAsync(rig, "Van 1");
        var item = await ItemAsync(rig);

        await ReceiveAsync(rig, main, item, 10, 5);
        var level = await ReceiveAsync(rig, main, item, 10, 7);
        Assert.Equal((20m, 6m, 120m), (level.OnHand, level.AverageCost, level.Value));

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/inventory/stock/receive", new { storeId = main.Id, itemId = item.Id, quantity = 0, unitCost = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/inventory/stock/receive", new { storeId = main.Id, itemId = item.Id, quantity = 1, unitCost = -1 })).StatusCode);

        // Moving stock keeps its cost; the van's average is the same 6.
        var moved = await ReadAsync<StockLevelDto[]>(await PostAsync(rig.Manager, "/api/v1/inventory/stock/transfer", new { fromStoreId = main.Id, toStoreId = van.Id, itemId = item.Id, quantity = 5 }));
        Assert.Equal(15m, moved[0].OnHand);
        Assert.Equal((5m, 6m), (moved[1].OnHand, moved[1].AverageCost));
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/inventory/stock/transfer", new { fromStoreId = main.Id, toStoreId = van.Id, itemId = item.Id, quantity = 16 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/inventory/stock/transfer", new { fromStoreId = main.Id, toStoreId = main.Id, itemId = item.Id, quantity = 1 })).StatusCode);

        // A direct adjustment needs a reason and the approve permission; it cannot take stock below zero.
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Technician, "/api/v1/inventory/stock/adjust", new { storeId = van.Id, itemId = item.Id, delta = -1, reason = "broke" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/inventory/stock/adjust", new { storeId = van.Id, itemId = item.Id, delta = -1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/inventory/stock/adjust", new { storeId = van.Id, itemId = item.Id, delta = -6, reason = "lost" })).StatusCode);
        var adjusted = await ReadAsync<StockLevelDto>(await PostAsync(rig.Manager, "/api/v1/inventory/stock/adjust", new { storeId = van.Id, itemId = item.Id, delta = -2, reason = "Damaged in transit" }));
        Assert.Equal(3m, adjusted.OnHand);

        // Every change is in the ledger, newest first, with who and why.
        var ledger = await ReadAsync<Page<MovementDto>>(await rig.Viewer.GetAsync($"/api/v1/inventory/movements?itemId={item.Id}"));
        Assert.Equal(5, ledger.Total); // 2 receipts, transfer out and in, 1 adjustment
        Assert.Equal(MovementType.Adjustment, ledger.Items[0].Type);
        Assert.Equal("Damaged in transit", ledger.Items[0].Reason);
        Assert.NotNull(ledger.Items[0].By);
        Assert.Equal(3m, ledger.Items[0].BalanceAfter);
    }

    [Fact]
    public async Task The_ledger_is_append_only_in_the_database_and_each_tenant_sees_only_its_own_stock()
    {
        var rig = await NewRigAsync();
        var other = await NewRigAsync();
        var store = await StoreAsync(rig);
        var item = await ItemAsync(rig);
        await ReceiveAsync(rig, store, item, 4, 2);

        // Even the application's own database login cannot rewrite or remove a movement.
        await using (var db = sql.Inventory(rig.Tenant))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("UPDATE inventory.StockMovements SET Quantity = 99 WHERE ItemId = {0}", item.Id));
        }

        await using (var db = sql.Inventory(rig.Tenant))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM inventory.StockMovements WHERE ItemId = {0}", item.Id));
        }

        // Another tenant: nothing visible by list or by id, and it cannot use these ids.
        Assert.Empty((await ReadAsync<Page<ItemDto>>(await other.Manager.GetAsync("/api/v1/inventory/items"))).Items);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Manager.GetAsync($"/api/v1/inventory/items/{item.Id}")).StatusCode);
        Assert.Empty((await ReadAsync<Page<StockLevelDto>>(await other.Manager.GetAsync("/api/v1/inventory/stock?includeZero=true"))).Items);
        var otherStore = await StoreAsync(other);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(other.Manager, "/api/v1/inventory/stock/receive", new { storeId = otherStore.Id, itemId = item.Id, quantity = 1, unitCost = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(other.Manager, "/api/v1/inventory/stock/receive", new { storeId = store.Id, itemId = (await ItemAsync(other)).Id, quantity = 1, unitCost = 1 })).StatusCode);

        // The database itself hides the rows from a session for the other tenant.
        await using (var db = sql.Inventory(other.Tenant))
        {
            Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT 1 AS Value FROM inventory.StockMovements WHERE ItemId = {0}", item.Id).CountAsync());
            Assert.Equal(0, await db.Movements.IgnoreQueryFilters().CountAsync(m => m.ItemId == item.Id));
        }

        await using (var db = sql.Inventory(rig.Tenant))
            Assert.Equal(1, await db.Movements.CountAsync(m => m.ItemId == item.Id));
    }

    // ---------------- parts on work orders ----------------

    [Fact]
    public async Task Parts_are_reserved_then_issued_and_returned_and_the_cost_lands_on_the_work_order()
    {
        var rig = await NewRigAsync();
        var store = await StoreAsync(rig);
        var item = await ItemAsync(rig);
        await ReceiveAsync(rig, store, item, 10, 5);
        await ReceiveAsync(rig, store, item, 10, 7); // average 6
        var one = await WorkOrderAsync(rig, "Chiller 1 fault");
        var two = await WorkOrderAsync(rig, "Chiller 2 fault");

        Assert.Equal(HttpStatusCode.Forbidden, (await Parts(rig.Technician, one.Id, "reserve", store, item, 1)).StatusCode); // planners reserve
        Assert.Equal(HttpStatusCode.Forbidden, (await Parts(rig.Viewer, one.Id, "issue", store, item, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Parts(rig.Manager, one.Id, "reserve", store, item, 21)).StatusCode);

        var reserved = await ReadAsync<WorkPartsDto>(await Parts(rig.Manager, one.Id, "reserve", store, item, 8));
        Assert.Equal(8m, Assert.Single(reserved.Lines).Reserved);
        var level = await LevelAsync(rig, store, item);
        Assert.Equal((20m, 8m, 12m), (level.OnHand, level.Reserved, level.Available));

        // What is promised to one work order is not there for another.
        Assert.Equal(HttpStatusCode.BadRequest, (await Parts(rig.Manager, two.Id, "reserve", store, item, 13)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Parts(rig.Technician, two.Id, "issue", store, item, 13)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Parts(rig.Manager, two.Id, "reserve", store, item, 10)).StatusCode);

        // The technician uses 3 of the 8 held: they leave stock at the average cost and the hold shrinks with them.
        var issued = await ReadAsync<WorkPartsDto>(await Parts(rig.Technician, one.Id, "issue", store, item, 3));
        var line = Assert.Single(issued.Lines);
        Assert.Equal((5m, 3m, 6m, 18m), (line.Reserved, line.Issued, line.UnitCost, line.Cost));
        level = await LevelAsync(rig, store, item);
        Assert.Equal((17m, 15m, 2m), (level.OnHand, level.Reserved, level.Available));

        // Cost is read from Inventory: the work order's material cost is the net of issues and returns.
        Assert.Equal(18m, (await ReadAsync<CostSummaryDto>(await rig.Manager.GetAsync($"/api/v1/work-orders/{one.Id}/cost-summary"))).Material);

        // Taking more than its own hold plus what is free is refused; the free 2 can be used.
        Assert.Equal(HttpStatusCode.BadRequest, (await Parts(rig.Technician, one.Id, "issue", store, item, 8)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Parts(rig.Technician, one.Id, "issue", store, item, 7)).StatusCode); // 5 held + 2 free

        // Unused parts go back at the cost they left at; the average of the store follows.
        Assert.Equal(HttpStatusCode.BadRequest, (await Parts(rig.Technician, one.Id, "return", store, item, 11)).StatusCode);
        var returned = await ReadAsync<WorkPartsDto>(await Parts(rig.Technician, one.Id, "return", store, item, 4, "Not needed"));
        Assert.Equal((6m, 36m), (Assert.Single(returned.Lines).Issued, returned.TotalCost));
        Assert.Equal(36m, (await ReadAsync<CostSummaryDto>(await rig.Manager.GetAsync($"/api/v1/work-orders/{one.Id}/cost-summary"))).Material);
        level = await LevelAsync(rig, store, item);
        Assert.Equal((14m, 10m, 6m), (level.OnHand, level.Reserved, level.AverageCost)); // 10 held by the other work order

        var moves = await ReadAsync<Page<MovementDto>>(await rig.Viewer.GetAsync($"/api/v1/inventory/movements?workOrderId={one.Id}"));
        Assert.Equal(3, moves.Total);
        Assert.Equal(MovementType.Return, moves.Items[0].Type);
    }

    [Fact]
    public async Task Many_people_issuing_at_once_never_take_more_than_there_is()
    {
        var rig = await NewRigAsync();
        var store = await StoreAsync(rig);
        var item = await ItemAsync(rig);
        await ReceiveAsync(rig, store, item, 5, 3);
        var wo = await WorkOrderAsync(rig);

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Parts(rig.Technician, wo.Id, "issue", store, item, 1)));
        Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(5, results.Count(r => r.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict));
        var level = await LevelAsync(rig, store, item);
        Assert.Equal(0m, level.OnHand);
        Assert.Equal(5m, Assert.Single((await PartsOf(rig.Viewer, wo.Id)).Lines).Issued);
        Assert.Equal(5, (await ReadAsync<Page<MovementDto>>(await rig.Viewer.GetAsync($"/api/v1/inventory/movements?workOrderId={wo.Id}&type=Issue"))).Total);
    }

    [Fact]
    public async Task Holds_are_let_go_when_the_work_order_is_cancelled_and_no_parts_go_to_finished_work()
    {
        var rig = await NewRigAsync();
        var store = await StoreAsync(rig);
        var item = await ItemAsync(rig);
        await ReceiveAsync(rig, store, item, 10, 2);
        var wo = await WorkOrderAsync(rig);
        Assert.Equal(HttpStatusCode.OK, (await Parts(rig.Manager, wo.Id, "reserve", store, item, 6)).StatusCode);
        Assert.Equal(4m, (await LevelAsync(rig, store, item)).Available);

        // A manual release also works, and can be repeated by reserving again.
        Assert.Equal(HttpStatusCode.OK, (await Parts(rig.Manager, wo.Id, "release", store, item, 0)).StatusCode);
        Assert.Equal(10m, (await LevelAsync(rig, store, item)).Available);
        Assert.Equal(HttpStatusCode.NotFound, (await Parts(rig.Manager, wo.Id, "release", store, item, 0)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Parts(rig.Manager, wo.Id, "reserve", store, item, 6)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Manager, $"/api/v1/work-orders/{wo.Id}/cancel", new { reason = "Replaced the whole unit" })).StatusCode);
        var processor = _api.Factory.Services.GetRequiredService<Fms.Platform.Outbox.OutboxProcessor>();
        for (var i = 0; i < 20 && await processor.ProcessBatchAsync() > 0; i++) { }

        Assert.Equal((10m, 0m), ((await LevelAsync(rig, store, item)).Available, (await LevelAsync(rig, store, item)).Reserved));
        Assert.Equal(HttpStatusCode.Conflict, (await Parts(rig.Manager, wo.Id, "reserve", store, item, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Parts(rig.Technician, wo.Id, "issue", store, item, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Parts(rig.Manager, Guid.NewGuid(), "reserve", store, item, 1)).StatusCode);
    }

    [Fact]
    public async Task A_work_order_of_another_tenant_cannot_be_given_parts()
    {
        var rig = await NewRigAsync();
        var other = await NewRigAsync();
        var store = await StoreAsync(rig);
        var item = await ItemAsync(rig);
        await ReceiveAsync(rig, store, item, 5, 2);
        var foreign = await WorkOrderAsync(other);
        Assert.Equal(HttpStatusCode.NotFound, (await Parts(rig.Manager, foreign.Id, "reserve", store, item, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.Manager.GetAsync($"/api/v1/work-orders/{foreign.Id}/parts")).StatusCode);
    }

    // ---------------- preventive maintenance parts ----------------

    [Fact]
    public async Task A_preventive_visit_reserves_the_parts_its_template_lists_and_a_shortage_never_blocks_the_visit()
    {
        var rig = await NewRigAsync();
        var store = await StoreAsync(rig, siteId: rig.Site.Id);
        var filter = await ItemAsync(rig, "Filter");
        var belt = await ItemAsync(rig, "Belt");
        await ReceiveAsync(rig, store, filter, 10, 4);
        await ReceiveAsync(rig, store, belt, 1, 9); // only one belt

        var cls = await ReadAsync<AssetClassDto>(await PostAsync(rig.Manager, "/api/v1/asset-classes", new { code = FacilitiesScope.Code("C"), name = "AHU" }));
        var asset = await ReadAsync<AssetDto>(await PostAsync(rig.Manager, "/api/v1/assets", new { code = FacilitiesScope.Code("A"), name = "AHU 1", assetClassId = cls.Id, locationId = rig.Site.Id, startActive = true }));
        var template = await ReadAsync<PmTemplateDto>(await PostAsync(rig.Manager, "/api/v1/pm/templates", new { name = "AHU service", priority = "Medium" }));

        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(rig.Manager, $"/api/v1/inventory/pm-templates/{Guid.NewGuid()}/parts", new { parts = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(rig.Manager, $"/api/v1/inventory/pm-templates/{template.Id}/parts", new { parts = new[] { new { itemId = filter.Id, quantity = 0 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(rig.Technician, $"/api/v1/inventory/pm-templates/{template.Id}/parts", new { parts = Array.Empty<object>() })).StatusCode);
        var saved = await ReadAsync<PmTemplatePartDto[]>(await PutAsync(rig.Manager, $"/api/v1/inventory/pm-templates/{template.Id}/parts", new { parts = new[] { new { itemId = filter.Id, quantity = 4 }, new { itemId = belt.Id, quantity = 2 } } }));
        Assert.Equal(2, saved.Length);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var schedule = await PostAsync(rig.Manager, "/api/v1/pm/schedules", new { name = "AHU", templateId = template.Id, targetKind = "Asset", assetId = asset.Id, timeBasis = "Fixed", intervalUnit = "Month", interval = 3, startOn = today.AddDays(2), leadDays = 7 });
        Assert.Equal(HttpStatusCode.Created, schedule.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Manager, "/api/v1/pm/run", new { })).StatusCode);

        var planned = (await ReadAsync<Page<WorkOrderDto>>(await rig.Manager.GetAsync("/api/v1/work-orders?pageSize=100"))).Items.Single(w => w.Type == Fms.Work.Domain.WorkOrderType.Planned);
        var parts = await PartsOf(rig.Manager, planned.Id);
        var held = Assert.Single(parts.Lines); // the filters; the second belt was not there, so that line is skipped, not half-made
        Assert.Equal((filter.Code, 4m), (held.ItemCode, held.Reserved));
        Assert.Equal(6m, (await LevelAsync(rig, store, filter)).Available);
        Assert.Equal(1m, (await LevelAsync(rig, store, belt)).Available);

        await using var audit = sql.App(rig.Tenant);
        Assert.True(await audit.AuditEvents.AnyAsync(a => a.Action == "inventory.reservation_short" && a.EntityId == planned.Id.ToString()));
    }

    // ---------------- low stock ----------------

    [Fact]
    public async Task Reorder_levels_flag_low_stock_and_the_stock_people_are_told_once_per_dip()
    {
        var rig = await NewRigAsync();
        var store = await StoreAsync(rig);
        var item = await ItemAsync(rig, "Fuse");
        var level = await ReceiveAsync(rig, store, item, 10, 1);

        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(rig.Manager, "/api/v1/inventory/stock/levels", new { storeId = store.Id, itemId = item.Id, minQty = 5, maxQty = 3, rowVersion = level.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(rig.Manager, "/api/v1/inventory/stock/levels", new { storeId = store.Id, itemId = item.Id, minQty = 5, maxQty = 20 })).StatusCode); // version needed
        var set = await ReadAsync<StockLevelDto>(await PutAsync(rig.Manager, "/api/v1/inventory/stock/levels", new { storeId = store.Id, itemId = item.Id, minQty = 5, maxQty = 20, rowVersion = level.RowVersion }));
        Assert.False(set.IsLow);
        Assert.Empty((await ReadAsync<Page<StockLevelDto>>(await rig.Viewer.GetAsync("/api/v1/inventory/stock?lowOnly=true"))).Items);

        var wo = await WorkOrderAsync(rig);
        Assert.Equal(HttpStatusCode.OK, (await Parts(rig.Technician, wo.Id, "issue", store, item, 6)).StatusCode); // 4 left: below the minimum of 5
        var low = Assert.Single((await ReadAsync<Page<StockLevelDto>>(await rig.Viewer.GetAsync("/api/v1/inventory/stock?lowOnly=true"))).Items);
        Assert.True(low.IsLow);
        Assert.Equal(16m, low.SuggestedOrder);

        var processor = _api.Factory.Services.GetRequiredService<Fms.Platform.Outbox.OutboxProcessor>();
        for (var i = 0; i < 20 && await processor.ProcessBatchAsync() > 0; i++) { }
        async Task<int> LowNotices() => (await ReadAsync<Page<NotificationDto>>(await rig.Manager.GetAsync("/api/v1/notifications"))).Items.Count(n => n.Type == "inventory.low");
        Assert.Equal(1, await LowNotices());

        // Further use while still low does not tell them again; recovering and dipping again does.
        Assert.Equal(HttpStatusCode.OK, (await Parts(rig.Technician, wo.Id, "issue", store, item, 1)).StatusCode);
        await ReceiveAsync(rig, store, item, 20, 1);
        Assert.Equal(HttpStatusCode.OK, (await Parts(rig.Technician, wo.Id, "issue", store, item, 20)).StatusCode);
        for (var i = 0; i < 20 && await processor.ProcessBatchAsync() > 0; i++) { }
        Assert.Equal(2, await LowNotices());
        Assert.Equal(0, (await ReadAsync<Page<NotificationDto>>(await rig.Viewer.GetAsync("/api/v1/notifications"))).Items.Count(n => n.Type == "inventory.low")); // viewers are not told
    }
}
