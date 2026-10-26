using Fms.BuildingBlocks.Security;
using Fms.Work.Domain;
using Fms.Work.Services;

namespace Fms.Api;

public static class WorkEndpoints
{
    private static readonly string[] Everyone = [Permissions.WorkOrdersRead, Permissions.WorkOrdersRequest];

    public static void MapWork(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/work-orders");

        g.MapGet("", async (WorkOrderService s, string? status, WorkPriority? priority, Guid? assetId, Guid? locationId, Guid? categoryId, bool? mine, bool? assignedToMe,
                bool? openOnly, bool? overdue, bool? slaBreached, string? q, int? page, int? pageSize, CancellationToken ct) =>
            {
                var statuses = new List<WorkOrderStatus>();
                foreach (var part in (status ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!Enum.TryParse<WorkOrderStatus>(part, true, out var parsed) || !Enum.IsDefined(parsed))
                        return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = [$"Unknown status '{part}'."] });
                    statuses.Add(parsed);
                }

                return Results.Ok(await s.ListAsync(new WorkOrderQuery(statuses, priority, assetId, locationId, categoryId, mine ?? false, assignedToMe ?? false,
                    openOnly ?? false, overdue ?? false, slaBreached ?? false, q, page, pageSize), ct));
            })
            .RequireAnyPermission(Everyone);

        g.MapGet("/assignees", async (WorkOrderService s, CancellationToken ct) => Results.Ok(await s.AssigneesAsync(ct)))
            .RequireAnyPermission(Permissions.WorkOrdersAssign, Permissions.WorkOrdersManage);

        // Lets someone who may report a problem find the asset (by typing or by scanning its label) without being able to browse the register.
        g.MapGet("/assets", async (WorkOrderService s, string? q, string? tag, CancellationToken ct) => Results.Ok(await s.FindAssetsAsync(q, tag, ct)))
            .RequirePermission(Permissions.WorkOrdersRequest);

        g.MapGet("/settings", async (WorkSetupService s, CancellationToken ct) => Results.Ok(await s.GetSettingsAsync(ct)))
            .RequireAnyPermission(Everyone);
        g.MapPut("/settings", async (SaveWorkSettingsRequest r, WorkSetupService s, CancellationToken ct) => (await s.SaveSettingsAsync(r, ct)).ToHttp())
            .RequirePermission(Permissions.WorkOrdersManage);

        var sla = g.MapGroup("/sla");
        sla.MapGet("/calendars", async (SlaSetupService s, bool? includeArchived, CancellationToken ct) => Results.Ok(await s.CalendarsAsync(includeArchived ?? false, ct)))
            .RequirePermission(Permissions.WorkOrdersManage);
        sla.MapPost("/calendars", async (SaveCalendarRequest r, SlaSetupService s, CancellationToken ct) =>
                (await s.CreateCalendarAsync(r, ct)).ToHttp(c => Results.Created($"/api/v1/work-orders/sla/calendars/{c.Id}", c)))
            .RequirePermission(Permissions.WorkOrdersManage);
        sla.MapPut("/calendars/{id:guid}", async (Guid id, SaveCalendarRequest r, SlaSetupService s, CancellationToken ct) => (await s.UpdateCalendarAsync(id, r, ct)).ToHttp())
            .RequirePermission(Permissions.WorkOrdersManage);
        sla.MapPost("/calendars/{id:guid}/archive", async (Guid id, RowVersionRequest r, SlaSetupService s, CancellationToken ct) => (await s.SetCalendarArchivedAsync(id, true, r.RowVersion, ct)).ToHttp())
            .RequirePermission(Permissions.WorkOrdersManage);
        sla.MapPost("/calendars/{id:guid}/restore", async (Guid id, RowVersionRequest r, SlaSetupService s, CancellationToken ct) => (await s.SetCalendarArchivedAsync(id, false, r.RowVersion, ct)).ToHttp())
            .RequirePermission(Permissions.WorkOrdersManage);
        sla.MapGet("/policies", async (SlaSetupService s, bool? includeArchived, CancellationToken ct) => Results.Ok(await s.PoliciesAsync(includeArchived ?? false, ct)))
            .RequirePermission(Permissions.WorkOrdersManage);
        sla.MapPost("/policies", async (SaveSlaPolicyRequest r, SlaSetupService s, CancellationToken ct) =>
                (await s.CreatePolicyAsync(r, ct)).ToHttp(p => Results.Created($"/api/v1/work-orders/sla/policies/{p.Id}", p)))
            .RequirePermission(Permissions.WorkOrdersManage);
        sla.MapPut("/policies/{id:guid}", async (Guid id, SaveSlaPolicyRequest r, SlaSetupService s, CancellationToken ct) => (await s.UpdatePolicyAsync(id, r, ct)).ToHttp())
            .RequirePermission(Permissions.WorkOrdersManage);
        sla.MapPost("/policies/{id:guid}/archive", async (Guid id, RowVersionRequest r, SlaSetupService s, CancellationToken ct) => (await s.SetPolicyArchivedAsync(id, true, r.RowVersion, ct)).ToHttp())
            .RequirePermission(Permissions.WorkOrdersManage);
        sla.MapPost("/policies/{id:guid}/restore", async (Guid id, RowVersionRequest r, SlaSetupService s, CancellationToken ct) => (await s.SetPolicyArchivedAsync(id, false, r.RowVersion, ct)).ToHttp())
            .RequirePermission(Permissions.WorkOrdersManage);

        var categories = g.MapGroup("/categories");
        categories.MapGet("", async (WorkSetupService s, bool? includeArchived, CancellationToken ct) => Results.Ok(await s.CategoriesAsync(includeArchived ?? false, ct)))
            .RequireAnyPermission(Everyone);
        categories.MapPost("", async (SaveCategoryRequest r, WorkSetupService s, CancellationToken ct) =>
                (await s.CreateCategoryAsync(r, ct)).ToHttp(c => Results.Created($"/api/v1/work-orders/categories/{c.Id}", c)))
            .RequirePermission(Permissions.WorkOrdersManage);
        categories.MapPut("/{id:guid}", async (Guid id, SaveCategoryRequest r, WorkSetupService s, CancellationToken ct) => (await s.UpdateCategoryAsync(id, r, ct)).ToHttp())
            .RequirePermission(Permissions.WorkOrdersManage);
        categories.MapPost("/{id:guid}/archive", async (Guid id, RowVersionRequest r, WorkSetupService s, CancellationToken ct) => (await s.SetArchivedAsync(id, true, r.RowVersion, ct)).ToHttp())
            .RequirePermission(Permissions.WorkOrdersManage);
        categories.MapPost("/{id:guid}/restore", async (Guid id, RowVersionRequest r, WorkSetupService s, CancellationToken ct) => (await s.SetArchivedAsync(id, false, r.RowVersion, ct)).ToHttp())
            .RequirePermission(Permissions.WorkOrdersManage);

        g.MapGet("/{id:guid}", async (Guid id, WorkOrderService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttp()).RequireAnyPermission(Everyone);
        g.MapPost("", async (CreateWorkOrderRequest r, WorkOrderService s, CancellationToken ct) =>
                (await s.CreateAsync(r, ct)).ToHttp(w => Results.Created($"/api/v1/work-orders/{w.Id}", w)))
            .RequirePermission(Permissions.WorkOrdersRequest);
        g.MapPut("/{id:guid}", async (Guid id, UpdateWorkOrderRequest r, WorkOrderService s, CancellationToken ct) => (await s.UpdateAsync(id, r, ct)).ToHttp())
            .RequireAnyPermission(Permissions.WorkOrdersRequest, Permissions.WorkOrdersManage);

        // The service decides whether this person may do this to this work order now; the endpoint only keeps out people who could never.
        g.MapPost("/{id:guid}/approve", async (Guid id, WorkOrderService s, CancellationToken ct) => (await s.ApproveAsync(id, ct)).ToHttp()).RequirePermission(Permissions.WorkOrdersApprove);
        g.MapPost("/{id:guid}/reject", async (Guid id, ReasonRequest r, WorkOrderService s, CancellationToken ct) => (await s.RejectAsync(id, r.Reason, ct)).ToHttp()).RequirePermission(Permissions.WorkOrdersApprove);
        g.MapPost("/{id:guid}/assign", async (Guid id, AssignRequest r, WorkOrderService s, CancellationToken ct) => (await s.AssignAsync(id, r.AssigneeId, ct)).ToHttp()).RequirePermission(Permissions.WorkOrdersAssign);
        g.MapPost("/{id:guid}/start", async (Guid id, WorkOrderService s, CancellationToken ct) => (await s.StartAsync(id, ct)).ToHttp()).RequireAnyPermission(Permissions.WorkOrdersWork, Permissions.WorkOrdersManage);
        g.MapPost("/{id:guid}/hold", async (Guid id, ReasonRequest r, WorkOrderService s, CancellationToken ct) => (await s.HoldAsync(id, r.Reason, ct)).ToHttp()).RequireAnyPermission(Permissions.WorkOrdersWork, Permissions.WorkOrdersManage);
        g.MapPost("/{id:guid}/resume", async (Guid id, WorkOrderService s, CancellationToken ct) => (await s.ResumeAsync(id, ct)).ToHttp()).RequireAnyPermission(Permissions.WorkOrdersWork, Permissions.WorkOrdersManage);
        g.MapPost("/{id:guid}/complete", async (Guid id, CompleteRequest r, WorkOrderService s, CancellationToken ct) => (await s.CompleteAsync(id, r, ct)).ToHttp()).RequireAnyPermission(Permissions.WorkOrdersWork, Permissions.WorkOrdersManage);
        g.MapPost("/{id:guid}/verify", async (Guid id, WorkOrderService s, CancellationToken ct) => (await s.VerifyAsync(id, ct)).ToHttp()).RequirePermission(Permissions.WorkOrdersVerify);
        g.MapPost("/{id:guid}/close", async (Guid id, WorkOrderService s, CancellationToken ct) => (await s.CloseAsync(id, ct)).ToHttp()).RequireAnyPermission(Permissions.WorkOrdersVerify, Permissions.WorkOrdersManage);
        g.MapPost("/{id:guid}/reopen", async (Guid id, ReasonRequest r, WorkOrderService s, CancellationToken ct) => (await s.ReopenAsync(id, r.Reason, ct)).ToHttp()).RequirePermission(Permissions.WorkOrdersManage);
        g.MapPost("/{id:guid}/cancel", async (Guid id, ReasonRequest r, WorkOrderService s, CancellationToken ct) => (await s.CancelAsync(id, r.Reason, ct)).ToHttp()).RequireAnyPermission(Permissions.WorkOrdersRequest, Permissions.WorkOrdersManage);
        g.MapPost("/{id:guid}/comments", async (Guid id, CommentRequest r, WorkOrderService s, CancellationToken ct) =>
                (await s.AddCommentAsync(id, r, ct)).ToHttp(c => Results.Created($"/api/v1/work-orders/{id}", c)))
            .RequireAnyPermission(Everyone);
    }
}

public static class NotificationEndpoints
{
    /// <summary>A person's own notices. Any signed-in person has them; every query is for the signed-in user only.</summary>
    public static void MapNotifications(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/notifications").RequireAuthorization();
        g.MapGet("", async (Fms.Platform.Notifications.NotificationService s, bool? unreadOnly, int? page, int? pageSize, CancellationToken ct) =>
            Results.Ok(await s.ListAsync(unreadOnly ?? false, page, pageSize, ct)));
        g.MapGet("/unread-count", async (Fms.Platform.Notifications.NotificationService s, CancellationToken ct) => Results.Ok(new { count = await s.UnreadCountAsync(ct) }));
        g.MapPost("/{id:guid}/read", async (Guid id, Fms.Platform.Notifications.NotificationService s, CancellationToken ct) => await s.MarkReadAsync(id, ct) ? Results.NoContent() : Results.NotFound());
        g.MapPost("/read-all", async (Fms.Platform.Notifications.NotificationService s, CancellationToken ct) => Results.Ok(new { marked = await s.MarkAllReadAsync(ct) }));
    }
}
