using System.Text.Json;
using Fms.BuildingBlocks.Audit;
using Fms.BuildingBlocks.Results;
using Fms.Facilities.Domain;
using Fms.Facilities.Services;
using Microsoft.EntityFrameworkCore;
using static Fms.IntegrationTests.SqlServerFixture;

namespace Fms.IntegrationTests;

[Collection(SqlCollection.Name)]
public sealed class LocationTests(SqlServerFixture sql)
{
    private static Dictionary<string, string[]> ErrorsOf<T>(Result<T> r) => r.Errors!.ToDictionary(e => e.Key, e => e.Value);

    [Fact]
    public async Task A_full_hierarchy_can_be_built_with_correct_paths()
    {
        await using var f = FacilitiesScope.For(sql, TenantA);
        var (site, building, floor, space) = await f.NewChainAsync();

        var stored = await f.Db.Locations.AsNoTracking().Where(l => new[] { site.Id, building.Id, floor.Id, space.Id }.Contains(l.Id)).ToDictionaryAsync(l => l.Id);
        Assert.Equal($"/{site.Id:N}/", stored[site.Id].Path);
        Assert.Equal($"/{site.Id:N}/{building.Id:N}/{floor.Id:N}/{space.Id:N}/", stored[space.Id].Path);
        Assert.Equal(floor.Id, space.ParentId);
    }

    [Theory]
    [InlineData(LocationType.Building, null)]
    [InlineData(LocationType.Floor, LocationType.Site)]
    [InlineData(LocationType.Space, LocationType.Site)]
    [InlineData(LocationType.Site, LocationType.Building)]
    [InlineData(LocationType.Floor, LocationType.Floor)]
    public async Task Invalid_parent_types_are_rejected(LocationType type, LocationType? parentType)
    {
        await using var f = FacilitiesScope.For(sql, TenantA);
        Guid? parentId = null;
        if (parentType is { } pt)
        {
            var chain = await f.NewChainAsync();
            parentId = pt switch { LocationType.Site => chain.Site.Id, LocationType.Building => chain.Building.Id, LocationType.Floor => chain.Floor.Id, _ => chain.Space.Id };
        }

        var result = await f.Locations.CreateAsync(new CreateLocationRequest(type, parentId, FacilitiesScope.Code(), "x", null, null, null, null, null));

        Assert.Equal(ResultStatus.Invalid, result.Status);
        Assert.Contains("parentId", ErrorsOf(result).Keys);
    }

    [Fact]
    public async Task A_space_may_sit_directly_under_a_building()
    {
        await using var f = FacilitiesScope.For(sql, TenantA);
        var site = await f.NewAsync(LocationType.Site, null);
        var building = await f.NewAsync(LocationType.Building, site.Id);

        var space = await f.NewAsync(LocationType.Space, building.Id);

        Assert.Equal(building.Id, space.ParentId);
    }

    [Fact]
    public async Task Required_fields_and_formats_are_validated()
    {
        await using var f = FacilitiesScope.For(sql, TenantA);
        var site = await f.NewAsync(LocationType.Site, null);

        var bad = await f.Locations.CreateAsync(new CreateLocationRequest(LocationType.Building, site.Id, "has space", "", null, new string('x', 501), "Mars/Base", -5, null));
        var tzOnBuilding = await f.Locations.CreateAsync(new CreateLocationRequest(LocationType.Building, site.Id, FacilitiesScope.Code(), "B", null, null, "Asia/Riyadh", null, null));

        Assert.Equal(ResultStatus.Invalid, bad.Status);
        Assert.True(new[] { "code", "name", "address", "timeZone", "areaSquareMetres" }.All(ErrorsOf(bad).ContainsKey));
        Assert.Contains("timeZone", ErrorsOf(tzOnBuilding).Keys);
    }

