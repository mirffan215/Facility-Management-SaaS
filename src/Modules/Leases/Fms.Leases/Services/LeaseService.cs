using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Leases.Domain;
using Fms.Leases.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Leases.Services;

/// <summary>Leases: their terms, how they move from draft to active to ended, and the rent schedule and deadlines worked out from them.</summary>
public sealed class LeaseService(LeasesDbContext db, IFacilityLookup facilities, ICurrentUser user, IAuditPort audit, TimeProvider clock)
{
    public const int MaxLeases = 5000;
    public const int SoonDays = 90;

    public async Task<Page<LeaseSummaryDto>> ListAsync(LeaseState? state, LeaseKind? kind, string? q, int? page, int? pageSize, CancellationToken ct = default)
    {
        var today = Common.Today(clock);
        var query = db.Leases.AsNoTracking().AsQueryable();
        if (state is { } s)
            query = s switch
            {
                LeaseState.Draft => query.Where(l => l.Status == LeaseStatus.Draft),
                LeaseState.Terminated => query.Where(l => l.Status == LeaseStatus.Terminated),
                LeaseState.Expired => query.Where(l => l.Status == LeaseStatus.Active && l.EndOn < today),
                _ => query.Where(l => l.Status == LeaseStatus.Active && l.EndOn >= today),
            };
        if (kind is { } k) query = query.Where(l => l.Kind == k);
        if (Common.Clean(q) is { } text) query = query.Where(l => l.Reference.Contains(text) || l.Title.Contains(text) || l.Counterparty.Contains(text) || (l.LocationName != null && l.LocationName.Contains(text)));
        var total = await query.CountAsync(ct);
        var (number, size) = Page<LeaseSummaryDto>.Normalize(page, pageSize);
        var rows = await query.OrderBy(l => l.Status == LeaseStatus.Terminated).ThenBy(l => l.EndOn).ThenBy(l => l.Reference).Skip((number - 1) * size).Take(size).ToListAsync(ct);
        return new Page<LeaseSummaryDto>(rows.Select(l => ToSummary(l, today)).ToList(), total, number, size);
    }

    public async Task<LeaseSummaryTotalsDto> SummaryAsync(CancellationToken ct = default)
    {
        var today = Common.Today(clock);
        var all = await db.Leases.AsNoTracking().Take(MaxLeases).ToListAsync(ct);
        var active = all.Where(l => LeaseMath.StateOf(l, today) == LeaseState.Active).ToList();
        var expiringSoon = active.Count(l => l.EndOn.DayNumber - today.DayNumber <= SoonDays);
        var deadlinesSoon = active.Count(l => LeaseMath.Upcoming(l, today).Any(d => d.Kind != DeadlineKind.Expiry && d.On.DayNumber - today.DayNumber <= l.AlertLeadDays));
        var rent = active.GroupBy(l => (l.Kind, l.CurrencyCode)).OrderBy(g => g.Key.Kind).ThenBy(g => g.Key.CurrencyCode)
            .Select(g => new RentTotalDto(g.Key.Kind, g.Key.CurrencyCode, g.Sum(l => LeaseMath.NextTwelveMonths(l, today)))).ToList();
        return new LeaseSummaryTotalsDto(active.Count, all.Count(l => l.Status == LeaseStatus.Draft), all.Count(l => LeaseMath.StateOf(l, today) == LeaseState.Expired), expiringSoon, deadlinesSoon, rent);
    }

