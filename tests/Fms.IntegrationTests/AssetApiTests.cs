using System.Net;
using System.Net.Http.Json;
using Fms.BuildingBlocks.Results;
using Fms.Facilities.Domain;
using Fms.Facilities.Services;
using Fms.Platform.Security;
using Microsoft.Extensions.DependencyInjection;
using static Fms.IntegrationTests.ApiHarness;
using static Fms.IntegrationTests.SqlServerFixture;

namespace Fms.IntegrationTests;

[Collection(SqlCollection.Name)]
public sealed class AssetApiTests(SqlServerFixture sql) : IDisposable
{
    private readonly ApiHarness _api = new(sql, settings: new Dictionary<string, string?> { ["Facilities:PublicBaseUrl"] = "https://fms.example.com/" });
    public void Dispose() => _api.Dispose();

    private async Task<(HttpClient Manager, AssetClassDto Class, LocationDto Space)> SetupAsync(Guid tenant)
    {
        var manager = await _api.ClientAsync(tenant, SystemRoles.FacilityManager);
        var site = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", new { type = "Site", code = FacilitiesScope.Code("S"), name = "S" }));
        var space = await ReadAsync<LocationDto>(await PostAsync(manager, "/api/v1/locations", new { type = "Building", parentId = site.Id, code = FacilitiesScope.Code("B"), name = "B" }));
        var cls = await ReadAsync<AssetClassDto>(await PostAsync(manager, "/api/v1/asset-classes", new { code = FacilitiesScope.Code("C"), name = "HVAC", nameAr = "تكييف" }));
        return (manager, cls, space);
    }

    private static object NewAsset(Guid classId, Guid locationId, string? code = null) =>
        new { code = code ?? FacilitiesScope.Code("A"), name = "Chiller 1", nameAr = "مبرد 1", assetClassId = classId, locationId, criticality = "High", manufacturer = "Carrier", serialNumber = "SN-1", startActive = true };

    [Fact]
    public async Task Assets_are_managed_end_to_end_with_string_enums_and_a_location_header()
    {
        var (manager, cls, space) = await SetupAsync(TenantA);

        var created = await PostAsync(manager, "/api/v1/assets", NewAsset(cls.Id, space.Id));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var asset = await ReadAsync<AssetDto>(created);
        Assert.Equal($"/api/v1/assets/{asset.Id}", created.Headers.Location!.OriginalString);
        Assert.Equal(AssetStatus.Active, asset.Status);
        Assert.Equal(Criticality.High, asset.Criticality);
        Assert.Contains("\"status\":\"Active\"", await manager.GetStringAsync($"/api/v1/assets/{asset.Id}"));

        var updated = await ReadAsync<AssetDto>(await PutAsync(manager, $"/api/v1/assets/{asset.Id}", new
        {
            code = asset.Code, name = "Chiller 1 (renamed)", assetClassId = cls.Id, locationId = space.Id, criticality = "Critical", rowVersion = asset.RowVersion,
        }));
        Assert.Equal("Chiller 1 (renamed)", updated.Name);

        var down = await PostAsync(manager, $"/api/v1/assets/{asset.Id}/status", new { status = "OutOfService", reason = "leak", rowVersion = updated.RowVersion });
        Assert.Equal(HttpStatusCode.OK, down.StatusCode);
        var noReason = await PostAsync(manager, $"/api/v1/assets/{asset.Id}/status", new { status = "Retired", rowVersion = (await ReadAsync<AssetDto>(down)).RowVersion });
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        var list = await ReadAsync<Page<AssetDto>>(await manager.GetAsync($"/api/v1/assets?locationId={space.Id}&status=OutOfService&q=Chiller"));
        Assert.Single(list.Items);
    }

    [Fact]
    public async Task Scanning_a_label_finds_the_asset_and_the_qr_image_encodes_the_public_url()
    {
        var (manager, cls, space) = await SetupAsync(TenantA);
        var asset = await ReadAsync<AssetDto>(await PostAsync(manager, "/api/v1/assets", NewAsset(cls.Id, space.Id)));

        var scanned = await ReadAsync<AssetDto>(await manager.GetAsync($"/api/v1/assets/by-tag/{asset.TagCode.ToLowerInvariant()}"));
        Assert.Equal(asset.Id, scanned.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync("/api/v1/assets/by-tag/ZZZZZ-ZZZZZ")).StatusCode);