    [Fact]
    public async Task Sites_take_an_iana_time_zone_and_arabic_names_round_trip()
    {
        await using var f = FacilitiesScope.For(sql, TenantA);
        var code = FacilitiesScope.Code("S");

        var created = await f.Locations.CreateAsync(new CreateLocationRequest(LocationType.Site, null, code, "Riyadh HQ", "المقر الرئيسي - الرياض", "King Fahd Rd", "Asia/Riyadh", 12000.5m, null));

        Assert.True(created.IsOk);
        await using var read = FacilitiesScope.For(sql, TenantA);
        var loaded = (await read.Locations.GetAsync(created.Value!.Id)).Value!;
        Assert.Equal("المقر الرئيسي - الرياض", loaded.NameAr);
        Assert.Equal("Asia/Riyadh", loaded.TimeZone);
        Assert.Equal(12000.5m, loaded.AreaSquareMetres);
    }

    [Fact]
    public async Task Codes_are_unique_among_active_locations_and_reusable_after_archiving()
    {
        await using var f = FacilitiesScope.For(sql, TenantA);
        var code = FacilitiesScope.Code("DUP");
        var first = await f.NewAsync(LocationType.Site, null, code);

        var duplicate = await f.Locations.CreateAsync(new CreateLocationRequest(LocationType.Site, null, code, "Other", null, null, null, null, null));
        Assert.Equal(ResultStatus.Conflict, duplicate.Status);

        await using var f2 = FacilitiesScope.For(sql, TenantA);
        var current = (await f2.Locations.GetAsync(first.Id)).Value!;
        Assert.True((await f2.Locations.ArchiveAsync(first.Id, current.RowVersion)).IsOk);
        await using var f3 = FacilitiesScope.For(sql, TenantA);
        Assert.True((await f3.Locations.CreateAsync(new CreateLocationRequest(LocationType.Site, null, code, "Reused", null, null, null, null, null))).IsOk);
    }

    [Fact]
    public async Task The_same_code_can_exist_in_two_tenants()
    {
        var code = FacilitiesScope.Code("X");
        await using var a = FacilitiesScope.For(sql, TenantA);
        await using var b = FacilitiesScope.For(sql, TenantB);

        await a.NewAsync(LocationType.Site, null, code);
        await b.NewAsync(LocationType.Site, null, code);
    }

    [Fact]
    public async Task Moving_a_node_rewrites_the_paths_of_everything_beneath_it()
    {
        await using var f = FacilitiesScope.For(sql, TenantA);
        var site = await f.NewAsync(LocationType.Site, null);
        var b1 = await f.NewAsync(LocationType.Building, site.Id);
        var b2 = await f.NewAsync(LocationType.Building, site.Id);
        var floor = await f.NewAsync(LocationType.Floor, b1.Id);
        var space = await f.NewAsync(LocationType.Space, floor.Id);

        await using var mover = FacilitiesScope.For(sql, TenantA);
        var moved = await mover.Locations.MoveAsync(floor.Id, new MoveLocationRequest(b2.Id, floor.RowVersion));

        Assert.True(moved.IsOk, moved.Message);
        await using var check = FacilitiesScope.For(sql, TenantA);
        var paths = await check.Db.Locations.AsNoTracking().Where(l => l.Id == floor.Id || l.Id == space.Id).ToDictionaryAsync(l => l.Id, l => l.Path);
        Assert.Equal($"/{site.Id:N}/{b2.Id:N}/{floor.Id:N}/", paths[floor.Id]);
        Assert.Equal($"/{site.Id:N}/{b2.Id:N}/{floor.Id:N}/{space.Id:N}/", paths[space.Id]);
        var under = await check.Locations.ListAsync(new LocationQuery(UnderId: b2.Id));
        Assert.Equal(new[] { floor.Id, space.Id }.Order(), under.Items.Select(l => l.Id).Order());
    }

    [Fact]
    public async Task A_location_cannot_move_under_itself_a_descendant_or_a_wrong_type()
    {
        await using var f = FacilitiesScope.For(sql, TenantA);
        var (site, building, floor, space) = await f.NewChainAsync();

        var underChild = await f.Locations.MoveAsync(building.Id, new MoveLocationRequest(floor.Id, building.RowVersion));
        var underSelf = await f.Locations.MoveAsync(building.Id, new MoveLocationRequest(building.Id, building.RowVersion));
        var wrongType = await f.Locations.MoveAsync(floor.Id, new MoveLocationRequest(site.Id, floor.RowVersion));
        var siteToRoot = await f.Locations.MoveAsync(site.Id, new MoveLocationRequest(building.Id, site.RowVersion));
        _ = space;

        Assert.All(new[] { underChild, underSelf, wrongType, siteToRoot }, r => Assert.Equal(ResultStatus.Invalid, r.Status));
    }

