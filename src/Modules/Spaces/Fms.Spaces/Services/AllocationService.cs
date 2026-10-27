using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Spaces.Domain;
using Fms.Spaces.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Spaces.Services;

/// <summary>Giving space to people and teams for a period, without ever putting more people in a space than it holds.</summary>
public sealed class AllocationService(SpacesDbContext db, SpaceService spaces, IFacilityLookup facilities, IUserDirectory users, IAuditPort audit, ICurrentUser user, TimeProvider clock)
{
    public const int MaxListed = 200;

    public async Task<IReadOnlyList<AllocationDto>> ListAsync(Guid? spaceId, Guid? userId, AllocationState? state, CancellationToken ct = default)
    {
        var today = Common.Today(clock);
        var query = db.Allocations.AsNoTracking().AsQueryable();
        if (spaceId is { } s) query = query.Where(a => a.SpaceId == s);
        if (userId is { } u) query = query.Where(a => a.UserId == u);
        query = state switch
        {
            AllocationState.Cancelled => query.Where(a => a.CancelledAt != null),
            AllocationState.Upcoming => query.Where(a => a.CancelledAt == null && a.StartOn > today),
            AllocationState.Current => query.Where(a => a.CancelledAt == null && a.StartOn <= today && (a.EndOn == null || a.EndOn >= today)),
            AllocationState.Ended => query.Where(a => a.CancelledAt == null && a.EndOn != null && a.EndOn < today),
            _ => query.Where(a => a.CancelledAt == null && (a.EndOn == null || a.EndOn >= today)), // what is, or will be, in force
        };
        var rows = await query.OrderBy(a => a.SpaceId).ThenBy(a => a.StartOn).Take(MaxListed).ToListAsync(ct);
        return await ToDtosAsync(rows, today, ct);
    }

    public async Task<Result<AllocationDto>> CreateAsync(CreateAllocationRequest r, CancellationToken ct = default)
    {
        var errors = new ValidationErrors();
        if (r.SpaceId is null) errors.Add("spaceId", "Choose a space.");
        if (r.Kind is not { } kind || !Enum.IsDefined(kind)) errors.Add("kind", "Choose whether this is for a person or a team.");
        if (r.StartOn is null) errors.Add("startOn", "Choose the first day.");
        if (r.StartOn is { } st && r.EndOn is { } en && en < st) errors.Add("endOn", "The last day cannot be before the first.");
        var team = Common.Clean(r.TeamName);
        var note = Common.Clean(r.Note);
        if (note is { Length: > 500 }) errors.Add("note", "The note is at most 500 characters.");
        var headcount = 1;
        if (r.Kind == OccupantKind.Person)
        {
            if (r.UserId is null) errors.Add("userId", "Choose the person.");
            if (r.Headcount is > 1) errors.Add("headcount", "A person takes one place.");
        }
        else if (r.Kind == OccupantKind.Team)
        {
            if (team is null || team.Length > 200) errors.Add("teamName", "Name the team (at most 200 characters).");
            if (r.Headcount is not { } h || h is < 1 or > SpaceRules.MaxCapacity) errors.Add("headcount", "Give the number of people in the team.");
            else headcount = h;
        }

        if (errors.Any) return errors.ToResult<AllocationDto>();

        var spaceRef = await facilities.GetLocationAsync(r.SpaceId!.Value, ct);
        if (spaceRef is not { Kind: "Space" }) return Result<AllocationDto>.Invalid("spaceId", "That space does not exist.");
        var today = Common.Today(clock);
        if (r.EndOn is { } end && end < today) return Result<AllocationDto>.Invalid("endOn", "The allocation would already be over.");

        string? userName = null;
        if (r.Kind == OccupantKind.Person)
        {
            var person = (await users.GetAsync([r.UserId!.Value], ct)).GetValueOrDefault(r.UserId.Value);
            if (person is not { IsActive: true }) return Result<AllocationDto>.Invalid("userId", "That person does not exist or is not active.");
            userName = person.DisplayName ?? person.Email;
        }

        // The check and the insert happen under a lock on the space, in one transaction (retried as a whole if the connection drops).
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await Common.LockSpaceAsync(db, spaceRef.Id, ct);

            var capacity = (await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.LocationId == spaceRef.Id, ct))?.Capacity ?? 0;
            if (capacity <= 0) return Result<AllocationDto>.Conflict("Set the space's capacity before allocating it.");

