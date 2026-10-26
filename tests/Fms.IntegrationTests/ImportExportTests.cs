using Fms.BuildingBlocks.Results;
using Fms.Facilities.Domain;
using Fms.Facilities.Services;
using Microsoft.EntityFrameworkCore;
using static Fms.IntegrationTests.SqlServerFixture;

namespace Fms.IntegrationTests;

[Collection(SqlCollection.Name)]
public sealed class ImportExportTests(SqlServerFixture sql)
{
    private static string U() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static string[] ErrorsOf(ImportReport r) => r.Rows.SelectMany(x => x.Errors).ToArray();

    [Fact]
    public async Task A_location_file_builds_the_hierarchy_using_codes_for_parents()
    {
        var u = U();
        var csv = $"""
            type,code,parentCode,name,nameAr,address,timeZone,areaSquareMetres
            Site,S-{u},,Riyadh HQ,المقر الرئيسي,King Fahd Rd,Asia/Riyadh,
            Building,B-{u},S-{u},"Tower A, north",البرج أ,,,١٢٠٠٫٥
            Floor,F-{u},B-{u},Floor 3,,,,300
            Space,R-{u},F-{u},Room 301,,,,25.5
            """;
        await using var f = FacilitiesScope.For(sql, TenantA);

        var result = await f.ImportExport.ImportLocationsAsync(csv, dryRun: false);

        Assert.True(result.IsOk);
        var report = result.Value!;
        Assert.True(report.Committed);
        Assert.Equal((4, 4, 0), (report.Total, report.Created, report.Failed));
        await using var check = FacilitiesScope.For(sql, TenantA);
        var tree = await check.Locations.ListAsync(new LocationQuery(Search: u));
        Assert.Equal(4, tree.Total);
        var building = tree.Items.Single(l => l.Code == $"B-{u}");
        Assert.Equal("Tower A, north", building.Name);
        Assert.Equal("البرج أ", building.NameAr);
        Assert.Equal(1200.5m, building.AreaSquareMetres);
        Assert.Equal("Asia/Riyadh", tree.Items.Single(l => l.Code == $"S-{u}").TimeZone);
    }

    [Fact]
    public async Task A_dry_run_reports_what_would_happen_and_saves_nothing()
    {
        var u = U();
        var csv = $"type,code,parentCode,name\nSite,S-{u},,One\nBuilding,B-{u},S-{u},Two\n";
        await using var f = FacilitiesScope.For(sql, TenantA);

        var report = (await f.ImportExport.ImportLocationsAsync(csv, dryRun: true)).Value!;

        Assert.True(report.DryRun);
        Assert.False(report.Committed);
        Assert.Equal(2, report.Created);
        await using var check = FacilitiesScope.For(sql, TenantA);
        Assert.Equal(0, (await check.Locations.ListAsync(new LocationQuery(Search: u))).Total);
        await using var audit = sql.App(TenantA);
        Assert.False(await audit.AuditEvents.AnyAsync(e => e.Action == "location.created" && e.ChangeSummaryJson!.Contains(u)));
    }

    [Fact]
    public async Task One_bad_row_saves_nothing_and_every_problem_is_listed_with_its_line()
    {
        var u = U();
        var csv = $"""
            type,code,parentCode,name,areaSquareMetres
            Site,S-{u},,Good site,
            Building,B-{u},S-{u},Good building,abc
            Floor,F-{u},NOPE-{u},Orphan floor,
            Wing,W-{u},S-{u},Bad type,
            Building,S-{u},S-{u},Duplicate code,
            """;
        await using var f = FacilitiesScope.For(sql, TenantA);

        var report = (await f.ImportExport.ImportLocationsAsync(csv, dryRun: false)).Value!;

        Assert.False(report.Committed);
        Assert.Equal(4, report.Failed);
        Assert.Equal(["Created", "Error", "Error", "Error", "Error"], report.Rows.Select(r => r.Outcome));
        Assert.Equal([2, 3, 4, 5, 6], report.Rows.Select(r => r.Line));
        Assert.Contains(report.Rows[1].Errors, e => e.StartsWith("areaSquareMetres:"));
        Assert.Contains(report.Rows[2].Errors, e => e.Contains("NOPE-"));
        Assert.Contains(report.Rows[3].Errors, e => e.StartsWith("type:"));
        Assert.NotEmpty(report.Rows[4].Errors);
        await using var check = FacilitiesScope.For(sql, TenantA);
        Assert.Equal(0, (await check.Locations.ListAsync(new LocationQuery(Search: u))).Total); // the good row was rolled back too
    }

