using Fms.BuildingBlocks.Security;
using Fms.Work.Services;

namespace Fms.Api;

public static class PmEndpoints
{
    public static void MapPm(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/pm");

        g.MapGet("/templates", async (PmService s, bool? includeArchived, CancellationToken ct) => Results.Ok(await s.TemplatesAsync(includeArchived ?? false, ct))).RequirePermission(Permissions.PmRead);
        g.MapPost("/templates", async (SavePmTemplateRequest r, PmService s, CancellationToken ct) => (await s.SaveTemplateAsync(null, r, ct)).ToHttp(t => Results.Created($"/api/v1/pm/templates/{t.Id}", t))).RequirePermission(Permissions.PmManage);
        g.MapPut("/templates/{id:guid}", async (Guid id, SavePmTemplateRequest r, PmService s, CancellationToken ct) => (await s.SaveTemplateAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.PmManage);
        g.MapPost("/templates/{id:guid}/archive", async (Guid id, RowVersionRequest r, PmService s, CancellationToken ct) => (await s.SetTemplateArchivedAsync(id, true, r.RowVersion, ct)).ToHttp()).RequirePermission(Permissions.PmManage);
        g.MapPost("/templates/{id:guid}/restore", async (Guid id, RowVersionRequest r, PmService s, CancellationToken ct) => (await s.SetTemplateArchivedAsync(id, false, r.RowVersion, ct)).ToHttp()).RequirePermission(Permissions.PmManage);

        g.MapGet("/schedules", async (PmService s, bool? includeArchived, CancellationToken ct) => Results.Ok(await s.SchedulesAsync(includeArchived ?? false, ct))).RequirePermission(Permissions.PmRead);
        g.MapPost("/schedules", async (SavePmScheduleRequest r, PmService s, CancellationToken ct) => (await s.SaveScheduleAsync(null, r, ct)).ToHttp(x => Results.Created($"/api/v1/pm/schedules/{x.Id}", x))).RequirePermission(Permissions.PmManage);
        g.MapPut("/schedules/{id:guid}", async (Guid id, SavePmScheduleRequest r, PmService s, CancellationToken ct) => (await s.SaveScheduleAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.PmManage);
        g.MapPost("/schedules/{id:guid}/archive", async (Guid id, RowVersionRequest r, PmService s, CancellationToken ct) => (await s.SetScheduleArchivedAsync(id, true, r.RowVersion, ct)).ToHttp()).RequirePermission(Permissions.PmManage);
        g.MapPost("/schedules/{id:guid}/restore", async (Guid id, RowVersionRequest r, PmService s, CancellationToken ct) => (await s.SetScheduleArchivedAsync(id, false, r.RowVersion, ct)).ToHttp()).RequirePermission(Permissions.PmManage);
        g.MapGet("/schedules/{id:guid}/plans", async (Guid id, PmService s, CancellationToken ct) => (await s.PlansAsync(id, ct)).ToHttp()).RequirePermission(Permissions.PmRead);

        g.MapGet("/forecast", async (PmService s, DateOnly? from, DateOnly? to, Guid? assetClassId, Guid? locationId, CancellationToken ct) => (await s.ForecastAsync(from, to, assetClassId, locationId, ct)).ToHttp())
            .RequirePermission(Permissions.PmRead);

        g.MapGet("/compliance", async (PmService s, DateOnly? from, DateOnly? to, Guid? assetClassId, CancellationToken ct) => (await s.ComplianceAsync(from, to, assetClassId, ct)).ToHttp())
            .RequirePermission(Permissions.PmRead);

        // The worker does this on a timer; this lets a manager do it now (after changing a schedule, or to see what would be created).
        g.MapPost("/run", async (PmEngine engine, CancellationToken ct) => Results.Ok(await engine.RunAsync(ct))).RequirePermission(Permissions.PmManage);
    }
}
