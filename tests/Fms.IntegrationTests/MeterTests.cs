using Fms.BuildingBlocks.Results;
using Fms.Facilities.Domain;
using Fms.Facilities.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using static Fms.IntegrationTests.SqlServerFixture;

namespace Fms.IntegrationTests;

[Collection(SqlCollection.Name)]
public sealed class MeterTests(SqlServerFixture sql)
{
    private static Dictionary<string, string[]> ErrorsOf<T>(Result<T> r) => r.Errors!.ToDictionary(e => e.Key, e => e.Value);

    private async Task<AssetDto> NewAssetAsync(Guid tenant, bool active = true)
    {
        await using var f = FacilitiesScope.For(sql, tenant);
        var site = await f.NewAsync(LocationType.Site, null);
        var cls = await f.NewClassAsync();
        return await f.NewAssetAsync(cls.Id, site.Id, active: active);
    }

    private async Task<Result<RecordedReading>> Record(Guid tenant, Guid meterId, decimal value, DateTimeOffset? at = null, bool reset = false, Guid? clientId = null, string? note = null)
    {
        await using var f = FacilitiesScope.For(sql, tenant);
        return await f.Meters.RecordAsync(meterId, new RecordReadingRequest(value, at, note, reset, clientId, null));
    }

    [Fact]
    public async Task Meters_are_defined_per_asset_with_validation_and_unique_names()
    {
        var asset = await NewAssetAsync(TenantA);
        await using var f = FacilitiesScope.For(sql, TenantA);
        var meter = await f.NewMeterAsync(asset.Id, name: "Energy");

        Assert.Null(meter.LastValue);
        Assert.Equal(ResultStatus.Conflict, (await f.Meters.CreateAsync(asset.Id, new CreateMeterRequest("Energy", null, "kWh", MeterKind.Cumulative, null, null))).Status);
        var bad = await f.Meters.CreateAsync(asset.Id, new CreateMeterRequest("", null, "", (MeterKind)9, 10, 5));
        Assert.True(new[] { "name", "unit", "kind", "maxValue" }.All(ErrorsOf(bad).ContainsKey));
        Assert.Equal(ResultStatus.NotFound, (await f.Meters.CreateAsync(Guid.NewGuid(), new CreateMeterRequest("x", null, "h", MeterKind.Gauge, null, null))).Status);
    }

    [Fact]
    public async Task An_asset_has_a_limited_number_of_active_meters_and_a_retired_asset_gets_none()
    {
        var asset = await NewAssetAsync(TenantA);
        await using var f = FacilitiesScope.For(sql, TenantA);
        for (var i = 0; i < MeterService.MaxMetersPerAsset; i++) await f.NewMeterAsync(asset.Id, name: $"M{i}");
        Assert.Equal(ResultStatus.Conflict, (await f.Meters.CreateAsync(asset.Id, new CreateMeterRequest("one too many", null, "h", MeterKind.Gauge, null, null))).Status);

        var retired = await NewAssetAsync(TenantA);
        await using (var g = FacilitiesScope.For(sql, TenantA))
            Assert.True((await g.Assets.ChangeStatusAsync(retired.Id, new ChangeAssetStatusRequest(AssetStatus.Retired, "x", retired.RowVersion))).IsOk);
        await using var h = FacilitiesScope.For(sql, TenantA);
        Assert.Equal(ResultStatus.Conflict, (await h.Meters.CreateAsync(retired.Id, new CreateMeterRequest("late", null, "h", MeterKind.Gauge, null, null))).Status);
    }

    [Fact]
    public async Task A_reading_updates_the_latest_value_and_records_who_took_it()
    {
        var asset = await NewAssetAsync(TenantA);
        await using var f = FacilitiesScope.For(sql, TenantA);
        var meter = await f.NewMeterAsync(asset.Id, MeterKind.Gauge, min: -50, max: 150);

        var at = DateTimeOffset.UtcNow.AddMinutes(-10);
        var recorded = await f.Meters.RecordAsync(meter.Id, new RecordReadingRequest(21.5m, at, "morning round", false, null, ReadingSource.Mobile));

        Assert.True(recorded.IsOk);
        Assert.Equal(f.UserId, recorded.Value!.Reading.RecordedBy);
        Assert.Equal(ReadingSource.Mobile, recorded.Value.Reading.Source);
        await using var g = FacilitiesScope.For(sql, TenantA);
        var latest = (await g.Meters.GetAsync(meter.Id)).Value!;
        Assert.Equal(21.5m, latest.LastValue);
        Assert.Equal(at, latest.LastReadAt);
    }

