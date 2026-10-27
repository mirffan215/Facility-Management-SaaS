using System.Text;
using Fms.BuildingBlocks.Security;
using Fms.BuildingBlocks.Reporting;
using Fms.Reporting.Services;

namespace Fms.Api;

public static class ReportingEndpoints
{
    public static void MapReporting(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/reports");
        g.MapGet("/dashboard", async (ReportService s, DateOnly? from, DateOnly? to, Guid? siteId, CancellationToken ct) => (await s.DashboardAsync(from, to, siteId, ct)).ToHttp()).RequirePermission(Permissions.ReportsRead);
        g.MapGet("/export.csv", async (ReportService s, HttpContext http, DateOnly? from, DateOnly? to, Guid? siteId, CancellationToken ct) =>
            {
                var result = await s.DashboardAsync(from, to, siteId, ct);
                if (!result.IsOk) return result.ToHttp();
                http.Response.Headers.CacheControl = "private, no-store";
                return Results.File(Encoding.UTF8.GetBytes(ReportService.ToCsv(result.Value!)), "text/csv; charset=utf-8", $"kpi-{result.Value!.From:yyyyMMdd}-{result.Value.To:yyyyMMdd}.csv");
            })
            .RequirePermission(Permissions.ReportsRead);

        // Access tokens for outside tools. The secret is returned once, when the token is made.
        var f = g.MapGroup("/feed");
        f.MapGet("/datasets", (FeedService s) => Results.Ok(s.Offered())).RequirePermission(Permissions.ReportsManage);
        f.MapGet("/tokens", async (FeedService s, CancellationToken ct) => Results.Ok(await s.ListAsync(ct))).RequirePermission(Permissions.ReportsManage);
        f.MapPost("/tokens", async (CreateFeedTokenRequest r, FeedService s, CancellationToken ct) => (await s.CreateAsync(r, ct)).ToHttp(x => Results.Created("/api/v1/reports/feed/tokens", x))).RequirePermission(Permissions.ReportsManage);
        f.MapPost("/tokens/{id:guid}/revoke", async (Guid id, FeedService s, CancellationToken ct) => (await s.RevokeAsync(id, ct)).ToHttp()).RequirePermission(Permissions.ReportsManage);

        // The feed itself: read-only OData for Power BI and Excel. It cannot sign in, so the token (Basic password or X-Api-Key) is the credential and every bad one gets the same 401.
        var o = v1.MapGroup("/odata");
        o.MapGet("", async (HttpContext http, FeedService s, CancellationToken ct) =>
            await FeedAsync(http, s, ct, (access, sets) => Task.FromResult(Results.Text(ODataService.ServiceDocument(BaseUrl(http), sets), "application/json; odata.metadata=minimal")))).AllowAnonymous();
        o.MapGet("/$metadata", async (HttpContext http, FeedService s, CancellationToken ct) =>
            await FeedAsync(http, s, ct, (access, sets) => Task.FromResult(Results.Text(ODataService.Metadata(sets), "application/xml")))).AllowAnonymous();
        o.MapGet("/{set}", async (string set, HttpContext http, FeedService s, CancellationToken ct) =>
            await FeedAsync(http, s, ct, async (access, sets) =>
            {
                var dataset = sets.FirstOrDefault(d => d.Name.Equals(set, StringComparison.OrdinalIgnoreCase));
                if (dataset is null) return Results.NotFound();
                var query = http.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString());
                var page = await ODataService.ReadAsync(dataset, BaseUrl(http), query, ct);
                return Results.Text(page.Json, "application/json; odata.metadata=minimal", statusCode: page.Status);
            })).AllowAnonymous();
    }

    private static string BaseUrl(HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}/api/v1/odata";

    /// <summary>Checks the token, then runs the request as that tenant over the datasets the token may read (and the caller's modules provide).</summary>
    private static async Task<IResult> FeedAsync(HttpContext http, FeedService s, CancellationToken ct, Func<FeedAccess, IReadOnlyList<IDatasetProvider>, Task<IResult>> run)
    {
        var access = await s.AuthenticateAsync(http.Request.Headers.Authorization.ToString(), http.Request.Headers["X-Api-Key"].ToString(), ct);
        if (access is null)
        {
            http.Response.Headers.WWWAuthenticate = "Basic realm=\"FMS data feed\"";
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        http.Response.Headers.CacheControl = "private, no-store";
        http.Response.Headers["OData-Version"] = "4.0";
        var sets = s.AllDatasets.Where(d => access.Datasets is null || access.Datasets.Contains(d.Name)).ToList();
        return await run(access, sets);
    }
}