    [Fact]
    public async Task Archiving_needs_children_archived_first_and_restoring_needs_the_parent_restored()
    {
        await using var f = FacilitiesScope.For(sql, TenantA);
        var (site, building, _, _) = await f.NewChainAsync();
        Assert.Equal(ResultStatus.Conflict, (await f.Locations.ArchiveAsync(building.Id, building.RowVersion)).Status);

        await using var g = FacilitiesScope.For(sql, TenantA);
        var leaf = (await g.Locations.ListAsync(new LocationQuery(Type: LocationType.Space, UnderId: site.Id))).Items.Single();
        var leafArchived = await g.Locations.ArchiveAsync(leaf.Id, leaf.RowVersion);
        Assert.True(leafArchived.IsOk);

        await using var h = FacilitiesScope.For(sql, TenantA);
        var floor = (await h.Locations.ListAsync(new LocationQuery(Type: LocationType.Floor, UnderId: site.Id))).Items.Single();
        Assert.True((await h.Locations.ArchiveAsync(floor.Id, floor.RowVersion)).IsOk);

        await using var i = FacilitiesScope.For(sql, TenantA);
        var restoreLeaf = await i.Locations.RestoreAsync(leaf.Id, leafArchived.Value!.RowVersion);
        Assert.Equal(ResultStatus.Conflict, restoreLeaf.Status);
    }

    [Fact]
    public async Task A_stale_row_version_is_a_conflict_and_nothing_is_changed()
    {
        await using var f = FacilitiesScope.For(sql, TenantA);
        var site = await f.NewAsync(LocationType.Site, null, name: "Original");

        await using var first = FacilitiesScope.For(sql, TenantA);
        var ok = await first.Locations.UpdateAsync(site.Id, new UpdateLocationRequest(site.Code, "Changed by A", null, null, null, null, null, site.RowVersion));
        Assert.True(ok.IsOk);

        await using var second = FacilitiesScope.For(sql, TenantA);
        var stale = await second.Locations.UpdateAsync(site.Id, new UpdateLocationRequest(site.Code, "Changed by B", null, null, null, null, null, site.RowVersion));

        Assert.Equal(ResultStatus.Conflict, stale.Status);
        await using var check = FacilitiesScope.For(sql, TenantA);
        Assert.Equal("Changed by A", (await check.Locations.GetAsync(site.Id)).Value!.Name);
    }

    [Fact]
    public async Task Changes_are_audited_in_the_same_transaction_and_failed_saves_leave_no_audit()
    {
        await using var f = FacilitiesScope.For(sql, TenantA);
        var site = await f.NewAsync(LocationType.Site, null);

        await using var stale = FacilitiesScope.For(sql, TenantA);
        var rename = await stale.Locations.UpdateAsync(site.Id, new UpdateLocationRequest(site.Code, "Renamed", null, null, null, null, null, site.RowVersion));
        Assert.True(rename.IsOk);
        await using var stale2 = FacilitiesScope.For(sql, TenantA);
        var failed = await stale2.Locations.UpdateAsync(site.Id, new UpdateLocationRequest(site.Code, "Lost update", null, null, null, null, null, site.RowVersion));
        Assert.Equal(ResultStatus.Conflict, failed.Status);

        await using var check = sql.App(TenantA);
        var actions = await check.AuditEvents.Where(e => e.EntityId == site.Id.ToString()).Select(e => e.Action).ToListAsync();
        Assert.Equal(["location.created", "location.updated"], actions.Order());
        var updated = await check.AuditEvents.SingleAsync(e => e.EntityId == site.Id.ToString() && e.Action == "location.updated");
        Assert.Contains("Renamed", updated.ChangeSummaryJson);
        Assert.DoesNotContain("Lost update", updated.ChangeSummaryJson);
    }

