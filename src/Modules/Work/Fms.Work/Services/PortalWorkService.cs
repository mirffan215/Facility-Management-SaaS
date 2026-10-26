using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Work.Domain;
using Fms.Work.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Work.Services;

public sealed record PortalWorkCommentDto(Guid Id, string? AuthorName, DateTimeOffset At, string Body, bool Mine);

public sealed record PortalWorkDto(
    Guid Id, string Display, string Title, string? Description, WorkPriority Priority, WorkOrderStatus Status, string? LocationName, string? AssetName, DateTimeOffset? DueAt,
    string? ContractNumber, int? ResponseMinutes, int? ResolutionMinutes, IReadOnlyList<PortalWorkCommentDto>? Comments);

public sealed record PortalCommentRequest(string? Body);

/// <summary>
/// The vendor portal's view of work orders given to the vendor. It shows the job and the contract terms, and lets the vendor write to the people running it; it shows nothing internal
/// (internal comments, costs, history, who else is involved) and changes nothing else. A work order of another vendor looks exactly like one that does not exist.
/// </summary>
public sealed class PortalWorkService(
    WorkDbContext db, IVendorScope scope, ICurrentUser user, IFacilityLookup facilities, IUserDirectory users, IAuditPort audit, TimeProvider clock, IVendorLookup? vendors = null)
{
    public async Task<Result<IReadOnlyList<PortalWorkDto>>> ListAsync(CancellationToken ct = default)
    {
        if (await scope.CurrentVendorIdAsync(ct) is not { } vendorId) return Result<IReadOnlyList<PortalWorkDto>>.Forbidden("You do not have access to the vendor portal.");
        var rows = await db.WorkOrders.AsNoTracking().Where(w => w.AssignedVendorId == vendorId).OrderByDescending(w => w.Number).Take(100).ToListAsync(ct);
        var places = await facilities.LocationsAsync(rows.Select(r => r.LocationId), ct);
        var assets = await facilities.AssetsAsync(rows.Where(r => r.AssetId != null).Select(r => r.AssetId!.Value), ct);
        return Result<IReadOnlyList<PortalWorkDto>>.Ok(rows.Select(w => ToDto(w, places, assets, null, null, null, null)).ToList());
    }

    public async Task<Result<PortalWorkDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        if (await scope.CurrentVendorIdAsync(ct) is not { } vendorId) return Result<PortalWorkDto>.Forbidden("You do not have access to the vendor portal.");
        var w = await db.WorkOrders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.AssignedVendorId == vendorId, ct);
        if (w is null) return Result<PortalWorkDto>.NotFound();

        var places = await facilities.LocationsAsync([w.LocationId], ct);
        var assets = await facilities.AssetsAsync(w.AssetId is { } a ? [a] : [], ct);
        ContractRef? contract = w.VendorContractId is { } cid && vendors is not null ? await vendors.GetContractAsync(cid, ct) : null;
        var sla = contract is not null && vendors is not null ? await vendors.SlaAsync(contract.Id, (int)w.Priority, ct) : null;

        var comments = await db.WorkOrderComments.AsNoTracking().Where(c => c.WorkOrderId == id && !c.IsInternal).OrderBy(c => c.At).ToListAsync(ct);
        var names = await users.GetAsync(comments.Select(c => c.AuthorId), ct);
        var me = user.UserId;
        var dtos = comments.Select(c => new PortalWorkCommentDto(c.Id, names.GetValueOrDefault(c.AuthorId)?.DisplayName ?? names.GetValueOrDefault(c.AuthorId)?.Email, c.At, c.Body, c.AuthorId == me)).ToList();
        return Result<PortalWorkDto>.Ok(ToDto(w, places, assets, contract?.Number, sla?.ResponseMinutes, sla?.ResolutionMinutes, dtos));
    }

    public async Task<Result<PortalWorkDto>> AddCommentAsync(Guid id, PortalCommentRequest r, CancellationToken ct = default)
    {
        if (await scope.CurrentVendorIdAsync(ct) is not { } vendorId || user.UserId is not { } me) return Result<PortalWorkDto>.Forbidden("You do not have access to the vendor portal.");
        var w = await db.WorkOrders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.AssignedVendorId == vendorId, ct);
        if (w is null) return Result<PortalWorkDto>.NotFound();
        if (!WorkOrderRules.IsOpen(w.Status)) return Result<PortalWorkDto>.Conflict("This work order is finished. Contact the facilities team if something still needs saying.");
        var body = r.Body?.Trim() ?? "";
        if (body.Length is 0 or > 2000) return Result<PortalWorkDto>.Invalid("body", "Write a message (max 2000 characters).");

        var comment = new WorkOrderComment { WorkOrderId = id, AuthorId = me, At = clock.GetUtcNow(), Body = body, IsInternal = false };
        db.WorkOrderComments.Add(comment);
        audit.Record("workorder.commented", nameof(WorkOrder), id.ToString(), JsonSerializer.Serialize(new { comment.Id, comment.IsInternal, byVendor = true }));
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    private static PortalWorkDto ToDto(
        WorkOrder w, IReadOnlyDictionary<Guid, FacilityRef> places, IReadOnlyDictionary<Guid, FacilityRef> assets, string? contractNumber, int? response, int? resolution, IReadOnlyList<PortalWorkCommentDto>? comments) =>
        new(w.Id, WorkOrderRules.DisplayNumber(w.Number), w.Title, comments is null ? null : w.Description, w.Priority, w.Status, places.GetValueOrDefault(w.LocationId)?.Name,
            w.AssetId is { } a ? assets.GetValueOrDefault(a)?.Name : null, w.DueAt, contractNumber, response, resolution, comments);
}
