using Fms.BuildingBlocks.Security;
using Fms.Vendors.Domain;
using Fms.Vendors.Services;
using Microsoft.AspNetCore.Http.Features;

namespace Fms.Api;

public static class VendorsEndpoints
{
    public static void MapVendors(this RouteGroupBuilder v1)
    {
        var v = v1.MapGroup("/vendors");
        v.MapGet("", async (VendorService s, string? q, VendorStatus? status, bool? includeArchived, int? page, int? pageSize, CancellationToken ct) =>
            Results.Ok(await s.ListAsync(q, status, includeArchived ?? false, page, pageSize, ct))).RequirePermission(Permissions.VendorsRead);
        v.MapPost("", async (SaveVendorRequest r, VendorService s, CancellationToken ct) => (await s.SaveAsync(null, r, ct)).ToHttp(x => Results.Created($"/api/v1/vendors/{x.Id}", x))).RequirePermission(Permissions.VendorsManage);
        v.MapGet("/{id:guid}", async (Guid id, VendorService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttp()).RequirePermission(Permissions.VendorsRead);
        v.MapPut("/{id:guid}", async (Guid id, SaveVendorRequest r, VendorService s, CancellationToken ct) => (await s.SaveAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.VendorsManage);
        v.MapPost("/{id:guid}/status", async (Guid id, SetVendorStatusRequest r, VendorService s, CancellationToken ct) => (await s.SetStatusAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.VendorsManage);
        v.MapPost("/{id:guid}/archive", async (Guid id, RowVersionRequest r, VendorService s, CancellationToken ct) => (await s.SetArchivedAsync(id, true, r.RowVersion, ct)).ToHttp()).RequirePermission(Permissions.VendorsManage);
        v.MapPost("/{id:guid}/restore", async (Guid id, RowVersionRequest r, VendorService s, CancellationToken ct) => (await s.SetArchivedAsync(id, false, r.RowVersion, ct)).ToHttp()).RequirePermission(Permissions.VendorsManage);
        v.MapGet("/{id:guid}/contacts", async (Guid id, VendorService s, CancellationToken ct) => (await s.ContactsAsync(id, ct)).ToHttp()).RequirePermission(Permissions.VendorsRead);
        v.MapPut("/{id:guid}/contacts", async (Guid id, SaveContactsRequest r, VendorService s, CancellationToken ct) => (await s.SaveContactsAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.VendorsManage);

        // The vendor portal's people: invitations (the link is shown once) and who has access.
        v.MapGet("/{id:guid}/portal-access", async (Guid id, PortalAdminService s, CancellationToken ct) => (await s.AccessAsync(id, ct)).ToHttp()).RequirePermission(Permissions.VendorsRead);
        v.MapPost("/{id:guid}/invitations", async (Guid id, InviteRequest r, PortalAdminService s, CancellationToken ct) => (await s.InviteAsync(id, r, ct)).ToHttp(x => Results.Created($"/api/v1/vendors/{id}/portal-access", x))).RequirePermission(Permissions.VendorsManage);
        v.MapPost("/{id:guid}/invitations/{invitationId:guid}/revoke", async (Guid id, Guid invitationId, PortalAdminService s, CancellationToken ct) => (await s.RevokeInvitationAsync(id, invitationId, ct)).ToHttp()).RequirePermission(Permissions.VendorsManage);
        v.MapPost("/{id:guid}/portal-users/{userId:guid}/active", async (Guid id, Guid userId, SetPortalUserActiveRequest r, PortalAdminService s, CancellationToken ct) => (await s.SetUserActiveAsync(id, userId, r.Active ?? false, ct)).ToHttp()).RequirePermission(Permissions.VendorsManage);

        var c = v1.MapGroup("/contracts");
        c.MapGet("", async (ContractService s, Guid? vendorId, ContractStatus? status, string? q, int? page, int? pageSize, CancellationToken ct) =>
            Results.Ok(await s.ListAsync(vendorId, status, q, page, pageSize, ct))).RequirePermission(Permissions.VendorsRead);
        c.MapPost("", async (SaveContractRequest r, ContractService s, CancellationToken ct) => (await s.SaveAsync(null, r, ct)).ToHttp(x => Results.Created($"/api/v1/contracts/{x.Id}", x))).RequirePermission(Permissions.VendorsManage);
        c.MapGet("/{id:guid}", async (Guid id, ContractService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttp()).RequirePermission(Permissions.VendorsRead);
        c.MapPut("/{id:guid}", async (Guid id, SaveContractRequest r, ContractService s, CancellationToken ct) => (await s.SaveAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.VendorsManage);
        c.MapPost("/{id:guid}/terminate", async (Guid id, TerminateContractRequest r, ContractService s, CancellationToken ct) => (await s.TerminateAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.VendorsManage);
        c.MapPut("/{id:guid}/coverage", async (Guid id, SaveCoverageRequest r, ContractService s, CancellationToken ct) => (await s.SaveCoverageAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.VendorsManage);
        c.MapPut("/{id:guid}/sla", async (Guid id, SaveSlaRequest r, ContractService s, CancellationToken ct) => (await s.SaveSlaAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.VendorsManage);
        c.MapGet("/{id:guid}/documents", async (Guid id, ContractDocumentService s, CancellationToken ct) => (await s.ListAsync(id, ct)).ToHttp()).RequirePermission(Permissions.VendorsRead);

        // multipart/form-data: file (required), kind, description.
        c.MapPost("/{id:guid}/documents", async (Guid id, HttpContext http, ContractDocumentService s, CancellationToken ct) =>
            {
                if (!http.Request.HasFormContentType) return Results.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "Send the file as multipart/form-data.");
                if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = ContractDocumentService.MaxBytes + 1024 * 1024;
                IFormCollection form;
                try { form = await http.Request.ReadFormAsync(ct); }
                catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge) { return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "File too large."); }
                catch (InvalidDataException) { return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The upload could not be read."); }

                if (form.Files.Count != 1 || form.Files[0].Name != "file")
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Send exactly one file in the 'file' field."] });
                var kind = ContractDocumentKind.Contract;
                if (form.TryGetValue("kind", out var kindText) && !Enum.TryParse(kindText.ToString(), ignoreCase: true, out kind))
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["kind"] = ["Unknown document kind."] });

                var file = form.Files[0];
                await using var stream = file.OpenReadStream();
                return (await s.UploadAsync(id, kind, form["description"].ToString(), file.FileName, stream, ct)).ToHttp(d => Results.Created($"/api/v1/contract-documents/{d.Id}", d));
            })
            .RequirePermission(Permissions.VendorsManage);

        var d = v1.MapGroup("/contract-documents");
        d.MapGet("/{id:guid}/download", async (Guid id, HttpContext http, ContractDocumentService s, CancellationToken ct) =>
            {
                var result = await s.DownloadAsync(id, ct);
                if (!result.IsOk) return result.ToHttp();
                http.Response.Headers.CacheControl = "private, no-store";
                http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
                return Results.File(result.Value!.Content, result.Value.Document.ContentType, result.Value.Document.FileName);
            })
            .RequirePermission(Permissions.VendorsRead);
        d.MapPost("/{id:guid}/delete", async (Guid id, DeleteDocumentRequest r, ContractDocumentService s, CancellationToken ct) => (await s.DeleteAsync(id, r, ct)).ToHttp()).RequirePermission(Permissions.VendorsManage);
    }
}