    public async Task<Result<LeaseDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var l = await db.Leases.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return l is null ? Result<LeaseDto>.NotFound() : Result<LeaseDto>.Ok(ToDto(l));
    }

    public async Task<Result<ScheduleDto>> ScheduleAsync(Guid id, CancellationToken ct = default)
    {
        var l = await db.Leases.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (l is null) return Result<ScheduleDto>.NotFound();
        var rows = LeaseMath.Schedule(l);
        return Result<ScheduleDto>.Ok(new ScheduleDto(l.Id, l.CurrencyCode, rows.Sum(r => r.Amount), rows.Select(r => new InstallmentDto(r.Number, r.DueOn, r.PeriodEnd, r.Amount, r.EscalationStep, r.IsRentFree, r.IsPartial)).ToList()));
    }

    public async Task<Result<LeaseDto>> CreateAsync(SaveLeaseRequest r, CancellationToken ct = default)
    {
        if (await db.Leases.CountAsync(ct) >= MaxLeases) return Result<LeaseDto>.Conflict($"At most {MaxLeases} leases.");
        var l = new Lease { CreatedAt = clock.GetUtcNow(), CreatedBy = user.UserId };
        var invalid = await ApplyAsync(l, r, ct);
        if (invalid is not null) return invalid;
        if (await db.Leases.AnyAsync(x => x.Reference == l.Reference, ct)) return Result<LeaseDto>.Conflict($"A lease with reference {l.Reference} already exists.");
        db.Leases.Add(l);
        audit.Record("lease.created", nameof(Lease), l.Id.ToString(), JsonSerializer.Serialize(new { l.Reference, l.Title, l.Kind }));
        await db.SaveChangesAsync(ct);
        return Result<LeaseDto>.Ok(ToDto(l));
    }

    public async Task<Result<LeaseDto>> UpdateAsync(Guid id, SaveLeaseRequest r, CancellationToken ct = default)
    {
        var l = await db.Leases.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (l is null) return Result<LeaseDto>.NotFound();
        if (l.Status == LeaseStatus.Terminated) return Result<LeaseDto>.Conflict("A terminated lease cannot be changed.");
        if (!Common.TryVersion(r.RowVersion, out var version)) return Result<LeaseDto>.Invalid("rowVersion", "Reload and try again.");
        db.Entry(l).Property(x => x.RowVersion).OriginalValue = version;
        var invalid = await ApplyAsync(l, r, ct);
        if (invalid is not null) { db.ChangeTracker.Clear(); return invalid; }
        if (await db.Leases.AnyAsync(x => x.Reference == l.Reference && x.Id != id, ct)) { db.ChangeTracker.Clear(); return Result<LeaseDto>.Conflict($"A lease with reference {l.Reference} already exists."); }
        l.LastAlertKey = null;
        l.LastAlertOn = null;
        audit.Record("lease.updated", nameof(Lease), id.ToString(), JsonSerializer.Serialize(new { l.Reference, l.Title }));
        return await SaveAsync(l, ct);
    }

    public async Task<Result<LeaseDto>> ActivateAsync(Guid id, ActivateRequest r, CancellationToken ct = default)
    {
        var l = await db.Leases.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (l is null) return Result<LeaseDto>.NotFound();
        if (!Common.TryVersion(r.RowVersion, out var version)) return Result<LeaseDto>.Invalid("rowVersion", "Reload and try again.");
        db.Entry(l).Property(x => x.RowVersion).OriginalValue = version;
        if (l.Status != LeaseStatus.Draft) return Result<LeaseDto>.Conflict("Only a draft lease can be activated.");
        l.Status = LeaseStatus.Active;
        l.ActivatedOn = Common.Today(clock);
        audit.Record("lease.activated", nameof(Lease), id.ToString(), JsonSerializer.Serialize(new { l.Reference }));
        return await SaveAsync(l, ct);
    }

    public async Task<Result<LeaseDto>> ExtendAsync(Guid id, ExtendRequest r, CancellationToken ct = default)
    {
        var l = await db.Leases.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (l is null) return Result<LeaseDto>.NotFound();
        if (!Common.TryVersion(r.RowVersion, out var version)) return Result<LeaseDto>.Invalid("rowVersion", "Reload and try again.");
        db.Entry(l).Property(x => x.RowVersion).OriginalValue = version;
        if (l.Status != LeaseStatus.Active) return Result<LeaseDto>.Conflict("Only an active lease can be extended.");
        var errors = new ValidationErrors();
        var reason = Common.Clean(r.Reason);
        if (reason is null || reason.Length > 500) errors.Add("reason", "Say why (at most 500 characters).");
        if (r.NewEndOn is not { } end) errors.Add("newEndOn", "Give the new end date.");
        else if (end <= l.EndOn) errors.Add("newEndOn", "The new end must be after the current end.");
        else if (LeaseMath.TermMonths(new Lease { StartOn = l.StartOn, EndOn = end }) > LeaseMath.MaxTermMonths) errors.Add("newEndOn", $"A lease cannot run longer than {LeaseMath.MaxTermMonths / 12} years.");
        if (errors.Any) { db.ChangeTracker.Clear(); return errors.ToResult<LeaseDto>(); }

        var from = l.EndOn;
        l.EndOn = r.NewEndOn!.Value;
        l.LastAlertKey = null;
        l.LastAlertOn = null;
        audit.Record("lease.extended", nameof(Lease), id.ToString(), JsonSerializer.Serialize(new { from, to = l.EndOn, reason }));
        return await SaveAsync(l, ct);
    }

    public async Task<Result<LeaseDto>> TerminateAsync(Guid id, TerminateRequest r, CancellationToken ct = default)
    {
        var l = await db.Leases.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (l is null) return Result<LeaseDto>.NotFound();
        if (!Common.TryVersion(r.RowVersion, out var version)) return Result<LeaseDto>.Invalid("rowVersion", "Reload and try again.");
        db.Entry(l).Property(x => x.RowVersion).OriginalValue = version;
        if (l.Status != LeaseStatus.Active) return Result<LeaseDto>.Conflict("Only an active lease can be terminated.");
        var errors = new ValidationErrors();
        var reason = Common.Clean(r.Reason);
        if (reason is null || reason.Length > 500) errors.Add("reason", "Say why (at most 500 characters).");
        var on = r.On ?? Common.Today(clock);
        if (on < l.StartOn) errors.Add("on", "It cannot end before it started.");
        if (errors.Any) { db.ChangeTracker.Clear(); return errors.ToResult<LeaseDto>(); }

        l.Status = LeaseStatus.Terminated;
        l.TerminatedOn = on;
        l.TerminationReason = reason;
        l.LastAlertKey = null;
        audit.Record("lease.terminated", nameof(Lease), id.ToString(), JsonSerializer.Serialize(new { l.Reference, on, reason }));
        return await SaveAsync(l, ct);
    }

    /// <summary>Only a draft that was never active can be removed; anything that was in force stays on record.</summary>
    public async Task<Result<LeaseDto>> DeleteAsync(Guid id, DeleteRequest r, CancellationToken ct = default)
    {
        var l = await db.Leases.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (l is null) return Result<LeaseDto>.NotFound();
        if (l.Status != LeaseStatus.Draft) return Result<LeaseDto>.Conflict("Only a draft lease can be deleted. End an active lease by terminating it.");
        var reason = Common.Clean(r.Reason);
        if (reason is null || reason.Length > 500) return Result<LeaseDto>.Invalid("reason", "Say why (at most 500 characters).");
        var dto = ToDto(l);
        db.Leases.Remove(l);
        audit.Record("lease.deleted", nameof(Lease), id.ToString(), JsonSerializer.Serialize(new { l.Reference, reason }));
        await db.SaveChangesAsync(ct);
        return Result<LeaseDto>.Ok(dto);
    }

    // ---- helpers

    private async Task<Result<LeaseDto>?> ApplyAsync(Lease l, SaveLeaseRequest r, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var reference = Common.Clean(r.Reference)?.ToUpperInvariant();
        if (reference is null || reference.Length > 30 || reference.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/'))) errors.Add("reference", "A short reference of letters, digits, - _ . or / (at most 30).");
        var title = Common.Clean(r.Title);
        if (title is null || title.Length > 200) errors.Add("title", "Give the lease a title (at most 200 characters).");
        var counterparty = Common.Clean(r.Counterparty);
        if (counterparty is null || counterparty.Length > 200) errors.Add("counterparty", "Name the landlord or occupier (at most 200 characters).");
        if (r.Kind is not { } kind || !Enum.IsDefined(kind)) errors.Add("kind", "Say whether the premises are rented or let out.");
        if (r.AreaSquareMetres is < 0 or > 10_000_000) errors.Add("areaSquareMetres", "The area must be between 0 and 10,000,000.");
        if (r.StartOn is null) errors.Add("startOn", "Say when it starts.");
        if (r.EndOn is not { } end) errors.Add("endOn", "Say when it ends.");
        else if (r.StartOn is { } start)
        {
            if (end < start) errors.Add("endOn", "It cannot end before it starts.");
            else if (LeaseMath.TermMonths(new Lease { StartOn = start, EndOn = end }) > LeaseMath.MaxTermMonths) errors.Add("endOn", $"A lease cannot run longer than {LeaseMath.MaxTermMonths / 12} years.");
        }

        var notice = r.NoticeDays ?? 0;
        if (notice is < 0 or > 730) errors.Add("noticeDays", "Notice is from 0 to 730 days.");
        else if (r.StartOn is { } s0 && r.EndOn is { } e0 && e0 >= s0 && e0.AddDays(-notice) < s0) errors.Add("noticeDays", "The notice date would fall before the lease starts.");
        var breakNotice = r.BreakNoticeDays ?? 0;
        if (breakNotice is < 0 or > 365) errors.Add("breakNoticeDays", "Break notice is from 0 to 365 days.");
        if (r.BreakOn is { } br && r.StartOn is { } s1 && r.EndOn is { } e1 && (br <= s1 || br >= e1)) errors.Add("breakOn", "The break date must fall inside the term.");
        if (r.RenewalMonths is < 1 or > 120) errors.Add("renewalMonths", "A renewal option adds 1 to 120 months.");
        var currency = Common.Clean(r.CurrencyCode)?.ToUpperInvariant() ?? "SAR";
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetter)) errors.Add("currencyCode", "Use a three-letter currency code, such as SAR.");
        if (r.BaseRent is null or < 0 or > 1_000_000_000_000m) errors.Add("baseRent", "Give the rent for one period (zero or more).");
        var frequency = r.Frequency ?? RentFrequency.Monthly;
        if (!LeaseMath.Frequencies.Contains((int)frequency)) errors.Add("frequency", "Rent is paid monthly, quarterly, half-yearly or yearly.");
        var escalation = r.EscalationPercent ?? 0;
        if (escalation is < 0 or > 100) errors.Add("escalationPercent", "The yearly rise is from 0 to 100 percent.");
        var every = r.EscalationEveryMonths ?? 12;
        if (escalation > 0 && every is < 1 or > 120) errors.Add("escalationEveryMonths", "The rent rises every 1 to 120 months.");
        var free = r.RentFreeMonths ?? 0;
        if (free < 0 || (r.StartOn is { } s2 && r.EndOn is { } e2 && e2 >= s2 && free > LeaseMath.TermMonths(new Lease { StartOn = s2, EndOn = e2 }) + 1)) errors.Add("rentFreeMonths", "Rent-free months cannot exceed the term.");
        if (r.Deposit is < 0 or > 1_000_000_000_000m) errors.Add("deposit", "The deposit cannot be negative.");
        var lead = r.AlertLeadDays ?? SoonDays;
        if (lead is < 0 or > 365) errors.Add("alertLeadDays", "Warn from 0 to 365 days ahead.");
        var notes = Common.Clean(r.Notes);
        if (notes is { Length: > 2000 }) errors.Add("notes", "At most 2000 characters.");
        FacilityRef? location = null;
        if (r.LocationId is { } lid && (location = await facilities.GetLocationAsync(lid, ct)) is null) errors.Add("locationId", "That place does not exist.");

        // Once a lease is in force, its dates and money are what was agreed: they change through extension or termination, not by editing.
        if (!errors.Any && l.Status == LeaseStatus.Active)
        {
            var same = r.Kind == l.Kind && r.StartOn == l.StartOn && r.EndOn == l.EndOn && currency == l.CurrencyCode && r.BaseRent == l.BaseRent && frequency == l.Frequency
                       && escalation == l.EscalationPercent && (escalation == 0 || every == l.EscalationEveryMonths) && free == l.RentFreeMonths && r.Deposit == l.Deposit;
            if (!same) errors.Add("baseRent", "The dates, rent and kind of an active lease cannot be edited. Extend it, or end it and start a new lease.");
        }

        if (errors.Any) return errors.ToResult<LeaseDto>();

        l.Reference = reference!;
        l.Title = title!;
        l.Counterparty = counterparty!;
        l.Kind = r.Kind!.Value;
        l.LocationId = location?.Id;
        l.LocationName = location?.Name;
        l.AreaSquareMetres = r.AreaSquareMetres;
        l.StartOn = r.StartOn!.Value;
        l.EndOn = r.EndOn!.Value;
        l.NoticeDays = notice;
        l.BreakOn = r.BreakOn;
        l.BreakNoticeDays = r.BreakOn is null ? 0 : breakNotice;
        l.RenewalMonths = r.RenewalMonths;
        l.CurrencyCode = currency;
        l.BaseRent = r.BaseRent!.Value;
        l.Frequency = frequency;
        l.EscalationPercent = escalation;
        l.EscalationEveryMonths = every;
        l.RentFreeMonths = free;
        l.Deposit = r.Deposit;
        l.AlertLeadDays = lead;
        l.Notes = notes;
        return null;
    }

    private async Task<Result<LeaseDto>> SaveAsync(Lease l, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<LeaseDto>.Conflict("Someone else changed this. Reload and try again.");
        }

        return Result<LeaseDto>.Ok(ToDto(l));
    }

    private DeadlineDto ToDeadline(Deadline d, DateOnly today) => new(d.Kind, d.On, d.On.DayNumber - today.DayNumber);

    private LeaseSummaryDto ToSummary(Lease l, DateOnly today)
    {
        var next = l.Status == LeaseStatus.Active ? LeaseMath.Upcoming(l, today).FirstOrDefault() : null;
        return new LeaseSummaryDto(l.Id, l.Reference, l.Title, l.Kind, LeaseMath.StateOf(l, today), l.Counterparty, l.LocationName, l.StartOn, l.EndOn, l.CurrencyCode, l.BaseRent, l.Frequency, next is null ? null : ToDeadline(next, today));
    }

    private LeaseDto ToDto(Lease l)
    {
        var today = Common.Today(clock);
        var deadlines = l.Status == LeaseStatus.Active ? LeaseMath.Upcoming(l, today).Select(d => ToDeadline(d, today)).ToList() : [];
        return new LeaseDto(l.Id, l.Reference, l.Title, l.Kind, LeaseMath.StateOf(l, today), l.Counterparty, l.LocationId, l.LocationName, l.AreaSquareMetres, l.StartOn, l.EndOn, l.NoticeDays,
            l.NoticeDays > 0 ? LeaseMath.NoticeDeadline(l) : null, l.BreakOn, l.BreakNoticeDays, LeaseMath.BreakDeadline(l), l.RenewalMonths, l.CurrencyCode, l.BaseRent, l.Frequency, l.EscalationPercent,
            l.EscalationEveryMonths, l.RentFreeMonths, l.Deposit, l.AlertLeadDays, l.Notes, l.ActivatedOn, l.TerminatedOn, l.TerminationReason, l.Status != LeaseStatus.Draft, deadlines,
            LeaseMath.Schedule(l).Sum(i => i.Amount), Common.Version(l.RowVersion));
    }
}