    [Fact]
    public async Task Rows_follow_the_same_rules_as_the_api()
    {
        var u = U();
        var csv = $"type,code,parentCode,name,timeZone\nSite,S-{u},,One,Mars/Base\nSite,bad code,,Two,\nFloor,F-{u},,Three,\n";
        await using var f = FacilitiesScope.For(sql, TenantA);

        var report = (await f.ImportExport.ImportLocationsAsync(csv, dryRun: true)).Value!;

        Assert.Contains(report.Rows[0].Errors, e => e.StartsWith("timeZone:"));
        Assert.Contains(report.Rows[1].Errors, e => e.StartsWith("code:"));
        Assert.Contains(report.Rows[2].Errors, e => e.StartsWith("parentId:"));
    }

    [Fact]
    public async Task A_parent_can_be_an_existing_location_but_never_another_tenants()
    {
        var u = U();
        await using var a = FacilitiesScope.For(sql, TenantA);
        var site = await a.NewAsync(LocationType.Site, null, $"EX-{u}");

        await using var a2 = FacilitiesScope.For(sql, TenantA);
        var ok = (await a2.ImportExport.ImportLocationsAsync($"type,code,parentCode,name\nBuilding,N-{u},EX-{u},New\n", false)).Value!;
        Assert.True(ok.Committed);

        await using var b = FacilitiesScope.For(sql, TenantB);
        var report = (await b.ImportExport.ImportLocationsAsync($"type,code,parentCode,name\nBuilding,Z-{u},EX-{u},Sneaky\n", false)).Value!;
        Assert.False(report.Committed);
        Assert.Contains(report.Rows[0].Errors, e => e.Contains($"EX-{u}"));
        _ = site;
    }

    [Fact]
    public async Task The_file_itself_is_validated_before_any_row_is_read()
    {
        await using var f = FacilitiesScope.For(sql, TenantA);
        async Task<Result<ImportReport>> Run(string csv) => await f.ImportExport.ImportLocationsAsync(csv, true);

        Assert.Equal(ResultStatus.Invalid, (await Run("")).Status);
        Assert.Equal(ResultStatus.Invalid, (await Run("type,code,name\n")).Status);                   // header only
        Assert.Equal(ResultStatus.Invalid, (await Run("type,name\nSite,x\n")).Status);                // missing code
        Assert.Equal(ResultStatus.Invalid, (await Run("type,code,name,colour\nSite,a,x,red\n")).Status); // unknown column
        Assert.Equal(ResultStatus.Invalid, (await Run("type,code,code,name\nSite,a,b,x\n")).Status);  // duplicate column
        Assert.Equal(ResultStatus.Invalid, (await Run("type,code,name\nSite,\"unclosed,x\n")).Status);
        var tooMany = "type,code,name\n" + string.Concat(Enumerable.Range(0, ImportExportService.MaxRows + 1).Select(i => $"Site,C{i},N\n"));
        Assert.Equal(ResultStatus.Invalid, (await Run(tooMany)).Status);
        Assert.Equal(ResultStatus.Invalid, (await Run("type,code,name\nSite,a," + new string('x', ImportExportService.MaxBytes) + "\n")).Status);
    }

