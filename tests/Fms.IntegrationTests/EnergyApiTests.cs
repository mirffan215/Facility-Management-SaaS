using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Fms.Api;
using Fms.BuildingBlocks.Results;
using Fms.Energy.Domain;
using Fms.Energy.Services;
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
public sealed class EnergyApiTests(SqlServerFixture sql) : IDisposable
{
    private readonly ApiHarness _api = new(sql);
    public void Dispose() => _api.Dispose();

    private sealed record Rig(Guid Tenant, HttpClient Manager, HttpClient Technician, HttpClient Viewer, HttpClient Requester, LocationDto Site);

    private sealed class Capture<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<string> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= Microsoft.Extensions.Logging.LogLevel.Error) Errors.Add(formatter(state, exception) + " " + exception);
        }
    }

    private async Task<Rig> NewRigAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var platform = sql.App(null, platform: true))
        {
            platform.Tenants.Add(new Fms.Platform.Domain.Tenant { Id = tenant, Name = "Nrg " + tenant.ToString("N")[..6], CreatedAt = DateTimeOffset.UtcNow });
            await platform.SaveChangesAsync();
            await SystemRoleSync.EnsureAsync(platform, tenant);
        }

        var manager = await _api.ClientAsync(tenant, SystemRoles.FacilityManager);
        var site = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", new { type = "Site", code = FacilitiesScope.Code("S"), name = "HQ" }));
        return new Rig(tenant, manager, await _api.ClientAsync(tenant, SystemRoles.Technician), await _api.ClientAsync(tenant, SystemRoles.Viewer), await _api.ClientAsync(tenant, SystemRoles.Requester), site);
    }

    private static string Code(string p = "M") => p + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static async Task<EnergyMeterDto> NewMeterAsync(HttpClient c, Rig rig, string? code = null, bool cumulative = true, EnergyKind kind = EnergyKind.Electricity, decimal? rollover = null)
    {
        var response = await PostAsync(c, "/api/v1/energy/meters", new { code = code ?? Code(), name = "Main incomer", kind, unit = kind == EnergyKind.Water ? "m3" : "kWh", locationId = rig.Site.Id, isCumulative = cumulative, rolloverAt = rollover });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<EnergyMeterDto>(response);
    }

    private static DateTimeOffset Midnight(int daysAgo) => new(DateTime.UtcNow.Date.AddDays(-daysAgo), TimeSpan.Zero);

    private static Task<HttpResponseMessage> Read(HttpClient c, EnergyMeterDto m, DateTimeOffset at, decimal value) =>
        PostAsync(c, $"/api/v1/energy/meters/{m.Id}/readings", new { at, value });

    private static MultipartFormDataContent Csv(string text, bool? dryRun = null)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", "readings.csv");
        if (dryRun is { } d) content.Add(new StringContent(d ? "true" : "false"), "dryRun");
        return content;
    }

    // ------------------------------------------------------------------ access and meters

    [Fact]
    public async Task Roles_decide_who_reads_records_and_manages()
    {
        var rig = await NewRigAsync();
        var meter = await NewMeterAsync(rig.Manager, rig);
        Assert.Equal(HttpStatusCode.OK, (await rig.Viewer.GetAsync("/api/v1/energy/meters")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Requester.GetAsync("/api/v1/energy/meters")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GuestClient().GetAsync("/api/v1/energy/meters")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Viewer, "/api/v1/energy/meters", new { code = "X", name = "x", kind = "Water", unit = "m3" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Technician, "/api/v1/energy/meters", new { code = "X", name = "x", kind = "Water", unit = "m3" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Read(rig.Technician, meter, DateTimeOffset.UtcNow.AddHours(-2), 10)).StatusCode); // technicians read meters
        Assert.Equal(HttpStatusCode.Forbidden, (await Read(rig.Viewer, meter, DateTimeOffset.UtcNow.AddHours(-1), 11)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Technician, "/api/v1/energy/devices", new { name = "x", meterIds = new[] { meter.Id } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Technician.GetAsync("/api/v1/energy/devices")).StatusCode);
    }

    [Fact]
    public async Task Meters_are_validated_unique_by_code_and_frozen_once_they_have_readings()
    {
        var rig = await NewRigAsync();
        var code = Code();
        var m = await NewMeterAsync(rig.Manager, rig, code);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, "/api/v1/energy/meters", new { code = code.ToLowerInvariant(), name = "Dup", kind = "Gas", unit = "m3" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/energy/meters", new { code = "bad code!", name = "x", kind = "Gas", unit = "m3" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/energy/meters", new { code = Code(), name = "", kind = "Gas", unit = "m3" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/energy/meters", new { code = Code(), name = "x", kind = "Gas", unit = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/energy/meters", new { code = Code(), name = "x", kind = "Gas", unit = "m3", locationId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/energy/meters", new { code = Code(), name = "x", kind = "Gas", unit = "m3", isCumulative = false, rolloverAt = 100 })).StatusCode);

        var renamed = await PutAsync(rig.Manager, $"/api/v1/energy/meters/{m.Id}", new { code, name = "Renamed", kind = "Electricity", unit = "kWh", isCumulative = true, rowVersion = m.RowVersion });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var current = await ReadAsync<EnergyMeterDto>(renamed);
        Assert.Equal(HttpStatusCode.Created, (await Read(rig.Manager, current, DateTimeOffset.UtcNow.AddHours(-3), 5)).StatusCode);
        var changed = await PutAsync(rig.Manager, $"/api/v1/energy/meters/{m.Id}", new { code, name = "Renamed", kind = "Water", unit = "m3", isCumulative = true, rowVersion = current.RowVersion });
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(rig.Manager, $"/api/v1/energy/meters/{m.Id}", new { code, name = "Stale", kind = "Electricity", unit = "kWh", rowVersion = m.RowVersion })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Manager, $"/api/v1/energy/meters/{m.Id}/archive", new { rowVersion = current.RowVersion })).StatusCode);
        Assert.Empty((await ReadAsync<Page<EnergyMeterDto>>(await rig.Viewer.GetAsync("/api/v1/energy/meters"))).Items);
        Assert.Equal(HttpStatusCode.Conflict, (await Read(rig.Manager, current, DateTimeOffset.UtcNow.AddHours(-1), 6)).StatusCode);
    }

    // ------------------------------------------------------------------ readings

    [Fact]
    public async Task A_person_cannot_enter_a_reading_that_runs_backwards_or_lies_about_time()
    {
        var rig = await NewRigAsync();
        var m = await NewMeterAsync(rig.Manager, rig);
        Assert.Equal(HttpStatusCode.Created, (await Read(rig.Manager, m, Midnight(3), 1000)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Read(rig.Manager, m, Midnight(1), 1200)).StatusCode);

        var backwards = await Read(rig.Manager, m, Midnight(0), 1100);
        Assert.Equal(HttpStatusCode.BadRequest, backwards.StatusCode);
        Assert.Contains("lower than the previous", await backwards.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await Read(rig.Manager, m, Midnight(2), 1300)).StatusCode); // higher than a later one
        Assert.Equal(HttpStatusCode.Created, (await Read(rig.Manager, m, Midnight(2), 1100)).StatusCode); // fits between
        Assert.Equal(HttpStatusCode.BadRequest, (await Read(rig.Manager, m, DateTimeOffset.UtcNow.AddHours(2), 5000)).StatusCode); // future
        Assert.Equal(HttpStatusCode.BadRequest, (await Read(rig.Manager, m, DateTimeOffset.UtcNow.AddDays(-800), 1)).StatusCode); // far past
        Assert.Equal(HttpStatusCode.BadRequest, (await Read(rig.Manager, m, DateTimeOffset.UtcNow.AddMinutes(-5), -1)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, $"/api/v1/energy/meters/{m.Id}/readings", new { })).StatusCode);

        // The same reading again is a duplicate, a different value for the same time is refused.
        Assert.Equal(HttpStatusCode.Conflict, (await Read(rig.Manager, m, Midnight(1), 1200)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Read(rig.Manager, m, Midnight(1), 1250)).StatusCode);
    }

    [Fact]
    public async Task A_wrong_reading_is_voided_with_a_reason_and_leaves_the_use_figures_and_the_history_stays()
    {
        var rig = await NewRigAsync();
        var m = await NewMeterAsync(rig.Manager, rig);
        await Read(rig.Manager, m, Midnight(3), 1000);
        var typo = await ReadAsync<ReadingDto>(await Read(rig.Manager, m, Midnight(2), 9000));
        await Read(rig.Manager, m, Midnight(1), 9500);

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, $"/api/v1/energy/readings/{typo.Id}/void", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Manager, $"/api/v1/energy/readings/{typo.Id}/void", new { reason = "Typed an extra digit" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, $"/api/v1/energy/readings/{typo.Id}/void", new { reason = "again" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Viewer, $"/api/v1/energy/readings/{typo.Id}/void", new { reason = "no" })).StatusCode);

        var list = await ReadAsync<Page<ReadingDto>>(await rig.Viewer.GetAsync($"/api/v1/energy/meters/{m.Id}/readings"));
        Assert.Equal(3, list.Total);
        Assert.Equal("Typed an extra digit", list.Items.Single(r => r.Id == typo.Id).VoidReason);
        Assert.Equal(9500, (await ReadAsync<EnergyMeterDto>(await rig.Viewer.GetAsync($"/api/v1/energy/meters/{m.Id}"))).LatestValue);

        // The same instant cannot be reused (it is taken by the voided one); a second later works.
        Assert.Equal(HttpStatusCode.BadRequest, (await Read(rig.Manager, m, Midnight(2), 1500)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Read(rig.Manager, m, Midnight(2).AddSeconds(1), 1500)).StatusCode);

        await using var db = sql.Energy(rig.Tenant);
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("UPDATE energy.Readings SET Value = 1"));
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM energy.Readings"));
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM energy.ReadingVoids"));
    }

    [Fact]
    public async Task A_csv_file_is_checked_row_by_row_before_anything_is_saved_and_good_rows_survive_bad_ones()
    {
        var rig = await NewRigAsync();
        var a = await NewMeterAsync(rig.Manager, rig, "CSV-A");
        var csv = $"meter,at,value,note\nCSV-A,{Midnight(3):O},100,bill\nCSV-A,{Midnight(2):O},150,\nNOPE,{Midnight(2):O},5,\nCSV-A,not a time,5,\nCSV-A,{Midnight(1):O},abc,\nCSV-A,{Midnight(1):O},170,\nCSV-A,{Midnight(1):O},170,\n";

        var dry = await rig.Technician.PostAsync("/api/v1/energy/readings/import", Csv(csv));
        Assert.Equal(HttpStatusCode.OK, dry.StatusCode);
        var preview = await ReadAsync<ImportResultDto>(dry);
        Assert.True(preview.DryRun);
        Assert.Equal(7, preview.Total);
        Assert.Equal(3, preview.Accepted);
        Assert.Equal(1, preview.Duplicates);
        Assert.Equal(3, preview.Rejected);
        Assert.Contains(preview.Rows, r => r.Line == 4 && r.Status == "Rejected" && r.Message!.Contains("Unknown", StringComparison.Ordinal));
        Assert.Empty((await ReadAsync<Page<ReadingDto>>(await rig.Viewer.GetAsync($"/api/v1/energy/meters/{a.Id}/readings"))).Items); // nothing saved by a dry run

        var real = await ReadAsync<ImportResultDto>(await rig.Technician.PostAsync("/api/v1/energy/readings/import", Csv(csv, dryRun: false)));
        Assert.False(real.DryRun);
        Assert.Equal(3, real.Accepted);
        Assert.Equal(3, (await ReadAsync<Page<ReadingDto>>(await rig.Viewer.GetAsync($"/api/v1/energy/meters/{a.Id}/readings"))).Total);

        // Doing it again changes nothing.
        var again = await ReadAsync<ImportResultDto>(await rig.Technician.PostAsync("/api/v1/energy/readings/import", Csv(csv, dryRun: false)));
        Assert.Equal(0, again.Accepted);
        Assert.Equal(4, again.Duplicates);

        Assert.Equal(HttpStatusCode.BadRequest, (await rig.Technician.PostAsync("/api/v1/energy/readings/import", Csv("meter,time\nA,1\n", false))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await rig.Technician.PostAsync("/api/v1/energy/readings/import", Csv("", false))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await rig.Technician.PostAsync("/api/v1/energy/readings/import", Csv("meter,at,value\n\"CSV-A,x,1", false))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Viewer.PostAsync("/api/v1/energy/readings/import", Csv(csv, false))).StatusCode);
    }

    // ------------------------------------------------------------------ use and cost

    [Fact]
    public async Task Use_is_worked_out_per_day_and_priced_with_the_tariff_in_force_that_day()
    {
        var rig = await NewRigAsync();
        var m = await NewMeterAsync(rig.Manager, rig);
        foreach (var (days, value) in new[] { (4, 1000m), (3, 1100m), (2, 1300m), (1, 1300m) }) Assert.Equal(HttpStatusCode.Created, (await Read(rig.Manager, m, Midnight(days), value)).StatusCode);

        string D(int ago) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-ago).ToString("yyyy-MM-dd");
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(rig.Manager, "/api/v1/energy/tariffs", new { kind = "Electricity", effectiveFrom = D(10), pricePerUnit = 0.5, currency = "sar" })).StatusCode);
        var second = await PostAsync(rig.Manager, "/api/v1/energy/tariffs", new { kind = "Electricity", effectiveFrom = D(2), pricePerUnit = 1.0, currency = "SAR" });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, "/api/v1/energy/tariffs", new { kind = "Electricity", effectiveFrom = D(2), pricePerUnit = 2.0, currency = "SAR" })).StatusCode);

        var use = await ReadAsync<ConsumptionDto>(await rig.Viewer.GetAsync($"/api/v1/energy/consumption?from={D(4)}&to={D(1)}"));
        var row = Assert.Single(use.Meters);
        Assert.Equal(300m, row.Total);                       // 100 on day -4, 200 on day -3, 0 on day -2
        Assert.Equal([100m, 200m, 0m], row.Days.Select(d => d.Amount).Take(3));
        Assert.Equal(100 * 0.5m + 200 * 0.5m, row.Cost);     // the second tariff starts after the busy days
        Assert.Equal("SAR", row.Currency);
        var total = Assert.Single(use.Totals);
        Assert.Equal(300m, total.Total);
        Assert.False(total.MixedUnits);

        // A tariff change is audited and applies to the days it covers.
        var tariff = await ReadAsync<TariffDto>(second);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(rig.Manager, $"/api/v1/energy/tariffs/{tariff.Id}", new { kind = "Electricity", effectiveFrom = D(4), pricePerUnit = 2.0, currency = "SAR", rowVersion = tariff.RowVersion })).StatusCode);
        var priced = Assert.Single((await ReadAsync<ConsumptionDto>(await rig.Viewer.GetAsync($"/api/v1/energy/consumption?from={D(4)}&to={D(1)}"))).Meters);
        Assert.Equal(600m, priced.Cost); // 2.0 per unit from day -4
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/energy/tariffs", new { kind = "Gas", effectiveFrom = D(1), pricePerUnit = -1, currency = "SAR" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/energy/tariffs", new { kind = "Gas", effectiveFrom = D(1), pricePerUnit = 1, currency = "RIYAL" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Technician, "/api/v1/energy/tariffs", new { kind = "Gas", effectiveFrom = D(1), pricePerUnit = 1, currency = "SAR" })).StatusCode);
    }

    [Fact]
    public async Task Without_a_tariff_there_is_use_but_no_cost_and_a_meter_reset_is_reported_not_counted()
    {
        var rig = await NewRigAsync();
        var m = await NewMeterAsync(rig.Manager, rig, kind: EnergyKind.Water);
        var key = await ReadAsync<CreatedDeviceDto>(await PostAsync(rig.Manager, "/api/v1/energy/devices", new { name = "Logger", meterIds = new[] { m.Id } }));
        var client = _api.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
        var post = await PostAsync(client, "/api/v1/energy/ingest", new { readings = new[] { new { meter = m.Code, at = Midnight(4), value = 900m }, new { meter = m.Code, at = Midnight(3), value = 950m }, new { meter = m.Code, at = Midnight(2), value = 10m }, new { meter = m.Code, at = Midnight(1), value = 40m } } });
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        string D(int ago) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-ago).ToString("yyyy-MM-dd");
        var row = Assert.Single((await ReadAsync<ConsumptionDto>(await rig.Viewer.GetAsync($"/api/v1/energy/consumption?from={D(4)}&to={D(1)}"))).Meters);
        Assert.Equal(80m, row.Total);   // 50 + 30; the drop from 950 to 10 is a reset, not negative use
        Assert.Equal(1, row.Resets);
        Assert.Null(row.Cost);
        Assert.Null(row.Currency);
        Assert.Equal(HttpStatusCode.BadRequest, (await rig.Viewer.GetAsync($"/api/v1/energy/consumption?from={D(1)}&to={D(5)}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await rig.Viewer.GetAsync($"/api/v1/energy/consumption?from={D(900)}&to={D(1)}")).StatusCode);
    }

    // ------------------------------------------------------------------ devices

    private HttpClient DeviceClient(string secret)
    {
        var client = _api.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }

    [Fact]
    public async Task A_device_posts_readings_for_its_own_meters_only_idempotently_and_loses_access_when_revoked()
    {
        var rig = await NewRigAsync();
        var mine = await NewMeterAsync(rig.Manager, rig);
        var other = await NewMeterAsync(rig.Manager, rig);

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/energy/devices", new { name = "x", meterIds = Array.Empty<Guid>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/energy/devices", new { name = "x", meterIds = new[] { Guid.NewGuid() } })).StatusCode);
        var created = await ReadAsync<CreatedDeviceDto>(await PostAsync(rig.Manager, "/api/v1/energy/devices", new { name = "Plant logger", meterIds = new[] { mine.Id } }));
        Assert.StartsWith("fmse_", created.Secret);
        Assert.DoesNotContain(created.Secret, await (await rig.Manager.GetAsync("/api/v1/energy/devices")).Content.ReadAsStringAsync()); // shown once

        var device = DeviceClient(created.Secret);
        var t1 = Midnight(2);
        var body = new { readings = new object[] { new { meter = mine.Code, at = t1, value = 100m }, new { meter = other.Code, at = t1, value = 5m }, new { meter = "NOPE", at = t1, value = 1m }, new { meter = mine.Code, at = (DateTimeOffset?)null, value = 1m } } };
        var first = await ReadAsync<IngestResultDto>(await PostAsync(device, "/api/v1/energy/ingest", body));
        Assert.Equal(1, first.Accepted);
        Assert.Equal([1, 2, 3], first.Errors.Select(e => e.Index));

        var second = await ReadAsync<IngestResultDto>(await PostAsync(device, "/api/v1/energy/ingest", body)); // a retry after a lost answer
        Assert.Equal(0, second.Accepted);
        Assert.Equal(1, second.Duplicates);
        Assert.Equal(1, (await ReadAsync<Page<ReadingDto>>(await rig.Viewer.GetAsync($"/api/v1/energy/meters/{mine.Id}/readings"))).Total);
        Assert.Empty((await ReadAsync<Page<ReadingDto>>(await rig.Viewer.GetAsync($"/api/v1/energy/meters/{other.Id}/readings"))).Items);

        // By id too, and a different value for the same time is an error for that row only.
        var conflict = await ReadAsync<IngestResultDto>(await PostAsync(device, "/api/v1/energy/ingest", new { readings = new[] { new { meter = mine.Id.ToString(), at = t1, value = 999m }, new { meter = mine.Id.ToString(), at = t1.AddHours(1), value = 101m } } }));
        Assert.Equal(1, conflict.Accepted);
        Assert.Single(conflict.Errors);

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(device, "/api/v1/energy/ingest", new { readings = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(device, "/api/v1/energy/ingest", new { readings = Enumerable.Range(0, 501).Select(i => new { meter = mine.Code, at = t1.AddMinutes(i + 5), value = 1m }) })).StatusCode);

        var listed = (await ReadAsync<List<DeviceDto>>(await rig.Manager.GetAsync("/api/v1/energy/devices"))).Single();
        Assert.NotNull(listed.LastUsedAt);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(rig.Manager, $"/api/v1/energy/devices/{listed.Id}/revoke", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(device, "/api/v1/energy/ingest", body)).StatusCode);
    }

    [Fact]
    public async Task Every_bad_device_secret_gets_the_same_answer_and_a_device_cannot_reach_another_organisation()
    {
        var a = await NewRigAsync();
        var b = await NewRigAsync();
        var meterA = await NewMeterAsync(a.Manager, a);
        var meterB = await NewMeterAsync(b.Manager, b);
        var key = await ReadAsync<CreatedDeviceDto>(await PostAsync(a.Manager, "/api/v1/energy/devices", new { name = "A logger", meterIds = new[] { meterA.Id } }));
        var body = new { readings = new[] { new { meter = meterA.Code, at = Midnight(1), value = 1m } } };

        var anonymous = _api.Factory.CreateClient();
        var responses = new[]
        {
            await PostAsync(anonymous, "/api/v1/energy/ingest", body),
            await PostAsync(DeviceClient("fmse_" + new string('x', 43)), "/api/v1/energy/ingest", body),
            await PostAsync(DeviceClient("wrong"), "/api/v1/energy/ingest", body),
            await PostAsync(DeviceClient(key.Secret + "x"), "/api/v1/energy/ingest", body),
        };
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
        var answers = new List<string>();
        foreach (var r in responses)
        {
            var json = await r.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            answers.Add($"{json.GetProperty("status")}/{json.GetProperty("title")}");
        }

        Assert.Single(answers.Distinct());

        // A's device cannot name B's meter, by code or by id.
        var device = DeviceClient(key.Secret);
        var attempt = await ReadAsync<IngestResultDto>(await PostAsync(device, "/api/v1/energy/ingest", new { readings = new[] { new { meter = meterB.Code, at = Midnight(1), value = 1m }, new { meter = meterB.Id.ToString(), at = Midnight(1), value = 1m } } }));
        Assert.Equal(0, attempt.Accepted);
        Assert.Equal(2, attempt.Errors.Count);
        Assert.Empty((await ReadAsync<Page<ReadingDto>>(await b.Manager.GetAsync($"/api/v1/energy/meters/{meterB.Id}/readings"))).Items);

        // A's people see none of B's meters or readings.
        Assert.Equal(HttpStatusCode.NotFound, (await a.Manager.GetAsync($"/api/v1/energy/meters/{meterB.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Manager.GetAsync($"/api/v1/energy/meters/{meterB.Id}/readings")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Read(a.Manager, meterB, DateTimeOffset.UtcNow.AddHours(-1), 1)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(a.Manager, "/api/v1/energy/devices", new { name = "x", meterIds = new[] { meterB.Id } })).StatusCode);

        await using var raw = sql.Energy(a.Tenant);
        Assert.Equal(0, await raw.Meters.IgnoreQueryFilters().CountAsync(m => m.Id == meterB.Id));
        Assert.Equal(0, await raw.Devices.IgnoreQueryFilters().CountAsync(d => d.TenantId == b.Tenant));
    }

    [Fact]
    public async Task An_expired_device_is_refused()
    {
        var rig = await NewRigAsync();
        var m = await NewMeterAsync(rig.Manager, rig);
        var key = await ReadAsync<CreatedDeviceDto>(await PostAsync(rig.Manager, "/api/v1/energy/devices", new { name = "Short", meterIds = new[] { m.Id }, expiresInDays = 1 }));
        await using (var db = sql.Energy(rig.Tenant))
        {
            var d = await db.Devices.FirstAsync();
            d.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(DeviceClient(key.Secret), "/api/v1/energy/ingest", new { readings = new[] { new { meter = m.Code, at = Midnight(1), value = 1m } } })).StatusCode);
        Assert.Equal("Expired", (await ReadAsync<List<DeviceDto>>(await rig.Manager.GetAsync("/api/v1/energy/devices"))).Single().State);
    }

    // ------------------------------------------------------------------ alarms

    [Fact]
    public async Task A_high_day_of_use_raises_an_alarm_once_tells_the_managers_and_clears_when_the_limit_is_raised()
    {
        var rig = await NewRigAsync();
        var m = await NewMeterAsync(rig.Manager, rig);
        await Read(rig.Manager, m, Midnight(2), 0);
        await Read(rig.Manager, m, Midnight(0), 200); // 100 a day, so 100 yesterday

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(rig.Manager, "/api/v1/energy/alarms/rules", new { meterId = m.Id, kind = "HighDailyUse", threshold = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Technician, "/api/v1/energy/alarms/rules", new { meterId = m.Id, kind = "HighDailyUse", threshold = 50 })).StatusCode);
        var rule = await ReadAsync<AlarmRuleDto>(await PostAsync(rig.Manager, "/api/v1/energy/alarms/rules", new { meterId = m.Id, kind = "HighDailyUse", threshold = 50 }));
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, "/api/v1/energy/alarms/rules", new { meterId = m.Id, kind = "HighDailyUse", threshold = 60 })).StatusCode);

        var log = new Capture<EnergyMonitor>();
        var monitor = new EnergyMonitor(_api.Factory.Services.GetRequiredService<IServiceScopeFactory>(), log);
        await monitor.RunOnceAsync();
        await monitor.RunOnceAsync();
        Assert.True(log.Errors.Count == 0, string.Join("\n", log.Errors));

        var events = await ReadAsync<Page<AlarmEventDto>>(await rig.Viewer.GetAsync("/api/v1/energy/alarms/events"));
        Assert.Equal(AlarmEventKind.Raised, Assert.Single(events.Items).Kind);
        Assert.Equal(100m, events.Items[0].Value);
        Assert.True((await ReadAsync<List<AlarmRuleDto>>(await rig.Viewer.GetAsync("/api/v1/energy/alarms/rules"))).Single().IsRaised);
        var notices = (await ReadAsync<Page<NotificationDto>>(await rig.Manager.GetAsync("/api/v1/notifications?pageSize=50"))).Items.Where(n => n.Type == "energy.highuse").ToList();
        Assert.Single(notices);
        Assert.DoesNotContain((await ReadAsync<Page<NotificationDto>>(await rig.Viewer.GetAsync("/api/v1/notifications?pageSize=50"))).Items, n => n.Type.StartsWith("energy.", StringComparison.Ordinal));

        var current = (await ReadAsync<List<AlarmRuleDto>>(await rig.Manager.GetAsync("/api/v1/energy/alarms/rules"))).Single();
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(rig.Manager, $"/api/v1/energy/alarms/rules/{rule.Id}", new { meterId = m.Id, kind = "HighDailyUse", threshold = 500, rowVersion = current.RowVersion })).StatusCode);
        await monitor.RunOnceAsync();
        var after = await ReadAsync<Page<AlarmEventDto>>(await rig.Viewer.GetAsync("/api/v1/energy/alarms/events"));
        Assert.Equal([AlarmEventKind.Cleared, AlarmEventKind.Raised], after.Items.Select(e => e.Kind));
        Assert.False((await ReadAsync<List<AlarmRuleDto>>(await rig.Viewer.GetAsync("/api/v1/energy/alarms/rules"))).Single().IsRaised);

        await using var db = sql.Energy(rig.Tenant);
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM energy.AlarmEvents"));
    }

    [Fact]
    public async Task A_meter_that_stops_reporting_raises_an_alarm()
    {
        var rig = await NewRigAsync();
        var m = await NewMeterAsync(rig.Manager, rig);
        await Read(rig.Manager, m, DateTimeOffset.UtcNow.AddHours(-48), 10);
        var quiet = await NewMeterAsync(rig.Manager, rig);
        await Read(rig.Manager, quiet, DateTimeOffset.UtcNow.AddMinutes(-30), 10);
        await PostAsync(rig.Manager, "/api/v1/energy/alarms/rules", new { meterId = m.Id, kind = "NoReading", threshold = 24 });
        await PostAsync(rig.Manager, "/api/v1/energy/alarms/rules", new { meterId = quiet.Id, kind = "NoReading", threshold = 24 });

        await new EnergyMonitor(_api.Factory.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<EnergyMonitor>.Instance).RunOnceAsync();
        var raised = (await ReadAsync<List<AlarmRuleDto>>(await rig.Viewer.GetAsync("/api/v1/energy/alarms/rules"))).Where(r => r.IsRaised).ToList();
        Assert.Equal(m.Id, Assert.Single(raised).MeterId);
        Assert.Contains((await ReadAsync<Page<NotificationDto>>(await rig.Manager.GetAsync("/api/v1/notifications?pageSize=50"))).Items, n => n.Type == "energy.noreading");
    }

    [Fact]
    public async Task An_alarm_rule_can_raise_a_work_order_once_for_the_meters_place_and_a_manager_can_raise_one_by_hand_otherwise()
    {
        var rig = await NewRigAsync();
        var auto = await NewMeterAsync(rig.Manager, rig);
        var hand = await NewMeterAsync(rig.Manager, rig);
        foreach (var m in new[] { auto, hand })
        {
            await Read(rig.Manager, m, Midnight(2), 0);
            await Read(rig.Manager, m, Midnight(0), 200);
        }

        var withWork = await ReadAsync<AlarmRuleDto>(await PostAsync(rig.Manager, "/api/v1/energy/alarms/rules", new { meterId = auto.Id, kind = "HighDailyUse", threshold = 50, createWorkOrder = true }));
        Assert.True(withWork.CreateWorkOrder);
        var without = await ReadAsync<AlarmRuleDto>(await PostAsync(rig.Manager, "/api/v1/energy/alarms/rules", new { meterId = hand.Id, kind = "HighDailyUse", threshold = 50 }));
        Assert.False(without.CreateWorkOrder);

        var monitor = new EnergyMonitor(_api.Factory.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<EnergyMonitor>.Instance);
        await monitor.RunOnceAsync();
        await monitor.RunOnceAsync();

        var events = (await ReadAsync<Page<AlarmEventDto>>(await rig.Viewer.GetAsync("/api/v1/energy/alarms/events"))).Items;
        var autoEvent = events.Single(e => e.MeterId == auto.Id);
        var handEvent = events.Single(e => e.MeterId == hand.Id);
        Assert.NotNull(autoEvent.WorkOrderId);
        Assert.Null(handEvent.WorkOrderId);
        var wo = (await ReadAsync<Fms.Work.Services.WorkOrderDetailDto>(await rig.Manager.GetAsync($"/api/v1/work-orders/{autoEvent.WorkOrderId}"))).Order;
        Assert.Contains("high use", wo.Title);
        Assert.Equal(rig.Site.Id, wo.LocationId);

        // By hand: forbidden for people who do not manage energy; the same work order however many times it is asked for.
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(rig.Technician, $"/api/v1/energy/alarms/events/{handEvent.Id}/work-order", new { })).StatusCode);
        var first = await ReadAsync<Fms.BuildingBlocks.Identity.WorkFromSourceResult>(await PostAsync(rig.Manager, $"/api/v1/energy/alarms/events/{handEvent.Id}/work-order", new { }));
        var second = await ReadAsync<Fms.BuildingBlocks.Identity.WorkFromSourceResult>(await PostAsync(rig.Manager, $"/api/v1/energy/alarms/events/{handEvent.Id}/work-order", new { }));
        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.WorkOrderId, second.WorkOrderId);
        var again = await ReadAsync<Fms.BuildingBlocks.Identity.WorkFromSourceResult>(await PostAsync(rig.Manager, $"/api/v1/energy/alarms/events/{autoEvent.Id}/work-order", new { }));
        Assert.False(again.Created); // the check had already raised it
        Assert.Equal(autoEvent.WorkOrderId, again.WorkOrderId);

        var titles = (await ReadAsync<Page<Fms.Work.Services.WorkOrderDto>>(await rig.Manager.GetAsync("/api/v1/work-orders?pageSize=100"))).Items.Select(w => w.Title).ToList();
        Assert.Equal(2, titles.Count(t => t.StartsWith("Energy alarm", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_cleared_alarm_or_another_organisations_alarm_cannot_be_given_a_work_order()
    {
        var rig = await NewRigAsync();
        var other = await NewRigAsync();
        var m = await NewMeterAsync(rig.Manager, rig);
        await Read(rig.Manager, m, Midnight(2), 0);
        await Read(rig.Manager, m, Midnight(0), 200);
        var rule = await ReadAsync<AlarmRuleDto>(await PostAsync(rig.Manager, "/api/v1/energy/alarms/rules", new { meterId = m.Id, kind = "HighDailyUse", threshold = 50 }));
        var monitor = new EnergyMonitor(_api.Factory.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<EnergyMonitor>.Instance);
        await monitor.RunOnceAsync();
        var raised = (await ReadAsync<Page<AlarmEventDto>>(await rig.Viewer.GetAsync("/api/v1/energy/alarms/events"))).Items.Single();
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(other.Manager, $"/api/v1/energy/alarms/events/{raised.Id}/work-order", new { })).StatusCode);

        rule = (await ReadAsync<List<AlarmRuleDto>>(await rig.Viewer.GetAsync("/api/v1/energy/alarms/rules"))).Single(); // the check updated it, so its version moved on
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(rig.Manager, $"/api/v1/energy/alarms/rules/{rule.Id}", new { meterId = m.Id, kind = "HighDailyUse", threshold = 500, rowVersion = rule.RowVersion })).StatusCode);
        await monitor.RunOnceAsync();
        var cleared = (await ReadAsync<Page<AlarmEventDto>>(await rig.Viewer.GetAsync("/api/v1/energy/alarms/events"))).Items.Single(e => e.Kind == AlarmEventKind.Cleared);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(rig.Manager, $"/api/v1/energy/alarms/events/{cleared.Id}/work-order", new { })).StatusCode);
    }

    // ------------------------------------------------------------------ reports

    [Fact]
    public async Task The_dashboard_and_the_data_feed_include_energy_without_names()
    {
        var rig = await NewRigAsync();
        var m = await NewMeterAsync(rig.Manager, rig);
        await Read(rig.Manager, m, Midnight(3), 100);
        await Read(rig.Manager, m, Midnight(1), 300);
        var text = await (await rig.Manager.GetAsync("/api/v1/reports/dashboard")).Content.ReadAsStringAsync();
        Assert.True(text.Contains("energy.meters", StringComparison.Ordinal), text);
        Assert.Contains("energy.useByKind", text);

        var admin = await _api.ClientAsync(rig.Tenant, SystemRoles.Administrator);
        var token = await ReadAsync<Fms.Reporting.Services.CreatedFeedTokenDto>(await PostAsync(admin, "/api/v1/reports/feed/tokens", new { name = "BI" }));
        var feed = _api.Factory.CreateClient();
        feed.DefaultRequestHeaders.Add("X-Api-Key", token.Secret);
        var readings = await feed.GetStringAsync("/api/v1/odata/EnergyReadings");
        Assert.Contains("\"Value\"", readings);
        var meters = await feed.GetStringAsync("/api/v1/odata/EnergyMeters");
        Assert.Contains(m.Code, meters);
        Assert.DoesNotContain("Main incomer", meters); // names typed by people are not in the feed
    }
}
