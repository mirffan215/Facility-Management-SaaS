using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Work.Domain;
using Fms.Work.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Work.Services;

/// <summary>Working calendars and SLA policies. Changes apply to work whose clocks start afterwards; running clocks are not moved.</summary>
public sealed class SlaSetupService(WorkDbContext db, IAuditPort audit, IFacilityLookup facilities)
{
    private const int MaxHolidays = 400;

    // ---------------- calendars ----------------

    public async Task<IReadOnlyList<CalendarDto>> CalendarsAsync(bool includeArchived, CancellationToken ct = default)
    {
        var calendars = await db.Calendars.AsNoTracking().Where(c => includeArchived || !c.IsArchived).OrderBy(c => c.Name).ToListAsync(ct);
        var ids = calendars.Select(c => c.Id).ToList();
        var holidays = await db.CalendarHolidays.AsNoTracking().Where(h => ids.Contains(h.CalendarId)).OrderBy(h => h.Date).ToListAsync(ct);
        var sites = await db.CalendarSites.AsNoTracking().Where(s => ids.Contains(s.CalendarId)).ToListAsync(ct);
        return calendars.Select(c => ToDto(c, holidays.Where(h => h.CalendarId == c.Id), sites.Where(s => s.CalendarId == c.Id))).ToList();
    }

    public async Task<Result<CalendarDto>> CreateCalendarAsync(SaveCalendarRequest r, CancellationToken ct = default)
    {
        var checks = await CheckCalendarAsync(r, null, ct);
        if (!checks.IsOk) return checks.Map(_ => (CalendarDto)null!);

        var calendar = new WorkCalendar { Name = r.Name!.Trim(), TimeZoneId = r.TimeZoneId!.Trim(), HoursJson = checks.Value!, IsDefault = r.IsDefault };
        return await SaveCalendarAsync(calendar, r, isNew: true, ct);
    }

    public async Task<Result<CalendarDto>> UpdateCalendarAsync(Guid id, SaveCalendarRequest r, CancellationToken ct = default)
    {
        var calendar = await db.Calendars.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (calendar is null) return Result<CalendarDto>.NotFound();
        if (!TryVersion(r.RowVersion, out var version)) return Result<CalendarDto>.Invalid("rowVersion", "rowVersion is required.");
        var checks = await CheckCalendarAsync(r, calendar, ct);
        if (!checks.IsOk) return checks.Map(_ => (CalendarDto)null!);

        calendar.Name = r.Name!.Trim();
        calendar.TimeZoneId = r.TimeZoneId!.Trim();
        calendar.HoursJson = checks.Value!;
        calendar.IsDefault = r.IsDefault;
        db.Entry(calendar).Property(c => c.RowVersion).OriginalValue = version;
        db.Entry(calendar).Property(c => c.Name).IsModified = true; // always write the row, so a stale version is detected even when only holidays or sites changed
        return await SaveCalendarAsync(calendar, r, isNew: false, ct);
    }

    public async Task<Result<CalendarDto>> SetCalendarArchivedAsync(Guid id, bool archived, string? rowVersion, CancellationToken ct = default)
    {
        var calendar = await db.Calendars.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (calendar is null) return Result<CalendarDto>.NotFound();
        if (!TryVersion(rowVersion, out var version)) return Result<CalendarDto>.Invalid("rowVersion", "rowVersion is required.");
        calendar.IsArchived = archived;
        if (archived) calendar.IsDefault = false;
        db.Entry(calendar).Property(c => c.RowVersion).OriginalValue = version;
        audit.Record(archived ? "workcalendar.archived" : "workcalendar.restored", nameof(WorkCalendar), id.ToString());
        return await SaveAsync(async () => (await CalendarsAsync(true, ct)).First(c => c.Id == id), ct);
    }

    private async Task<Result<string>> CheckCalendarAsync(SaveCalendarRequest r, WorkCalendar? existing, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 200) errors.Add("name", "Name is required (max 200 characters).");
        if (string.IsNullOrWhiteSpace(r.TimeZoneId) || !TimeZoneInfo.TryFindSystemTimeZoneById(r.TimeZoneId.Trim(), out _)) errors.Add("timeZoneId", "Choose a valid time zone (for example Asia/Riyadh).");

