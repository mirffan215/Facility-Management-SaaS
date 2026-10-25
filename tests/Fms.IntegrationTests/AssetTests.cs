using System.Text.Json;
using Fms.BuildingBlocks.Results;
using Fms.Facilities.Domain;
using Fms.Facilities.Services;
using Microsoft.EntityFrameworkCore;
using static Fms.IntegrationTests.SqlServerFixture;

namespace Fms.IntegrationTests;

[Collection(SqlCollection.Name)]
public sealed class AssetTests(SqlServerFixture sql)
{
    private static Dictionary<string, string[]> ErrorsOf<T>(Result<T> r) => r.Errors!.ToDictionary(e => e.Key, e => e.Value);

    private async Task<(Guid ClassId, Guid LocationId, LocationDto Site, LocationDto Floor)> BaseAsync(FacilitiesScope f)
    {
        var (site, _, floor, space) = await f.NewChainAsync();
        var cls = await f.NewClassAsync();
        return (cls.Id, space.Id, site, floor);
    }

    [Fact]
    public async Task An_asset_is_created_planned_with_a_unique_tag_and_is_found_by_it()
    {
        await using var f = FacilitiesScope.For(sql, TenantA);
        var (classId, locationId, _, _) = await BaseAsync(f);

        var asset = await f.NewAssetAsync(classId, locationId);
        var other = await f.NewAssetAsync(classId, locationId);

        Assert.Equal(AssetStatus.Planned, asset.Status);
        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{5}-[0-9A-HJKMNP-TV-Z]{5}$", asset.TagCode);
        Assert.NotEqual(asset.TagCode, other.TagCode);

        await using var scan = FacilitiesScope.For(sql, TenantA);
        Assert.Equal(asset.Id, (await scan.Assets.GetByTagAsync(asset.TagCode.ToLowerInvariant().Replace("-", " "))).Value!.Id);
        Assert.Equal(ResultStatus.NotFound, (await scan.Assets.GetByTagAsync("not-a-tag")).Status);
        Assert.Equal(ResultStatus.NotFound, (await scan.Assets.GetByTagAsync(null)).Status);
    }

    [Fact]
    public async Task Tags_do_not_resolve_across_tenants()
    {
        await using var a = FacilitiesScope.For(sql, TenantA);
        var (classId, locationId, _, _) = await BaseAsync(a);
        var asset = await a.NewAssetAsync(classId, locationId);

        await using var b = FacilitiesScope.For(sql, TenantB);
        Assert.Equal(ResultStatus.NotFound, (await b.Assets.GetByTagAsync(asset.TagCode)).Status);
        Assert.Equal(ResultStatus.NotFound, (await b.Assets.GetAsync(asset.Id)).Status);
    }

    [Fact]
    public async Task Reissuing_a_tag_invalidates_the_old_one()
    {
        await using var f = FacilitiesScope.For(sql, TenantA);
        var (classId, locationId, _, _) = await BaseAsync(f);
        var asset = await f.NewAssetAsync(classId, locationId);

        await using var g = FacilitiesScope.For(sql, TenantA);
        var reissued = await g.Assets.ReissueTagAsync(asset.Id, asset.RowVersion);

        Assert.True(reissued.IsOk);
        Assert.NotEqual(asset.TagCode, reissued.Value!.TagCode);
        await using var scan = FacilitiesScope.For(sql, TenantA);
        Assert.Equal(ResultStatus.NotFound, (await scan.Assets.GetByTagAsync(asset.TagCode)).Status);
        Assert.True((await scan.Assets.GetByTagAsync(reissued.Value.TagCode)).IsOk);
    }

    [Fact]
    public async Task Asset_codes_are_unique_for_ever_even_after_retirement()
    {
        await using var f = FacilitiesScope.For(sql, TenantA);
        var (classId, locationId, _, _) = await BaseAsync(f);
        var code = FacilitiesScope.Code("DUP");
        var first = await f.NewAssetAsync(classId, locationId, code, active: true);

        await using var g = FacilitiesScope.For(sql, TenantA);
        Assert.True((await g.Assets.ChangeStatusAsync(first.Id, new ChangeAssetStatusRequest(AssetStatus.Retired, "scrapped", first.RowVersion))).IsOk);

        await using var h = FacilitiesScope.For(sql, TenantA);
        Assert.Equal(ResultStatus.Conflict, (await h.Assets.CreateAsync(FacilitiesScope.AssetRequest(classId, locationId, code))).Status);

        await using var b = FacilitiesScope.For(sql, TenantB);
        var (classB, locationB, _, _) = await BaseAsync(b);
        Assert.True((await b.Assets.CreateAsync(FacilitiesScope.AssetRequest(classB, locationB, code))).IsOk);
    }

