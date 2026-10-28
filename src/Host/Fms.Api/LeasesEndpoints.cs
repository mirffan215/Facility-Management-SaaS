using Microsoft.AspNetCore.Http.Features;
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

        g.MapGet("/{id:guid}/documents", async (Guid id, LeaseDocumentService s, CancellationToken ct) => (await s.ListAsync(id, ct)).ToHttp()).RequirePermission(Permissions.LeasesRead);
        g.MapPost("/{id:guid}/documents", async (Guid id, HttpContext http, LeaseDocumentService s, CancellationToken ct) =>
            {
                if (!http.Request.HasFormContentType) return Results.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "Send the file as multipart/form-data.");
                if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = LeaseDocumentService.MaxBytes + 1024 * 1024;
                IFormCollection form;
                try { form = await http.Request.ReadFormAsync(ct); }
                catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge) { return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "File too large."); }
                catch (InvalidDataException) { return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The upload could not be read."); }

                if (form.Files.Count != 1 || form.Files[0].Name != "file")
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Send exactly one file in the 'file' field."] });
                var file = form.Files[0];
                await using var stream = file.OpenReadStream();
                return (await s.UploadAsync(id, file.FileName, stream, ct)).ToHttp(d => Results.Created($"/api/v1/leases/documents/{d.Id}", d));
            })
            .RequirePermission(Permissions.LeasesManage);
        g.MapGet("/documents/{id:guid}", async (Guid id, HttpContext http, LeaseDocumentService s, CancellationToken ct) =>
            {
                var result = await s.DownloadAsync(id, ct);
                if (!result.IsOk) return result.ToHttp();
                http.Response.Headers.CacheControl = "private, no-store";
                return Results.File(result.Value!.Content, result.Value.Document.ContentType, result.Value.Document.FileName);
            })
            .RequirePermission(Permissions.LeasesRead);
        g.MapPost("/documents/{id:guid}/delete", async (Guid id, DeleteRequest r, LeaseDocumentService s, CancellationToken ct) => (await s.DeleteAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.LeasesManage);
    }
}