        string hoursJson = "";
        if (r.Hours is null) errors.Add("hours", "Working hours are required.");
        else
        {
            var week = WorkingHours.TryParse(JsonSerializer.Serialize(r.Hours.ToDictionary(h => h.Key, h => h.Value is { Start: not null, End: not null } v ? new { start = v.Start, end = v.End } : null)), out var hoursError);
            if (week is null) errors.Add("hours", hoursError ?? "Working hours are not valid.");
            else hoursJson = WorkingHours.Serialize(week);
        }

        var holidays = r.Holidays ?? [];
        if (holidays.Count > MaxHolidays) errors.Add("holidays", $"At most {MaxHolidays} holidays.");
        if (holidays.Any(h => string.IsNullOrWhiteSpace(h.Name) || h.Name.Length > 200)) errors.Add("holidays", "Every holiday needs a name (max 200 characters).");
        if (holidays.Select(h => h.Date).Distinct().Count() != holidays.Count) errors.Add("holidays", "A date is listed twice.");

        var siteIds = (r.SiteIds ?? []).Distinct().ToList();
        if (siteIds.Count > 0)
        {
            var found = await facilities.LocationsAsync(siteIds, ct);
            foreach (var site in siteIds)
            {
                if (!found.TryGetValue(site, out var location) || location.Kind != "Site") { errors.Add("siteIds", "Only sites can follow a calendar."); break; }
            }

            var taken = await db.CalendarSites.AsNoTracking().Where(s => siteIds.Contains(s.SiteId) && s.CalendarId != (existing == null ? Guid.Empty : existing.Id)).AnyAsync(ct);
            if (taken) errors.Add("siteIds", "A site can follow only one calendar; one of these already follows another.");
        }

