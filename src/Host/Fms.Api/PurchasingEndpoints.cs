using Fms.BuildingBlocks.Security;
using Fms.Purchasing.Domain;
using Fms.Purchasing.Services;

namespace Fms.Api;

public static class PurchasingEndpoints
{
    private static readonly string[] Everyone = [Permissions.PurchasingRead, Permissions.PurchasingRequest];
    private static readonly string[] Deciders = [Permissions.PurchasingApprove1, Permissions.PurchasingApprove2, Permissions.PurchasingApprove3];

    public static void MapPurchasing(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/purchasing");

        g.MapGet("/settings", async (SettingsService s, CancellationToken ct) => Results.Ok(await s.GetAsync(ct))).RequireAnyPermission(Everyone);
        g.MapPut("/settings", async (SaveSettingsRequest r, SettingsService s, CancellationToken ct) => (await s.SaveAsync(r, ct)).ToHttp()).RequirePermission(Permissions.PurchasingManage);

        // Requests: anyone who may request or read; people without purchasing.read see only their own.
        g.MapGet("/requests", async (RequestService s, RequestStatus? status, bool? awaitingMe, bool? mine, string? q, int? page, int? pageSize, CancellationToken ct) =>
            Results.Ok(await s.ListAsync(status, awaitingMe ?? false, mine ?? false, q, page, pageSize, ct))).RequireAnyPermission(Everyone.Concat(Deciders).ToArray());
        g.MapGet("/requests/{id:guid}", async (Guid id, RequestService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttp()).RequireAnyPermission(Everyone.Concat(Deciders).ToArray());
        g.MapPost("/requests", async (SaveRequestRequest r, RequestService s, CancellationToken ct) => (await s.SaveAsync(null, r, ct)).ToHttp(x => Results.Created($"/api/v1/purchasing/requests/{x.Id}", x))).RequirePermission(Permissions.PurchasingRequest);
        g.MapPut("/requests/{id:guid}", async (Guid id, SaveRequestRequest r, RequestService s, CancellationToken ct) => (await s.SaveAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.PurchasingRequest);
        g.MapPost("/requests/{id:guid}/submit", async (Guid id, RequestService s, CancellationToken ct) => (await s.SubmitAsync(id, ct)).ToHttp()).RequirePermission(Permissions.PurchasingRequest);
        g.MapPost("/requests/{id:guid}/cancel", async (Guid id, DecisionRequest d, RequestService s, CancellationToken ct) => (await s.CancelAsync(id, d, ct)).ToHttp()).RequireAnyPermission(Everyone);
        g.MapPost("/requests/{id:guid}/approve", async (Guid id, DecisionRequest d, RequestService s, CancellationToken ct) => (await s.ApproveAsync(id, d, ct)).ToHttp()).RequireAnyPermission(Deciders);
        g.MapPost("/requests/{id:guid}/reject", async (Guid id, DecisionRequest d, RequestService s, CancellationToken ct) => (await s.RejectAsync(id, d, ct)).ToHttp()).RequireAnyPermission(Deciders);
        g.MapPost("/requests/{id:guid}/order", async (Guid id, OrderFromRequestRequest r, OrderService s, CancellationToken ct) => (await s.OrderFromRequestAsync(id, r, ct)).ToHttp(x => Results.Created($"/api/v1/purchasing/orders/{x.Id}", x))).RequirePermission(Permissions.PurchasingOrder);

        // Orders.
        g.MapGet("/orders", async (OrderService s, OrderStatus? status, Guid? vendorId, string? q, int? page, int? pageSize, CancellationToken ct) =>
            Results.Ok(await s.ListAsync(status, vendorId, q, page, pageSize, ct))).RequirePermission(Permissions.PurchasingRead);
        g.MapGet("/orders/{id:guid}", async (Guid id, OrderService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttp()).RequirePermission(Permissions.PurchasingRead);
        g.MapPost("/orders/{id:guid}/acknowledge", async (Guid id, OrderService s, CancellationToken ct) => (await s.AcknowledgeAsync(id, ct)).ToHttp()).RequirePermission(Permissions.PurchasingOrder);
        g.MapPost("/orders/{id:guid}/cancel", async (Guid id, CancelOrderRequest r, OrderService s, CancellationToken ct) => (await s.CancelAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.PurchasingOrder);
        g.MapPost("/orders/{id:guid}/receive", async (Guid id, ReceiveRequest r, OrderService s, CancellationToken ct) => (await s.ReceiveAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.PurchasingReceive);
    }
}
