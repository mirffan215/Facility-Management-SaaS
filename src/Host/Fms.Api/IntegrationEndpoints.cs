using Fms.BuildingBlocks.Integration;
using Fms.BuildingBlocks.Security;
using Fms.Integration.Domain;
using Fms.Integration.Services;

namespace Fms.Api;

public static class IntegrationEndpoints
{
    public static void MapIntegration(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/integration/erp");
        g.MapGet("/settings", async (ErpSettingsService s, CancellationToken ct) => Results.Ok(await s.GetAsync(ct))).RequirePermission(Permissions.IntegrationRead);
        g.MapPut("/settings", async (SaveErpSettingsRequest r, ErpSettingsService s, CancellationToken ct) => (await s.SaveAsync(r, ct)).ToHttp()).RequirePermission(Permissions.IntegrationManage);
        g.MapGet("/summary", async (ErpAdminService s, CancellationToken ct) => Results.Ok(await s.SummaryAsync(ct))).RequirePermission(Permissions.IntegrationRead);
        g.MapGet("/items", async (ErpAdminService s, ExportStatus? status, ErpEntity? entity, bool? problemsOnly, int? page, int? pageSize, CancellationToken ct) =>
            Results.Ok(await s.ListAsync(status, entity, problemsOnly ?? false, page, pageSize, ct))).RequirePermission(Permissions.IntegrationRead);
        g.MapPost("/items/{id:guid}/retry", async (Guid id, ErpAdminService s, CancellationToken ct) => (await s.RetryAsync(id, ct)).ToHttp()).RequirePermission(Permissions.IntegrationManage);
        g.MapPost("/reconcile", async (ErpAdminService s, CancellationToken ct) => (await s.ReconcileAsync(ct)).ToHttp()).RequirePermission(Permissions.IntegrationManage);
    }
}
