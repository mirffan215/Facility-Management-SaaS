using Fms.BuildingBlocks.Security;
using Fms.Inventory.Domain;
using Fms.Inventory.Services;

namespace Fms.Api;

public static class InventoryEndpoints
{
    public static void MapInventory(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/inventory");

        g.MapGet("/stores", async (CatalogService s, bool? includeArchived, CancellationToken ct) => Results.Ok(await s.StoresAsync(includeArchived ?? false, ct))).RequirePermission(Permissions.InventoryRead);
        g.MapPost("/stores", async (SaveStoreRequest r, CatalogService s, CancellationToken ct) => (await s.SaveStoreAsync(null, r, ct)).ToHttp(x => Results.Created($"/api/v1/inventory/stores/{x.Id}", x))).RequirePermission(Permissions.InventoryManage);
        g.MapPut("/stores/{id:guid}", async (Guid id, SaveStoreRequest r, CatalogService s, CancellationToken ct) => (await s.SaveStoreAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.InventoryManage);
        g.MapPost("/stores/{id:guid}/archive", async (Guid id, RowVersionRequest r, CatalogService s, CancellationToken ct) => (await s.SetStoreArchivedAsync(id, true, r.RowVersion, ct)).ToHttp()).RequirePermission(Permissions.InventoryManage);
        g.MapPost("/stores/{id:guid}/restore", async (Guid id, RowVersionRequest r, CatalogService s, CancellationToken ct) => (await s.SetStoreArchivedAsync(id, false, r.RowVersion, ct)).ToHttp()).RequirePermission(Permissions.InventoryManage);

        g.MapGet("/items", async (CatalogService s, string? q, bool? includeArchived, int? page, int? pageSize, CancellationToken ct) => Results.Ok(await s.ItemsAsync(q, includeArchived ?? false, page, pageSize, ct))).RequirePermission(Permissions.InventoryRead);
        g.MapGet("/items/{id:guid}", async (Guid id, CatalogService s, CancellationToken ct) => (await s.GetItemAsync(id, ct)).ToHttp()).RequirePermission(Permissions.InventoryRead);
        g.MapGet("/items/{id:guid}/stock", async (Guid id, StockService s, CancellationToken ct) => (await s.LevelsOfItemAsync(id, ct)).ToHttp()).RequirePermission(Permissions.InventoryRead);
        g.MapPost("/items", async (SaveItemRequest r, CatalogService s, CancellationToken ct) => (await s.SaveItemAsync(null, r, ct)).ToHttp(x => Results.Created($"/api/v1/inventory/items/{x.Id}", x))).RequirePermission(Permissions.InventoryManage);
        g.MapPut("/items/{id:guid}", async (Guid id, SaveItemRequest r, CatalogService s, CancellationToken ct) => (await s.SaveItemAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.InventoryManage);
        g.MapPost("/items/{id:guid}/archive", async (Guid id, RowVersionRequest r, CatalogService s, CancellationToken ct) => (await s.SetItemArchivedAsync(id, true, r.RowVersion, ct)).ToHttp()).RequirePermission(Permissions.InventoryManage);
        g.MapPost("/items/{id:guid}/restore", async (Guid id, RowVersionRequest r, CatalogService s, CancellationToken ct) => (await s.SetItemArchivedAsync(id, false, r.RowVersion, ct)).ToHttp()).RequirePermission(Permissions.InventoryManage);

        g.MapGet("/stock", async (StockService s, Guid? storeId, Guid? itemId, string? q, bool? lowOnly, bool? includeZero, int? page, int? pageSize, CancellationToken ct) =>
            Results.Ok(await s.LevelsAsync(storeId, itemId, q, lowOnly ?? false, includeZero ?? false, page, pageSize, ct))).RequirePermission(Permissions.InventoryRead);
        g.MapGet("/movements", async (StockService s, Guid? storeId, Guid? itemId, Guid? workOrderId, MovementType? type, int? page, int? pageSize, CancellationToken ct) =>
            Results.Ok(await s.MovementsAsync(storeId, itemId, workOrderId, type, page, pageSize, ct))).RequirePermission(Permissions.InventoryRead);
        g.MapPut("/stock/levels", async (SetLevelRequest r, StockService s, CancellationToken ct) => (await s.SetLevelAsync(r, ct)).ToHttp()).RequirePermission(Permissions.InventoryManage);
        g.MapPost("/stock/receive", async (ReceiveRequest r, StockService s, CancellationToken ct) => (await s.ReceiveAsync(r, ct)).ToHttp()).RequirePermission(Permissions.InventoryManage);
        g.MapPost("/stock/transfer", async (TransferRequest r, StockService s, CancellationToken ct) => (await s.TransferAsync(r, ct)).ToHttp()).RequirePermission(Permissions.InventoryManage);
        g.MapPost("/stock/adjust", async (AdjustRequest r, StockService s, CancellationToken ct) => (await s.AdjustAsync(r, ct)).ToHttp()).RequirePermission(Permissions.InventoryApprove);

        // Parts a preventive-maintenance template needs; reserved when its visits are created.
        g.MapGet("/pm-templates/{templateId:guid}/parts", async (Guid templateId, CatalogService s, CancellationToken ct) => Results.Ok(await s.PmPartsAsync(templateId, ct))).RequirePermission(Permissions.InventoryRead);
        g.MapPut("/pm-templates/{templateId:guid}/parts", async (Guid templateId, SavePmPartsRequest r, CatalogService s, CancellationToken ct) => (await s.SavePmPartsAsync(templateId, r, ct)).ToHttp()).RequirePermission(Permissions.InventoryManage);

        // Parts on a work order.
        var wo = v1.MapGroup("/work-orders/{workOrderId:guid}/parts");
        wo.MapGet("", async (Guid workOrderId, WorkPartsService s, CancellationToken ct) => (await s.PartsAsync(workOrderId, ct)).ToHttp()).RequirePermission(Permissions.InventoryRead);
        wo.MapPost("/reserve", async (Guid workOrderId, PartRequest r, WorkPartsService s, CancellationToken ct) => (await s.ReserveAsync(workOrderId, r, ct)).ToHttp()).RequirePermission(Permissions.InventoryManage);
        wo.MapPost("/release", async (Guid workOrderId, PartRequest r, WorkPartsService s, CancellationToken ct) => (await s.ReleaseAsync(workOrderId, r, ct)).ToHttp()).RequirePermission(Permissions.InventoryManage);
        wo.MapPost("/issue", async (Guid workOrderId, PartRequest r, WorkPartsService s, CancellationToken ct) => (await s.IssueAsync(workOrderId, r, ct)).ToHttp()).RequirePermission(Permissions.InventoryIssue);
        wo.MapPost("/return", async (Guid workOrderId, PartRequest r, WorkPartsService s, CancellationToken ct) => (await s.ReturnAsync(workOrderId, r, ct)).ToHttp()).RequirePermission(Permissions.InventoryIssue);
    }
}
