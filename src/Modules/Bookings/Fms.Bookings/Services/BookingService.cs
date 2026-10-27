using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Integration;
using Fms.BuildingBlocks.Messaging;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Bookings.Domain;
using Fms.Bookings.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Bookings.Services;

public static class BookingMessages
{
    /// <summary>A booking is waiting for approval. Payload: <see cref="BookingNoticeMessage"/>.</summary>
    public const string Pending = "bookings.pending";

    /// <summary>A booking was approved, rejected or cancelled by someone other than its organizer. Payload: <see cref="BookingNoticeMessage"/>.</summary>
    public const string Decided = "bookings.decided";
}

public sealed record BookingNoticeMessage(Guid BookingId, int Count = 1);

/// <summary>Booking resources: no two bookings may hold the same resource at the same time, whoever asks and however many ask at once.</summary>
public sealed class BookingService(
    BookingsDbContext db, ICurrentUser user, ICurrentPermissions permissions, IUserDirectory users, IAuditPort audit, IOutbox outbox, TimeProvider clock)
{
    public const int MaxHoldingPerPerson = 100;
    public const int MaxSeries = 26;
    public const int MaxListed = 300;

    // ---------------- reading ----------------

    /// <summary>One's own bookings (default: from now on), or everyone's for approvers and managers.</summary>
    public async Task<Result<IReadOnlyList<BookingDto>>> ListAsync(bool everyone, Guid? resourceId, DateTimeOffset? from, DateTimeOffset? to, BookingStatus? status, bool pendingOnly, CancellationToken ct = default)
    {
        if (user.UserId is not { } me) return Result<IReadOnlyList<BookingDto>>.Forbidden("Sign in to see bookings.");
        var perms = await permissions.GetAsync(ct);
        var all = everyone && (perms.Contains(Permissions.BookingsApprove) || perms.Contains(Permissions.BookingsManage));
        if (everyone && !all) return Result<IReadOnlyList<BookingDto>>.Forbidden("You can see your own bookings only.");

        var query = db.Bookings.AsNoTracking().AsQueryable();
        if (!all) query = query.Where(b => b.OrganizerId == me);
        if (resourceId is { } r) query = query.Where(b => b.ResourceId == r);
        if (status is { } s) query = query.Where(b => b.Status == s);
        if (pendingOnly) query = query.Where(b => b.Status == BookingStatus.Pending);
        var since = from ?? clock.GetUtcNow().AddHours(-12);
        query = query.Where(b => b.EndsAt >= since);
        if (to is { } until) query = query.Where(b => b.StartsAt <= until);

        var rows = await query.OrderBy(b => b.StartsAt).Take(MaxListed).ToListAsync(ct);
        return Result<IReadOnlyList<BookingDto>>.Ok(await ToDtosAsync(rows, me, ct));
    }

    public async Task<Result<BookingDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        if (user.UserId is not { } me) return Result<BookingDto>.Forbidden("Sign in to see bookings.");
        var b = await db.Bookings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (b is null || !await CanSeeAsync(b, me, ct)) return Result<BookingDto>.NotFound();
        return Result<BookingDto>.Ok((await ToDtosAsync([b], me, ct))[0]);
    }

    /// <summary>When a resource is taken between two moments. Shows that a slot is busy and whether it is the caller's, not who has it.</summary>
    public async Task<Result<AvailabilityDto>> AvailabilityAsync(Guid resourceId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        if (to <= from || to - from > TimeSpan.FromDays(35)) return Result<AvailabilityDto>.Invalid("to", "Ask for at most 35 days at a time, and the end must be after the start.");
        if (!await db.Resources.AnyAsync(r => r.Id == resourceId, ct)) return Result<AvailabilityDto>.NotFound();
        var me = user.UserId;
        var rows = await db.Bookings.AsNoTracking()
            .Where(b => b.ResourceId == resourceId && BookingRules.Holding.Contains(b.Status) && b.EndsAt > from && b.StartsAt < to)
            .OrderBy(b => b.StartsAt).Select(b => new BusySlotDto(b.StartsAt, b.EndsAt, b.Status == BookingStatus.Pending, b.OrganizerId == me)).ToListAsync(ct);
        return Result<AvailabilityDto>.Ok(new AvailabilityDto(resourceId, from, to, rows));
    }

    // ---------------- booking ----------------

    public async Task<Result<CreatedBookingsDto>> CreateAsync(CreateBookingRequest r, CancellationToken ct = default)
    {
        if (user.UserId is not { } me) return Result<CreatedBookingsDto>.Forbidden("Sign in to book.");
        var errors = new ValidationErrors();
        if (r.ResourceId is null) errors.Add("resourceId", "Choose what to book.");
        var title = Common.Clean(r.Title);
        if (title is null || title.Length > 200) errors.Add("title", "Give the booking a title (at most 200 characters).");
        var notes = Common.Clean(r.Notes);
        if (notes is { Length: > 1000 }) errors.Add("notes", "Notes are at most 1000 characters.");
        if (r.StartsAt is null) errors.Add("startsAt", "Choose when it starts.");
        if (r.EndsAt is null) errors.Add("endsAt", "Choose when it ends.");
        var repeat = r.RepeatWeeks ?? 1;
        if (repeat is < 1 or > MaxSeries) errors.Add("repeatWeeks", $"A series is from 1 to {MaxSeries} weeks.");
        if (errors.Any) return errors.ToResult<CreatedBookingsDto>();

        var resource = await db.Resources.AsNoTracking().FirstOrDefaultAsync(x => x.Id == r.ResourceId, ct);
        if (resource is null || resource.IsArchived) return Result<CreatedBookingsDto>.Invalid("resourceId", "That resource cannot be booked.");
        var attendees = r.Attendees ?? 1;
        if (attendees < 1 || attendees > resource.Capacity) return Result<CreatedBookingsDto>.Invalid("attendees", $"It holds {resource.Capacity}; choose between 1 and {resource.Capacity} people.");

        var now = clock.GetUtcNow();
        var length = r.EndsAt!.Value - r.StartsAt!.Value;
        var starts = BookingRules.WeeklyStarts(r.StartsAt.Value, repeat, resource.TimeZone);
        for (var i = 0; i < starts.Count; i++)
        {
            if (BookingRules.Check(resource, starts[i], starts[i] + length, now) is { } problem)
                return Result<CreatedBookingsDto>.Invalid(problem.Field, repeat > 1 ? $"{problem.Message} (week {i + 1})" : problem.Message);
        }

        var perms = await permissions.GetAsync(ct);
        if (!perms.Contains(Permissions.BookingsBook)) return Result<CreatedBookingsDto>.Forbidden("You are not allowed to book.");
        var status = resource.RequiresApproval ? BookingStatus.Pending : BookingStatus.Confirmed;
        var seriesId = repeat > 1 ? Guid.NewGuid() : (Guid?)null;

        // The check and the inserts happen under a lock on the resource, in one transaction (retried as a whole if the connection drops).
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await Common.LockResourceAsync(db, resource.Id, ct);

            var holding = await db.Bookings.CountAsync(b => b.OrganizerId == me && BookingRules.Holding.Contains(b.Status) && b.EndsAt > now, ct);
            if (holding + starts.Count > MaxHoldingPerPerson) return Result<CreatedBookingsDto>.Conflict($"You can hold at most {MaxHoldingPerPerson} upcoming bookings. Cancel some first.");

            var windowStart = starts.Min() - TimeSpan.FromMinutes(resource.BufferMinutes);
            var windowEnd = starts.Max() + length + TimeSpan.FromMinutes(resource.BufferMinutes);
            var existing = await db.Bookings.AsNoTracking()
                .Where(b => b.ResourceId == resource.Id && BookingRules.Holding.Contains(b.Status) && b.EndsAt > windowStart && b.StartsAt < windowEnd)
                .Select(b => new { b.StartsAt, b.EndsAt }).ToListAsync(ct);

            var created = new List<Booking>();
            for (var i = 0; i < starts.Count; i++)
            {
                var start = starts[i];
                if (existing.Any(e => BookingRules.Clash(start, start + length, e.StartsAt, e.EndsAt, resource.BufferMinutes)))
                    return Result<CreatedBookingsDto>.Conflict(repeat > 1 ? $"{resource.Name} is already taken at the time in week {i + 1}. Nothing was booked." : $"{resource.Name} is already taken at that time.");
                created.Add(new Booking
                {
                    ResourceId = resource.Id, OrganizerId = me, Title = title!, StartsAt = start, EndsAt = start + length, Attendees = attendees, Status = status, SeriesId = seriesId, Notes = notes, CreatedAt = now,
                });
            }

            db.Bookings.AddRange(created);
            foreach (var b in created) outbox.Enqueue(CalendarMessages.Sync, new CalendarSyncMessage(b.Id));
            if (status == BookingStatus.Pending) outbox.Enqueue(BookingMessages.Pending, new BookingNoticeMessage(created[0].Id, created.Count));
            audit.Record("booking.created", nameof(Booking), created[0].Id.ToString(), JsonSerializer.Serialize(new { resource.Code, title, created[0].StartsAt, created[0].EndsAt, count = created.Count, status }));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Result<CreatedBookingsDto>.Ok(new CreatedBookingsDto(await ToDtosAsync(created, me, ct)));
        });
    }

    public async Task<Result<BookingDto>> CancelAsync(Guid id, CancelBookingRequest r, CancellationToken ct = default)
    {
        if (user.UserId is not { } me) return Result<BookingDto>.Forbidden("Sign in first.");
        var b = await db.Bookings.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (b is null) return Result<BookingDto>.NotFound();
        var perms = await permissions.GetAsync(ct);
        var mine = b.OrganizerId == me;
        if (!mine && !perms.Contains(Permissions.BookingsManage)) return Result<BookingDto>.NotFound();
        if (!Common.TryVersion(r.RowVersion, out var version)) return Result<BookingDto>.Invalid("rowVersion", "Reload and try again.");
        var reason = Common.Clean(r.Reason);
        if (reason is { Length: > 500 } || (!mine && reason is null)) return Result<BookingDto>.Invalid("reason", "A reason is required when cancelling someone else's booking (at most 500 characters).");
        db.Entry(b).Property(x => x.RowVersion).OriginalValue = version;

        var now = clock.GetUtcNow();
        if (!BookingRules.IsHolding(b.Status)) return Result<BookingDto>.Conflict("This booking is no longer active.");
        if (b.EndsAt <= now) return Result<BookingDto>.Conflict("This booking is already over.");

        b.Status = BookingStatus.Cancelled;
        b.CancelledAt = now;
        b.CancelledBy = me;
        b.CancelReason = reason;
        outbox.Enqueue(CalendarMessages.Sync, new CalendarSyncMessage(b.Id));
        if (!mine) outbox.Enqueue(BookingMessages.Decided, new BookingNoticeMessage(b.Id));
        audit.Record("booking.cancelled", nameof(Booking), id.ToString(), JsonSerializer.Serialize(new { b.ResourceId, by = mine ? "organizer" : "manager", reason }));
        return await CommitAsync(b, me, ct);
    }

    /// <summary>Approves a pending booking. Never one's own, whatever one's rights: the person who asks and the person who allows are different.</summary>
    public Task<Result<BookingDto>> ApproveAsync(Guid id, DecideBookingRequest r, CancellationToken ct = default) => DecideAsync(id, r, approve: true, ct);

    public Task<Result<BookingDto>> RejectAsync(Guid id, DecideBookingRequest r, CancellationToken ct = default) => DecideAsync(id, r, approve: false, ct);

    private async Task<Result<BookingDto>> DecideAsync(Guid id, DecideBookingRequest r, bool approve, CancellationToken ct)
    {
        if (user.UserId is not { } me) return Result<BookingDto>.Forbidden("Sign in first.");
        var b = await db.Bookings.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (b is null) return Result<BookingDto>.NotFound();
        if (b.OrganizerId == me) return Result<BookingDto>.Forbidden("You cannot decide your own booking. Ask someone else with approval rights.");
        if (!Common.TryVersion(r.RowVersion, out var version)) return Result<BookingDto>.Invalid("rowVersion", "Reload and try again.");
        var note = Common.Clean(r.Note);
        if (note is { Length: > 500 } || (!approve && note is null)) return Result<BookingDto>.Invalid("note", "Say why it is rejected (at most 500 characters).");
        db.Entry(b).Property(x => x.RowVersion).OriginalValue = version;

        if (b.Status != BookingStatus.Pending) return Result<BookingDto>.Conflict("This booking is not waiting for a decision.");
        var now = clock.GetUtcNow();
        if (b.EndsAt <= now) return Result<BookingDto>.Conflict("This booking is already over.");

        b.Status = approve ? BookingStatus.Confirmed : BookingStatus.Rejected;
        b.DecidedBy = me;
        b.DecidedAt = now;
        b.DecisionNote = note;
        outbox.Enqueue(CalendarMessages.Sync, new CalendarSyncMessage(b.Id));
        outbox.Enqueue(BookingMessages.Decided, new BookingNoticeMessage(b.Id));
        audit.Record(approve ? "booking.approved" : "booking.rejected", nameof(Booking), id.ToString(), JsonSerializer.Serialize(new { b.ResourceId, note }));
        return await CommitAsync(b, me, ct);
    }

    /// <summary>The organizer arrives. Allowed from a few minutes before the start until the grace period is over.</summary>
    public async Task<Result<BookingDto>> CheckInAsync(Guid id, string? rowVersion, CancellationToken ct = default)
    {
        if (user.UserId is not { } me) return Result<BookingDto>.Forbidden("Sign in first.");
        var b = await db.Bookings.FirstOrDefaultAsync(x => x.Id == id && x.OrganizerId == me, ct);
        if (b is null) return Result<BookingDto>.NotFound();
        if (!Common.TryVersion(rowVersion, out var version)) return Result<BookingDto>.Invalid("rowVersion", "Reload and try again.");
        db.Entry(b).Property(x => x.RowVersion).OriginalValue = version;
        if (b.Status != BookingStatus.Confirmed) return Result<BookingDto>.Conflict(b.Status == BookingStatus.Pending ? "This booking is still waiting for approval." : "This booking cannot be checked in to.");

        var resource = await db.Resources.AsNoTracking().FirstAsync(x => x.Id == b.ResourceId, ct);
        var now = clock.GetUtcNow();
        if (!BookingRules.CanCheckIn(b.StartsAt, b.EndsAt, resource.CheckInRequired ? resource.CheckInGraceMinutes : 0, now))
            return Result<BookingDto>.Conflict(now < b.StartsAt ? $"Check-in opens {BookingRules.CheckInOpensMinutesBefore} minutes before the start." : "The time to check in has passed.");

        b.Status = BookingStatus.CheckedIn;
        b.CheckedInAt = now;
        audit.Record("booking.checked_in", nameof(Booking), id.ToString(), JsonSerializer.Serialize(new { b.ResourceId }));
        return await CommitAsync(b, me, ct);
    }

    private async Task<Result<BookingDto>> CommitAsync(Booking b, Guid me, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<BookingDto>.Conflict("Someone else changed this booking. Reload and try again.");
        }

        return Result<BookingDto>.Ok((await ToDtosAsync([b], me, ct))[0]);
    }

    private async Task<bool> CanSeeAsync(Booking b, Guid me, CancellationToken ct)
    {
        if (b.OrganizerId == me) return true;
        var perms = await permissions.GetAsync(ct);
        return perms.Contains(Permissions.BookingsApprove) || perms.Contains(Permissions.BookingsManage);
    }

    private async Task<IReadOnlyList<BookingDto>> ToDtosAsync(List<Booking> rows, Guid me, CancellationToken ct)
    {
        var resourceIds = rows.Select(b => b.ResourceId).Distinct().ToList();
        var resources = await db.Resources.AsNoTracking().Where(r => resourceIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var people = await users.GetAsync(rows.Select(b => b.OrganizerId).Distinct(), ct);
        var now = clock.GetUtcNow();
        return rows.Select(b =>
        {
            var res = resources.GetValueOrDefault(b.ResourceId);
            var canCheckIn = b.OrganizerId == me && b.Status == BookingStatus.Confirmed && res is not null
                && BookingRules.CanCheckIn(b.StartsAt, b.EndsAt, res.CheckInRequired ? res.CheckInGraceMinutes : 0, now);
            return new BookingDto(
                b.Id, b.ResourceId, res?.Code ?? "?", res?.Name ?? "?", res?.Kind ?? ResourceKind.Other, b.OrganizerId, people.TryGetValue(b.OrganizerId, out var p) ? p.DisplayName ?? p.Email : null, b.Title, b.StartsAt, b.EndsAt,
                b.Attendees, b.Status, b.SeriesId, b.Notes, b.DecisionNote, b.CancelReason, b.CheckedInAt, canCheckIn, b.OrganizerId == me, Common.Version(b.RowVersion));
        }).ToList();
    }
}
