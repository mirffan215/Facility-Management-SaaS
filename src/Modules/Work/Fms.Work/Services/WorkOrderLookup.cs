using Fms.BuildingBlocks.Identity;
using Fms.Work.Domain;
using Fms.Work.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Work.Services;

/// <summary>What the Inventory module may ask about work orders and preventive templates, in the current tenant only.</summary>
public sealed class WorkOrderLookup(WorkDbContext db) : IWorkOrderLookup
{
    public async Task<WorkOrderRef?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var w = await db.WorkOrders.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Id, x.Number, x.Title, x.AssetId, x.LocationId, x.Status }).FirstOrDefaultAsync(ct);
        return w is null
            ? null
            : new WorkOrderRef(w.Id, WorkOrderRules.DisplayNumber(w.Number), w.Title, w.AssetId, w.LocationId,
                w.Status is WorkOrderStatus.Approved or WorkOrderStatus.Assigned or WorkOrderStatus.InProgress or WorkOrderStatus.OnHold or WorkOrderStatus.Completed);
    }

    public Task<bool> PmTemplateExistsAsync(Guid pmTemplateId, CancellationToken ct = default) => db.PmTemplates.AsNoTracking().AnyAsync(t => t.Id == pmTemplateId, ct);
}
