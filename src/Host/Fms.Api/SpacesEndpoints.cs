using Fms.BuildingBlocks.Security;
using Fms.Spaces.Services;
using Microsoft.AspNetCore.Http.Features;

namespace Fms.Api;

public static class SpacesEndpoints
{
    public static void MapSpaces(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/spaces");
        g.MapGet("/{locationId:guid}", async (Guid locationId, SpaceService s, CancellationToken ct) => (await s.GetAsync(locationId, ct)).ToHttp()).RequirePermission(Permissions.SpacesRead);
        g.MapPut("/{locationId:guid}", async (Guid locationId, SaveSpaceRequest r, SpaceService s, CancellationToken ct) => (await s.SaveAsync(locationId, r, ct)).ToHttp()).RequirePermission(Permissions.SpacesManage);
        g.MapGet("/occupancy/{locationId:guid}", async (Guid locationId, DateOnly? on, SpaceService s, CancellationToken ct) => (await s.OccupancyAsync(locationId, on, ct)).ToHttp()).RequirePermission(Permissions.SpacesRead);

        g.MapGet("/floors/{floorId:guid}", async (Guid floorId, PlanService s, CancellationToken ct) => (await s.ViewAsync(floorId, ct)).ToHttp()).RequirePermission(Permissions.SpacesRead);
        g.MapPut("/floors/{floorId:guid}/pins", async (Guid floorId, SavePinsRequest r, PlanService s, CancellationToken ct) => (await s.SavePinsAsync(floorId, r, ct)).ToHttp()).RequirePermission(Permissions.SpacesManage);

        // multipart/form-data: file (required).
        g.MapPost("/floors/{floorId:guid}/plan", async (Guid floorId, HttpContext http, PlanService s, CancellationToken ct) =>
            {
                if (!http.Request.HasFormContentType) return Results.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "Send the file as multipart/form-data.");
                if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = PlanService.MaxBytes + 1024 * 1024;
                IFormCollection form;
                try { form = await http.Request.ReadFormAsync(ct); }
                catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge) { return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "File too large."); }
                catch (InvalidDataException) { return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The upload could not be read."); }

                if (form.Files.Count != 1 || form.Files[0].Name != "file")
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Send exactly one file in the 'file' field."] });
                var file = form.Files[0];
                await using var stream = file.OpenReadStream();
                return (await s.UploadAsync(floorId, file.FileName, stream, ct)).ToHttp(p => Results.Created($"/api/v1/spaces/plans/{p.Id}/image", p));
            })
            .RequirePermission(Permissions.SpacesManage);

        g.MapGet("/plans/{planId:guid}/image", async (Guid planId, HttpContext http, PlanService s, CancellationToken ct) =>
            {
                var result = await s.DownloadAsync(planId, ct);
                if (!result.IsOk) return result.ToHttp();
                http.Response.Headers.CacheControl = "private, no-store";
                http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
                return Results.File(result.Value!.Content, result.Value.Plan.ContentType);
            })
            .RequirePermission(Permissions.SpacesRead);

        var a = v1.MapGroup("/allocations");
        a.MapGet("", async (AllocationService s, Guid? spaceId, Guid? userId, AllocationState? state, CancellationToken ct) => Results.Ok(await s.ListAsync(spaceId, userId, state, ct))).RequirePermission(Permissions.SpacesRead);
        a.MapPost("", async (CreateAllocationRequest r, AllocationService s, CancellationToken ct) => (await s.CreateAsync(r, ct)).ToHttp(x => Results.Created($"/api/v1/allocations/{x.Id}", x))).RequirePermission(Permissions.SpacesManage);
        a.MapPost("/{id:guid}/release", async (Guid id, ReleaseAllocationRequest r, AllocationService s, CancellationToken ct) => (await s.ReleaseAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.SpacesManage);
    }
}