    [Fact]
    public async Task Tenants_cannot_see_use_or_change_each_others_locations()
    {
        await using var a = FacilitiesScope.For(sql, TenantA);
        var siteA = await a.NewAsync(LocationType.Site, null, name: "Secret A");

        await using var b = FacilitiesScope.For(sql, TenantB);
        Assert.Equal(ResultStatus.NotFound, (await b.Locations.GetAsync(siteA.Id)).Status);
        var asParent = await b.Locations.CreateAsync(new CreateLocationRequest(LocationType.Building, siteA.Id, FacilitiesScope.Code(), "x", null, null, null, null, null));
        Assert.Contains("parentId", ErrorsOf(asParent).Keys);
        Assert.Equal(ResultStatus.NotFound, (await b.Locations.UpdateAsync(siteA.Id, new UpdateLocationRequest("X", "hacked", null, null, null, null, null, siteA.RowVersion))).Status);
        Assert.DoesNotContain(siteA.Id, (await b.Locations.ListAsync(new LocationQuery())).Items.Select(l => l.Id));
        Assert.DoesNotContain(siteA.Id, (await b.Locations.TreeAsync()).Select(n => n.Id));
    }

    [Fact]
    public async Task The_database_itself_hides_locations_from_other_tenants()
    {
        await using var a = FacilitiesScope.For(sql, TenantA);
        var site = await a.NewAsync(LocationType.Site, null);

        await using var b = sql.Facilities(TenantB);
        Assert.False(await b.Locations.IgnoreQueryFilters().AnyAsync(l => l.Id == site.Id));
        Assert.Equal(0, await b.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM facilities.Locations WHERE Id = {site.Id}").SingleAsync());
        Assert.Equal(0, await b.Database.ExecuteSqlAsync($"UPDATE facilities.Locations SET Name = N'pwned' WHERE Id = {site.Id}"));
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => b.Database.ExecuteSqlAsync(
            $"INSERT INTO facilities.Locations (Id, TenantId, Type, Code, Name, Path, CustomFieldsJson, IsArchived, CreatedAt, UpdatedAt) VALUES ({Guid.NewGuid()}, {TenantA}, 1, N'FORGED', N'x', N'/x/', N'{{}}', 0, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET())"));
    }

    [Fact]
    public async Task Listing_filters_by_type_parent_search_and_pages()
    {
        await using var f = FacilitiesScope.For(sql, TenantA);
        var site = await f.NewAsync(LocationType.Site, null, name: "Searchable Campus");
        var buildings = new List<LocationDto>();
        for (var i = 0; i < 5; i++) buildings.Add(await f.NewAsync(LocationType.Building, site.Id, name: $"Tower {i}"));
        await f.Locations.CreateAsync(new CreateLocationRequest(LocationType.Building, site.Id, FacilitiesScope.Code("AR"), "Arabic named", "برج الرياض", null, null, null, null));

        await using var r = FacilitiesScope.For(sql, TenantA);
        var children = await r.Locations.ListAsync(new LocationQuery(ParentId: site.Id, PageSize: 4));
        Assert.Equal(6, children.Total);
        Assert.Equal(4, children.Items.Count);
        Assert.Equal(2, (await r.Locations.ListAsync(new LocationQuery(ParentId: site.Id, PageSize: 4, Page: 2))).Items.Count);
        Assert.Single((await r.Locations.ListAsync(new LocationQuery(Search: "برج الرياض"))).Items);
        Assert.Single((await r.Locations.ListAsync(new LocationQuery(Search: "Searchable Campus", Type: LocationType.Site))).Items);
        Assert.Empty((await r.Locations.ListAsync(new LocationQuery(Search: "Searchable Campus", Type: LocationType.Floor))).Items);
        Assert.Equal(100, (await r.Locations.ListAsync(new LocationQuery(PageSize: 5000))).PageSize);
    }

