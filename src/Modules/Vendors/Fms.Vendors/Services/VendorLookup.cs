using Fms.BuildingBlocks.Identity;
using Fms.Vendors.Domain;
using Fms.Vendors.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Vendors.Services;

/// <summary>What other modules may ask about vendors and contracts, in the current tenant only.</summary>
public sealed class VendorLookup(VendorsDbContext db, IFacilityLookup facilities, TimeProvider clock) : IVendorLookup
{
    public async Task<VendorRef?> GetVendorAsync(Guid vendorId, CancellationToken ct = default)
    {
        var v = await db.Vendors.AsNoTracking().FirstOrDefaultAsync(x => x.Id == vendorId, ct);
        return v is null ? null : ToRef(v);
    }

    public async Task<IReadOnlyDictionary<Guid, VendorRef>> VendorsAsync(IEnumerable<Guid> vendorIds, CancellationToken ct = default)
    {
        var ids = vendorIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, VendorRef>();
        return (await db.Vendors.AsNoTracking().Where(v => ids.Contains(v.Id)).ToListAsync(ct)).ToDictionary(v => v.Id, ToRef);
    }

    public async Task<ContractRef?> GetContractAsync(Guid contractId, CancellationToken ct = default)
    {
        var c = await db.Contracts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == contractId, ct);
        return c is null ? null : new ContractRef(c.Id, c.VendorId, c.Number, c.Title, c.StartOn, c.EndOn, ContractRules.InForce(c, Common.Today(clock)));
    }

    public async Task<bool> CoversAsync(Guid contractId, Guid? assetId, Guid? locationId, CancellationToken ct = default)
    {
        var lines = await db.Coverage.AsNoTracking().Where(x => x.ContractId == contractId).ToListAsync(ct);
        if (lines.Count == 0) return true; // a general contract

        var asset = assetId is { } a ? await facilities.GetAssetAsync(a, ct) : null;
        if (asset is not null && lines.Any(l => l.Kind == CoverageKind.Asset && l.TargetId == asset.Id)) return true;
        if (asset?.ClassId is { } cls && lines.Any(l => l.Kind == CoverageKind.AssetClass && l.TargetId == cls)) return true;
        var place = locationId ?? asset?.LocationId;
        if (place is { } loc && await facilities.SiteOfAsync(loc, ct) is { } site && lines.Any(l => l.Kind == CoverageKind.Site && l.TargetId == site)) return true;
        return false;
    }

    public async Task<IReadOnlyList<VendorOffer>> OffersAsync(Guid? assetId, Guid? locationId, int priority, int max, CancellationToken ct = default)
    {
        var today = Common.Today(clock);
        var contracts = await db.Contracts.AsNoTracking().Where(c => c.StartOn <= today && c.EndOn >= today).OrderByDescending(c => c.StartOn).Take(200).ToListAsync(ct);
        var inForce = contracts.Where(c => ContractRules.InForce(c, today)).ToList();
        if (inForce.Count == 0) return [];
        var vendors = await VendorsAsync(inForce.Select(c => c.VendorId), ct);
        var offers = new List<VendorOffer>();
        foreach (var c in inForce)
        {
            if (!vendors.TryGetValue(c.VendorId, out var vendor) || !vendor.CanReceiveWork) continue;
            if (!await CoversAsync(c.Id, assetId, locationId, ct)) continue;
            offers.Add(new VendorOffer(vendor, new ContractRef(c.Id, c.VendorId, c.Number, c.Title, c.StartOn, c.EndOn, true), await SlaAsync(c.Id, priority, ct)));
            if (offers.Count >= Math.Clamp(max, 1, 50)) break;
        }

        return offers;
    }

    public async Task<ContractSlaRef?> SlaAsync(Guid contractId, int priority, CancellationToken ct = default)
    {
        var t = await db.SlaTargets.AsNoTracking().FirstOrDefaultAsync(x => x.ContractId == contractId && x.Priority == priority, ct);
        return t is null ? null : new ContractSlaRef(t.Priority, t.ResponseMinutes, t.ResolutionMinutes);
    }

    private static VendorRef ToRef(Vendor v) => new(v.Id, v.Code, v.Name, v.NameAr, v.Status == VendorStatus.Active && !v.IsArchived);
}
