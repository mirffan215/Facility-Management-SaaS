using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Messaging;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Visitors.Domain;
using Fms.Visitors.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Visitors.Services;

/// <summary>Expecting, welcoming and seeing off visitors. Hosts see their own visitors; reception sees everyone's and does the checking in and out.</summary>
public sealed class VisitService(
    VisitorsDbContext db, IFacilityLookup facilities, IUserDirectory users, ICurrentUser user, ICurrentPermissions permissions, IAuditPort audit, IOutbox outbox, PassService passes, TimeProvider clock)
{
    public const int MaxOpenPerHost = 100;
    public const int MaxListed = 300;

    // ---------------- reading ----------------

    /// <summary>The caller's own visitors: recent and coming.</summary>
    public async Task<Result<IReadOnlyList<VisitDto>>> MineAsync(CancellationToken ct = default)
    {
        if (user.UserId is not { } me) return Result<IReadOnlyList<VisitDto>>.Forbidden("Sign in first.");
        var since = clock.GetUtcNow().AddDays(-7);
        var rows = await db.Visits.AsNoTracking().Where(v => v.HostId == me && v.ExpectedAt >= since).OrderBy(v => v.ExpectedAt).Take(MaxListed).ToListAsync(ct);
        return Result<IReadOnlyList<VisitDto>>.Ok(rows.Select(v => ToDto(v, clock.GetUtcNow())).ToList());
    }

    /// <summary>Everyone's visitors for reception: by day, status, site or a name.</summary>
    public async Task<IReadOnlyList<VisitDto>> ListAsync(DateOnly? day, VisitStatus? status, Guid? siteId, string? q, CancellationToken ct = default)
    {
        var query = db.Visits.AsNoTracking().AsQueryable();
        var d = day ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var from = new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        // A day is judged by the server's UTC calendar; the screen asks for a wide enough window to cover the site's own day.
        query = query.Where(v => v.ExpectedAt >= from.AddHours(-12) && v.ExpectedAt < from.AddHours(36));
        if (status is { } s) query = query.Where(v => v.Status == s);
        if (siteId is { } site) query = query.Where(v => v.SiteId == site);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(v => v.VisitorName.Contains(term) || (v.VisitorCompany != null && v.VisitorCompany.Contains(term)) || v.HostName.Contains(term));
        }

        var rows = await query.OrderBy(v => v.ExpectedAt).Take(MaxListed).ToListAsync(ct);
        var now = clock.GetUtcNow();
        return rows.Select(v => ToDto(v, now)).ToList();
    }

    /// <summary>Who is on site now, longest first, optionally for one site. The list a fire roll call starts from.</summary>
    public async Task<IReadOnlyList<VisitDto>> OnSiteAsync(Guid? siteId, CancellationToken ct = default)
    {
        var query = db.Visits.AsNoTracking().Where(v => v.Status == VisitStatus.CheckedIn);
        if (siteId is { } site) query = query.Where(v => v.SiteId == site);
        var rows = await query.OrderBy(v => v.CheckedInAt).Take(MaxListed).ToListAsync(ct);
        var now = clock.GetUtcNow();
        return rows.Select(v => ToDto(v, now)).ToList();
    }

    /// <summary>Finds a visit from what reception scanned or typed (the pass code, or the address its QR code holds).</summary>
    public async Task<Result<VisitDto>> LookupAsync(string? scanned, CancellationToken ct = default)
    {
        var code = VisitorRules.CodeFrom(scanned);
        if (code is null) return Result<VisitDto>.NotFound("That is not a visitor pass.");
        var hash = Common.Hash(code);
        var v = await db.Visits.AsNoTracking().FirstOrDefaultAsync(x => x.PassHash == hash && x.AnonymisedAt == null, ct);
        return v is null ? Result<VisitDto>.NotFound("That is not a visitor pass.") : Result<VisitDto>.Ok(ToDto(v, clock.GetUtcNow()));
    }

    // ---------------- expecting ----------------

    /// <summary>Pre-registers a visitor. A host expects their own; reception may expect one on a host's behalf.</summary>
    public async Task<Result<CreatedVisitDto>> CreateAsync(CreateVisitRequest r, CancellationToken ct = default)
    {
        if (user.UserId is not { } me) return Result<CreatedVisitDto>.Forbidden("Sign in first.");
        var errors = new ValidationErrors();
        var name = Common.Clean(r.VisitorName);
        if (name is null || name.Length > 200) errors.Add("visitorName", "Give the visitor's name (at most 200 characters).");
        var company = Common.Clean(r.VisitorCompany);
        if (company is { Length: > 200 }) errors.Add("visitorCompany", "The company is at most 200 characters.");
        var email = Common.Clean(r.VisitorEmail);
        if (email is not null && !Common.IsEmail(email)) errors.Add("visitorEmail", "That does not look like an email address.");
        var phone = Common.Clean(r.VisitorPhone);
        if (phone is { Length: > 40 }) errors.Add("visitorPhone", "The phone number is at most 40 characters.");
        var purpose = Common.Clean(r.Purpose);
        if (purpose is { Length: > 500 }) errors.Add("purpose", "The purpose is at most 500 characters.");
        if (r.Kind is not { } kind || !Enum.IsDefined(kind)) errors.Add("kind", "Choose what kind of visit this is.");
        if (r.SiteId is null) errors.Add("siteId", "Choose the site being visited.");
        if (r.ExpectedAt is null) errors.Add("expectedAt", "Choose when the visitor is expected.");
        var now = clock.GetUtcNow();
        if (r.ExpectedAt is { } at)
        {
            if (at < now.AddHours(-12)) errors.Add("expectedAt", "That time has already passed.");
            else if (at > now.AddDays(365)) errors.Add("expectedAt", "Visits can be expected at most a year ahead.");
            if (r.ExpectedUntil is { } until && (until < at || until - at > VisitorRules.MaxStay)) errors.Add("expectedUntil", $"The visit must end after it starts, and last at most {VisitorRules.MaxStay.Days} days.");
        }

        if (errors.Any) return errors.ToResult<CreatedVisitDto>();

        var site = await facilities.GetLocationAsync(r.SiteId!.Value, ct);
        if (site is not { Kind: "Site" }) return Result<CreatedVisitDto>.Invalid("siteId", "Choose one of your sites.");

        var perms = await permissions.GetAsync(ct);
        var hostId = me;
        if (r.HostId is { } other && other != me)
        {
            if (!perms.Contains(Permissions.VisitorsReception)) return Result<CreatedVisitDto>.Forbidden("Only reception can expect a visitor for someone else.");
            hostId = other;
        }

        var host = (await users.GetAsync([hostId], ct)).GetValueOrDefault(hostId);
        if (host is not { IsActive: true }) return Result<CreatedVisitDto>.Invalid("hostId", "That host does not exist or is not active.");
        if (await db.Visits.CountAsync(v => v.HostId == hostId && v.Status == VisitStatus.Expected && v.ExpectedAt >= now.AddDays(-1), ct) >= MaxOpenPerHost)
            return Result<CreatedVisitDto>.Conflict($"A host can have at most {MaxOpenPerHost} visitors expected. Cancel some first.");

        var code = Common.NewCode();
        var v = new Visit
        {
            HostId = hostId, HostName = host.DisplayName ?? host.Email ?? "—", SiteId = site.Id, SiteName = site.Name, Kind = r.Kind!.Value, VisitorName = name!, VisitorCompany = company, VisitorEmail = email,
            VisitorPhone = phone, Purpose = purpose, ExpectedAt = r.ExpectedAt!.Value, ExpectedUntil = r.ExpectedUntil, PassHash = Common.Hash(code), CreatedAt = now, CreatedBy = me,
        };
        db.Visits.Add(v);
        audit.Record("visit.created", nameof(Visit), v.Id.ToString(), JsonSerializer.Serialize(new { v.HostId, v.SiteId, v.Kind, v.ExpectedAt }));
        await db.SaveChangesAsync(ct);
        return Result<CreatedVisitDto>.Ok(new CreatedVisitDto(ToDto(v, now), $"/visit/{code}", passes.UrlFor(code), passes.QrSvg(code)));
    }

    /// <summary>A new pass for a visit; the old code stops working. For a pass that was lost or sent to the wrong person.</summary>
    public async Task<Result<CreatedVisitDto>> NewPassAsync(Guid id, CancellationToken ct = default)
    {
        var v = await FindMineOrAnyAsync(id, ct);
        if (v.Visit is null) return Result<CreatedVisitDto>.NotFound();
        if (v.Visit.Status != VisitStatus.Expected) return Result<CreatedVisitDto>.Conflict("Only a visit that has not happened yet can have a new pass.");
        var code = Common.NewCode();
        v.Visit.PassHash = Common.Hash(code);
        audit.Record("visit.pass_renewed", nameof(Visit), id.ToString());
        await db.SaveChangesAsync(ct);
        return Result<CreatedVisitDto>.Ok(new CreatedVisitDto(ToDto(v.Visit, clock.GetUtcNow()), $"/visit/{code}", passes.UrlFor(code), passes.QrSvg(code)));
    }

    public async Task<Result<VisitDto>> CancelAsync(Guid id, CancelVisitRequest r, CancellationToken ct = default)
    {
        var found = await FindMineOrAnyAsync(id, ct);
        if (found.Visit is not { } v) return Result<VisitDto>.NotFound();
        if (!Common.TryVersion(r.RowVersion, out var version)) return Result<VisitDto>.Invalid("rowVersion", "Reload and try again.");
        var reason = Common.Clean(r.Reason);
        if (reason is { Length: > 500 }) return Result<VisitDto>.Invalid("reason", "The reason is at most 500 characters.");
        db.Entry(v).Property(x => x.RowVersion).OriginalValue = version;
        if (v.Status != VisitStatus.Expected) return Result<VisitDto>.Conflict("Only a visit that has not happened yet can be cancelled.");

        v.Status = VisitStatus.Cancelled;
        v.CancelledAt = clock.GetUtcNow();
        v.CancelReason = reason;
        audit.Record("visit.cancelled", nameof(Visit), id.ToString(), JsonSerializer.Serialize(new { reason }));
        return await CommitAsync(v, ct);
    }

    // ---------------- at the desk ----------------

    /// <summary>
    /// Welcomes the visitor. Refused when their name is on the watchlist unless someone with security rights gives a reason (reception is only told to call security, not why),
    /// and for contractors when the induction is required and has not been acknowledged.
    /// </summary>
    public async Task<Result<VisitDto>> CheckInAsync(Guid id, CheckInRequest r, CancellationToken ct = default)
    {
        if (user.UserId is not { } me) return Result<VisitDto>.Forbidden("Sign in first.");
        var v = await db.Visits.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (v is null) return Result<VisitDto>.NotFound();
        if (!Common.TryVersion(r.RowVersion, out var version)) return Result<VisitDto>.Invalid("rowVersion", "Reload and try again.");
        var badge = Common.Clean(r.BadgeNumber);
        if (badge is { Length: > 20 }) return Result<VisitDto>.Invalid("badgeNumber", "The badge number is at most 20 characters.");
        db.Entry(v).Property(x => x.RowVersion).OriginalValue = version;

        var now = clock.GetUtcNow();
        if (v.Status == VisitStatus.CheckedIn) return Result<VisitDto>.Conflict("This visitor is already checked in.");
        if (!VisitorRules.CanCheckIn(v.Status, v.ExpectedAt, v.ExpectedUntil, now))
            return Result<VisitDto>.Conflict(v.Status == VisitStatus.Expected ? "This visit is not due yet, or its time is over." : "This visit can no longer be checked in.");

        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (v.Kind == VisitKind.Contractor && settings is { RequireInduction: true })
        {
            if (r.InductionAcknowledged != true) return Result<VisitDto>.Invalid("inductionAcknowledged", "The visitor must acknowledge the site induction first.");
        }

        var key = VisitorRules.Normalize(v.VisitorName);
        var match = key.Length > 0 && await db.Watchlist.AnyAsync(w => w.NameKey == key && w.RemovedAt == null, ct);
        if (match)
        {
            var perms = await permissions.GetAsync(ct);
            var reason = Common.Clean(r.OverrideReason);
            if (!perms.Contains(Permissions.VisitorsSecurity) || reason is null || reason.Length > 500)
            {
                audit.Record("visit.watchlist_blocked", nameof(Visit), id.ToString());
                await db.SaveChangesAsync(ct); // the audit entry stands even though the check-in does not
                return Result<VisitDto>.Forbidden("This visitor cannot be checked in. Please call security.");
            }

            v.WatchlistMatch = true;
            v.OverrideReason = reason;
            v.OverrideBy = me;
            audit.Record("visit.watchlist_override", nameof(Visit), id.ToString(), JsonSerializer.Serialize(new { reason }));
        }

        if (v.Kind == VisitKind.Contractor && settings is { RequireInduction: true }) v.InductionAt = now;
        v.Status = VisitStatus.CheckedIn;
        v.CheckedInAt = now;
        v.CheckedInBy = me;
        v.BadgeNumber = badge;
        outbox.Enqueue(VisitorMessages.Arrived, new VisitNoticeMessage(v.Id));
        audit.Record("visit.checked_in", nameof(Visit), id.ToString(), JsonSerializer.Serialize(new { v.SiteId, badge }));
        return await CommitAsync(v, ct);
    }

    public async Task<Result<VisitDto>> CheckOutAsync(Guid id, CheckOutRequest r, CancellationToken ct = default)
    {
        if (user.UserId is not { } me) return Result<VisitDto>.Forbidden("Sign in first.");
        var v = await db.Visits.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (v is null) return Result<VisitDto>.NotFound();
        if (!Common.TryVersion(r.RowVersion, out var version)) return Result<VisitDto>.Invalid("rowVersion", "Reload and try again.");
        db.Entry(v).Property(x => x.RowVersion).OriginalValue = version;
        if (v.Status != VisitStatus.CheckedIn) return Result<VisitDto>.Conflict("This visitor is not checked in.");

        v.Status = VisitStatus.CheckedOut;
        v.CheckedOutAt = clock.GetUtcNow();
        v.CheckedOutBy = me;
        audit.Record("visit.checked_out", nameof(Visit), id.ToString());
        return await CommitAsync(v, ct);
    }

    // ---------------- helpers ----------------

    private async Task<(Visit? Visit, bool IsReception)> FindMineOrAnyAsync(Guid id, CancellationToken ct)
    {
        if (user.UserId is not { } me) return (null, false);
        var perms = await permissions.GetAsync(ct);
        var reception = perms.Contains(Permissions.VisitorsReception);
        var v = await db.Visits.FirstOrDefaultAsync(x => x.Id == id && (reception || x.HostId == me), ct);
        return (v, reception);
    }

    private async Task<Result<VisitDto>> CommitAsync(Visit v, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<VisitDto>.Conflict("Someone else changed this visit. Reload and try again.");
        }

        return Result<VisitDto>.Ok(ToDto(v, clock.GetUtcNow()));
    }

    internal static VisitDto ToDto(Visit v, DateTimeOffset now) => new(
        v.Id, v.HostId, v.HostName, v.SiteId, v.SiteName, v.Kind, v.VisitorName, v.VisitorCompany, v.VisitorEmail, v.VisitorPhone, v.Purpose, v.ExpectedAt, v.ExpectedUntil, v.Status, v.CheckedInAt, v.CheckedOutAt,
        v.BadgeNumber, v.InductionAt, v.CancelReason, VisitorRules.CanCheckIn(v.Status, v.ExpectedAt, v.ExpectedUntil, now), v.AnonymisedAt is not null, Common.Version(v.RowVersion));
}