        return errors.Any ? errors.ToResult<string>() : Result<string>.Ok(hoursJson);
    }

    private async Task<Result<CalendarDto>> SaveCalendarAsync(WorkCalendar calendar, SaveCalendarRequest r, bool isNew, CancellationToken ct)
    {
        var holidays = (r.Holidays ?? []).Select(h => new WorkCalendarHoliday { CalendarId = calendar.Id, Date = h.Date, Name = h.Name.Trim() }).ToList();
        var sites = (r.SiteIds ?? []).Distinct().Select(s => new WorkCalendarSite { CalendarId = calendar.Id, SiteId = s }).ToList();

        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                if (calendar.IsDefault)
                {
                    // Only one default: step the old one down first so the unique index is never violated in between.
                    var others = await db.Calendars.Where(c => c.IsDefault && c.Id != calendar.Id).ToListAsync(ct);
                    foreach (var o in others) o.IsDefault = false;
                    if (others.Count > 0) await db.SaveChangesAsync(ct);
                }

                if (isNew && db.Entry(calendar).State == EntityState.Detached) db.Calendars.Add(calendar);
                else if (!isNew)
                {
                    db.CalendarHolidays.RemoveRange(await db.CalendarHolidays.Where(h => h.CalendarId == calendar.Id).ToListAsync(ct));
                    db.CalendarSites.RemoveRange(await db.CalendarSites.Where(s => s.CalendarId == calendar.Id).ToListAsync(ct));
                    await db.SaveChangesAsync(ct);
                }

                db.CalendarHolidays.AddRange(holidays);
                db.CalendarSites.AddRange(sites);
                audit.Record(isNew ? "workcalendar.created" : "workcalendar.updated", nameof(WorkCalendar), calendar.Id.ToString(),
                    JsonSerializer.Serialize(new { calendar.Name, calendar.TimeZoneId, calendar.IsDefault, holidays = holidays.Count, sites = sites.Count }));
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return Result<CalendarDto>.Ok(ToDto(calendar, holidays, sites));
            });
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<CalendarDto>.Conflict("The calendar was changed by someone else. Reload it and try again.");
        }
        catch (DbUpdateException ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return Result<CalendarDto>.Conflict("That name, default or site is already used by another calendar.");
        }
    }

    // ---------------- policies ----------------

    public async Task<IReadOnlyList<SlaPolicyDto>> PoliciesAsync(bool includeArchived, CancellationToken ct = default)
    {
        var policies = await db.SlaPolicies.AsNoTracking().Where(p => includeArchived || !p.IsArchived).OrderBy(p => p.Name).ToListAsync(ct);
        var ids = policies.Select(p => p.Id).ToList();
        var targets = await db.SlaTargets.AsNoTracking().Where(t => ids.Contains(t.PolicyId)).ToListAsync(ct);
        var categoryIds = policies.Where(p => p.CategoryId != null).Select(p => p.CategoryId!.Value).ToList();
        var categories = await db.Categories.AsNoTracking().Where(c => categoryIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        return policies.Select(p => ToDto(p, targets.Where(t => t.PolicyId == p.Id), p.CategoryId is { } c ? categories.GetValueOrDefault(c) : null)).ToList();
    }

    public async Task<Result<SlaPolicyDto>> CreatePolicyAsync(SaveSlaPolicyRequest r, CancellationToken ct = default)
    {
        var errors = await CheckPolicyAsync(r, ct);
        if (errors.Any) return errors.ToResult<SlaPolicyDto>();
        var policy = new SlaPolicy { Name = r.Name!.Trim(), CategoryId = r.CategoryId, IsDefault = r.IsDefault };
        return await SavePolicyAsync(policy, r, isNew: true, ct);
    }

    public async Task<Result<SlaPolicyDto>> UpdatePolicyAsync(Guid id, SaveSlaPolicyRequest r, CancellationToken ct = default)
    {
        var policy = await db.SlaPolicies.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (policy is null) return Result<SlaPolicyDto>.NotFound();
        if (!TryVersion(r.RowVersion, out var version)) return Result<SlaPolicyDto>.Invalid("rowVersion", "rowVersion is required.");
        var errors = await CheckPolicyAsync(r, ct);
        if (errors.Any) return errors.ToResult<SlaPolicyDto>();

        policy.Name = r.Name!.Trim();
        policy.CategoryId = r.CategoryId;
        policy.IsDefault = r.IsDefault;
        db.Entry(policy).Property(p => p.RowVersion).OriginalValue = version;
        db.Entry(policy).Property(p => p.Name).IsModified = true; // always write the row, so a stale version is detected even when only the targets changed
        return await SavePolicyAsync(policy, r, isNew: false, ct);
    }

    public async Task<Result<SlaPolicyDto>> SetPolicyArchivedAsync(Guid id, bool archived, string? rowVersion, CancellationToken ct = default)
    {
        var policy = await db.SlaPolicies.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (policy is null) return Result<SlaPolicyDto>.NotFound();
        if (!TryVersion(rowVersion, out var version)) return Result<SlaPolicyDto>.Invalid("rowVersion", "rowVersion is required.");
        policy.IsArchived = archived;
        db.Entry(policy).Property(p => p.RowVersion).OriginalValue = version;
        audit.Record(archived ? "slapolicy.archived" : "slapolicy.restored", nameof(SlaPolicy), id.ToString());
        return await SaveAsync(async () => (await PoliciesAsync(true, ct)).First(p => p.Id == id), ct);
    }

    private async Task<ValidationErrors> CheckPolicyAsync(SaveSlaPolicyRequest r, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 200) errors.Add("name", "Name is required (max 200 characters).");
        if (r.IsDefault == (r.CategoryId is not null)) errors.Add("categoryId", "A policy is either the default for everything or for one category.");
        if (r.CategoryId is { } c && !await db.Categories.AnyAsync(x => x.Id == c, ct)) errors.Add("categoryId", "Category not found.");

        var targets = r.Targets ?? [];
        var priorities = Enum.GetValues<WorkPriority>();
        if (targets.Count != priorities.Length || priorities.Any(p => targets.Count(t => t.Priority == p) != 1))
            errors.Add("targets", "Give targets for each of the four priorities, once each.");
        foreach (var t in targets)
        {
            if (t.ResponseMinutes is < 1 or > SlaRules.MaxMinutes || t.ResolutionMinutes is < 1 or > SlaRules.MaxMinutes) errors.Add("targets", $"{t.Priority}: minutes must be between 1 and {SlaRules.MaxMinutes}.");
            else if (t.ResolutionMinutes < t.ResponseMinutes) errors.Add("targets", $"{t.Priority}: resolution cannot be sooner than response.");
        }

        return errors;
    }

    private async Task<Result<SlaPolicyDto>> SavePolicyAsync(SlaPolicy policy, SaveSlaPolicyRequest r, bool isNew, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                if (policy.IsDefault)
                {
                    var others = await db.SlaPolicies.Where(p => p.IsDefault && !p.IsArchived && p.Id != policy.Id).ToListAsync(ct);
                    foreach (var o in others) o.IsDefault = false;
                    if (others.Count > 0) await db.SaveChangesAsync(ct);
                }

                if (isNew && db.Entry(policy).State == EntityState.Detached) db.SlaPolicies.Add(policy);
                else if (!isNew)
                {
                    db.SlaTargets.RemoveRange(await db.SlaTargets.Where(t => t.PolicyId == policy.Id).ToListAsync(ct));
                    await db.SaveChangesAsync(ct);
                }

                var targets = r.Targets!.Select(t => new SlaTarget { PolicyId = policy.Id, Priority = t.Priority, ResponseMinutes = t.ResponseMinutes, ResolutionMinutes = t.ResolutionMinutes }).ToList();
                db.SlaTargets.AddRange(targets);
                audit.Record(isNew ? "slapolicy.created" : "slapolicy.updated", nameof(SlaPolicy), policy.Id.ToString(),
                    JsonSerializer.Serialize(new { policy.Name, policy.CategoryId, policy.IsDefault, targets = targets.Select(t => new { t.Priority, t.ResponseMinutes, t.ResolutionMinutes }) }));
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return Result<SlaPolicyDto>.Ok(ToDto(policy, targets, null));
            });
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<SlaPolicyDto>.Conflict("The policy was changed by someone else. Reload it and try again.");
        }
        catch (DbUpdateException ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return Result<SlaPolicyDto>.Conflict("There is already a policy for that category (or a default policy).");
        }
    }

    // ---------------- helpers ----------------

    private async Task<Result<T>> SaveAsync<T>(Func<Task<T>> read, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return Result<T>.Ok(await read());
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<T>.Conflict("It was changed by someone else. Reload it and try again.");
        }
        catch (DbUpdateException ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return Result<T>.Conflict("That would clash with an existing calendar or policy.");
        }
    }

    private static CalendarDto ToDto(WorkCalendar c, IEnumerable<WorkCalendarHoliday> holidays, IEnumerable<WorkCalendarSite> sites)
    {
        var week = WorkingHours.TryParse(c.HoursJson, out _) ?? new Dictionary<DayOfWeek, WorkingDay?>();
        var hours = WorkingHours.Days.ToDictionary(d => d.ToString(),
            d => week.GetValueOrDefault(d) is { } w ? new DayHoursDto(Fmt(w.Start), Fmt(w.End)) : null);
        return new CalendarDto(c.Id, c.Name, c.TimeZoneId, hours, c.IsDefault, c.IsArchived,
            holidays.OrderBy(h => h.Date).Select(h => new HolidayDto(h.Date, h.Name)).ToList(), sites.Select(s => s.SiteId).ToList(), Convert.ToBase64String(c.RowVersion));
    }

    private static string Fmt(TimeSpan t) => t == TimeSpan.FromHours(24) ? "24:00" : t.ToString(@"hh\:mm");

    private static SlaPolicyDto ToDto(SlaPolicy p, IEnumerable<SlaTarget> targets, string? categoryName) =>
        new(p.Id, p.Name, p.CategoryId, categoryName, p.IsDefault, p.IsArchived,
            targets.OrderBy(t => t.Priority).Select(t => new SlaTargetDto(t.Priority, t.ResponseMinutes, t.ResolutionMinutes)).ToList(), Convert.ToBase64String(p.RowVersion));

    private static bool TryVersion(string? value, out byte[] version)
    {
        version = [];
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { version = Convert.FromBase64String(value); return version.Length > 0; }
        catch (FormatException) { return false; }
    }
}
