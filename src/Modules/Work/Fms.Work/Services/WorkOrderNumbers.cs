using Fms.Work.Domain;
using Fms.Work.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Work.Services;

/// <summary>
/// Work order numbers are handed out one at a time per tenant: an application lock held for the caller's transaction, so parallel creations queue
/// briefly instead of colliding. Call inside a transaction; the unique index on (tenant, number) stays as the backstop.
/// </summary>
public static class WorkOrderNumbers
{
    public static async Task<int> NextAsync(WorkDbContext db, string tenantKey, CancellationToken ct)
    {
        var lockResult = await db.Database
            .SqlQuery<int>($"DECLARE @r int; EXEC @r = sp_getapplock @Resource = {"fms.wo-number:" + tenantKey}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000; SELECT @r AS Value")
            .ToListAsync(ct);
        if (lockResult.Count != 1 || lockResult[0] < 0) throw new InvalidOperationException("Could not reserve a work order number.");
        return (await db.WorkOrders.MaxAsync(w => (int?)w.Number, ct) ?? 0) + 1;
    }
}