    [Fact]
    public async Task Readings_are_checked_for_range_date_note_source_and_reset_use()
    {
        var asset = await NewAssetAsync(TenantA);
        await using var f = FacilitiesScope.For(sql, TenantA);
        var gauge = await f.NewMeterAsync(asset.Id, MeterKind.Gauge, min: 0, max: 100);
        var now = DateTimeOffset.UtcNow;

        var tooHigh = await f.Meters.RecordAsync(gauge.Id, new RecordReadingRequest(101, null, null, false, null, null));
        var tooLow = await f.Meters.RecordAsync(gauge.Id, new RecordReadingRequest(-1, null, null, false, null, null));
        var future = await f.Meters.RecordAsync(gauge.Id, new RecordReadingRequest(5, now.AddHours(1), null, false, null, null));
        var ancient = await f.Meters.RecordAsync(gauge.Id, new RecordReadingRequest(5, now.AddYears(-11), null, false, null, null));
        var longNote = await f.Meters.RecordAsync(gauge.Id, new RecordReadingRequest(5, null, new string('n', 501), false, null, null));
        var resetGauge = await f.Meters.RecordAsync(gauge.Id, new RecordReadingRequest(5, null, null, true, null, null));
        var device = await f.Meters.RecordAsync(gauge.Id, new RecordReadingRequest(5, null, null, false, null, ReadingSource.Device));

        Assert.Contains("value", ErrorsOf(tooHigh).Keys);
        Assert.Contains("value", ErrorsOf(tooLow).Keys);
        Assert.Contains("readAt", ErrorsOf(future).Keys);
        Assert.Contains("readAt", ErrorsOf(ancient).Keys);
        Assert.Contains("note", ErrorsOf(longNote).Keys);
        Assert.Contains("isReset", ErrorsOf(resetGauge).Keys);
        Assert.Contains("source", ErrorsOf(device).Keys);
        Assert.Equal(ResultStatus.NotFound, (await f.Meters.RecordAsync(Guid.NewGuid(), new RecordReadingRequest(1, null, null, false, null, null))).Status);
    }

    [Fact]
    public async Task A_cumulative_meter_cannot_go_down_unless_it_is_a_reset()
    {
        var asset = await NewAssetAsync(TenantA);
        await using var f = FacilitiesScope.For(sql, TenantA);
        var meter = await f.NewMeterAsync(asset.Id);
        var t = DateTimeOffset.UtcNow.AddDays(-10);

        Assert.True((await Record(TenantA, meter.Id, 1000, t)).IsOk);
        Assert.True((await Record(TenantA, meter.Id, 1500, t.AddDays(1))).IsOk);
        Assert.Contains("value", ErrorsOf(await Record(TenantA, meter.Id, 1400, t.AddDays(2))).Keys);
        Assert.True((await Record(TenantA, meter.Id, 1500, t.AddDays(2))).IsOk);              // unchanged is fine
        Assert.True((await Record(TenantA, meter.Id, 20, t.AddDays(3), reset: true)).IsOk);    // meter replaced
        Assert.True((await Record(TenantA, meter.Id, 80, t.AddDays(4))).IsOk);                 // counts from the reset
        Assert.Contains("value", ErrorsOf(await Record(TenantA, meter.Id, 10, t.AddDays(5))).Keys);
    }

    [Fact]
    public async Task Backdated_cumulative_readings_must_fit_between_their_neighbours()
    {
        var asset = await NewAssetAsync(TenantA);
        await using var f = FacilitiesScope.For(sql, TenantA);
        var meter = await f.NewMeterAsync(asset.Id);
        var t = DateTimeOffset.UtcNow.AddDays(-10);
        await Record(TenantA, meter.Id, 100, t);
        await Record(TenantA, meter.Id, 200, t.AddDays(2));

        Assert.True((await Record(TenantA, meter.Id, 150, t.AddDays(1))).IsOk);
        Assert.Contains("value", ErrorsOf(await Record(TenantA, meter.Id, 250, t.AddDays(1).AddHours(1))).Keys);
        Assert.Contains("value", ErrorsOf(await Record(TenantA, meter.Id, 90, t.AddDays(1).AddHours(2))).Keys);
    }

