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
}