    [Fact]
    public async Task References_and_fields_are_validated()
    {
        await using var f = FacilitiesScope.For(sql, TenantA);
        var (classId, locationId, _, floor) = await BaseAsync(f);

        var missing = await f.Assets.CreateAsync(FacilitiesScope.AssetRequest(Guid.NewGuid(), Guid.NewGuid()));
        Assert.True(new[] { "assetClassId", "locationId" }.All(ErrorsOf(missing).ContainsKey));

        var badFields = await f.Assets.CreateAsync(new CreateAssetRequest("bad code", "", null, classId, locationId, null, (Criticality)99, new string('m', 201), null, null,
            new DateOnly(2026, 5, 1), new DateOnly(2026, 1, 1), null));
        Assert.True(new[] { "code", "name", "criticality", "manufacturer", "warrantyExpiry" }.All(ErrorsOf(badFields).ContainsKey));

        await using var g = FacilitiesScope.For(sql, TenantA);
        var archivedClass = await g.NewClassAsync();
        await using var h = FacilitiesScope.For(sql, TenantA);
        Assert.True((await h.Classes.SetArchivedAsync(archivedClass.Id, true, archivedClass.RowVersion)).IsOk);
        await using var i = FacilitiesScope.For(sql, TenantA);
        Assert.Contains("assetClassId", ErrorsOf(await i.Assets.CreateAsync(FacilitiesScope.AssetRequest(archivedClass.Id, locationId))).Keys);
        _ = floor;
    }

    [Fact]
    public async Task Cannot_use_another_tenants_class_location_or_parent_asset()
    {
        await using var a = FacilitiesScope.For(sql, TenantA);
        var (classA, locationA, _, _) = await BaseAsync(a);
        var assetA = await a.NewAssetAsync(classA, locationA);

        await using var b = FacilitiesScope.For(sql, TenantB);
        var (classB, locationB, _, _) = await BaseAsync(b);

        var r = await b.Assets.CreateAsync(FacilitiesScope.AssetRequest(classA, locationA, parent: assetA.Id));
        Assert.True(new[] { "assetClassId", "locationId", "parentAssetId" }.All(ErrorsOf(r).ContainsKey));
        var mixed = await b.Assets.CreateAsync(FacilitiesScope.AssetRequest(classB, locationA));
        Assert.Contains("locationId", ErrorsOf(mixed).Keys);
        _ = locationB;
    }

    [Fact]
    public async Task Parts_nest_but_not_in_cycles_or_beyond_the_depth_limit_or_under_retired_assets()
    {
        await using var f = FacilitiesScope.For(sql, TenantA);
        var (classId, locationId, _, _) = await BaseAsync(f);
        var chiller = await f.NewAssetAsync(classId, locationId, active: true);
        var pump = await f.NewAssetAsync(classId, locationId, parent: chiller.Id);

        await using var g = FacilitiesScope.For(sql, TenantA);
        var self = await g.Assets.UpdateAsync(chiller.Id, FacilitiesScope.ToSave(chiller) with { ParentAssetId = chiller.Id });
        Assert.Contains("parentAssetId", ErrorsOf(self).Keys);
        var cycle = await g.Assets.UpdateAsync(chiller.Id, FacilitiesScope.ToSave(chiller) with { ParentAssetId = pump.Id });
        Assert.Contains("parentAssetId", ErrorsOf(cycle).Keys);

        var deepest = pump;
        for (var i = 0; i < AssetRules.MaxDepth - 2; i++) deepest = await f.NewAssetAsync(classId, locationId, parent: deepest.Id);
        var tooDeep = await f.Assets.CreateAsync(FacilitiesScope.AssetRequest(classId, locationId, parent: deepest.Id));
        Assert.Contains("parentAssetId", ErrorsOf(tooDeep).Keys);

        var loneParent = await f.NewAssetAsync(classId, locationId, active: true);
        await using var h = FacilitiesScope.For(sql, TenantA);
        Assert.True((await h.Assets.ChangeStatusAsync(loneParent.Id, new ChangeAssetStatusRequest(AssetStatus.Retired, "gone", loneParent.RowVersion))).IsOk);
        Assert.Contains("parentAssetId", ErrorsOf(await f.Assets.CreateAsync(FacilitiesScope.AssetRequest(classId, locationId, parent: loneParent.Id))).Keys);
    }

