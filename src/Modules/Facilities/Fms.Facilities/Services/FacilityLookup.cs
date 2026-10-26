using Fms.BuildingBlocks.Identity;
using Fms.Facilities.Domain;
using Fms.Facilities.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Facilities.Services;

/// <summary>Answers other modules' questions about locations and assets; the tenant filter of the context applies.</summary>
public sealed class FacilityLookup(FacilitiesDbContext db) : IFacilityLookup
{
    public async Task<FacilityRef?> GetLocationAsync(Guid id, CancellationToken ct = default) =>
        (await LocationsAsync([id], ct)).GetValueOrDefault(id);

    public async Task<FacilityRef?> GetAssetAsync(Guid id, CancellationToken ct = default) =>
        (await AssetsAsync([id], ct)).GetValueOrDefault(id);

    public async Task<Guid?> SiteOfAsync(Guid locationId, CancellationToken ct = default)
    {
        var path = await db.Locations.AsNoTracking().Where(l => l.Id == locationId).Select(l => l.Path).FirstOrDefaultAsync(ct);
        if (path is null) return null;
        var first = path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return Guid.TryParseExact(first, "N", out var site) ? site : null;
    }

    public async Task<IReadOnlyList<FacilityRef>> FindAssetsAsync(string? search, string? tag, int limit, CancellationToken ct = default)
    {
        var query = db.Assets.AsNoTracking().Where(a => a.Status != AssetStatus.Retired);
        if (AssetTag.Normalize(tag) is { } normalized) query = query.Where(a => a.TagCode == normalized);
        else if (!string.IsNullOrWhiteSpace(tag)) return [];
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(a => a.Code.Contains(term) || a.Name.Contains(term) || (a.NameAr != null && a.NameAr.Contains(term)));
        }

        var rows = await query.OrderBy(a => a.Code).Take(Math.Clamp(limit, 1, 50)).Select(a => new { a.Id, a.Code, a.Name, a.LocationId }).ToListAsync(ct);
        return rows.Select(r => new FacilityRef(r.Id, r.Code, r.Name, r.LocationId)).ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, FacilityRef>> LocationsAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var wanted = ids.Distinct().ToList();
        if (wanted.Count == 0) return new Dictionary<Guid, FacilityRef>();
        var rows = await db.Locations.AsNoTracking().Where(l => wanted.Contains(l.Id)).Select(l => new { l.Id, l.Code, l.Name, l.Type }).ToListAsync(ct);
        return rows.ToDictionary(r => r.Id, r => new FacilityRef(r.Id, r.Code, r.Name, null, r.Type.ToString()));
    }

    public async Task<IReadOnlyDictionary<Guid, FacilityRef>> AssetsAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var wanted = ids.Distinct().ToList();
        if (wanted.Count == 0) return new Dictionary<Guid, FacilityRef>();
        var rows = await db.Assets.AsNoTracking().Where(a => wanted.Contains(a.Id)).Select(a => new { a.Id, a.Code, a.Name, a.LocationId }).ToListAsync(ct);
        return rows.ToDictionary(r => r.Id, r => new FacilityRef(r.Id, r.Code, r.Name, r.LocationId));
    }
}
