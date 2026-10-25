using System.Net;
using System.Net.Http.Json;
using Fms.BuildingBlocks.Results;
using Fms.Facilities.Domain;
using Fms.Facilities.Services;
using Fms.Platform.Security;
using static Fms.IntegrationTests.ApiHarness;
using static Fms.IntegrationTests.SqlServerFixture;

namespace Fms.IntegrationTests;

[Collection(SqlCollection.Name)]
public sealed class MeterApiTests(SqlServerFixture sql) : IDisposable
{
    private readonly ApiHarness _api = new(sql);
    public void Dispose() => _api.Dispose();

    private async Task<(HttpClient Manager, AssetDto Asset, MeterDto Meter)> SetupAsync(Guid tenant, MeterKind kind = MeterKind.Cumulative)
    {
        var manager = await _api.ClientAsync(tenant, SystemRoles.FacilityManager);
        var site = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", new { type = "Site", code = FacilitiesScope.Code("S"), name = "S" }));
        var cls = await ReadAsync<AssetClassDto>(await PostAsync(manager, "/api/v1/asset-classes", new { code = FacilitiesScope.Code("C"), name = "C" }));
        var asset = await ReadAsync<AssetDto>(await PostAsync(manager, "/api/v1/assets", new { code = FacilitiesScope.Code("A"), name = "Pump", assetClassId = cls.Id, locationId = site.Id, startActive = true }));
        var created = await PostAsync(manager, $"/api/v1/assets/{asset.Id}/meters", new { name = "Run hours", unit = "h", kind = kind.ToString() });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (manager, asset, await ReadAsync<MeterDto>(created));
    }

    [Fact]
    public async Task Technicians_record_readings_managers_define_meters_and_void_mistakes()
    {
        var (manager, asset, meter) = await SetupAsync(TenantA);
        var technician = await _api.ClientAsync(TenantA, SystemRoles.Technician);
        var viewer = await _api.ClientAsync(TenantA, SystemRoles.Viewer);
        var requester = await _api.ClientAsync(TenantA, SystemRoles.Requester);

        var recorded = await PostAsync(technician, $"/api/v1/meters/{meter.Id}/readings", new { value = 1200.5, note = "round 3", source = "Mobile" });
        Assert.Equal(HttpStatusCode.Created, recorded.StatusCode);
        var reading = await ReadAsync<MeterReadingDto>(recorded);
        Assert.Equal(ReadingSource.Mobile, reading.Source);

        Assert.Equal(1200.5m, (await viewer.GetFromJsonAsync<MeterDto>($"/api/v1/meters/{meter.Id}", Json))!.LastValue);
        Assert.Single((await viewer.GetFromJsonAsync<MeterDto[]>($"/api/v1/assets/{asset.Id}/meters", Json))!);
        Assert.Single((await ReadAsync<Page<MeterReadingDto>>(await viewer.GetAsync($"/api/v1/meters/{meter.Id}/readings"))).Items);

        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(viewer, $"/api/v1/meters/{meter.Id}/readings", new { value = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(technician, $"/api/v1/assets/{asset.Id}/meters", new { name = "x", unit = "h", kind = "Gauge" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(technician, $"/api/v1/meter-readings/{reading.Id}/void", new { reason = "typo" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.GetAsync($"/api/v1/meters/{meter.Id}")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(manager, $"/api/v1/meter-readings/{reading.Id}/void", new { reason = "typo" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(manager, $"/api/v1/meter-readings/{reading.Id}/void", new { reason = "again" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(manager, $"/api/v1/meter-readings/{reading.Id}/void", new { reason = "" })).StatusCode);
    }

    [Fact]
    public async Task A_retried_reading_returns_200_with_the_original_instead_of_a_second_201()
    {
        var (_, _, meter) = await SetupAsync(TenantA);
        var technician = await _api.ClientAsync(TenantA, SystemRoles.Technician);
        var clientId = Guid.NewGuid();

        var first = await PostAsync(technician, $"/api/v1/meters/{meter.Id}/readings", new { value = 10, clientId });
        var retry = await PostAsync(technician, $"/api/v1/meters/{meter.Id}/readings", new { value = 10, clientId });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal((await ReadAsync<MeterReadingDto>(first)).Id, (await ReadAsync<MeterReadingDto>(retry)).Id);
    }

    [Fact]
    public async Task Rule_violations_are_validation_problems()
    {
        var (_, _, meter) = await SetupAsync(TenantA);
        var technician = await _api.ClientAsync(TenantA, SystemRoles.Technician);
        await PostAsync(technician, $"/api/v1/meters/{meter.Id}/readings", new { value = 500 });

        var lower = await PostAsync(technician, $"/api/v1/meters/{meter.Id}/readings", new { value = 400 });
        Assert.Equal(HttpStatusCode.BadRequest, lower.StatusCode);
        Assert.Equal("application/problem+json", lower.Content.Headers.ContentType?.MediaType);
        Assert.Contains("cannot go down", await lower.Content.ReadAsStringAsync());

        var reset = await PostAsync(technician, $"/api/v1/meters/{meter.Id}/readings", new { value = 5, isReset = true });
        Assert.Equal(HttpStatusCode.Created, reset.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(technician, $"/api/v1/meters/{meter.Id}/readings", new { value = 5, readAt = DateTimeOffset.UtcNow.AddDays(2) })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(technician, $"/api/v1/meters/{Guid.NewGuid()}/readings", new { value = 1 })).StatusCode);
    }

    [Fact]
    public async Task The_batch_endpoint_returns_a_result_per_reading()
    {
        var (_, _, meter) = await SetupAsync(TenantA);
        var technician = await _api.ClientAsync(TenantA, SystemRoles.Technician);
        var t = DateTimeOffset.UtcNow.AddDays(-2);

        var response = await PostAsync(technician, "/api/v1/meters/readings/batch", new
        {
            readings = new object[]
            {
                new { meterId = meter.Id, value = 10, readAt = t, clientId = Guid.NewGuid() },
                new { meterId = meter.Id, value = 5, readAt = t.AddHours(1) },
                new { meterId = meter.Id, value = 20, readAt = t.AddHours(2) },
            },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await ReadAsync<BatchReadingResult[]>(response);
        Assert.Equal(["Created", "Rejected", "Created"], results.Select(r => r.Outcome));
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(technician, "/api/v1/meters/readings/batch", new { readings = Array.Empty<object>() })).StatusCode);
    }

    [Fact]
    public async Task Another_tenant_cannot_use_a_meter_through_the_api()
    {
        var (_, asset, meter) = await SetupAsync(TenantA);
        var technicianB = await _api.ClientAsync(TenantB, SystemRoles.Technician);
        var managerB = await _api.ClientAsync(TenantB, SystemRoles.FacilityManager);

        Assert.Equal(HttpStatusCode.NotFound, (await technicianB.GetAsync($"/api/v1/meters/{meter.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await technicianB.GetAsync($"/api/v1/assets/{asset.Id}/meters")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(technicianB, $"/api/v1/meters/{meter.Id}/readings", new { value = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(managerB, $"/api/v1/assets/{asset.Id}/meters", new { name = "x", unit = "h", kind = "Gauge" })).StatusCode);
        var batch = await ReadAsync<BatchReadingResult[]>(await PostAsync(technicianB, "/api/v1/meters/readings/batch", new { readings = new[] { new { meterId = meter.Id, value = 1 } } }));
        Assert.Equal("Rejected", batch.Single().Outcome);
    }
}