    [Fact]
    public async Task Status_follows_the_lifecycle_and_important_changes_need_a_reason()
    {
        await using var f = FacilitiesScope.For(sql, TenantA);
        var (classId, locationId, _, _) = await BaseAsync(f);
        var asset = await f.NewAssetAsync(classId, locationId);

        async Task<Result<AssetDto>> Change(AssetDto from, AssetStatus to, string? reason = null)
        {
            await using var s = FacilitiesScope.For(sql, TenantA);
            return await s.Assets.ChangeStatusAsync(asset.Id, new ChangeAssetStatusRequest(to, reason, from.RowVersion));
        }

        Assert.Equal(ResultStatus.Conflict, (await Change(asset, AssetStatus.UnderMaintenance)).Status);
        var active = (await Change(asset, AssetStatus.Active)).Value!;
        Assert.Equal(ResultStatus.Invalid, (await Change(active, AssetStatus.OutOfService)).Status);
        var down = (await Change(active, AssetStatus.OutOfService, "compressor failure")).Value!;
        Assert.Equal("compressor failure", down.StatusReason);
        var retired = (await Change(down, AssetStatus.Retired, "beyond repair")).Value!;
        Assert.NotNull(retired.RetiredAt);
        Assert.Equal(ResultStatus.Conflict, (await Change(retired, AssetStatus.Active)).Status);

        await using var edit = FacilitiesScope.For(sql, TenantA);
        Assert.Equal(ResultStatus.Conflict, (await edit.Assets.UpdateAsync(asset.Id, FacilitiesScope.ToSave(retired))).Status);
        Assert.Equal(ResultStatus.Conflict, (await edit.Assets.ReissueTagAsync(asset.Id, retired.RowVersion)).Status);
    }

    [Fact]
    public async Task An_assembly_cannot_be_retired_while_its_parts_are_in_use()
    {
        await using var f = FacilitiesScope.For(sql, TenantA);
        var (classId, locationId, _, _) = await BaseAsync(f);
        var chiller = await f.NewAssetAsync(classId, locationId, active: true);
        var pump = await f.NewAssetAsync(classId, locationId, parent: chiller.Id, active: true);

        await using var g = FacilitiesScope.For(sql, TenantA);
        var blocked = await g.Assets.ChangeStatusAsync(chiller.Id, new ChangeAssetStatusRequest(AssetStatus.Retired, "x", chiller.RowVersion));
        Assert.Equal(ResultStatus.Conflict, blocked.Status);

        await using var h = FacilitiesScope.For(sql, TenantA);
        Assert.True((await h.Assets.ChangeStatusAsync(pump.Id, new ChangeAssetStatusRequest(AssetStatus.Retired, "x", pump.RowVersion))).IsOk);
        await using var i = FacilitiesScope.For(sql, TenantA);
        Assert.True((await i.Assets.ChangeStatusAsync(chiller.Id, new ChangeAssetStatusRequest(AssetStatus.Retired, "x", chiller.RowVersion))).IsOk);
    }

    [Fact]
    public async Task Locations_with_assets_cannot_be_archived_until_the_assets_are_retired()
    {
        await using var f = FacilitiesScope.For(sql, TenantA);
        var site = await f.NewAsync(LocationType.Site, null);
        var cls = await f.NewClassAsync();
        var asset = await f.NewAssetAsync(cls.Id, site.Id, active: true);

        await using var g = FacilitiesScope.For(sql, TenantA);
        Assert.Equal(ResultStatus.Conflict, (await g.Locations.ArchiveAsync(site.Id, site.RowVersion)).Status);
        await using var h = FacilitiesScope.For(sql, TenantA);
        Assert.Equal(ResultStatus.Conflict, (await h.Classes.SetArchivedAsync(cls.Id, true, cls.RowVersion)).Status);

        await using var i = FacilitiesScope.For(sql, TenantA);
        Assert.True((await i.Assets.ChangeStatusAsync(asset.Id, new ChangeAssetStatusRequest(AssetStatus.Retired, "done", asset.RowVersion))).IsOk);
        await using var j = FacilitiesScope.For(sql, TenantA);
        Assert.True((await j.Locations.ArchiveAsync(site.Id, site.RowVersion)).IsOk);
        await using var k = FacilitiesScope.For(sql, TenantA);
        Assert.True((await k.Classes.SetArchivedAsync(cls.Id, true, cls.RowVersion)).IsOk);
    }

