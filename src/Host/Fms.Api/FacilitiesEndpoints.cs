using Fms.BuildingBlocks.Security;
using Fms.Facilities.Domain;
using Fms.Facilities.Services;

namespace Fms.Api;

public sealed record RowVersionRequest(string? RowVersion);

public static class FacilitiesEndpoints
{
    public static void MapFacilities(this RouteGroupBuilder v1)
    {
        MapLocations(v1.MapGroup("/locations"));
        MapCustomFields(v1.MapGroup("/custom-fields"));
        MapAssetClasses(v1.MapGroup("/asset-classes"));
        MapAssets(v1.MapGroup("/assets"));
        MapMeters(v1);
    }

    private static void MapLocations(RouteGroupBuilder g)
    {
        g.MapGet("", async (LocationService s, Guid? parentId, Guid? underId, LocationType? type, string? q, bool? includeArchived, int? page, int? pageSize, CancellationToken ct) =>
                Results.Ok(await s.ListAsync(new LocationQuery(parentId, underId, type, q, includeArchived ?? false, page, pageSize), ct)))
            .RequirePermission(Permissions.LocationsRead);

        g.MapGet("/tree", async (LocationService s, CancellationToken ct) => Results.Ok(await s.TreeAsync(ct)))
            .RequirePermission(Permissions.LocationsRead);

        g.MapGet("/{id:guid}", async (Guid id, LocationService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttp())
            .RequirePermission(Permissions.LocationsRead);

        g.MapPost("", async (CreateLocationRequest r, LocationService s, CancellationToken ct) =>
                (await s.CreateAsync(r, ct)).ToHttp(l => Results.Created($"/api/v1/locations/{l.Id}", l)))
            .RequirePermission(Permissions.LocationsManage);

        g.MapPut("/{id:guid}", async (Guid id, UpdateLocationRequest r, LocationService s, CancellationToken ct) => (await s.UpdateAsync(id, r, ct)).ToHttp())
            .RequirePermission(Permissions.LocationsManage);

        g.MapPost("/{id:guid}/move", async (Guid id, MoveLocationRequest r, LocationService s, CancellationToken ct) => (await s.MoveAsync(id, r, ct)).ToHttp())
            .RequirePermission(Permissions.LocationsManage);

        g.MapPost("/{id:guid}/archive", async (Guid id, RowVersionRequest r, LocationService s, CancellationToken ct) => (await s.ArchiveAsync(id, r.RowVersion, ct)).ToHttp())
            .RequirePermission(Permissions.LocationsManage);

        g.MapPost("/{id:guid}/restore", async (Guid id, RowVersionRequest r, LocationService s, CancellationToken ct) => (await s.RestoreAsync(id, r.RowVersion, ct)).ToHttp())
            .RequirePermission(Permissions.LocationsManage);
    }

    private static void MapCustomFields(RouteGroupBuilder g)
    {
        g.MapGet("", async (CustomFieldService s, CustomFieldTarget? target, bool? includeArchived, CancellationToken ct) =>
                Results.Ok(await s.ListAsync(target, includeArchived ?? false, ct)))
            .RequireAnyPermission(Permissions.LocationsRead, Permissions.AssetsRead, Permissions.CustomFieldsManage);

        g.MapPost("", async (CreateCustomFieldRequest r, CustomFieldService s, CancellationToken ct) =>
                (await s.CreateAsync(r, ct)).ToHttp(d => Results.Created($"/api/v1/custom-fields/{d.Id}", d)))
            .RequirePermission(Permissions.CustomFieldsManage);

        g.MapPut("/{id:guid}", async (Guid id, UpdateCustomFieldRequest r, CustomFieldService s, CancellationToken ct) => (await s.UpdateAsync(id, r, ct)).ToHttp())
            .RequirePermission(Permissions.CustomFieldsManage);

        g.MapPost("/{id:guid}/archive", async (Guid id, CustomFieldService s, CancellationToken ct) => (await s.SetArchivedAsync(id, true, ct)).ToHttp())
            .RequirePermission(Permissions.CustomFieldsManage);

        g.MapPost("/{id:guid}/restore", async (Guid id, CustomFieldService s, CancellationToken ct) => (await s.SetArchivedAsync(id, false, ct)).ToHttp())
            .RequirePermission(Permissions.CustomFieldsManage);
    }

    private static void MapAssetClasses(RouteGroupBuilder g)
    {
        g.MapGet("", async (AssetClassService s, bool? includeArchived, CancellationToken ct) => Results.Ok(await s.ListAsync(includeArchived ?? false, ct)))
            .RequirePermission(Permissions.AssetsRead);

        g.MapPost("", async (SaveAssetClassRequest r, AssetClassService s, CancellationToken ct) =>
                (await s.CreateAsync(r, ct)).ToHttp(c => Results.Created($"/api/v1/asset-classes/{c.Id}", c)))
            .RequirePermission(Permissions.AssetsManage);

        g.MapPut("/{id:guid}", async (Guid id, SaveAssetClassRequest r, AssetClassService s, CancellationToken ct) => (await s.UpdateAsync(id, r, ct)).ToHttp())
            .RequirePermission(Permissions.AssetsManage);

        g.MapPost("/{id:guid}/archive", async (Guid id, RowVersionRequest r, AssetClassService s, CancellationToken ct) => (await s.SetArchivedAsync(id, true, r.RowVersion, ct)).ToHttp())
            .RequirePermission(Permissions.AssetsManage);

        g.MapPost("/{id:guid}/restore", async (Guid id, RowVersionRequest r, AssetClassService s, CancellationToken ct) => (await s.SetArchivedAsync(id, false, r.RowVersion, ct)).ToHttp())
            .RequirePermission(Permissions.AssetsManage);
    }

    private static void MapAssets(RouteGroupBuilder g)
    {
        g.MapGet("", async (AssetService s, Guid? locationId, bool? includeSublocations, Guid? classId, AssetStatus? status, Criticality? criticality,
                    Guid? parentAssetId, string? q, DateOnly? warrantyExpiresBefore, bool? includeRetired, int? page, int? pageSize, CancellationToken ct) =>
                Results.Ok(await s.ListAsync(new AssetQuery(locationId, includeSublocations ?? true, classId, status, criticality, parentAssetId, q,
                    warrantyExpiresBefore, includeRetired ?? false, page, pageSize), ct)))
            .RequirePermission(Permissions.AssetsRead);

        g.MapGet("/{id:guid}", async (Guid id, AssetService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttp())
            .RequirePermission(Permissions.AssetsRead);

        g.MapGet("/by-tag/{tag}", async (string tag, AssetService s, CancellationToken ct) => (await s.GetByTagAsync(tag, ct)).ToHttp())
            .RequirePermission(Permissions.AssetsRead);

        g.MapGet("/{id:guid}/qr.svg", async (Guid id, AssetService s, AssetLabelService labels, CancellationToken ct) =>
            {
                var asset = await s.GetAsync(id, ct);
                if (!asset.IsOk) return asset.ToHttp();
                return Results.Text(labels.QrSvg(AssetTag.Normalize(asset.Value!.TagCode)!), "image/svg+xml");
            })
            .RequirePermission(Permissions.AssetsRead);

        g.MapPost("", async (CreateAssetRequest r, AssetService s, CancellationToken ct) =>
                (await s.CreateAsync(r, ct)).ToHttp(a => Results.Created($"/api/v1/assets/{a.Id}", a)))
            .RequirePermission(Permissions.AssetsManage);

        g.MapPut("/{id:guid}", async (Guid id, SaveAssetRequest r, AssetService s, CancellationToken ct) => (await s.UpdateAsync(id, r, ct)).ToHttp())
            .RequirePermission(Permissions.AssetsManage);

        g.MapPost("/{id:guid}/status", async (Guid id, ChangeAssetStatusRequest r, AssetService s, CancellationToken ct) => (await s.ChangeStatusAsync(id, r, ct)).ToHttp())
            .RequirePermission(Permissions.AssetsManage);

        g.MapPost("/{id:guid}/reissue-tag", async (Guid id, RowVersionRequest r, AssetService s, CancellationToken ct) => (await s.ReissueTagAsync(id, r.RowVersion, ct)).ToHttp())
            .RequirePermission(Permissions.AssetsManage);
    }

    private static void MapMeters(RouteGroupBuilder v1)
    {
        v1.MapGet("/assets/{assetId:guid}/meters", async (Guid assetId, MeterService s, bool? includeArchived, CancellationToken ct) =>
                (await s.ListForAssetAsync(assetId, includeArchived ?? false, ct)).ToHttp())
            .RequirePermission(Permissions.MetersRead);

        v1.MapPost("/assets/{assetId:guid}/meters", async (Guid assetId, CreateMeterRequest r, MeterService s, CancellationToken ct) =>
                (await s.CreateAsync(assetId, r, ct)).ToHttp(m => Results.Created($"/api/v1/meters/{m.Id}", m)))
            .RequirePermission(Permissions.MetersManage);

        var g = v1.MapGroup("/meters");

        g.MapGet("/{id:guid}", async (Guid id, MeterService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttp())
            .RequirePermission(Permissions.MetersRead);

        g.MapPut("/{id:guid}", async (Guid id, UpdateMeterRequest r, MeterService s, CancellationToken ct) => (await s.UpdateAsync(id, r, ct)).ToHttp())
            .RequirePermission(Permissions.MetersManage);

        g.MapPost("/{id:guid}/archive", async (Guid id, RowVersionRequest r, MeterService s, CancellationToken ct) => (await s.SetArchivedAsync(id, true, r.RowVersion, ct)).ToHttp())
            .RequirePermission(Permissions.MetersManage);

        g.MapPost("/{id:guid}/restore", async (Guid id, RowVersionRequest r, MeterService s, CancellationToken ct) => (await s.SetArchivedAsync(id, false, r.RowVersion, ct)).ToHttp())
            .RequirePermission(Permissions.MetersManage);

        g.MapGet("/{id:guid}/readings", async (Guid id, MeterService s, DateTimeOffset? from, DateTimeOffset? to, bool? includeVoided, int? page, int? pageSize, CancellationToken ct) =>
                (await s.ListReadingsAsync(id, new ReadingQuery(from, to, includeVoided ?? false, page, pageSize), ct)).ToHttp())
            .RequirePermission(Permissions.MetersRead);

        // 201 for a new reading, 200 when the same ClientId was already stored (a safe retry).
        g.MapPost("/{id:guid}/readings", async (Guid id, RecordReadingRequest r, MeterService s, CancellationToken ct) =>
                (await s.RecordAsync(id, r, ct)).ToHttp(x => x.WasDuplicate ? Results.Ok(x.Reading) : Results.Created($"/api/v1/meters/{id}/readings", x.Reading)))
            .RequirePermission(Permissions.MetersRecord);

        g.MapPost("/readings/batch", async (BatchReadingsRequest r, MeterService s, CancellationToken ct) =>
                (await s.RecordBatchAsync(r.Readings, ct)).ToHttp())
            .RequirePermission(Permissions.MetersRecord);

        v1.MapPost("/meter-readings/{readingId:guid}/void", async (Guid readingId, VoidReadingRequest r, MeterService s, CancellationToken ct) =>
                (await s.VoidAsync(readingId, r, ct)).ToHttp())
            .RequirePermission(Permissions.MetersManage);
    }
}

public sealed record BatchReadingsRequest(List<BatchReadingItem>? Readings);
