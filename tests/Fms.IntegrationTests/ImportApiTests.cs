using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Fms.Facilities.Services;
using Fms.Platform.Security;
using static Fms.IntegrationTests.ApiHarness;
using static Fms.IntegrationTests.SqlServerFixture;

namespace Fms.IntegrationTests;

[Collection(SqlCollection.Name)]
public sealed class ImportApiTests(SqlServerFixture sql) : IDisposable
{
    private readonly ApiHarness _api = new(sql);
    public void Dispose() => _api.Dispose();

    private static string U() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static MultipartFormDataContent File(string csv, string field = "file")
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(csv)) { Headers = { ContentType = new MediaTypeHeaderValue("text/csv") } }, field, "locations.csv");
        return form;
    }

    private static StringContent Body(string csv) => new(csv, new UTF8Encoding(false), "text/csv");

    [Fact]
    public async Task A_manager_imports_a_csv_upload_after_a_dry_run()
    {
        var manager = await _api.ClientAsync(TenantA, SystemRoles.FacilityManager);
        var u = U();
        var csv = $"type,code,parentCode,name,nameAr\nSite,S-{u},,Main,الرئيسي\nBuilding,B-{u},S-{u},Tower,البرج\n";

        var dry = await ReadAsync<ImportReport>(await manager.PostAsync("/api/v1/locations/import?dryRun=true", File(csv)));
        Assert.True(dry.DryRun);
        Assert.False(dry.Committed);
        Assert.Equal(2, dry.Created);
        Assert.Empty((await ReadAsync<Fms.BuildingBlocks.Results.Page<LocationDto>>(await manager.GetAsync($"/api/v1/locations?q={u}"))).Items);

        var real = await ReadAsync<ImportReport>(await manager.PostAsync("/api/v1/locations/import", File(csv)));
        Assert.True(real.Committed);
        Assert.Equal(2, (await ReadAsync<Fms.BuildingBlocks.Results.Page<LocationDto>>(await manager.GetAsync($"/api/v1/locations?q={u}"))).Total);
    }

    [Fact]
    public async Task Scripts_can_post_a_raw_csv_body_and_get_a_row_report_even_when_it_fails()
    {
        var manager = await _api.ClientAsync(TenantA, SystemRoles.FacilityManager);
        var u = U();

        var response = await manager.PostAsync("/api/v1/locations/import", Body($"type,code,name\nSite,S-{u},One\nSite,bad code,Two\n"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // the request worked; the report says the file did not
        var report = await ReadAsync<ImportReport>(response);
        Assert.False(report.Committed);
        Assert.Equal(1, report.Failed);
        Assert.Equal(3, report.Rows[1].Line);
    }

    [Fact]
    public async Task Bad_files_are_rejected_with_the_right_status()
    {
        var manager = await _api.ClientAsync(TenantA, SystemRoles.FacilityManager);

        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsync("/api/v1/locations/import", Body("type,name\nSite,x\n"))).StatusCode);   // missing column
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsync("/api/v1/locations/import", File("type,code,name\nSite,a,b\n", "upload"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await manager.PostAsync("/api/v1/locations/import", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);
        var notUtf8 = new ByteArrayContent([0x74, 0x79, 0xFF, 0xFE]) { Headers = { ContentType = new MediaTypeHeaderValue("text/csv") } };
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsync("/api/v1/locations/import", notUtf8)).StatusCode);
        var huge = "type,code,name\n" + new string('x', ImportExportService.MaxBytes + 10);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await manager.PostAsync("/api/v1/locations/import", Body(huge))).StatusCode);
    }

    [Fact]
    public async Task Only_managers_import_but_readers_can_export_and_tenants_stay_apart()
    {
        var managerA = await _api.ClientAsync(TenantA, SystemRoles.FacilityManager);
        var technician = await _api.ClientAsync(TenantA, SystemRoles.Technician);
        var requester = await _api.ClientAsync(TenantA, SystemRoles.Requester);
        var managerB = await _api.ClientAsync(TenantB, SystemRoles.FacilityManager);
        var u = U();
        await managerA.PostAsync("/api/v1/locations/import", Body($"type,code,name\nSite,S-{u},Visible only to A\n"));

        Assert.Equal(HttpStatusCode.Forbidden, (await technician.PostAsync("/api/v1/locations/import", Body($"type,code,name\nSite,T-{u},x\n"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await technician.PostAsync("/api/v1/assets/import", Body("code,name,classCode,locationCode\n"))).StatusCode);

        var export = await technician.GetAsync("/api/v1/locations/export.csv");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("text/csv", export.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", export.Content.Headers.ContentDisposition?.DispositionType);
        var text = await export.Content.ReadAsStringAsync();
        Assert.Contains($"S-{u}", text);
        Assert.StartsWith("﻿type,code,parentCode", text);

        Assert.Equal(HttpStatusCode.Forbidden, (await requester.GetAsync("/api/v1/assets/export.csv")).StatusCode);
        Assert.DoesNotContain($"S-{u}", await (await managerB.GetAsync("/api/v1/locations/export.csv")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.Factory.CreateClient().GetAsync("/api/v1/assets/export.csv")).StatusCode);
    }

    [Fact]
    public async Task Assets_import_through_the_api()
    {
        var manager = await _api.ClientAsync(TenantA, SystemRoles.FacilityManager);
        var u = U();
        await manager.PostAsync("/api/v1/locations/import", Body($"type,code,name\nSite,S-{u},Site\n"));
        await PostAsync(manager, "/api/v1/asset-classes", new { code = $"C-{u}", name = "Pumps" });

        var report = await ReadAsync<ImportReport>(await manager.PostAsync("/api/v1/assets/import",
            Body($"code,name,classCode,locationCode,status\nP-{u},Pump,C-{u},S-{u},Active\n")));

        Assert.True(report.Committed);
        var exported = await (await manager.GetAsync("/api/v1/assets/export.csv")).Content.ReadAsStringAsync();
        Assert.Contains($"P-{u},Pump,,C-{u},S-{u},,Medium,Active", exported);
    }
}