        var qr = await manager.GetAsync($"/api/v1/assets/{asset.Id}/qr.svg");
        Assert.Equal(HttpStatusCode.OK, qr.StatusCode);
        Assert.Equal("image/svg+xml", qr.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", qr.Headers.GetValues("X-Content-Type-Options").Single());
        var svg = await qr.Content.ReadAsStringAsync();
        Assert.StartsWith("<svg", svg.TrimStart());

        var labels = _api.Factory.Services.GetRequiredService<AssetLabelService>();
        Assert.Equal($"https://fms.example.com/a/{AssetTag.Normalize(asset.TagCode)}", labels.PayloadFor(AssetTag.Normalize(asset.TagCode)!));
    }

    [Fact]
    public async Task Permissions_split_reading_from_changing_assets()
    {
        var (manager, cls, space) = await SetupAsync(TenantA);
        var asset = await ReadAsync<AssetDto>(await PostAsync(manager, "/api/v1/assets", NewAsset(cls.Id, space.Id)));
        var technician = await _api.ClientAsync(TenantA, SystemRoles.Technician);
        var viewer = await _api.ClientAsync(TenantA, SystemRoles.Viewer);
        var requester = await _api.ClientAsync(TenantA, SystemRoles.Requester);

        Assert.Equal(HttpStatusCode.OK, (await technician.GetAsync($"/api/v1/assets/{asset.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/v1/assets")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await technician.GetAsync("/api/v1/asset-classes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.GetAsync("/api/v1/assets")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.GetAsync($"/api/v1/assets/{asset.Id}/qr.svg")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await technician.PostAsJsonAsync("/api/v1/assets", NewAsset(cls.Id, space.Id), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await technician.PostAsJsonAsync($"/api/v1/assets/{asset.Id}/status", new { status = "Retired", reason = "x", rowVersion = asset.RowVersion }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync($"/api/v1/assets/{asset.Id}/reissue-tag", new { rowVersion = asset.RowVersion }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/asset-classes", new { code = "X", name = "x" }, Json)).StatusCode);
    }

    [Fact]
    public async Task Another_tenant_cannot_read_scan_or_change_an_asset()
    {
        var (managerA, cls, space) = await SetupAsync(TenantA);
        var asset = await ReadAsync<AssetDto>(await PostAsync(managerA, "/api/v1/assets", NewAsset(cls.Id, space.Id)));
        var managerB = await _api.ClientAsync(TenantB, SystemRoles.FacilityManager);

        Assert.Equal(HttpStatusCode.NotFound, (await managerB.GetAsync($"/api/v1/assets/{asset.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.GetAsync($"/api/v1/assets/by-tag/{asset.TagCode}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await managerB.GetAsync($"/api/v1/assets/{asset.Id}/qr.svg")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(managerB, $"/api/v1/assets/{asset.Id}/status", new { status = "Retired", reason = "x", rowVersion = asset.RowVersion })).StatusCode);
        var listB = await ReadAsync<Page<AssetDto>>(await managerB.GetAsync("/api/v1/assets"));
        Assert.DoesNotContain(asset.Id, listB.Items.Select(a => a.Id));
        var steal = await PostAsync(managerB, "/api/v1/assets", NewAsset(cls.Id, space.Id));
        Assert.Equal(HttpStatusCode.BadRequest, steal.StatusCode);
    }

    [Fact]
    public async Task Validation_conflict_and_stale_edits_return_the_right_problem_details()
    {
        var (manager, cls, space) = await SetupAsync(TenantA);
        var code = FacilitiesScope.Code("A");
        var asset = await ReadAsync<AssetDto>(await PostAsync(manager, "/api/v1/assets", NewAsset(cls.Id, space.Id, code)));

        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(manager, "/api/v1/assets", NewAsset(cls.Id, space.Id, code))).StatusCode);
        var invalid = await PostAsync(manager, "/api/v1/assets", new { code = "bad code", name = "", assetClassId = Guid.NewGuid(), locationId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("application/problem+json", invalid.Content.Headers.ContentType?.MediaType);

        Assert.Equal(HttpStatusCode.OK, (await PutAsync(manager, $"/api/v1/assets/{asset.Id}", new { code, name = "first", assetClassId = cls.Id, locationId = space.Id, rowVersion = asset.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(manager, $"/api/v1/assets/{asset.Id}", new { code, name = "second", assetClassId = cls.Id, locationId = space.Id, rowVersion = asset.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/v1/assets/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Every_response_forbids_content_type_sniffing()
    {
        var response = await _api.Factory.CreateClient().GetAsync("/health/live");
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }
}
