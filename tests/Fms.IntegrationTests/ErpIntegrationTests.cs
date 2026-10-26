using System.Net;
using System.Net.Http.Json;
using Fms.BuildingBlocks.Integration;
using Fms.BuildingBlocks.Results;
using Fms.Facilities.Services;
using Fms.Integration.Domain;
using Fms.Integration.Services;
using Fms.Inventory.Services;
using Fms.Platform.Security;
using Fms.Platform.Services;
using Fms.Purchasing.Services;
using Fms.Vendors.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Fms.IntegrationTests.ApiHarness;

namespace Fms.IntegrationTests;

[Collection(SqlCollection.Name)]
public sealed class ErpIntegrationTests(SqlServerFixture sql) : IDisposable
{
    private readonly ApiHarness _api = new(sql, settings: new Dictionary<string, string?> { ["Erp:AllowFakeAdapter"] = "true" });
    public void Dispose() => _api.Dispose();

    private FakeErpAdapter Erp => _api.Factory.Services.GetRequiredService<FakeErpAdapter>();
    private ErpSyncRunner Runner => _api.Factory.Services.GetRequiredService<ErpSyncRunner>();

    private sealed record Rig(Guid Tenant, string Company, HttpClient Manager, HttpClient Admin, HttpClient Technician, HttpClient Viewer);

    private async Task<Rig> NewRigAsync(bool connect = true)
    {
        var tenant = Guid.NewGuid();
        await using (var platform = sql.App(null, platform: true))
        {
            platform.Tenants.Add(new Fms.Platform.Domain.Tenant { Id = tenant, Name = "Erp " + tenant.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
            await platform.SaveChangesAsync();
            await SystemRoleSync.EnsureAsync(platform, tenant);
        }

        var rig = new Rig(tenant, "CO" + tenant.ToString("N")[..6].ToUpperInvariant(), await _api.ClientAsync(tenant, SystemRoles.FacilityManager), await _api.ClientAsync(tenant, SystemRoles.Administrator),
            await _api.ClientAsync(tenant, SystemRoles.Technician), await _api.ClientAsync(tenant, SystemRoles.Viewer));
        if (connect) Assert.Equal(HttpStatusCode.OK, (await PutAsync(rig.Admin, "/api/v1/integration/erp/settings", new { enabled = true, adapter = "fake", companyId = rig.Company })).StatusCode);
        return rig;
    }

    /// <summary>What the worker and the outbox worker would do over a few seconds: deliver messages, send, deliver the acknowledgements.</summary>
    private async Task PumpAsync(DateTimeOffset? asOf = null, int rounds = 3)
    {
        var processor = _api.Factory.Services.GetRequiredService<Fms.Platform.Outbox.OutboxProcessor>();
        for (var r = 0; r < rounds; r++)
        {
            for (var i = 0; i < 20 && await processor.ProcessBatchAsync() > 0; i++) { }
            await Runner.RunOnceAsync(asOf);
        }

        for (var i = 0; i < 20 && await processor.ProcessBatchAsync() > 0; i++) { }
    }

    private static async Task<VendorDto> VendorAsync(Rig rig, string name = "Acme Supplies") =>
        await ReadAsync<VendorDto>(await PostAsync(rig.Manager, "/api/v1/vendors", new { code = FacilitiesScope.Code("V"), name }));

    private static async Task<ErpItemDto[]> ItemsAsync(Rig rig, string query = "") =>
        (await ReadAsync<Page<ErpItemDto>>(await rig.Manager.GetAsync("/api/v1/integration/erp/items?pageSize=100" + query))).Items.ToArray();

    // ---------------- settings ----------------

    [Fact]
    public async Task The_connection_is_set_up_by_administrators_seen_by_managers_and_only_with_an_installed_adapter()
    {
        var rig = await NewRigAsync(connect: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Technician.GetAsync("/api/v1/integration/erp/settings")).StatusCode);
        var before = await ReadAsync<ErpSettingsDto>(await rig.Manager.GetAsync("/api/v1/integration/erp/settings"));
        Assert.False(before.Enabled);
        Assert.Contains("fake", before.AvailableAdapters);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(rig.Manager, "/api/v1/integration/erp/settings", new { enabled = true, adapter = "fake", companyId = "X1" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(rig.Admin, "/api/v1/integration/erp/settings", new { enabled = true, adapter = "d365", companyId = "X1" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(rig.Admin, "/api/v1/integration/erp/settings", new { enabled = true, adapter = "fake", companyId = "bad company!" })).StatusCode);

        var saved = await ReadAsync<ErpSettingsDto>(await PutAsync(rig.Admin, "/api/v1/integration/erp/settings", new { enabled = true, adapter = "fake", companyId = "USMF" }));
        Assert.True(saved.Enabled);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(rig.Admin, "/api/v1/integration/erp/settings", new { enabled = false, adapter = "fake", companyId = "USMF" })).StatusCode); // needs the version once saved
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(rig.Admin, "/api/v1/integration/erp/settings", new { enabled = false, adapter = "fake", companyId = "USMF", rowVersion = before.RowVersion ?? "AAAAAAAAB9E=" })).StatusCode);
        var off = await ReadAsync<ErpSettingsDto>(await PutAsync(rig.Admin, "/api/v1/integration/erp/settings", new { enabled = false, adapter = "fake", companyId = "USMF", rowVersion = saved.RowVersion }));
        Assert.False(off.Enabled);