    [Fact]
    public async Task Moving_an_asset_to_another_location_and_a_stale_edit_conflict()
    {
        await using var f = FacilitiesScope.For(sql, TenantA);
        var (classId, locationId, site, _) = await BaseAsync(f);
        var asset = await f.NewAssetAsync(classId, locationId);
        var newSpace = await f.NewAsync(LocationType.Building, site.Id);

        await using var g = FacilitiesScope.For(sql, TenantA);
        var moved = await g.Assets.UpdateAsync(asset.Id, FacilitiesScope.ToSave(asset) with { LocationId = newSpace.Id });
        Assert.True(moved.IsOk);
        Assert.Equal(newSpace.Id, moved.Value!.LocationId);

        await using var h = FacilitiesScope.For(sql, TenantA);
        Assert.Equal(ResultStatus.Conflict, (await h.Assets.UpdateAsync(asset.Id, FacilitiesScope.ToSave(asset) with { Name = "stale" })).Status);
    }

    [Fact]
    public async Task Class_scoped_custom_fields_apply_only_to_that_class()
    {
        await using var f = FacilitiesScope.For(sql, TenantA);
        var (hvac, locationId, _, _) = await BaseAsync(f);
        var lift = await f.NewClassAsync();
        var key = "tonnage" + Guid.NewGuid().ToString("N")[..5];
        Assert.True((await f.CustomFields.CreateAsync(new CreateCustomFieldRequest(CustomFieldTarget.Asset, key, "Cooling capacity", "سعة التبريد", CustomFieldType.Number, true, null, hvac, 0))).IsOk);

        await using var g = FacilitiesScope.For(sql, TenantA);
        Assert.Contains($"customFields.{key}", ErrorsOf(await g.Assets.CreateAsync(FacilitiesScope.AssetRequest(hvac, locationId))).Keys);
        var withValue = await g.Assets.CreateAsync(FacilitiesScope.AssetRequest(hvac, locationId, fields: FacilitiesScope.Fields(new Dictionary<string, object> { [key] = 450 })));
        Assert.True(withValue.IsOk);
        Assert.Equal(450, withValue.Value!.CustomFields[key].GetInt32());

        await using var h = FacilitiesScope.For(sql, TenantA);
        Assert.True((await h.Assets.CreateAsync(FacilitiesScope.AssetRequest(lift.Id, locationId))).IsOk); // not required for other classes
        Assert.Contains($"customFields.{key}", ErrorsOf(await h.Assets.CreateAsync(FacilitiesScope.AssetRequest(lift.Id, locationId, fields: FacilitiesScope.Fields(new Dictionary<string, object> { [key] = 1 })))).Keys);
    }

    [Fact]
    public async Task Listing_filters_by_location_subtree_class_status_search_warranty_and_pages()
    {
        await using var f = FacilitiesScope.For(sql, TenantA);
        var (site, building, floor, space) = await f.NewChainAsync();
        var otherSite = await f.NewAsync(LocationType.Site, null);
        var cls = await f.NewClassAsync();
        var cls2 = await f.NewClassAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var a1 = (await f.Assets.CreateAsync(FacilitiesScope.AssetRequest(cls.Id, space.Id, active: true, name: "Chiller المبرد", serial: "SN-77", warranty: today.AddDays(10)))).Value!;
        var a2 = await f.NewAssetAsync(cls2.Id, floor.Id);
        var a3 = await f.NewAssetAsync(cls.Id, otherSite.Id, active: true);
        var retired = await f.NewAssetAsync(cls.Id, space.Id, active: true);
        await using (var g = FacilitiesScope.For(sql, TenantA))
            Assert.True((await g.Assets.ChangeStatusAsync(retired.Id, new ChangeAssetStatusRequest(AssetStatus.Retired, "r", retired.RowVersion))).IsOk);

        await using var r = FacilitiesScope.For(sql, TenantA);
        var underBuilding = await r.Assets.ListAsync(new AssetQuery(LocationId: building.Id));
        Assert.Equal(new[] { a1.Id, a2.Id }.Order(), underBuilding.Items.Select(a => a.Id).Order());
        Assert.Equal([a2.Id], (await r.Assets.ListAsync(new AssetQuery(LocationId: floor.Id, IncludeSublocations: false))).Items.Select(a => a.Id));
        Assert.Equal([a1.Id], (await r.Assets.ListAsync(new AssetQuery(LocationId: site.Id, ClassId: cls.Id))).Items.Select(a => a.Id));
        Assert.Equal([a1.Id], (await r.Assets.ListAsync(new AssetQuery(Search: "المبرد"))).Items.Select(a => a.Id));
        Assert.Equal([a1.Id], (await r.Assets.ListAsync(new AssetQuery(Search: "SN-77"))).Items.Select(a => a.Id));
        Assert.Equal([a1.Id], (await r.Assets.ListAsync(new AssetQuery(WarrantyExpiresBefore: today.AddDays(30), LocationId: site.Id))).Items.Select(a => a.Id));
        Assert.Equal(WarrantyState.ExpiringSoon, (await r.Assets.GetAsync(a1.Id)).Value!.Warranty);
        Assert.DoesNotContain(retired.Id, (await r.Assets.ListAsync(new AssetQuery(LocationId: site.Id))).Items.Select(a => a.Id));
        Assert.Contains(retired.Id, (await r.Assets.ListAsync(new AssetQuery(LocationId: site.Id, IncludeRetired: true))).Items.Select(a => a.Id));
        Assert.Contains(retired.Id, (await r.Assets.ListAsync(new AssetQuery(Status: AssetStatus.Retired))).Items.Select(a => a.Id));
        Assert.Equal([a3.Id], (await r.Assets.ListAsync(new AssetQuery(LocationId: otherSite.Id))).Items.Select(a => a.Id));
        var page = await r.Assets.ListAsync(new AssetQuery(LocationId: site.Id, IncludeRetired: true, PageSize: 2));
        Assert.Equal(3, page.Total);
        Assert.Equal(2, page.Items.Count);
    }

