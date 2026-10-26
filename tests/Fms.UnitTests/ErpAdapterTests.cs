using System.Text.Json;
using Fms.BuildingBlocks.Integration;
using Fms.Integration.Domain;
using Fms.Integration.Services;

namespace Fms.UnitTests;

public class ErpAdapterTests
{
    private static ErpExportCommand Vendor(Guid id, string key, string name = "Acme") =>
        new("CO1", ErpEntity.Vendor, id, key, JsonSerializer.Serialize(new { vendorId = id, name }));

    [Fact]
    public async Task Sending_the_same_key_twice_leaves_one_record_and_returns_the_same_reference()
    {
        var erp = new FakeErpAdapter();
        var id = Guid.NewGuid();
        var first = await erp.ExportAsync(Vendor(id, "Vendor:1:1"));
        var second = await erp.ExportAsync(Vendor(id, "Vendor:1:1"));
        Assert.True(first.Success);
        Assert.Equal(first.ErpReference, second.ErpReference);
        Assert.Equal(1, erp.Count("CO1", ErpEntity.Vendor));
    }

    [Fact]
    public async Task A_new_version_of_the_same_thing_updates_the_record_and_keeps_its_reference()
    {
        var erp = new FakeErpAdapter();
        var id = Guid.NewGuid();
        var first = await erp.ExportAsync(Vendor(id, "Vendor:1:1", "Acme"));
        var second = await erp.ExportAsync(Vendor(id, "Vendor:1:2", "Acme Ltd"));
        Assert.Equal(first.ErpReference, second.ErpReference);
        Assert.Contains("Acme Ltd", erp.Find("CO1", ErpEntity.Vendor, id)!.PayloadJson);
        Assert.Equal(1, erp.Count("CO1", ErpEntity.Vendor));
    }

    [Fact]
    public async Task The_same_thing_in_two_companies_is_two_records()
    {
        var erp = new FakeErpAdapter();
        var id = Guid.NewGuid();
        await erp.ExportAsync(Vendor(id, "k1"));
        await erp.ExportAsync(Vendor(id, "k2") with { CompanyId = "CO2" });
        Assert.Equal(1, erp.Count("CO1", ErpEntity.Vendor));
        Assert.Equal(1, erp.Count("CO2", ErpEntity.Vendor));
    }

    [Fact]
    public async Task An_order_for_a_vendor_the_system_has_not_seen_is_a_retryable_failure_until_the_vendor_arrives()
    {
        var erp = new FakeErpAdapter();
        var vendorId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var order = new ErpExportCommand("CO1", ErpEntity.PurchaseOrder, orderId, "PO:1:1", JsonSerializer.Serialize(new { vendorId }));
        var early = await erp.ExportAsync(order);
        Assert.False(early.Success);
        Assert.True(early.Retryable);
        await erp.ExportAsync(Vendor(vendorId, "V:1"));
        Assert.True((await erp.ExportAsync(order)).Success);
    }

    [Fact]
    public async Task Verification_tells_matched_from_changed_from_missing()
    {
        var erp = new FakeErpAdapter();
        var id = Guid.NewGuid();
        var cmd = Vendor(id, "k");
        var verify = new ErpVerifyCommand("CO1", ErpEntity.Vendor, id, null, cmd.PayloadJson);
        Assert.Equal(ErpMatch.Missing, (await erp.VerifyAsync(verify)).Match);
        await erp.ExportAsync(cmd);
        Assert.Equal(ErpMatch.Matched, (await erp.VerifyAsync(verify)).Match);
        erp.Tamper("CO1", ErpEntity.Vendor, id);
        Assert.Equal(ErpMatch.Mismatch, (await erp.VerifyAsync(verify)).Match);
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(7, 1920)]
    [InlineData(8, 3600)]      // capped at the ceiling
    [InlineData(40, 3600)]     // and never overflows
    public void Retries_wait_longer_each_time_up_to_a_ceiling(int attempts, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), ErpRetry.Delay(attempts, TimeSpan.FromSeconds(30), TimeSpan.FromHours(1)));

    [Theory]
    [InlineData(7, 8, false)]
    [InlineData(8, 8, true)]
    [InlineData(9, 8, true)]
    public void It_gives_up_at_the_limit(int attempts, int max, bool giveUp) => Assert.Equal(giveUp, ErpRetry.GiveUp(attempts, max));
}