    [Fact]
    public async Task Imports_are_audited_only_when_committed()
    {
        var u = U();
        await using var f = FacilitiesScope.For(sql, TenantA);
        await f.ImportExport.ImportLocationsAsync($"type,code,name\nSite,A-{u},x\n", dryRun: true);
        await using var g = FacilitiesScope.For(sql, TenantA);
        await g.ImportExport.ImportLocationsAsync($"type,code,name\nSite,B-{u},x\nSite,B-{u},dup\n", dryRun: false);
        await using var h = FacilitiesScope.For(sql, TenantA);
        await h.ImportExport.ImportLocationsAsync($"type,code,name\nSite,C-{u},x\n", dryRun: false);

        await using var check = sql.App(TenantA);
        var created = await check.AuditEvents.Where(e => e.Action == "location.created" && e.ChangeSummaryJson!.Contains(u)).ToListAsync();
        Assert.Single(created);
        Assert.Contains($"C-{u}", created[0].ChangeSummaryJson);
    }

    [Fact]
    public async Task An_asset_file_links_classes_locations_and_parts_by_code_and_fills_custom_fields()
    {
        var u = U();
        await using var f = FacilitiesScope.For(sql, TenantA);
        var site = await f.NewAsync(LocationType.Site, null, $"S-{u}");
        var cls = await f.NewClassAsync($"HVAC-{u}");
        var key = "tons" + u.ToLowerInvariant()[..4];
        var flag = "crit" + u.ToLowerInvariant()[..4];
        Assert.True((await f.CustomFields.CreateAsync(new CreateCustomFieldRequest(CustomFieldTarget.Asset, key, "Tonnage", null, CustomFieldType.Number, false, null, null, 0))).IsOk);
        Assert.True((await f.CustomFields.CreateAsync(new CreateCustomFieldRequest(CustomFieldTarget.Asset, flag, "Critical path", null, CustomFieldType.Boolean, false, null, null, 1))).IsOk);

        var csv = $"""
            code,name,nameAr,classCode,locationCode,parentAssetCode,criticality,status,manufacturer,serialNumber,installDate,warrantyExpiry,cf.{key},cf.{flag}
            CH-{u},Chiller 1,مبرد ١,HVAC-{u},S-{u},,High,Active,Carrier,SN-1,2024-01-15,2029-01-14,٤٥٠,نعم
            PU-{u},Pump 1,,HVAC-{u},S-{u},CH-{u},Medium,,Grundfos,SN-2,,,12.5,no
            """;
        await using var g = FacilitiesScope.For(sql, TenantA);
        var report = (await g.ImportExport.ImportAssetsAsync(csv, dryRun: false)).Value!;

        Assert.True(report.Committed, string.Join("; ", ErrorsOf(report)));
        await using var check = FacilitiesScope.For(sql, TenantA);
        var chiller = (await check.Assets.ListAsync(new AssetQuery(Search: $"CH-{u}"))).Items.Single();
        var pump = (await check.Assets.ListAsync(new AssetQuery(Search: $"PU-{u}"))).Items.Single();
        Assert.Equal(AssetStatus.Active, chiller.Status);
        Assert.Equal(AssetStatus.Planned, pump.Status);
        Assert.Equal(chiller.Id, pump.ParentAssetId);
        Assert.Equal(450m, chiller.CustomFields[key].GetDecimal());
        Assert.True(chiller.CustomFields[flag].GetBoolean());
        Assert.False(pump.CustomFields[flag].GetBoolean());
        Assert.Equal(new DateOnly(2029, 1, 14), chiller.WarrantyExpiry);
        Assert.Equal(site.Id, chiller.LocationId);
    }

    [Fact]
    public async Task Asset_rows_with_unknown_references_or_bad_values_are_reported()
    {
        var u = U();
        await using var f = FacilitiesScope.For(sql, TenantA);
        await f.NewAsync(LocationType.Site, null, $"S-{u}");
        await f.NewClassAsync($"C-{u}");
        var csv = $"""
            code,name,classCode,locationCode,criticality,status,installDate
            A1-{u},Ok,C-{u},S-{u},Low,,
            A2-{u},Bad class,NOPE,S-{u},,,
            A3-{u},Bad place,C-{u},NOPE,,,
            A4-{u},Bad crit,C-{u},S-{u},Urgent,,
            A5-{u},Bad status,C-{u},S-{u},,Retired,
            A6-{u},Bad date,C-{u},S-{u},,,25/10/2026
            """;
        await using var g = FacilitiesScope.For(sql, TenantA);

        var report = (await g.ImportExport.ImportAssetsAsync(csv, false)).Value!;

        Assert.False(report.Committed);
        Assert.Equal(5, report.Failed);
        Assert.Contains(report.Rows[1].Errors, e => e.StartsWith("classCode:"));
        Assert.Contains(report.Rows[2].Errors, e => e.StartsWith("locationCode:"));
        Assert.Contains(report.Rows[3].Errors, e => e.StartsWith("criticality:"));
        Assert.Contains(report.Rows[4].Errors, e => e.StartsWith("status:"));
        Assert.Contains(report.Rows[5].Errors, e => e.StartsWith("installDate:"));
    }

