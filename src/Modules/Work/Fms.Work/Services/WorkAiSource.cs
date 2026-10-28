using Fms.BuildingBlocks.Identity;
using Fms.Work.Domain;
using Fms.Work.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Work.Services;

/// <summary>What the AI module may read of work orders: the words, never the people. Voided and archived things are left out.</summary>
public sealed class WorkAiSource(WorkDbContext db, IFacilityLookup facilities) : IWorkAiSource
{
    public async Task<IReadOnlyList<AiCategory>> CategoriesAsync(CancellationToken ct = default) =>
        (await db.Categories.AsNoTracking().Where(c => !c.IsArchived).OrderBy(c => c.Name).Take(100).ToListAsync(ct)).Select(c => new AiCategory(c.Id, c.Code, c.Name)).ToList();

    public async Task<AiWorkText?> GetAsync(Guid workOrderId, CancellationToken ct = default)
    {
        var w = await db.WorkOrders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == workOrderId, ct);
        if (w is null) return null;
        var category = w.CategoryId is { } cid ? (await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct))?.Name : null;
        var place = (await facilities.GetLocationAsync(w.LocationId, ct))?.Name;
        var asset = w.AssetId is { } aid ? (await facilities.GetAssetAsync(aid, ct))?.Name : null;
        return new AiWorkText(w.Id, WorkOrderRules.DisplayNumber(w.Number), w.Title, w.Description, w.Status.ToString(), w.Priority.ToString(), category, place, asset, w.RequestedAt);
    }

    public async Task<IReadOnlyList<AiWorkStep>> StepsAsync(Guid workOrderId, int max, CancellationToken ct = default)
    {
        max = Math.Clamp(max, 1, 100);
        var events = await db.WorkOrderEvents.AsNoTracking().Where(e => e.WorkOrderId == workOrderId).OrderByDescending(e => e.At).Take(max)
            .Select(e => new AiWorkStep(e.At, "status:" + e.ToStatus, e.Note)).ToListAsync(ct);
        var comments = await db.WorkOrderComments.AsNoTracking().Where(c => c.WorkOrderId == workOrderId).OrderByDescending(c => c.At).Take(max)
            .Select(c => new AiWorkStep(c.At, "comment", c.Body)).ToListAsync(ct);
        return events.Concat(comments).OrderByDescending(s => s.At).Take(max).ToList();
    }
}
