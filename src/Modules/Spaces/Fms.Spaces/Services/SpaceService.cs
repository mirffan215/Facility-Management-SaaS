using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Spaces.Domain;
using Fms.Spaces.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Spaces.Services;

/// <summary>Capacity and use of spaces, and how full a place is on a day.</summary>
public sealed class SpaceService(SpacesDbContext db, IFacilityLookup facilities, IAuditPort audit, TimeProvider clock)
{
    public async Task<Result<SpaceDto>> GetAsync(Guid locationId, CancellationToken ct = default)
    {
        var space = await facilities.GetLocationAsync(locationId, ct);
        if (space is not { Kind: "Space" }) return Result<SpaceDto>.NotFound();
        var profile = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.LocationId == locationId, ct);
        var today = Common.Today(clock);
        var allocated = await AllocatedAsync([locationId], today, ct);
        return Result<SpaceDto>.Ok(ToDto(space, profile, allocated.GetValueOrDefault(locationId)));
    }

    public async Task<Result<SpaceDto>> SaveAsync(Guid locationId, SaveSpaceRequest r, CancellationToken ct = default)
    {
        var space = await facilities.GetLocationAsync(locationId, ct);
        if (space is not { Kind: "Space" }) return Result<SpaceDto>.NotFound();

        var errors = new ValidationErrors();
        if (r.Kind is not { } kind || !Enum.IsDefined(kind)) errors.Add("kind", "Choose what the space is used for.");
        if (r.Capacity is not { } capacity || capacity is < 0 or > SpaceRules.MaxCapacity) errors.Add("capacity", $"Capacity is a number from 0 to {SpaceRules.MaxCapacity}.");
        var notes = Common.Clean(r.Notes);
        if (notes is { Length: > 1000 }) errors.Add("notes", "Notes are at most 1000 characters.");
        if (errors.Any) return errors.ToResult<SpaceDto>();

        var profile = await db.Profiles.FirstOrDefaultAsync(p => p.LocationId == locationId, ct);
        var today = Common.Today(clock);
        if (profile is null)
        {
            profile = new SpaceProfile { LocationId = locationId };
            db.Profiles.Add(profile);
        }
        else
        {
            if (!Common.TryVersion(r.RowVersion, out var version)) return Result<SpaceDto>.Invalid("rowVersion", "Reload the space and try again.");
            db.Entry(profile).Property(p => p.RowVersion).OriginalValue = version;

            // Taking seats away from people who hold them would silently break their allocation.
            var held = await PeakAsync(locationId, today, null, ct);
            if (r.Capacity < held) return Result<SpaceDto>.Conflict($"Up to {held} people are allocated from today on, so the capacity cannot be less than {held}. Release some allocations first.");
        }

        profile.Kind = r.Kind!.Value;
        profile.Capacity = r.Capacity!.Value;
        profile.Notes = notes;
        profile.UpdatedAt = clock.GetUtcNow();
        audit.Record("space.profile_saved", nameof(SpaceProfile), locationId.ToString(), JsonSerializer.Serialize(new { space.Code, profile.Kind, profile.Capacity }));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<SpaceDto>.Conflict("Someone else changed this space. Reload and try again.");
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return Result<SpaceDto>.Conflict("Someone else changed this space. Reload and try again.");
        }

        return await GetAsync(locationId, ct);
    }

    /// <summary>How many people are placed in each space, and how full the place is, on one day (default today). Works for a site, building, floor or one space.</summary>
    public async Task<Result<OccupancyDto>> OccupancyAsync(Guid locationId, DateOnly? on, CancellationToken ct = default)
    {
        var tree = await facilities.LocationsUnderAsync(locationId, ct);
        if (tree.Count == 0) return Result<OccupancyDto>.NotFound();
        var day = on ?? Common.Today(clock);
        var spaces = tree.Where(l => l.Kind == "Space").ToList();
        var ids = spaces.Select(s => s.Id).ToList();
        var profiles = await db.Profiles.AsNoTracking().Where(p => ids.Contains(p.LocationId)).ToDictionaryAsync(p => p.LocationId, ct);
        var allocated = await AllocatedAsync(ids, day, ct);

        var rows = spaces.Select(s =>
        {
            var p = profiles.GetValueOrDefault(s.Id);
            var cap = p?.Capacity ?? 0;
            return new OccupancyRow(s.Id, s.Code, s.Name, p?.Kind, cap, allocated.GetValueOrDefault(s.Id), SpaceRules.UtilisationPercent(cap, allocated.GetValueOrDefault(s.Id)));
        }).ToList();
        var capacity = rows.Sum(x => x.Capacity);
        var total = rows.Where(x => x.Capacity > 0).Sum(x => x.Allocated);
        return Result<OccupancyDto>.Ok(new OccupancyDto(locationId, day, capacity, total, SpaceRules.UtilisationPercent(capacity, total), rows.Count(x => x.Capacity == 0), rows));
    }

    internal async Task<Dictionary<Guid, int>> AllocatedAsync(IReadOnlyCollection<Guid> spaceIds, DateOnly day, CancellationToken ct)
    {
        var rows = await db.Allocations.AsNoTracking()
            .Where(a => spaceIds.Contains(a.SpaceId) && a.CancelledAt == null && a.StartOn <= day && (a.EndOn == null || a.EndOn >= day))
            .GroupBy(a => a.SpaceId).Select(g => new { g.Key, N = g.Sum(a => a.Headcount) }).ToListAsync(ct);
        return rows.ToDictionary(r => r.Key, r => r.N);
    }

    internal async Task<int> PeakAsync(Guid spaceId, DateOnly from, DateOnly? to, CancellationToken ct, Guid? except = null)
    {
        var rows = await db.Allocations.AsNoTracking().Where(a => a.SpaceId == spaceId && a.CancelledAt == null && a.Id != except).Select(a => new { a.StartOn, a.EndOn, a.Headcount }).ToListAsync(ct);
        return SpaceRules.Peak(rows.Select(a => (a.StartOn, a.EndOn, a.Headcount)), from, to);
    }

    internal static SpaceDto ToDto(FacilityRef space, SpaceProfile? p, int allocatedToday) =>
        new(space.Id, space.Code, space.Name, p?.Kind, p?.Capacity ?? 0, p?.Notes, allocatedToday, p is null ? null : Common.Version(p.RowVersion));
}
