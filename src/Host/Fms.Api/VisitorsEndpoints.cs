using Fms.BuildingBlocks.Security;
using Fms.Visitors.Domain;
using Fms.Visitors.Services;

namespace Fms.Api;

public static class VisitorsEndpoints
{
    public static void MapVisitors(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/visits");

        // Hosts: their own visitors.
        g.MapGet("/mine", async (VisitService s, CancellationToken ct) => (await s.MineAsync(ct)).ToHttp()).RequirePermission(Permissions.VisitorsHost);
        g.MapPost("", async (CreateVisitRequest r, VisitService s, CancellationToken ct) => (await s.CreateAsync(r, ct)).ToHttp(x => Results.Created($"/api/v1/visits/mine", x))).RequirePermission(Permissions.VisitorsHost);
        g.MapPost("/{id:guid}/cancel", async (Guid id, CancelVisitRequest r, VisitService s, CancellationToken ct) => (await s.CancelAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.VisitorsHost);
        g.MapPost("/{id:guid}/pass", async (Guid id, VisitService s, CancellationToken ct) => (await s.NewPassAsync(id, ct)).ToHttp()).RequirePermission(Permissions.VisitorsHost);

        // Reception: everyone's visitors, and the desk.
        g.MapGet("", async (VisitService s, DateOnly? day, VisitStatus? status, Guid? siteId, string? q, CancellationToken ct) => Results.Ok(await s.ListAsync(day, status, siteId, q, ct))).RequirePermission(Permissions.VisitorsReception);
        g.MapGet("/on-site", async (VisitService s, Guid? siteId, CancellationToken ct) => Results.Ok(await s.OnSiteAsync(siteId, ct))).RequirePermission(Permissions.VisitorsReception);
        g.MapGet("/lookup", async (VisitService s, string code, CancellationToken ct) => (await s.LookupAsync(code, ct)).ToHttp()).RequirePermission(Permissions.VisitorsReception);
        g.MapPost("/{id:guid}/check-in", async (Guid id, CheckInRequest r, VisitService s, CancellationToken ct) => (await s.CheckInAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.VisitorsReception);
        g.MapPost("/{id:guid}/check-out", async (Guid id, CheckOutRequest r, VisitService s, CancellationToken ct) => (await s.CheckOutAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.VisitorsReception);

        // Security: the watchlist and the rules. Reception may read the induction text it must show, so the settings can be read by both.
        g.MapGet("/watchlist", async (WatchlistService s, CancellationToken ct) => Results.Ok(await s.ListAsync(ct))).RequirePermission(Permissions.VisitorsSecurity);
        g.MapPost("/watchlist", async (AddWatchlistRequest r, WatchlistService s, CancellationToken ct) => (await s.AddAsync(r, ct)).ToHttp(x => Results.Created("/api/v1/visits/watchlist", x))).RequirePermission(Permissions.VisitorsSecurity);
        g.MapPost("/watchlist/{id:guid}/remove", async (Guid id, RemoveWatchlistRequest r, WatchlistService s, CancellationToken ct) => (await s.RemoveAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.VisitorsSecurity);
        g.MapGet("/settings", async (VisitorSettingsService s, CancellationToken ct) => Results.Ok(await s.GetAsync(ct))).RequireAnyPermission(Permissions.VisitorsSecurity, Permissions.VisitorsReception, Permissions.VisitorsHost);
        g.MapPut("/settings", async (SaveVisitorSettingsRequest r, VisitorSettingsService s, CancellationToken ct) => (await s.SaveAsync(r, ct)).ToHttp()).RequirePermission(Permissions.VisitorsSecurity);

        // What a visitor opens from the link or QR code they were sent. They cannot sign in, so the code in the address is the credential; every wrong code gets the same answer.
        g.MapGet("/pass/{code}", async (string code, PassService s, CancellationToken ct) => await s.ViewAsync(code, ct) is { } pass ? Results.Ok(pass) : Results.NotFound()).AllowAnonymous();
        g.MapGet("/pass/{code}/qr.svg", async (string code, HttpContext http, PassService s, CancellationToken ct) =>
            {
                var svg = await s.QrFor(code, ct);
                if (svg is null) return Results.NotFound();
                http.Response.Headers.CacheControl = "private, no-store";
                return Results.Text(svg, "image/svg+xml");
            })
            .AllowAnonymous();
    }
}