    [Fact]
    public async Task Export_then_import_into_another_tenant_reproduces_the_data()
    {
        var u = U();
        await using var f = FacilitiesScope.For(sql, TenantA);
        var site = await f.NewAsync(LocationType.Site, null, $"S-{u}", "Main campus");
        var building = await f.NewAsync(LocationType.Building, site.Id, $"B-{u}", "Tower, East");
        var cls = await f.NewClassAsync($"K-{u}");
        await f.NewAssetAsync(cls.Id, building.Id, $"X-{u}", active: true, name: "Pump \"One\"");

        await using var g = FacilitiesScope.For(sql, TenantA);
        var locationsCsv = (await g.ImportExport.ExportLocationsAsync()).Value!;
        var assetsCsv = (await g.ImportExport.ExportAssetsAsync()).Value!;
        Assert.StartsWith("﻿", locationsCsv);

        // Keep only this test's rows, then load them into a tenant that has nothing.
        string Only(string csv) => string.Join("\n", csv.Split('\n').Where((line, i) => i == 0 || line.Contains(u)));
        await using var b = FacilitiesScope.For(sql, TenantB);
        await b.NewClassAsync($"K-{u}");
        var placed = (await b.ImportExport.ImportLocationsAsync(Only(locationsCsv), false)).Value!;
        Assert.True(placed.Committed, string.Join("; ", ErrorsOf(placed)));
        await using var b2 = FacilitiesScope.For(sql, TenantB);
        var assetsIn = (await b2.ImportExport.ImportAssetsAsync(Only(assetsCsv), false)).Value!;
        Assert.True(assetsIn.Committed, string.Join("; ", ErrorsOf(assetsIn)));

        await using var check = FacilitiesScope.For(sql, TenantB);
        var asset = (await check.Assets.ListAsync(new AssetQuery(Search: $"X-{u}"))).Items.Single();
        Assert.Equal("Pump \"One\"", asset.Name);
        Assert.Equal("Tower, East", (await check.Locations.ListAsync(new LocationQuery(Search: $"B-{u}"))).Items.Single().Name);
    }

    [Fact]
    public async Task Export_defuses_formulas_and_contains_only_the_tenants_active_rows()
    {
        var u = U();
        await using var a = FacilitiesScope.For(sql, TenantA);
        await a.NewAsync(LocationType.Site, null, $"EV-{u}", "=HYPERLINK(\"http://evil.test\",\"click\")");
        var archived = await a.NewAsync(LocationType.Site, null, $"AR-{u}");
        await using (var g = FacilitiesScope.For(sql, TenantA)) await g.Locations.ArchiveAsync(archived.Id, archived.RowVersion);
        await using var b = FacilitiesScope.For(sql, TenantB);
        await b.NewAsync(LocationType.Site, null, $"OTHER-{u}");

        await using var export = FacilitiesScope.For(sql, TenantA);
        var csv = (await export.ImportExport.ExportLocationsAsync()).Value!;

        Assert.Contains($"EV-{u}", csv);
        Assert.DoesNotContain($"AR-{u}", csv);
        Assert.DoesNotContain($"OTHER-{u}", csv);
        Assert.Contains("'=HYPERLINK", csv);
        Assert.DoesNotContain(",=HYPERLINK", csv);
        Assert.DoesNotContain("\"=HYPERLINK", csv);
    }
}