        // Without the switch in configuration, the built-in fake is not on offer.
        using var strict = new ApiHarness(sql);
        var other = await strict.ClientAsync(rig.Tenant, SystemRoles.Administrator);
        Assert.DoesNotContain("fake", (await ReadAsync<ErpSettingsDto>(await other.GetAsync("/api/v1/integration/erp/settings"))).AvailableAdapters);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(other, "/api/v1/integration/erp/settings", new { enabled = true, adapter = "fake", companyId = "USMF", rowVersion = off.RowVersion })).StatusCode);
    }

    // ---------------- sending ----------------

    [Fact]
    public async Task A_vendor_is_queued_when_it_changes_sent_once_and_its_account_comes_back()
    {
        var rig = await NewRigAsync();
        var vendor = await VendorAsync(rig);
        await PumpAsync(rounds: 1); // delivered into the queue only
        var queued = Assert.Single(await ItemsAsync(rig));
        Assert.Equal((ErpEntity.Vendor, ExportStatus.Succeeded, vendor.Id), (queued.Entity, queued.Status, queued.EntityId)); // one pass sends it too
        Assert.Contains(vendor.Code, queued.Label);
        Assert.StartsWith("FAKE-VENDOR-", queued.ErpReference);

        await PumpAsync();
        var account = (await ReadAsync<VendorDto>(await rig.Manager.GetAsync($"/api/v1/vendors/{vendor.Id}"))).ErpAccount;
        Assert.Equal(queued.ErpReference, account);
        Assert.Equal(1, Erp.Count(rig.Company, ErpEntity.Vendor));
        Assert.Contains("Acme Supplies", Erp.Find(rig.Company, ErpEntity.Vendor, vendor.Id)!.PayloadJson);

        // A change is sent again as a new version, to the same account.
        var current = await ReadAsync<VendorDto>(await rig.Manager.GetAsync($"/api/v1/vendors/{vendor.Id}"));
        await PutAsync(rig.Manager, $"/api/v1/vendors/{vendor.Id}", new { code = current.Code, name = "Acme Supplies Ltd", rowVersion = current.RowVersion });
        await PumpAsync();
        Assert.Contains("Acme Supplies Ltd", Erp.Find(rig.Company, ErpEntity.Vendor, vendor.Id)!.PayloadJson);
        Assert.Equal(1, Erp.Count(rig.Company, ErpEntity.Vendor));
        Assert.Equal(queued.ErpReference, (await ReadAsync<VendorDto>(await rig.Manager.GetAsync($"/api/v1/vendors/{vendor.Id}"))).ErpAccount);
    }

    [Fact]
    public async Task Changes_made_before_a_send_are_collapsed_into_the_latest_and_nothing_is_queued_while_the_connection_is_off()
    {
        var off = await NewRigAsync(connect: false);
        await VendorAsync(off);
        await PumpAsync();
        Assert.Empty(await ItemsAsync(off));

        var rig = await NewRigAsync();
        var vendor = await VendorAsync(rig, "First name");
        var v1 = await ReadAsync<VendorDto>(await rig.Manager.GetAsync($"/api/v1/vendors/{vendor.Id}"));
        var v2 = await ReadAsync<VendorDto>(await PutAsync(rig.Manager, $"/api/v1/vendors/{vendor.Id}", new { code = v1.Code, name = "Second name", rowVersion = v1.RowVersion }));
        await PutAsync(rig.Manager, $"/api/v1/vendors/{vendor.Id}", new { code = v2.Code, name = "Third name", rowVersion = v2.RowVersion });
        var processor = _api.Factory.Services.GetRequiredService<Fms.Platform.Outbox.OutboxProcessor>();
        for (var i = 0; i < 20 && await processor.ProcessBatchAsync() > 0; i++) { } // all three announcements reach the queue before anything is sent

        var items = await ItemsAsync(rig);
        Assert.Equal(3, items.Length);
        Assert.Equal(2, items.Count(i => i.Status == ExportStatus.Superseded));
        var before = Erp.Calls;
        await PumpAsync();
        Assert.Equal(1, Erp.Calls - before); // one send, the latest
        Assert.Contains("Third name", Erp.Find(rig.Company, ErpEntity.Vendor, vendor.Id)!.PayloadJson);
    }

    private async Task<(OrderDto Order, VendorDto Vendor)> OrderAsync(Rig rig)
    {
        var vendor = await VendorAsync(rig, "Filter Supplies");
        var store = await ReadAsync<StoreDto>(await PostAsync(rig.Manager, "/api/v1/inventory/stores", new { code = FacilitiesScope.Code("ST"), name = "Main" }));
        var item = await ReadAsync<ItemDto>(await PostAsync(rig.Manager, "/api/v1/inventory/items", new { code = FacilitiesScope.Code("IT"), name = "Filter" }));
        var draft = await ReadAsync<RequestDto>(await PostAsync(rig.Technician, "/api/v1/purchasing/requests", new { title = "Filters", lines = new[] { new { itemId = item.Id, storeId = store.Id, quantity = 10, estimatedUnitPrice = 100 } } }));
        await PostAsync(rig.Technician, $"/api/v1/purchasing/requests/{draft.Id}/submit", new { });
        var approver = await _api.ClientAsync(rig.Tenant, SystemRoles.FacilityManager);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(approver, $"/api/v1/purchasing/requests/{draft.Id}/approve", new { })).StatusCode);
        return (await ReadAsync<OrderDto>(await PostAsync(rig.Manager, $"/api/v1/purchasing/requests/{draft.Id}/order", new { vendorId = vendor.Id })), vendor);
    }

    [Fact]
    public async Task An_order_waits_for_its_vendor_retries_with_growing_delays_and_then_goes_through_with_its_receipts()
    {
        var rig = await NewRigAsync();
        var vendorFailures = 1;
        Erp.Intercept = c => c.Entity == ErpEntity.Vendor && vendorFailures-- > 0 ? new ErpResult(false, null, true, "The accounting system timed out.") : null;
        var (order, vendor) = await OrderAsync(rig);

        await PumpAsync(rounds: 1);
        var items = await ItemsAsync(rig);
        var vendorItem = items.Single(i => i.Entity == ErpEntity.Vendor);
        var orderItem = items.Single(i => i.Entity == ErpEntity.PurchaseOrder);
        Assert.Equal((ExportStatus.Pending, 1, "The accounting system timed out."), (vendorItem.Status, vendorItem.Attempts, vendorItem.LastError));
        Assert.Equal(ExportStatus.Pending, orderItem.Status); // the vendor is not there yet, so the order has to wait
        Assert.True(vendorItem.NextAttemptAt > DateTimeOffset.UtcNow.AddSeconds(20)); // not retried straight away

        // Time passes: the vendor goes first, then the order.
        await PumpAsync(asOf: DateTimeOffset.UtcNow.AddMinutes(10));
        items = await ItemsAsync(rig);
        Assert.All(items, i => Assert.Equal(ExportStatus.Succeeded, i.Status));
        Assert.Equal(2, items.Single(i => i.Entity == ErpEntity.Vendor).Attempts);
        Assert.Equal(order.Display, (await ReadAsync<OrderDto>(await rig.Manager.GetAsync($"/api/v1/purchasing/orders/{order.Id}"))).Display);
        Assert.StartsWith("FAKE-PURCHASEORDER-", (await ReadAsync<OrderDto>(await rig.Manager.GetAsync($"/api/v1/purchasing/orders/{order.Id}"))).ErpReference);
        Assert.Equal(items.Single(i => i.Entity == ErpEntity.Vendor).ErpReference, (await ReadAsync<VendorDto>(await rig.Manager.GetAsync($"/api/v1/vendors/{vendor.Id}"))).ErpAccount);

        // Receiving goods and cancelling are announced too.
        var line = (await ReadAsync<OrderDto>(await rig.Manager.GetAsync($"/api/v1/purchasing/orders/{order.Id}"))).Lines!.Single();
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Manager, $"/api/v1/purchasing/orders/{order.Id}/receive", new { deliveryNote = "DN-5", lines = new[] { new { lineId = line.Id, quantity = 4 } } })).StatusCode);
        await PumpAsync();
        Assert.Equal(1, Erp.Count(rig.Company, ErpEntity.GoodsReceipt));
        Assert.Single(await ItemsAsync(rig, "&entity=GoodsReceipt&status=Succeeded")); // the receipt is sent once
    }

    [Fact]
    public async Task A_cancelled_order_is_sent_as_a_new_version_of_the_same_order()
    {
        var rig = await NewRigAsync();
        var (order, _) = await OrderAsync(rig);
        await PumpAsync();
        Assert.Contains("Issued", Erp.Find(rig.Company, ErpEntity.PurchaseOrder, order.Id)!.PayloadJson);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Manager, $"/api/v1/purchasing/orders/{order.Id}/cancel", new { reason = "Wrong vendor" })).StatusCode);
        await PumpAsync();
        Assert.Contains("Cancelled", Erp.Find(rig.Company, ErpEntity.PurchaseOrder, order.Id)!.PayloadJson);
        Assert.Equal(1, Erp.Count(rig.Company, ErpEntity.PurchaseOrder));
    }

    // ---------------- failures ----------------

    [Fact]
    public async Task A_refusal_is_set_aside_for_a_person_who_can_fix_the_cause_and_retry_it()
    {
        var rig = await NewRigAsync();
        Erp.Intercept = c => c.Entity == ErpEntity.Vendor ? new ErpResult(false, null, false, "Vendor group is not set up in the accounting system.") : null;
        var vendor = await VendorAsync(rig);
        await PumpAsync();
        var failed = Assert.Single(await ItemsAsync(rig, "&problemsOnly=true"));
        Assert.Equal((ExportStatus.DeadLetter, 1, vendor.Id), (failed.Status, failed.Attempts, failed.EntityId));
        Assert.Contains("Vendor group", failed.LastError);
        var summary = await ReadAsync<ErpSummaryDto>(await rig.Manager.GetAsync("/api/v1/integration/erp/summary"));
        Assert.Equal(1, summary.DeadLetter);

        await PumpAsync(asOf: DateTimeOffset.UtcNow.AddHours(5)); // nothing changes by itself
        Assert.Equal(ExportStatus.DeadLetter, (await ItemsAsync(rig)).Single().Status);

        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Manager, $"/api/v1/integration/erp/items/{failed.Id}/retry", new { })).StatusCode);
        Erp.Intercept = null; // the cause is fixed
        var retried = await ReadAsync<ErpItemDto>(await PostAsync(rig.Admin, $"/api/v1/integration/erp/items/{failed.Id}/retry", new { }));
        Assert.Equal((ExportStatus.Pending, 0), (retried.Status, retried.Attempts));
        await PumpAsync();
        Assert.Equal(ExportStatus.Succeeded, (await ItemsAsync(rig)).Single().Status);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Admin, $"/api/v1/integration/erp/items/{failed.Id}/retry", new { })).StatusCode); // nothing to retry now
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(rig.Admin, $"/api/v1/integration/erp/items/{Guid.NewGuid()}/retry", new { })).StatusCode);
    }

    [Fact]
    public async Task A_failure_that_never_clears_stops_after_the_set_number_of_tries()
    {
        var rig = await NewRigAsync();
        Erp.Intercept = _ => new ErpResult(false, null, true, "Connection refused.");
        await VendorAsync(rig);
        var clock = DateTimeOffset.UtcNow;
        for (var i = 0; i < 10; i++)
        {
            clock = clock.AddHours(2);
            await PumpAsync(asOf: clock, rounds: 1);
        }

        var item = Assert.Single(await ItemsAsync(rig));
        Assert.Equal((ExportStatus.DeadLetter, 8), (item.Status, item.Attempts));
    }

    // ---------------- reconciliation ----------------

    [Fact]
    public async Task Reconciliation_finds_what_the_accounting_system_changed_or_lost()
    {
        var rig = await NewRigAsync();
        var matching = await VendorAsync(rig, "Matches");
        var changed = await VendorAsync(rig, "Gets changed");
        var lost = await VendorAsync(rig, "Gets lost");
        await PumpAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Manager, "/api/v1/integration/erp/reconcile", new { })).StatusCode);
        var clean = await ReadAsync<ReconcileResultDto>(await PostAsync(rig.Admin, "/api/v1/integration/erp/reconcile", new { }));
        Assert.Equal((3, 3, 0, 0), (clean.Checked, clean.Matched, clean.Mismatched, clean.Missing));
        Assert.Equal(0, (await ReadAsync<ErpSummaryDto>(await rig.Manager.GetAsync("/api/v1/integration/erp/summary"))).Mismatched);

        Erp.Tamper(rig.Company, ErpEntity.Vendor, changed.Id);
        Erp.Remove(rig.Company, ErpEntity.Vendor, lost.Id);
        // Things checked within the last hour are left alone; checked later they are looked at again.
        Assert.Equal(0, (await ReadAsync<ReconcileResultDto>(await PostAsync(rig.Admin, "/api/v1/integration/erp/reconcile", new { }))).Checked);
        await using (var db = sql.Integration(null, platform: true))
            await db.Database.ExecuteSqlRawAsync("UPDATE integration.ErpExportItems SET ReconciledAt = DATEADD(HOUR, -3, SYSDATETIMEOFFSET()) WHERE TenantId = {0}", rig.Tenant);

        var found = await ReadAsync<ReconcileResultDto>(await PostAsync(rig.Admin, "/api/v1/integration/erp/reconcile", new { }));
        Assert.Equal((3, 1, 1, 1), (found.Checked, found.Matched, found.Mismatched, found.Missing));
        var problems = await ItemsAsync(rig, "&problemsOnly=true");
        Assert.Equal(2, problems.Length);
        Assert.Contains(problems, p => p.EntityId == changed.Id && p.Reconcile == ReconcileState.Mismatch);
        Assert.Contains(problems, p => p.EntityId == lost.Id && p.Reconcile == ReconcileState.Missing);
        Assert.DoesNotContain(problems, p => p.EntityId == matching.Id);
        Assert.Equal(2, (await ReadAsync<ErpSummaryDto>(await rig.Manager.GetAsync("/api/v1/integration/erp/summary"))).Mismatched);
    }

    // ---------------- concurrency and isolation ----------------

    [Fact]
    public async Task Two_workers_at_once_send_each_item_exactly_once()
    {
        var rig = await NewRigAsync();
        for (var i = 0; i < 12; i++) await VendorAsync(rig, $"Vendor {i}");
        var processor = _api.Factory.Services.GetRequiredService<Fms.Platform.Outbox.OutboxProcessor>();
        for (var i = 0; i < 30 && await processor.ProcessBatchAsync() > 0; i++) { }
        Assert.Equal(12, (await ItemsAsync(rig, "&status=Pending")).Length);

        var before = Erp.Calls;
        var runners = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Runner.RunOnceAsync()));
        for (var i = 0; i < 3; i++) await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Runner.RunOnceAsync()));
        Assert.Equal(12, Erp.Calls - before);
        Assert.Equal(12, (await ItemsAsync(rig, "&status=Succeeded")).Length);
        Assert.Equal(12, Erp.Count(rig.Company, ErpEntity.Vendor));
    }

    [Fact]
    public async Task Each_tenant_sends_to_its_own_company_and_sees_only_its_own_queue()
    {
        var a = await NewRigAsync();
        var b = await NewRigAsync();
        var va = await VendorAsync(a, "Only in A");
        await VendorAsync(b, "Only in B");
        await PumpAsync();
        Assert.Equal(1, Erp.Count(a.Company, ErpEntity.Vendor));
        Assert.Equal(1, Erp.Count(b.Company, ErpEntity.Vendor));
        Assert.Contains("Only in A", Erp.Find(a.Company, ErpEntity.Vendor, va.Id)!.PayloadJson);
        Assert.Null(Erp.Find(b.Company, ErpEntity.Vendor, va.Id));
        Assert.Equal(va.Id, Assert.Single(await ItemsAsync(a)).EntityId);
        Assert.DoesNotContain(await ItemsAsync(b), i => i.EntityId == va.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(b.Admin, $"/api/v1/integration/erp/items/{(await ItemsAsync(a))[0].Id}/retry", new { })).StatusCode);
        await using var db = sql.Integration(b.Tenant);
        Assert.Equal(0, await db.ErpItems.IgnoreQueryFilters().CountAsync(i => i.EntityId == va.Id));
    }
}
