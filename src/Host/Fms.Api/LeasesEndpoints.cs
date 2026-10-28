using Fms.BuildingBlocks.Security;
using Fms.Leases.Domain;
using Fms.Leases.Services;

namespace Fms.Api;

public static class LeasesEndpoints
{
    public static void MapLeases(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/leases");

        g.MapGet("", async (LeaseService s, LeaseState? state, LeaseKind? kind, string? q, int? page, int? pageSize, CancellationToken ct) => Results.Ok(await s.ListAsync(state, kind, q, page, pageSize, ct))).RequirePermission(Permissions.LeasesRead);
        g.MapGet("/summary", async (LeaseService s, CancellationToken ct) => Results.Ok(await s.SummaryAsync(ct))).RequirePermission(Permissions.LeasesRead);
        g.MapGet("/{id:guid}", async (Guid id, LeaseService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttp()).RequirePermission(Permissions.LeasesRead);
        g.MapGet("/{id:guid}/schedule", async (Guid id, LeaseService s, CancellationToken ct) => (await s.ScheduleAsync(id, ct)).ToHttp()).RequirePermission(Permissions.LeasesRead);
        g.MapPost("", async (SaveLeaseRequest r, LeaseService s, CancellationToken ct) => (await s.CreateAsync(r, ct)).ToHttp(x => Results.Created($"/api/v1/leases/{x.Id}", x))).RequirePermission(Permissions.LeasesManage);
        g.MapPut("/{id:guid}", async (Guid id, SaveLeaseRequest r, LeaseService s, CancellationToken ct) => (await s.UpdateAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.LeasesManage);
        g.MapPost("/{id:guid}/activate", async (Guid id, ActivateRequest r, LeaseService s, CancellationToken ct) => (await s.ActivateAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.LeasesManage);
        g.MapPost("/{id:guid}/extend", async (Guid id, ExtendRequest r, LeaseService s, CancellationToken ct) => (await s.ExtendAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.LeasesManage);
        g.MapPost("/{id:guid}/terminate", async (Guid id, TerminateRequest r, LeaseService s, CancellationToken ct) => (await s.TerminateAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.LeasesManage);
        g.MapPost("/{id:guid}/delete", async (Guid id, DeleteRequest r, LeaseService s, CancellationToken ct) => (await s.DeleteAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.LeasesManage);
    }
}
