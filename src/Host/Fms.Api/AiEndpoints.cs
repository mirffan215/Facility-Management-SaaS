using Fms.Ai.Services;
using Fms.BuildingBlocks.Security;

namespace Fms.Api;

public static class AiEndpoints
{
    public static void MapAi(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/ai");

        g.MapGet("/status", async (AiSettingsService s, CancellationToken ct) => Results.Ok(await s.StatusAsync(ct))).RequireAnyPermission(Permissions.AiUse, Permissions.AiManage);
        g.MapGet("/settings", async (AiSettingsService s, CancellationToken ct) => Results.Ok(await s.GetAsync(ct))).RequirePermission(Permissions.AiManage);
        g.MapPut("/settings", async (SaveAiSettingsRequest r, AiSettingsService s, CancellationToken ct) => (await s.SaveAsync(r, ct)).ToHttp()).RequirePermission(Permissions.AiManage);
        g.MapGet("/usage", async (AiSettingsService s, CancellationToken ct) => Results.Ok(await s.UsageAsync(ct))).RequirePermission(Permissions.AiManage);
        g.MapPost("/triage", async (TriageRequest r, AiAssistService s, CancellationToken ct) => (await s.TriageAsync(r, ct)).ToHttp()).RequirePermission(Permissions.AiUse);
        g.MapPost("/work-orders/{id:guid}/summary", async (Guid id, AiAssistService s, CancellationToken ct) => (await s.SummariseAsync(id, ct)).ToHttp()).RequirePermission(Permissions.AiUse);
    }
}