    [Fact]
    public async Task Custom_fields_are_validated_stored_and_kept_when_the_definition_is_archived()
    {
        await using var f = FacilitiesScope.For(sql, TenantA);
        var key = "f" + Guid.NewGuid().ToString("N")[..8];
        var def = await f.CustomFields.CreateAsync(new CreateCustomFieldRequest(CustomFieldTarget.Location, key, "Fire rating", "تصنيف الحريق", CustomFieldType.Choice, true, ["A", "B"], null, 1));
        Assert.True(def.IsOk);

        await using var g = FacilitiesScope.For(sql, TenantA);
        var site = await g.Locations.CreateAsync(new CreateLocationRequest(LocationType.Site, null, FacilitiesScope.Code(), "S", null, null, null, null, null));
        Assert.Contains($"customFields.{key}", ErrorsOf(site).Keys); // required but missing

        var badChoice = await g.Locations.CreateAsync(new CreateLocationRequest(LocationType.Site, null, FacilitiesScope.Code(), "S", null, null, null, null, FacilitiesScope.Fields(new Dictionary<string, object> { [key] = "Z" })));
        Assert.Contains($"customFields.{key}", ErrorsOf(badChoice).Keys);
        var unknown = await g.Locations.CreateAsync(new CreateLocationRequest(LocationType.Site, null, FacilitiesScope.Code(), "S", null, null, null, null, FacilitiesScope.Fields(new Dictionary<string, object> { [key] = "A", ["nope"] = 1 })));
        Assert.Contains("customFields.nope", ErrorsOf(unknown).Keys);

        var created = await g.Locations.CreateAsync(new CreateLocationRequest(LocationType.Site, null, FacilitiesScope.Code(), "S", null, null, null, null, FacilitiesScope.Fields(new Dictionary<string, object> { [key] = "A" })));
        Assert.True(created.IsOk);
        Assert.Equal("A", created.Value!.CustomFields[key].GetString());

        await using var h = FacilitiesScope.For(sql, TenantA);
        Assert.True((await h.CustomFields.SetArchivedAsync(def.Value!.Id, true)).IsOk);
        await using var i = FacilitiesScope.For(sql, TenantA);
        var renamed = await i.Locations.UpdateAsync(created.Value.Id, new UpdateLocationRequest(created.Value.Code, "S2", null, null, null, null, null, created.Value.RowVersion));
        Assert.True(renamed.IsOk, renamed.Message);
        Assert.Equal("A", renamed.Value!.CustomFields[key].GetString()); // archived definition's value survived the save
    }

    [Fact]
    public async Task Custom_field_definitions_are_validated_unique_per_target_and_tenant_isolated()
    {
        await using var f = FacilitiesScope.For(sql, TenantA);
        var key = "k" + Guid.NewGuid().ToString("N")[..8];
        Assert.True((await f.CustomFields.CreateAsync(new CreateCustomFieldRequest(CustomFieldTarget.Location, key, "L", null, CustomFieldType.Text, false, null, null, 0))).IsOk);

        await using var g = FacilitiesScope.For(sql, TenantA);
        Assert.Equal(ResultStatus.Conflict, (await g.CustomFields.CreateAsync(new CreateCustomFieldRequest(CustomFieldTarget.Location, key, "L", null, CustomFieldType.Text, false, null, null, 0))).Status);
        Assert.Equal(ResultStatus.Invalid, (await g.CustomFields.CreateAsync(new CreateCustomFieldRequest(CustomFieldTarget.Location, "Bad Key", "", null, CustomFieldType.Choice, false, [], null, 0))).Status);
        Assert.Equal(ResultStatus.Invalid, (await g.CustomFields.CreateAsync(new CreateCustomFieldRequest(CustomFieldTarget.Location, "dup_choices", "L", null, CustomFieldType.Choice, false, ["a", "a"], null, 0))).Status);
        Assert.Equal(ResultStatus.Invalid, (await g.CustomFields.CreateAsync(new CreateCustomFieldRequest(CustomFieldTarget.Location, "scoped", "L", null, CustomFieldType.Text, false, null, Guid.NewGuid(), 0))).Status);

        await using var b = FacilitiesScope.For(sql, TenantB);
        Assert.DoesNotContain(await b.CustomFields.ListAsync(null, true), d => d.Key == key);
        Assert.True((await b.CustomFields.CreateAsync(new CreateCustomFieldRequest(CustomFieldTarget.Location, key, "L", null, CustomFieldType.Text, false, null, null, 0))).IsOk);
    }
}
