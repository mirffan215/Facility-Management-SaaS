using Microsoft.AspNetCore.Http.Features;
using Fms.BuildingBlocks.Security;
using Fms.Compliance.Domain;
using Fms.Compliance.Services;

namespace Fms.Api;

public static class ComplianceEndpoints
{
    public static void MapCompliance(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/compliance");

        g.MapGet("/summary", async (ObligationService s, CancellationToken ct) => Results.Ok(await s.SummaryAsync(ct))).RequirePermission(Permissions.ComplianceRead);
        g.MapGet("/obligations", async (ObligationService s, ComplianceStatus? status, ObligationCategory? category, Guid? locationId, string? q, bool? includeArchived, int? page, int? pageSize, CancellationToken ct) =>
            Results.Ok(await s.ListAsync(status, category, locationId, q, includeArchived ?? false, page, pageSize, ct))).RequirePermission(Permissions.ComplianceRead);
        g.MapGet("/obligations/{id:guid}", async (Guid id, ObligationService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttp()).RequirePermission(Permissions.ComplianceRead);
        g.MapPost("/obligations", async (SaveObligationRequest r, ObligationService s, CancellationToken ct) => (await s.CreateAsync(r, ct)).ToHttp(x => Results.Created($"/api/v1/compliance/obligations/{x.Id}", x))).RequirePermission(Permissions.ComplianceManage);
        g.MapPut("/obligations/{id:guid}", async (Guid id, SaveObligationRequest r, ObligationService s, CancellationToken ct) => (await s.UpdateAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.ComplianceManage);
        g.MapPost("/obligations/{id:guid}/archive", async (Guid id, ArchiveObligationRequest r, ObligationService s, CancellationToken ct) => (await s.ArchiveAsync(id, true, r, ct)).ToHttp()).RequirePermission(Permissions.ComplianceManage);
        g.MapPost("/obligations/{id:guid}/restore", async (Guid id, ArchiveObligationRequest r, ObligationService s, CancellationToken ct) => (await s.ArchiveAsync(id, false, r, ct)).ToHttp()).RequirePermission(Permissions.ComplianceManage);
        g.MapPost("/obligations/{id:guid}/completions", async (Guid id, RecordCompletionRequest r, ObligationService s, CancellationToken ct) =>
            (await s.RecordAsync(id, r, ct)).ToHttp(x => Results.Created($"/api/v1/compliance/obligations/{id}", x))).RequirePermission(Permissions.ComplianceManage);

        // multipart/form-data: file (required).
        g.MapPost("/completions/{id:guid}/documents", async (Guid id, HttpContext http, CompletionDocumentService s, CancellationToken ct) =>
            {
                if (!http.Request.HasFormContentType) return Results.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "Send the file as multipart/form-data.");
                if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = CompletionDocumentService.MaxBytes + 1024 * 1024;
                IFormCollection form;
                try { form = await http.Request.ReadFormAsync(ct); }
                catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge) { return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "File too large."); }
                catch (InvalidDataException) { return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The upload could not be read."); }

                if (form.Files.Count != 1 || form.Files[0].Name != "file")
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Send exactly one file in the 'file' field."] });
                var file = form.Files[0];
                await using var stream = file.OpenReadStream();
                return (await s.UploadAsync(id, file.FileName, stream, ct)).ToHttp(d => Results.Created($"/api/v1/compliance/documents/{d.Id}", d));
            })
            .RequirePermission(Permissions.ComplianceManage);
        g.MapGet("/documents/{id:guid}", async (Guid id, HttpContext http, CompletionDocumentService s, CancellationToken ct) =>
            {
                var result = await s.DownloadAsync(id, ct);
                if (!result.IsOk) return result.ToHttp();
                http.Response.Headers.CacheControl = "private, no-store";
                return Results.File(result.Value!.Content, result.Value.Document.ContentType, result.Value.Document.FileName);
            })
            .RequirePermission(Permissions.ComplianceRead);
        g.MapPost("/documents/{id:guid}/delete", async (Guid id, DeleteDocumentRequest r, CompletionDocumentService s, CancellationToken ct) => (await s.DeleteAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.ComplianceManage);

        // What kinds of permit exist. Anyone who may ask for a permit needs to see them.
        g.MapGet("/permit-types", async (PermitTypeService s, bool? includeArchived, CancellationToken ct) => Results.Ok(await s.ListAsync(includeArchived ?? false, ct)))
            .RequireAnyPermission(Permissions.PermitsRead, Permissions.PermitsRequest, Permissions.ComplianceManage);
        g.MapPost("/permit-types", async (SavePermitTypeRequest r, PermitTypeService s, CancellationToken ct) => (await s.CreateAsync(r, ct)).ToHttp(x => Results.Created($"/api/v1/compliance/permit-types/{x.Id}", x))).RequirePermission(Permissions.ComplianceManage);
        g.MapPut("/permit-types/{id:guid}", async (Guid id, SavePermitTypeRequest r, PermitTypeService s, CancellationToken ct) => (await s.UpdateAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.ComplianceManage);
        g.MapPost("/permit-types/{id:guid}/archive", async (Guid id, ArchiveObligationRequest r, PermitTypeService s, CancellationToken ct) => (await s.ArchiveAsync(id, true, r, ct)).ToHttp()).RequirePermission(Permissions.ComplianceManage);
        g.MapPost("/permit-types/{id:guid}/restore", async (Guid id, ArchiveObligationRequest r, PermitTypeService s, CancellationToken ct) => (await s.ArchiveAsync(id, false, r, ct)).ToHttp()).RequirePermission(Permissions.ComplianceManage);

        var p = v1.MapGroup("/permits");
        p.MapGet("", async (PermitService s, PermitStatus? status, Guid? locationId, bool? overdue, string? q, int? page, int? pageSize, CancellationToken ct) =>
            Results.Ok(await s.ListAsync(status, locationId, overdue, false, q, page, pageSize, ct))).RequirePermission(Permissions.PermitsRead);
        p.MapGet("/mine", async (PermitService s, PermitStatus? status, int? page, int? pageSize, CancellationToken ct) =>
            Results.Ok(await s.ListAsync(status, null, null, true, null, page, pageSize, ct))).RequirePermission(Permissions.PermitsRequest);
        p.MapGet("/{id:guid}", async (Guid id, PermitService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttp()).RequireAnyPermission(Permissions.PermitsRead, Permissions.PermitsRequest);
        p.MapPost("", async (RequestPermitRequest r, PermitService s, CancellationToken ct) => (await s.RequestAsync(r, ct)).ToHttp(x => Results.Created($"/api/v1/permits/{x.Id}", x))).RequirePermission(Permissions.PermitsRequest);
        p.MapPost("/{id:guid}/approve", async (Guid id, PermitDecisionRequest r, PermitService s, CancellationToken ct) => (await s.ApproveAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.PermitsApprove);
        p.MapPost("/{id:guid}/reject", async (Guid id, PermitDecisionRequest r, PermitService s, CancellationToken ct) => (await s.RejectAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.PermitsApprove);
        p.MapPost("/{id:guid}/issue", async (Guid id, IssuePermitRequest r, PermitService s, CancellationToken ct) => (await s.IssueAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.PermitsIssue);
        p.MapPost("/{id:guid}/start", async (Guid id, StartPermitRequest r, PermitService s, CancellationToken ct) => (await s.StartAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.PermitsWork);
        p.MapPost("/{id:guid}/close", async (Guid id, ClosePermitRequest r, PermitService s, CancellationToken ct) => (await s.CloseAsync(id, r, ct)).ToHttp()).RequireAnyPermission(Permissions.PermitsWork, Permissions.PermitsIssue);
        p.MapPost("/{id:guid}/cancel", async (Guid id, PermitDecisionRequest r, PermitService s, CancellationToken ct) => (await s.CancelAsync(id, r, ct)).ToHttp()).RequireAnyPermission(Permissions.PermitsRequest, Permissions.PermitsApprove, Permissions.PermitsIssue);
    }
}