            if (r.Kind == OccupantKind.Person)
            {
                var mine = await db.Allocations.AsNoTracking().Where(a => a.UserId == r.UserId && a.CancelledAt == null).Select(a => new { a.StartOn, a.EndOn }).ToListAsync(ct);
                if (mine.Any(a => SpaceRules.Overlap(a.StartOn, a.EndOn, r.StartOn!.Value, r.EndOn))) return Result<AllocationDto>.Conflict("This person already has a space for part of that period.");
            }

            var peak = await spaces.PeakAsync(spaceRef.Id, r.StartOn!.Value, r.EndOn, ct);
            if (peak + headcount > capacity)
                return Result<AllocationDto>.Conflict($"{spaceRef.Name} holds {capacity} and {peak} are already placed there at the busiest time of that period; {headcount} more do not fit.");

            var a = new Allocation
            {
                SpaceId = spaceRef.Id, Kind = r.Kind!.Value, UserId = r.Kind == OccupantKind.Person ? r.UserId : null, TeamName = r.Kind == OccupantKind.Team ? team : null, Headcount = headcount,
                StartOn = r.StartOn!.Value, EndOn = r.EndOn, Note = note, CreatedAt = clock.GetUtcNow(), CreatedBy = user.UserId,
            };
            db.Allocations.Add(a);
            audit.Record("space.allocated", nameof(Allocation), a.Id.ToString(), JsonSerializer.Serialize(new { spaceRef.Code, a.Kind, a.UserId, a.TeamName, a.Headcount, a.StartOn, a.EndOn }));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Result<AllocationDto>.Ok(ToDto(a, spaceRef, userName, today));
        });
    }

    /// <summary>Ends an allocation from a day (default today). One that has not started is cancelled instead. The record stays as history.</summary>
    public async Task<Result<AllocationDto>> ReleaseAsync(Guid id, ReleaseAllocationRequest r, CancellationToken ct = default)
    {
        var a = await db.Allocations.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return Result<AllocationDto>.NotFound();
        var reason = Common.Clean(r.Reason);
        if (reason is null || reason.Length > 500) return Result<AllocationDto>.Invalid("reason", "A reason is required (at most 500 characters).");
        if (!Common.TryVersion(r.RowVersion, out var version)) return Result<AllocationDto>.Invalid("rowVersion", "Reload and try again.");
        db.Entry(a).Property(x => x.RowVersion).OriginalValue = version;

        var today = Common.Today(clock);
        var from = r.From ?? today;
        if (a.CancelledAt is not null) return Result<AllocationDto>.Conflict("This allocation was already cancelled.");
        if (a.EndOn is { } end && end < from) return Result<AllocationDto>.Conflict("This allocation is already over by then.");

        var now = clock.GetUtcNow();
        if (from <= a.StartOn) a.CancelledAt = now;
        else
        {
            a.EndOn = from.AddDays(-1);
            a.ReleasedAt = now;
        }

        a.EndedBy = user.UserId;
        a.EndReason = reason;
        audit.Record(a.CancelledAt is null ? "space.released" : "space.allocation_cancelled", nameof(Allocation), id.ToString(), JsonSerializer.Serialize(new { a.SpaceId, from, reason }));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<AllocationDto>.Conflict("Someone else changed this allocation. Reload and try again.");
        }

        return Result<AllocationDto>.Ok((await ToDtosAsync([a], today, ct))[0]);
    }

    private async Task<IReadOnlyList<AllocationDto>> ToDtosAsync(List<Allocation> rows, DateOnly today, CancellationToken ct)
    {
        var places = await facilities.LocationsAsync(rows.Select(a => a.SpaceId), ct);
        var people = await users.GetAsync(rows.Where(a => a.UserId is not null).Select(a => a.UserId!.Value), ct);
        return rows.Select(a => ToDto(a, places.GetValueOrDefault(a.SpaceId), a.UserId is { } u && people.TryGetValue(u, out var p) ? p.DisplayName ?? p.Email : null, today)).ToList();
    }

    private static AllocationDto ToDto(Allocation a, FacilityRef? space, string? userName, DateOnly today)
    {
        var state = a.CancelledAt is not null ? AllocationState.Cancelled
            : a.StartOn > today ? AllocationState.Upcoming
            : a.EndOn is { } end && end < today ? AllocationState.Ended
            : AllocationState.Current;
        return new AllocationDto(a.Id, a.SpaceId, space?.Code ?? "?", space?.Name ?? "?", a.Kind, a.UserId, userName, a.TeamName, a.Headcount, a.StartOn, a.EndOn, state, a.Note, a.EndReason, Common.Version(a.RowVersion));
    }
}
