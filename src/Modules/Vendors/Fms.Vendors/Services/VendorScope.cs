using Fms.BuildingBlocks.Identity;
using Fms.Vendors.Domain;
using Fms.Vendors.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Vendors.Services;

/// <summary>
/// Which vendor the signed-in person belongs to. The answer is null unless the person has an active link to a vendor that is not blocked or archived, and every portal view is
/// filtered by it. There is no way for a request to name a different vendor: the vendor is never taken from the request.
/// </summary>
public sealed class VendorScope(VendorsDbContext db, ICurrentUser user) : IVendorScope
{
    private bool _loaded;
    private Guid? _vendorId;

    public async Task<Guid?> CurrentVendorIdAsync(CancellationToken ct = default)
    {
        if (_loaded) return _vendorId;
        _loaded = true;
        if (user.UserId is not { } me) return null;
        _vendorId = await (from u in db.VendorUsers.AsNoTracking()
                           join v in db.Vendors.AsNoTracking() on u.VendorId equals v.Id
                           where u.UserId == me && u.IsActive && !v.IsArchived && v.Status != VendorStatus.Blocked
                           select (Guid?)v.Id).FirstOrDefaultAsync(ct);
        return _vendorId;
    }
}