    [Fact]
    public async Task Gauges_may_move_both_ways()
    {
        var asset = await NewAssetAsync(TenantA);
        await using var f = FacilitiesScope.For(sql, TenantA);
        var meter = await f.NewMeterAsync(asset.Id, MeterKind.Gauge);
        var t = DateTimeOffset.UtcNow.AddHours(-5);

        Assert.True((await Record(TenantA, meter.Id, 30, t)).IsOk);
        Assert.True((await Record(TenantA, meter.Id, 10, t.AddHours(1))).IsOk);
        Assert.True((await Record(TenantA, meter.Id, 50, t.AddHours(2))).IsOk);
    }

    [Fact]
    public async Task A_retried_submission_with_the_same_client_id_is_stored_once()
    {
        var asset = await NewAssetAsync(TenantA);
        await using var f = FacilitiesScope.For(sql, TenantA);
        var meter = await f.NewMeterAsync(asset.Id);
        var clientId = Guid.NewGuid();

        var first = await Record(TenantA, meter.Id, 100, clientId: clientId);
        var again = await Record(TenantA, meter.Id, 100, clientId: clientId);
        var changedPayload = await Record(TenantA, meter.Id, 999, clientId: clientId);

        Assert.False(first.Value!.WasDuplicate);
        Assert.True(again.Value!.WasDuplicate);
        Assert.True(changedPayload.Value!.WasDuplicate);
        Assert.Equal(first.Value.Reading.Id, again.Value.Reading.Id);
        Assert.Equal(100, changedPayload.Value.Reading.Value); // the original wins; a client id is not a way to edit
        await using var check = sql.Facilities(TenantA);
        Assert.Equal(1, await check.MeterReadings.CountAsync(r => r.MeterId == meter.Id));
    }

