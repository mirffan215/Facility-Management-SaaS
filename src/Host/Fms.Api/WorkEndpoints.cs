using Fms.BuildingBlocks.Security;
using Fms.Work.Domain;
using Fms.Work.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace Fms.Api;

public static class WorkEndpoints
{
    private static readonly string[] Everyone = [Permissions.WorkOrdersRead, Permissions.WorkOrdersRequest];

    /// <summary>When the person says they did it (an app that was offline reports its own clock). The service limits how far this is trusted.</summary>
    private static DateTimeOffset? PerformedAt(HttpContext http) =>
        http.Request.Headers.TryGetValue("X-Performed-At", out var v) && DateTimeOffset.TryParse(v.ToString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    /// <summary>Files are only ever served as downloads, never rendered in the app's origin.</summary>
    private static async Task<IResult> ServeAsync(HttpContext http, Fms.BuildingBlocks.Results.Result<DownloadedFile> result)
    {
        if (!result.IsOk) return result.ToHttp();
        http.Response.Headers.CacheControl = "private, no-store";
        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        await Task.CompletedTask;
        return Results.File(result.Value!.Content, result.Value.ContentType, result.Value.FileName);
    }

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

        // A technician's phone downloads this to work without a connection (see ADR-0013).
        g.MapGet("/offline/jobs", async (OfflineJobsService s, CancellationToken ct) => Results.Ok(await s.JobsAsync(ct)))
            .RequirePermission(Permissions.WorkOrdersWork);

        g.MapGet("/{id:guid}/vendor", async (Guid id, WorkOrderService s, CancellationToken ct) => (await s.VendorAssignmentAsync(id, ct)).ToHttp(x => x is null ? Results.NoContent() : Results.Ok(x))).RequirePermission(Permissions.WorkOrdersRead);
        g.MapPost("/{id:guid}/vendor", async (Guid id, AssignVendorRequest r, WorkOrderService s, CancellationToken ct) => (await s.AssignVendorAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.WorkOrdersAssign);
        g.MapPost("/{id:guid}/vendor/clear", async (Guid id, WorkOrderService s, CancellationToken ct) => (await s.ClearVendorAsync(id, ct)).ToHttp()).RequirePermission(Permissions.WorkOrdersAssign);
        g.MapGet("/assignees", async (WorkOrderService s, CancellationToken ct) => Results.Ok(await s.AssigneesAsync(ct)))
            .RequireAnyPermission(Permissions.WorkOrdersAssign, Permissions.WorkOrdersManage);

        // Lets someone who may report a problem find the asset (by typing or by scanning its label) without being able to browse the register.
        g.MapGet("/assets", async (WorkOrderService s, string? q, string? tag, CancellationToken ct) => Results.Ok(await s.FindAssetsAsync(q, tag, ct)))
            .RequirePermission(Permissions.WorkOrdersRequest);

        g.MapGet("/settings", async (WorkSetupService s, CancellationToken ct) => Results.Ok(await s.GetSettingsAsync(ct)))
            .RequireAnyPermission(Everyone);
        g.MapPut("/settings", async (SaveWorkSettingsRequest r, WorkSetupService s, CancellationToken ct) => (await s.SaveSettingsAsync(r, ct)).ToHttp())
            .RequirePermission(Permissions.WorkOrdersManage);

        // The workflow designer: rules inside fixed safety limits, versioned and audited. Anyone who raises or sees work may read the rules; changing them needs workorders.manage.
        g.MapGet("/workflow", async (WorkflowDesigner s, CancellationToken ct) => Results.Ok(await s.GetAsync(ct))).RequireAnyPermission(Everyone);
        g.MapGet("/workflow/history", async (WorkflowDesigner s, CancellationToken ct) => Results.Ok(await s.HistoryAsync(ct))).RequirePermission(Permissions.WorkOrdersManage);
        g.MapPost("/workflow/publish", async (PublishWorkflowRequest r, WorkflowDesigner s, CancellationToken ct) => (await s.PublishAsync(r, ct)).ToHttp()).RequirePermission(Permissions.WorkOrdersManage);
        g.MapPost("/workflow/restore", async (RestoreWorkflowRequest r, WorkflowDesigner s, CancellationToken ct) => (await s.RestoreAsync(r, ct)).ToHttp()).RequirePermission(Permissions.WorkOrdersManage);

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

        var templates = g.MapGroup("/checklists");
        templates.MapGet("", async (ChecklistTemplateService s, bool? includeArchived, CancellationToken ct) => Results.Ok(await s.ListAsync(includeArchived ?? false, ct)))
            .RequireAnyPermission(Permissions.WorkOrdersRead, Permissions.WorkOrdersManage);
        templates.MapPost("", async (SaveChecklistTemplateRequest r, ChecklistTemplateService s, CancellationToken ct) =>
                (await s.CreateAsync(r, ct)).ToHttp(t => Results.Created($"/api/v1/work-orders/checklists/{t.Id}", t)))
            .RequirePermission(Permissions.WorkOrdersManage);
        templates.MapPut("/{id:guid}", async (Guid id, SaveChecklistTemplateRequest r, ChecklistTemplateService s, CancellationToken ct) => (await s.UpdateAsync(id, r, ct)).ToHttp())
            .RequirePermission(Permissions.WorkOrdersManage);
        templates.MapPost("/{id:guid}/archive", async (Guid id, RowVersionRequest r, ChecklistTemplateService s, CancellationToken ct) => (await s.SetArchivedAsync(id, true, r.RowVersion, ct)).ToHttp())
            .RequirePermission(Permissions.WorkOrdersManage);
        templates.MapPost("/{id:guid}/restore", async (Guid id, RowVersionRequest r, ChecklistTemplateService s, CancellationToken ct) => (await s.SetArchivedAsync(id, false, r.RowVersion, ct)).ToHttp())
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

        g.MapGet("/{id:guid}/suggestions", async (Guid id, AssignmentSuggestionService s, CancellationToken ct) => (await s.SuggestAsync(id, ct)).ToHttp()).RequirePermission(Permissions.WorkOrdersAssign);
        g.MapGet("/similar", async (SimilarRequestService s, Guid? locationId, Guid? assetId, string? title, CancellationToken ct) => Results.Ok(await s.FindAsync(locationId, assetId, title, ct))).RequirePermission(Permissions.WorkOrdersRead);
        g.MapGet("/predictions", async (PredictiveService s, Guid? assetId, CancellationToken ct) => Results.Ok(await s.ListAsync(assetId, ct))).RequireAnyPermission(Permissions.WorkOrdersManage, Permissions.WorkOrdersAssign);
        g.MapGet("/repeat-problems", async (RepeatProblemService s, int? days, int? minimum, CancellationToken ct) => Results.Ok(await s.ListAsync(days, minimum, ct))).RequireAnyPermission(Permissions.WorkOrdersManage, Permissions.WorkOrdersAssign);
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
        g.MapPost("/{id:guid}/start", async (Guid id, HttpContext http, WorkOrderService s, CancellationToken ct) => (await s.StartAsync(id, PerformedAt(http), ct)).ToHttp()).RequireAnyPermission(Permissions.WorkOrdersWork, Permissions.WorkOrdersManage);
        g.MapPost("/{id:guid}/hold", async (Guid id, ReasonRequest r, HttpContext http, WorkOrderService s, CancellationToken ct) => (await s.HoldAsync(id, r.Reason, PerformedAt(http), ct)).ToHttp()).RequireAnyPermission(Permissions.WorkOrdersWork, Permissions.WorkOrdersManage);
        g.MapPost("/{id:guid}/resume", async (Guid id, HttpContext http, WorkOrderService s, CancellationToken ct) => (await s.ResumeAsync(id, PerformedAt(http), ct)).ToHttp()).RequireAnyPermission(Permissions.WorkOrdersWork, Permissions.WorkOrdersManage);
        g.MapPost("/{id:guid}/complete", async (Guid id, CompleteRequest r, HttpContext http, WorkOrderService s, CancellationToken ct) => (await s.CompleteAsync(id, r, PerformedAt(http), ct)).ToHttp()).RequireAnyPermission(Permissions.WorkOrdersWork, Permissions.WorkOrdersManage);
        g.MapPost("/{id:guid}/verify", async (Guid id, WorkOrderService s, CancellationToken ct) => (await s.VerifyAsync(id, ct)).ToHttp()).RequirePermission(Permissions.WorkOrdersVerify);
        g.MapPost("/{id:guid}/close", async (Guid id, WorkOrderService s, CancellationToken ct) => (await s.CloseAsync(id, ct)).ToHttp()).RequireAnyPermission(Permissions.WorkOrdersVerify, Permissions.WorkOrdersManage);
        g.MapPost("/{id:guid}/reopen", async (Guid id, ReasonRequest r, WorkOrderService s, CancellationToken ct) => (await s.ReopenAsync(id, r.Reason, ct)).ToHttp()).RequirePermission(Permissions.WorkOrdersManage);
        g.MapPost("/{id:guid}/cancel", async (Guid id, ReasonRequest r, WorkOrderService s, CancellationToken ct) => (await s.CancelAsync(id, r.Reason, ct)).ToHttp()).RequireAnyPermission(Permissions.WorkOrdersRequest, Permissions.WorkOrdersManage);
        // Checklist, time, costs, signatures and files. Each service decides what this person may see and do on this work order right now.
        g.MapGet("/{id:guid}/checklist", async (Guid id, WorkEvidenceService s, CancellationToken ct) => (await s.ChecklistAsync(id, ct)).ToHttp()).RequireAnyPermission(Everyone);
        g.MapPost("/{id:guid}/checklist", async (Guid id, AttachChecklistRequest r, WorkEvidenceService s, CancellationToken ct) => (await s.AttachChecklistAsync(id, r.TemplateId, ct)).ToHttp())
            .RequireAnyPermission(Permissions.WorkOrdersAssign, Permissions.WorkOrdersWork, Permissions.WorkOrdersManage);
        g.MapPost("/{id:guid}/checklist/{itemId:guid}", async (Guid id, Guid itemId, SetChecklistResultRequest r, WorkEvidenceService s, CancellationToken ct) => (await s.SetChecklistResultAsync(id, itemId, r, ct)).ToHttp())
            .RequireAnyPermission(Permissions.WorkOrdersWork, Permissions.WorkOrdersManage);

        g.MapGet("/{id:guid}/time", async (Guid id, WorkEvidenceService s, CancellationToken ct) => (await s.TimeAsync(id, ct)).ToHttp()).RequirePermission(Permissions.WorkOrdersRead);
        g.MapPost("/{id:guid}/time", async (Guid id, AddTimeRequest r, WorkEvidenceService s, CancellationToken ct) => (await s.AddTimeAsync(id, r, ct)).ToHttp())
            .RequireAnyPermission(Permissions.WorkOrdersWork, Permissions.WorkOrdersManage);
        g.MapPost("/{id:guid}/time/{entryId:guid}/void", async (Guid id, Guid entryId, ReasonRequest r, WorkEvidenceService s, CancellationToken ct) => (await s.VoidTimeAsync(id, entryId, r.Reason, ct)).ToHttp())
            .RequireAnyPermission(Permissions.WorkOrdersWork, Permissions.WorkOrdersManage);
        g.MapGet("/{id:guid}/costs", async (Guid id, WorkEvidenceService s, CancellationToken ct) => (await s.CostsAsync(id, ct)).ToHttp()).RequirePermission(Permissions.WorkOrdersRead);
        g.MapPost("/{id:guid}/costs", async (Guid id, AddCostRequest r, WorkEvidenceService s, CancellationToken ct) => (await s.AddCostAsync(id, r, ct)).ToHttp())
            .RequireAnyPermission(Permissions.WorkOrdersWork, Permissions.WorkOrdersManage);
        g.MapPost("/{id:guid}/costs/{lineId:guid}/void", async (Guid id, Guid lineId, ReasonRequest r, WorkEvidenceService s, CancellationToken ct) => (await s.VoidCostAsync(id, lineId, r.Reason, ct)).ToHttp())
            .RequireAnyPermission(Permissions.WorkOrdersWork, Permissions.WorkOrdersManage);
        g.MapGet("/{id:guid}/cost-summary", async (Guid id, WorkEvidenceService s, CancellationToken ct) => (await s.SummaryAsync(id, ct)).ToHttp()).RequirePermission(Permissions.WorkOrdersRead);

        g.MapGet("/{id:guid}/signatures", async (Guid id, WorkFilesService s, CancellationToken ct) => (await s.SignaturesAsync(id, ct)).ToHttp()).RequireAnyPermission(Everyone);
        g.MapPost("/{id:guid}/signatures", async (Guid id, AddSignatureRequest r, WorkFilesService s, CancellationToken ct) => (await s.AddSignatureAsync(id, r, ct)).ToHttp())
            .RequireAnyPermission(Permissions.WorkOrdersWork, Permissions.WorkOrdersManage);
        g.MapGet("/signatures/{signatureId:guid}/image", async (Guid signatureId, HttpContext http, WorkFilesService s, CancellationToken ct) => await ServeAsync(http, await s.SignatureImageAsync(signatureId, ct)))
            .RequireAnyPermission(Everyone);

        g.MapGet("/{id:guid}/attachments", async (Guid id, WorkFilesService s, CancellationToken ct) => (await s.AttachmentsAsync(id, ct)).ToHttp()).RequireAnyPermission(Everyone);
        // multipart/form-data: file (required), kind (Before, After, Other), description.
        g.MapPost("/{id:guid}/attachments", async (Guid id, HttpContext http, WorkFilesService s, IOptions<WorkOptions> options, CancellationToken ct) =>
            {
                if (!http.Request.HasFormContentType) return Results.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "Send the file as multipart/form-data.");
                if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = options.Value.MaxAttachmentBytes + 1024 * 1024;
                IFormCollection form;
                try { form = await http.Request.ReadFormAsync(ct); }
                catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge) { return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "File too large."); }
                catch (InvalidDataException) { return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The upload could not be read."); }

                if (form.Files.Count != 1 || form.Files[0].Name != "file")
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Send exactly one file in the 'file' field."] });
                var kind = AttachmentKind.Other;
                if (form.TryGetValue("kind", out var kindText) && !Enum.TryParse(kindText.ToString(), ignoreCase: true, out kind))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["kind"] = ["Unknown kind."] });

                var file = form.Files[0];
                await using var stream = file.OpenReadStream();
                return (await s.UploadAsync(id, kind, form["description"].ToString(), file.FileName, stream, ct)).ToHttp(a => Results.Created($"/api/v1/work-orders/attachments/{a.Id}", a));
            })
            .RequireAnyPermission(Permissions.WorkOrdersRequest, Permissions.WorkOrdersWork, Permissions.WorkOrdersManage);
        g.MapGet("/attachments/{attachmentId:guid}/download", async (Guid attachmentId, HttpContext http, WorkFilesService s, CancellationToken ct) => await ServeAsync(http, await s.DownloadAttachmentAsync(attachmentId, ct)))
            .RequireAnyPermission(Everyone);
        g.MapPost("/attachments/{attachmentId:guid}/delete", async (Guid attachmentId, ReasonRequest r, WorkFilesService s, CancellationToken ct) => (await s.DeleteAttachmentAsync(attachmentId, r.Reason, ct)).ToHttp())
            .RequireAnyPermission(Permissions.WorkOrdersRequest, Permissions.WorkOrdersWork, Permissions.WorkOrdersManage);

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