    [Fact]
    public async Task Asset_changes_are_audited_with_reasons_and_the_database_isolates_tenants()
    {
        await using var f = FacilitiesScope.For(sql, TenantA);
        var (classId, locationId, _, _) = await BaseAsync(f);
        var asset = await f.NewAssetAsync(classId, locationId, active: true);
        await using (var g = FacilitiesScope.For(sql, TenantA))
            Assert.True((await g.Assets.ChangeStatusAsync(asset.Id, new ChangeAssetStatusRequest(AssetStatus.OutOfService, "leaking refrigerant", asset.RowVersion))).IsOk);

        await using var check = sql.App(TenantA);
        var events = await check.AuditEvents.Where(e => e.EntityId == asset.Id.ToString()).OrderBy(e => e.OccurredAt).ToListAsync();
        Assert.Equal(["asset.created", "asset.status_changed"], events.Select(e => e.Action));
        Assert.Contains("leaking refrigerant", events[1].ChangeSummaryJson);

        await using var b = sql.Facilities(TenantB);
        Assert.Equal(0, await b.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM facilities.Assets WHERE Id = {asset.Id}").SingleAsync());
        Assert.Equal(0, await b.Database.ExecuteSqlAsync($"UPDATE facilities.Assets SET Name = N'pwned' WHERE Id = {asset.Id}"));
        Assert.Equal(0, await b.Database.ExecuteSqlAsync($"DELETE FROM facilities.Assets WHERE Id = {asset.Id}"));
    }

    [Fact]
    public async Task Asset_classes_nest_validate_and_keep_codes_unique_among_active_classes()
    {
        await using var f = FacilitiesScope.For(sql, TenantA);
        var root = await f.NewClassAsync();
        var code = FacilitiesScope.Code("C");
        var child = await f.NewClassAsync(code, root.Id);
        Assert.Equal(root.Id, child.ParentId);

        await using var g = FacilitiesScope.For(sql, TenantA);
        Assert.Equal(ResultStatus.Conflict, (await g.Classes.CreateAsync(new SaveAssetClassRequest(null, code, "dup", null, null))).Status);
        Assert.Equal(ResultStatus.Invalid, (await g.Classes.CreateAsync(new SaveAssetClassRequest(Guid.NewGuid(), "bad code", "", null, null))).Status);

        await using var h = FacilitiesScope.For(sql, TenantA);
        Assert.Equal(ResultStatus.Conflict, (await h.Classes.SetArchivedAsync(root.Id, true, root.RowVersion)).Status); // has an active sub-class

        await using var b = FacilitiesScope.For(sql, TenantB);
        Assert.DoesNotContain(await b.Classes.ListAsync(true), c => c.Id == root.Id);
        Assert.Equal(ResultStatus.NotFound, (await b.Classes.UpdateAsync(root.Id, new SaveAssetClassRequest(null, "X", "hacked", null, root.RowVersion))).Status);
    }
}
