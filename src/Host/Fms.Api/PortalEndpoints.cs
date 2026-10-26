using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Security;
using Fms.BuildingBlocks.Tenancy;
using Fms.Platform.Services;
using Fms.Purchasing.Services;
using Fms.Vendors.Services;
using Fms.Work.Services;

namespace Fms.Api;

public sealed record AcceptInvitationRequest(string? Token);

public sealed record AcceptedDto(string Kind, Guid TargetId);

/// <summary>
/// The vendor portal. Every route here needs <c>portal.use</c>, which only the Vendor role carries, and every query behind it is filtered by the signed-in person's vendor
/// (never by anything in the request). The one route outside that is the acceptance of an invitation, for someone who is signed in but belongs to no tenant yet.
/// </summary>
public static class PortalEndpoints
{
    public static void MapPortal(this RouteGroupBuilder v1)
    {
        // Signed in, but no tenant needed (the invitation says which). Not covered by the "must have a tenant" default, and not open to anonymous callers.
        v1.MapPost("/portal/invitations/accept", async (AcceptInvitationRequest r, HttpContext http, GuestAccessService guests, PortalAdminService admin, TenantContext tenant, CancellationToken ct) =>
            {
                var provider = http.User.FindFirst(FmsClaims.Provider)?.Value;
                var subject = http.User.FindFirst("sub")?.Value;
                if (provider is null || subject is null) return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Sign in first.");

                var email = http.User.FindFirst("email")?.Value ?? http.User.FindFirst("preferred_username")?.Value;
                var accepted = await guests.AcceptAsync(r.Token, provider, subject, email, http.User.FindFirst("name")?.Value, tenant.TenantId, ct);
                if (!accepted.IsOk) return accepted.ToHttp();

                // Now that the tenant is known, record which vendor the person belongs to. Safe to repeat if this step failed the first time.
                if (tenant.TenantId is null) tenant.SetTenant(accepted.Value!.TenantId);
                if (accepted.Value!.Kind == GuestKinds.Vendor)
                {
                    var linked = await admin.LinkUserAsync(accepted.Value.UserId, accepted.Value.TargetId, ct);
                    if (!linked.IsOk) return linked.ToHttp();
                }

                return Results.Ok(new AcceptedDto(accepted.Value.Kind, accepted.Value.TargetId));
            })
            .RequireAuthorization(p => p.RequireAuthenticatedUser());

        var g = v1.MapGroup("/portal");
        g.MapGet("/me", async (PortalService s, CancellationToken ct) => (await s.MeAsync(ct)).ToHttp()).RequirePermission(Permissions.PortalUse);

        g.MapGet("/contracts", async (PortalService s, CancellationToken ct) => (await s.ContractsAsync(ct)).ToHttp()).RequirePermission(Permissions.PortalUse);
        g.MapGet("/contracts/{id:guid}/documents", async (Guid id, PortalService s, CancellationToken ct) => (await s.DocumentsAsync(id, ct)).ToHttp()).RequirePermission(Permissions.PortalUse);
        g.MapGet("/contract-documents/{id:guid}/download", async (Guid id, HttpContext http, PortalService s, CancellationToken ct) =>
            {
                var result = await s.DownloadAsync(id, ct);
                if (!result.IsOk) return result.ToHttp();
                http.Response.Headers.CacheControl = "private, no-store";
                http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
                return Results.File(result.Value!.Content, result.Value.Document.ContentType, result.Value.Document.FileName);
            })
            .RequirePermission(Permissions.PortalUse);

        g.MapGet("/orders", async (PortalOrderService s, CancellationToken ct) => (await s.ListAsync(ct)).ToHttp()).RequirePermission(Permissions.PortalUse);
        g.MapGet("/orders/{id:guid}", async (Guid id, PortalOrderService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttp()).RequirePermission(Permissions.PortalUse);
        g.MapPost("/orders/{id:guid}/acknowledge", async (Guid id, PortalOrderService s, CancellationToken ct) => (await s.AcknowledgeAsync(id, ct)).ToHttp()).RequirePermission(Permissions.PortalUse);

        g.MapGet("/work-orders", async (PortalWorkService s, CancellationToken ct) => (await s.ListAsync(ct)).ToHttp()).RequirePermission(Permissions.PortalUse);
        g.MapGet("/work-orders/{id:guid}", async (Guid id, PortalWorkService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttp()).RequirePermission(Permissions.PortalUse);
        g.MapPost("/work-orders/{id:guid}/comments", async (Guid id, PortalCommentRequest r, PortalWorkService s, CancellationToken ct) => (await s.AddCommentAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.PortalUse);
    }
}
