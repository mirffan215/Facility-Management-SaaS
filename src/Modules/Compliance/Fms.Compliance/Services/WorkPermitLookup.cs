using Fms.BuildingBlocks.Identity;
using Fms.Compliance.Domain;
using Fms.Compliance.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Compliance.Services;

public sealed class WorkPermitLookup(ComplianceDbContext db) : IWorkPermitLookup
{
    private static readonly PermitStatus[] InPlay = [PermitStatus.Requested, PermitStatus.Approved, PermitStatus.Issued, PermitStatus.Started];

    public async Task<IReadOnlyList<WorkPermitRef>> ForWorkOrdersAsync(IEnumerable<Guid> workOrderIds, CancellationToken ct = default)
    {
        var ids = workOrderIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        var rows = await db.Permits.AsNoTracking().Where(p => p.WorkOrderId != null && ids.Contains(p.WorkOrderId.Value) && InPlay.Contains(p.Status))
            .OrderBy(p => p.PlannedStart).Select(p => new { p.Id, p.Number, WorkOrderId = p.WorkOrderId!.Value, p.TypeName, p.Status, p.LocationName, p.PlannedStart, p.PlannedEnd }).ToListAsync(ct);
        return rows.Select(r => new WorkPermitRef(r.Id, r.Number, r.WorkOrderId, r.TypeName, r.Status.ToString(), r.LocationName, r.PlannedStart, r.PlannedEnd)).ToList();
    }
}