    [Fact]
    public async Task Racing_submissions_of_one_client_id_create_exactly_one_reading()
    {
        var asset = await NewAssetAsync(TenantA);
        await using var f = FacilitiesScope.For(sql, TenantA);
        var meter = await f.NewMeterAsync(asset.Id, MeterKind.Gauge);
        var clientId = Guid.NewGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Record(TenantA, meter.Id, 42, clientId: clientId)));

        Assert.All(results, r => Assert.True(r.IsOk, r.Message));
        Assert.Single(results.Select(r => r.Value!.Reading.Id).Distinct());
        Assert.Equal(1, results.Count(r => !r.Value!.WasDuplicate));
        await using var check = sql.Facilities(TenantA);
        Assert.Equal(1, await check.MeterReadings.CountAsync(r => r.MeterId == meter.Id));
    }

    [Fact]
    public async Task A_batch_reports_each_item_and_validates_in_time_order()
    {
        var asset = await NewAssetAsync(TenantA);
        await using var f = FacilitiesScope.For(sql, TenantA);
        var meter = await f.NewMeterAsync(asset.Id);
        var t = DateTimeOffset.UtcNow.AddDays(-5);
        var dup = Guid.NewGuid();
        await Record(TenantA, meter.Id, 100, t.AddHours(-1), clientId: dup);

        await using var g = FacilitiesScope.For(sql, TenantA);
        var batch = await g.Meters.RecordBatchAsync(
        [
            new BatchReadingItem(meter.Id, 300, t.AddDays(2), null, false, null, null),   // submitted first but happened last
            new BatchReadingItem(meter.Id, 200, t.AddDays(1), null, false, null, null),
            new BatchReadingItem(meter.Id, 100, t.AddHours(-1), null, false, dup, null),   // already stored
            new BatchReadingItem(meter.Id, 50, t.AddDays(3), null, false, null, null),     // lower than 300: rejected
            new BatchReadingItem(Guid.NewGuid(), 1, null, null, false, null, null),        // unknown meter
        ]);

        Assert.True(batch.IsOk);
        Assert.Equal(["Created", "Created", "Duplicate", "Rejected", "Rejected"], batch.Value!.Select(r => r.Outcome));
        Assert.Equal([0, 1, 2, 3, 4], batch.Value!.Select(r => r.Index));
        Assert.NotNull(batch.Value![3].Error);
    }

    [Fact]
    public async Task Batches_have_a_size_limit_and_cannot_be_empty()
    {
        await using var f = FacilitiesScope.For(sql, TenantA);
        Assert.Equal(ResultStatus.Invalid, (await f.Meters.RecordBatchAsync([])).Status);
        Assert.Equal(ResultStatus.Invalid, (await f.Meters.RecordBatchAsync(null)).Status);
        var tooMany = Enumerable.Range(0, MeterService.MaxBatchSize + 1).Select(_ => new BatchReadingItem(Guid.NewGuid(), 1, null, null, false, null, null)).ToList();
        Assert.Equal(ResultStatus.Invalid, (await f.Meters.RecordBatchAsync(tooMany)).Status);
    }

    [Fact]
    public async Task Voiding_a_typo_unblocks_later_readings_and_keeps_the_record()
    {
        var asset = await NewAssetAsync(TenantA);
        await using var f = FacilitiesScope.For(sql, TenantA);
        var meter = await f.NewMeterAsync(asset.Id);
        var t = DateTimeOffset.UtcNow.AddDays(-5);
        await Record(TenantA, meter.Id, 1000, t);
        var typo = (await Record(TenantA, meter.Id, 100000, t.AddDays(1))).Value!.Reading;

        Assert.Contains("value", ErrorsOf(await Record(TenantA, meter.Id, 1100, t.AddDays(2))).Keys); // lower than the typo, so refused until the typo is voided
        await using (var v = FacilitiesScope.For(sql, TenantA))
        {
            Assert.Equal(ResultStatus.Invalid, (await v.Meters.VoidAsync(typo.Id, new VoidReadingRequest(" "))).Status);
            Assert.True((await v.Meters.VoidAsync(typo.Id, new VoidReadingRequest("extra zero"))).IsOk);
        }

        Assert.True((await Record(TenantA, meter.Id, 1100, t.AddDays(2))).IsOk);
        await using var g = FacilitiesScope.For(sql, TenantA);
        Assert.Equal(1100, (await g.Meters.GetAsync(meter.Id)).Value!.LastValue);
        var visible = (await g.Meters.ListReadingsAsync(meter.Id, new ReadingQuery())).Value!;
        Assert.DoesNotContain(visible.Items, r => r.Id == typo.Id);
        var all = (await g.Meters.ListReadingsAsync(meter.Id, new ReadingQuery(IncludeVoided: true))).Value!;
        Assert.True(all.Items.Single(r => r.Id == typo.Id).IsVoided);

        await using var h = FacilitiesScope.For(sql, TenantA);
        Assert.Equal(ResultStatus.Conflict, (await h.Meters.VoidAsync(typo.Id, new VoidReadingRequest("again"))).Status);
        await using var check = sql.App(TenantA);
        Assert.Contains(await check.AuditEvents.Where(e => e.EntityId == typo.Id.ToString()).Select(e => e.Action).ToListAsync(), a => a == "meter.reading_voided");
    }

    [Fact]
    public async Task The_database_refuses_to_change_or_delete_readings_and_voids()
    {
        var asset = await NewAssetAsync(TenantA);
        await using var f = FacilitiesScope.For(sql, TenantA);
        var meter = await f.NewMeterAsync(asset.Id, MeterKind.Gauge);
        var reading = (await Record(TenantA, meter.Id, 7)).Value!.Reading;
        await using (var v = FacilitiesScope.For(sql, TenantA)) await v.Meters.VoidAsync(reading.Id, new VoidReadingRequest("test"));

        await using var db = sql.Facilities(TenantA);
        var update = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlAsync($"UPDATE facilities.MeterReadings SET Value = 8 WHERE Id = {reading.Id}"));
        var delete = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlAsync($"DELETE FROM facilities.MeterReadings WHERE Id = {reading.Id}"));
        var voidDelete = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlAsync($"DELETE FROM facilities.MeterReadingVoids WHERE ReadingId = {reading.Id}"));
        Assert.Equal(51001, update.Number);
        Assert.Equal(51001, delete.Number);
        Assert.Equal(51002, voidDelete.Number);
    }

    [Fact]
    public async Task Archived_meters_and_retired_assets_accept_no_readings()
    {
        var asset = await NewAssetAsync(TenantA);
        await using var f = FacilitiesScope.For(sql, TenantA);
        var meter = await f.NewMeterAsync(asset.Id, MeterKind.Gauge);
        await using (var g = FacilitiesScope.For(sql, TenantA))
            Assert.True((await g.Meters.SetArchivedAsync(meter.Id, true, meter.RowVersion)).IsOk);

        Assert.Equal(ResultStatus.Conflict, (await Record(TenantA, meter.Id, 1)).Status);

        await using var h = FacilitiesScope.For(sql, TenantA);
        var current = (await h.Meters.GetAsync(meter.Id)).Value!;
        Assert.True((await h.Meters.SetArchivedAsync(meter.Id, false, current.RowVersion)).IsOk);
        Assert.True((await Record(TenantA, meter.Id, 1)).IsOk);

        await using var i = FacilitiesScope.For(sql, TenantA);
        var live = (await i.Assets.GetAsync(asset.Id)).Value!;
        Assert.True((await i.Assets.ChangeStatusAsync(asset.Id, new ChangeAssetStatusRequest(AssetStatus.Retired, "done", live.RowVersion))).IsOk);
        Assert.Equal(ResultStatus.Conflict, (await Record(TenantA, meter.Id, 2)).Status);
    }

    [Fact]
    public async Task Another_tenant_cannot_see_record_or_void_and_the_database_agrees()
    {
        var asset = await NewAssetAsync(TenantA);
        await using var f = FacilitiesScope.For(sql, TenantA);
        var meter = await f.NewMeterAsync(asset.Id, MeterKind.Gauge);
        var reading = (await Record(TenantA, meter.Id, 7)).Value!.Reading;

        await using var b = FacilitiesScope.For(sql, TenantB);
        Assert.Equal(ResultStatus.NotFound, (await b.Meters.GetAsync(meter.Id)).Status);
        Assert.Equal(ResultStatus.NotFound, (await b.Meters.ListForAssetAsync(asset.Id, true)).Status);
        Assert.Equal(ResultStatus.NotFound, (await Record(TenantB, meter.Id, 8)).Status);
        Assert.Equal(ResultStatus.NotFound, (await b.Meters.ListReadingsAsync(meter.Id, new ReadingQuery())).Status);
        Assert.Equal(ResultStatus.NotFound, (await b.Meters.VoidAsync(reading.Id, new VoidReadingRequest("x"))).Status);
        Assert.Equal(0, await b.Db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM facilities.MeterReadings WHERE MeterId = {meter.Id}").SingleAsync());
        await Assert.ThrowsAsync<SqlException>(() => b.Db.Database.ExecuteSqlAsync(
            $"INSERT INTO facilities.MeterReadings (Id, TenantId, MeterId, Value, ReadAt, RecordedAt, Source, IsReset) VALUES ({Guid.NewGuid()}, {TenantA}, {meter.Id}, 1, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), 1, 0)"));
    }

    [Fact]
    public async Task Readings_list_filters_by_period_and_pages_newest_first()
    {
        var asset = await NewAssetAsync(TenantA);
        await using var f = FacilitiesScope.For(sql, TenantA);
        var meter = await f.NewMeterAsync(asset.Id, MeterKind.Gauge);
        var t = DateTimeOffset.UtcNow.AddDays(-10);
        for (var i = 0; i < 6; i++) await Record(TenantA, meter.Id, i, t.AddDays(i));

        await using var g = FacilitiesScope.For(sql, TenantA);
        var page1 = (await g.Meters.ListReadingsAsync(meter.Id, new ReadingQuery(PageSize: 4))).Value!;
        Assert.Equal(6, page1.Total);
        Assert.Equal([5m, 4m, 3m, 2m], page1.Items.Select(r => r.Value));
        var window = (await g.Meters.ListReadingsAsync(meter.Id, new ReadingQuery(From: t.AddDays(1), To: t.AddDays(3)))).Value!;
        Assert.Equal([3m, 2m, 1m], window.Items.Select(r => r.Value));
    }
}
