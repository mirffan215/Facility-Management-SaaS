using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Work.Domain;
using Fms.Work.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Work.Services;

/// <summary>Preventive maintenance set-up (templates, schedules), the plans they produce, and the forecast calendar.</summary>
public sealed class PmService(WorkDbContext db, IAuditPort audit, ICurrentUser user, IFacilityLookup facilities, IUserDirectory users, PmEngine engine, TimeProvider clock)
{
    public const int MaxForecastItems = 2000;
    public const int MaxForecastDays = 400;

    // ---------------- templates ----------------

    public async Task<IReadOnlyList<PmTemplateDto>> TemplatesAsync(bool includeArchived, CancellationToken ct = default)
    {
        var rows = await db.PmTemplates.AsNoTracking().Where(t => includeArchived || !t.IsArchived).OrderBy(t => t.Name).ToListAsync(ct);
        var checklistIds = rows.Where(r => r.ChecklistTemplateId != null).Select(r => r.ChecklistTemplateId!.Value).ToList();
        var checklists = await db.ChecklistTemplates.AsNoTracking().Where(c => checklistIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        return rows.Select(t => ToDto(t, t.ChecklistTemplateId is { } c ? checklists.GetValueOrDefault(c) : null)).ToList();
    }

    public async Task<Result<PmTemplateDto>> SaveTemplateAsync(Guid? id, SavePmTemplateRequest r, CancellationToken ct = default)
    {
        PmTemplate? template = null;
        if (id is { } existing)
        {
            template = await db.PmTemplates.FirstOrDefaultAsync(t => t.Id == existing, ct);
            if (template is null) return Result<PmTemplateDto>.NotFound();
            if (!TryVersion(r.RowVersion, out _)) return Result<PmTemplateDto>.Invalid("rowVersion", "rowVersion is required.");
        }

        var errors = new ValidationErrors();
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 200) errors.Add("name", "Name is required (max 200 characters).");
        if (r.NameAr is { Length: > 200 }) errors.Add("nameAr", "Arabic name is at most 200 characters.");
        if (r.Instructions is { Length: > 4000 }) errors.Add("instructions", "Instructions are at most 4000 characters.");
        if (r.Priority is { } p && !Enum.IsDefined(p)) errors.Add("priority", "Unknown priority.");
        if (r.EstimatedMinutes is { } m && (m < 1 || m > 100_000)) errors.Add("estimatedMinutes", "Estimated minutes must be between 1 and 100,000.");
        if (r.CategoryId is { } c && !await db.Categories.AnyAsync(x => x.Id == c, ct)) errors.Add("categoryId", "Category not found.");
        if (r.ChecklistTemplateId is { } cl && !await db.ChecklistTemplates.AnyAsync(x => x.Id == cl, ct)) errors.Add("checklistTemplateId", "Checklist not found.");
        if (errors.Any) return errors.ToResult<PmTemplateDto>();

        var t = template ?? new PmTemplate();
        t.Name = r.Name!.Trim();
        t.NameAr = Clean(r.NameAr);
        t.Instructions = Clean(r.Instructions);
        t.Priority = r.Priority ?? WorkPriority.Medium;
        t.EstimatedMinutes = r.EstimatedMinutes;
        t.CategoryId = r.CategoryId;
        t.ChecklistTemplateId = r.ChecklistTemplateId;
        if (template is null) db.PmTemplates.Add(t);
        else
        {
            TryVersion(r.RowVersion, out var version);
            db.Entry(t).Property(x => x.RowVersion).OriginalValue = version;
            db.Entry(t).Property(x => x.Name).IsModified = true;
        }

        audit.Record(template is null ? "pmtemplate.created" : "pmtemplate.updated", nameof(PmTemplate), t.Id.ToString(), JsonSerializer.Serialize(new { t.Name, t.Priority, t.CategoryId, t.ChecklistTemplateId }));
        return await CommitAsync(t, "A template with that name already exists.", ct);
    }

    public async Task<Result<PmTemplateDto>> SetTemplateArchivedAsync(Guid id, bool archived, string? rowVersion, CancellationToken ct = default)
    {
        var t = await db.PmTemplates.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return Result<PmTemplateDto>.NotFound();
        if (!TryVersion(rowVersion, out var version)) return Result<PmTemplateDto>.Invalid("rowVersion", "rowVersion is required.");
        if (archived && await db.PmSchedules.AnyAsync(s => s.TemplateId == id && !s.IsArchived, ct)) return Result<PmTemplateDto>.Conflict("Archive the schedules that use this template first.");
        t.IsArchived = archived;
        db.Entry(t).Property(x => x.RowVersion).OriginalValue = version;
        audit.Record(archived ? "pmtemplate.archived" : "pmtemplate.restored", nameof(PmTemplate), id.ToString());
        return await CommitAsync(t, "A template with that name already exists.", ct);
    }

    private async Task<Result<PmTemplateDto>> CommitAsync(PmTemplate t, string duplicateMessage, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            var checklist = t.ChecklistTemplateId is { } c ? await db.ChecklistTemplates.AsNoTracking().Where(x => x.Id == c).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
            return Result<PmTemplateDto>.Ok(ToDto(t, checklist));
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<PmTemplateDto>.Conflict("The template was changed by someone else. Reload it and try again.");
        }
        catch (DbUpdateException ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return Result<PmTemplateDto>.Conflict(duplicateMessage);
        }
    }

    // ---------------- schedules ----------------

    public async Task<IReadOnlyList<PmScheduleDto>> SchedulesAsync(bool includeArchived, CancellationToken ct = default)
    {
        var rows = await db.PmSchedules.AsNoTracking().Where(s => includeArchived || !s.IsArchived).OrderBy(s => s.Name).ToListAsync(ct);
        var templates = await db.PmTemplates.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var counts = await db.PmPlans.AsNoTracking().Where(p => p.IsActive).GroupBy(p => p.ScheduleId).Select(g => new { g.Key, Assets = g.Count(), Open = g.Count(p => p.OpenWorkOrderId != null) }).ToDictionaryAsync(x => x.Key, ct);
        var assets = await facilities.AssetsAsync(rows.Where(r => r.AssetId != null).Select(r => r.AssetId!.Value), ct);
        var classes = await facilities.AssetClassNamesAsync(rows.Where(r => r.AssetClassId != null).Select(r => r.AssetClassId!.Value), ct);
        var people = await users.GetAsync(rows.Where(r => r.AssignToUserId != null).Select(r => r.AssignToUserId!.Value), ct);
        return rows.Select(s => new PmScheduleDto(
            s.Id, s.Name, s.TemplateId, templates.GetValueOrDefault(s.TemplateId), s.TargetKind, s.AssetId, s.AssetId is { } a ? assets.GetValueOrDefault(a)?.Name : null, s.AssetClassId,
            s.AssetClassId is { } c ? classes.GetValueOrDefault(c) : null, s.TimeBasis, s.IntervalUnit, s.Interval, s.StartOn, s.LeadDays, s.MeterName, s.MeterInterval, s.AssignToUserId,
            s.AssignToUserId is { } u && people.TryGetValue(u, out var p) ? p.DisplayName ?? p.Email : null, s.IsArchived,
            counts.TryGetValue(s.Id, out var n) ? n.Assets : 0, counts.TryGetValue(s.Id, out var o) ? o.Open : 0, Convert.ToBase64String(s.RowVersion))).ToList();
    }

    public async Task<Result<PmScheduleDto>> SaveScheduleAsync(Guid? id, SavePmScheduleRequest r, CancellationToken ct = default)
    {
        PmSchedule? existing = null;
        if (id is { } sid)
        {
            existing = await db.PmSchedules.FirstOrDefaultAsync(s => s.Id == sid, ct);
            if (existing is null) return Result<PmScheduleDto>.NotFound();
            if (!TryVersion(r.RowVersion, out _)) return Result<PmScheduleDto>.Invalid("rowVersion", "rowVersion is required.");
        }

        var errors = new ValidationErrors();
        var basis = r.TimeBasis ?? PmTimeBasis.Fixed;
        var unit = r.IntervalUnit ?? PmIntervalUnit.Month;
        var meterName = Clean(r.MeterName);
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 200) errors.Add("name", "Name is required (max 200 characters).");
        if (r.TemplateId is not { } templateId || !await db.PmTemplates.AnyAsync(t => t.Id == templateId && !t.IsArchived, ct)) errors.Add("templateId", "Choose a template.");
        if (!Enum.IsDefined(basis)) errors.Add("timeBasis", "Unknown schedule type.");
        if (!Enum.IsDefined(unit)) errors.Add("intervalUnit", "Unknown interval unit.");

        var hasTime = basis != PmTimeBasis.None;
        var hasMeter = r.MeterInterval is not null || meterName is not null;
        if (!hasTime && !hasMeter) errors.Add("timeBasis", "A schedule needs a time rule, a meter rule, or both.");
        if (hasTime)
        {
            if (r.Interval is not (>= 1 and <= Recurrence.MaxInterval)) errors.Add("interval", $"Repeat every 1 to {Recurrence.MaxInterval}.");
            if (r.StartOn is null) errors.Add("startOn", "Choose the first date.");
        }

        if (hasMeter)
        {
            if (meterName is null or { Length: > 200 }) errors.Add("meterName", "Name the meter (as it is called on each asset).");
            if (r.MeterInterval is not (> 0 and <= 1_000_000_000m)) errors.Add("meterInterval", "Say how far the meter runs between services.");
        }

        if (r.LeadDays is not (null or (>= 0 and <= 365))) errors.Add("leadDays", "Lead time is 0 to 365 days.");

        var kind = r.TargetKind ?? PmTargetKind.Asset;
        string? assetNote = null;
        if (kind == PmTargetKind.Asset)
        {
            if (r.AssetId is not { } assetId || await facilities.GetAssetAsync(assetId, ct) is not { Active: true }) errors.Add("assetId", "Choose an asset that is in service.");
        }
        else if (kind == PmTargetKind.AssetClass)
        {
            if (r.AssetClassId is not { } classId || (await facilities.AssetClassNamesAsync([classId], ct)).Count == 0) errors.Add("assetClassId", "Choose an asset class.");
        }
        else errors.Add("targetKind", "Unknown target.");

        if (r.AssignToUserId is { } who && !(await users.WithPermissionAsync(Permissions.WorkOrdersWork, ct)).Any(u => u.Id == who)) errors.Add("assignToUserId", "That person is not active or cannot do work orders.");
        if (errors.Any) return errors.ToResult<PmScheduleDto>();
        _ = assetNote;

        var s = existing ?? new PmSchedule { CreatedBy = user.UserId ?? Guid.Empty };
        s.Name = r.Name!.Trim();
        s.TemplateId = r.TemplateId!.Value;
        s.TargetKind = kind;
        s.AssetId = kind == PmTargetKind.Asset ? r.AssetId : null;
        s.AssetClassId = kind == PmTargetKind.AssetClass ? r.AssetClassId : null;
        s.TimeBasis = basis;
        s.IntervalUnit = unit;
        s.Interval = hasTime ? r.Interval!.Value : 1;
        s.StartOn = hasTime ? r.StartOn!.Value : DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        s.LeadDays = r.LeadDays ?? 7;
        s.MeterName = hasMeter ? meterName : null;
        s.MeterInterval = hasMeter ? r.MeterInterval : null;
        s.AssignToUserId = r.AssignToUserId;
        if (existing is null) db.PmSchedules.Add(s);
        else
        {
            TryVersion(r.RowVersion, out var version);
            db.Entry(s).Property(x => x.RowVersion).OriginalValue = version;
            db.Entry(s).Property(x => x.Name).IsModified = true;
        }

        audit.Record(existing is null ? "pmschedule.created" : "pmschedule.updated", nameof(PmSchedule), s.Id.ToString(),
            JsonSerializer.Serialize(new { s.Name, s.TemplateId, s.TargetKind, s.AssetId, s.AssetClassId, s.TimeBasis, s.IntervalUnit, s.Interval, s.StartOn, s.LeadDays, s.MeterName, s.MeterInterval }));
        return await CommitScheduleAsync(s.Id, ct);
    }

    public async Task<Result<PmScheduleDto>> SetScheduleArchivedAsync(Guid id, bool archived, string? rowVersion, CancellationToken ct = default)
    {
        var s = await db.PmSchedules.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return Result<PmScheduleDto>.NotFound();
        if (!TryVersion(rowVersion, out var version)) return Result<PmScheduleDto>.Invalid("rowVersion", "rowVersion is required.");
        s.IsArchived = archived;
        db.Entry(s).Property(x => x.RowVersion).OriginalValue = version;
        audit.Record(archived ? "pmschedule.archived" : "pmschedule.restored", nameof(PmSchedule), id.ToString());
        return await CommitScheduleAsync(id, ct);
    }

    private async Task<Result<PmScheduleDto>> CommitScheduleAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<PmScheduleDto>.Conflict("The schedule was changed by someone else. Reload it and try again.");
        }

        // Plans follow at once, so the person sees the effect of what they just saved.
        await engine.MaterializeAsync(ct);
        return Result<PmScheduleDto>.Ok((await SchedulesAsync(true, ct)).First(x => x.Id == id));
    }

    public async Task<Result<IReadOnlyList<PmPlanDto>>> PlansAsync(Guid scheduleId, CancellationToken ct = default)
    {
        var schedule = await db.PmSchedules.AsNoTracking().FirstOrDefaultAsync(s => s.Id == scheduleId, ct);
        if (schedule is null) return Result<IReadOnlyList<PmPlanDto>>.NotFound();
        var plans = await db.PmPlans.AsNoTracking().Where(p => p.ScheduleId == scheduleId).OrderBy(p => p.NextDueOn).ToListAsync(ct);
        var assets = await facilities.AssetsAsync(plans.Select(p => p.AssetId), ct);
        var rows = new List<PmPlanDto>();
        foreach (var p in plans)
        {
            var a = assets.GetValueOrDefault(p.AssetId);
            decimal? current = p.MeterId is { } m ? (await facilities.LatestReadingAsync(m, ct))?.Value : null;
            rows.Add(new PmPlanDto(p.Id, p.ScheduleId, p.AssetId, a?.Code, a?.Name, p.NextDueOn, p.LastCompletedOn, p.OpenWorkOrderId, p.IsActive, p.LastServiceMeterValue, current,
                schedule.MeterInterval != null && p.MeterId is null));
        }

        return Result<IReadOnlyList<PmPlanDto>>.Ok(rows);
    }

    // ---------------- forecast ----------------

    /// <summary>What is open and what is coming: existing preventive work orders at their due dates, then each plan's next dates, then meter-based estimates.</summary>
    public async Task<Result<ForecastDto>> ForecastAsync(DateOnly? from, DateOnly? to, Guid? assetClassId, Guid? locationId, CancellationToken ct = default)
    {
        var today = await engine.TodayAsync(ct);
        var start = from ?? today.AddDays(-30);
        var end = to ?? today.AddDays(90);
        if (end < start) return Result<ForecastDto>.Invalid("to", "The end cannot be before the start.");
        if (end.DayNumber - start.DayNumber > MaxForecastDays) return Result<ForecastDto>.Invalid("to", $"The range is at most {MaxForecastDays} days.");

        var schedules = await db.PmSchedules.AsNoTracking().Where(s => !s.IsArchived).ToDictionaryAsync(s => s.Id, ct);
        var templates = await db.PmTemplates.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var plans = await db.PmPlans.AsNoTracking().Where(p => p.IsActive).ToListAsync(ct);
        plans = plans.Where(p => schedules.ContainsKey(p.ScheduleId)).ToList();
        var assets = await facilities.AssetsAsync(plans.Select(p => p.AssetId), ct);
        if (assetClassId is { } ac) plans = plans.Where(p => assets.GetValueOrDefault(p.AssetId)?.ClassId == ac).ToList();
        if (locationId is { } lc) plans = plans.Where(p => assets.GetValueOrDefault(p.AssetId)?.LocationId == lc).ToList();

        var openIds = plans.Where(p => p.OpenWorkOrderId != null).Select(p => p.OpenWorkOrderId!.Value).ToList();
        var occurrences = await db.PmOccurrences.AsNoTracking().Where(o => openIds.Contains(o.WorkOrderId)).ToDictionaryAsync(o => o.WorkOrderId, ct);
        var orders = await db.WorkOrders.AsNoTracking().Where(w => openIds.Contains(w.Id)).Select(w => new { w.Id, w.Number, w.Status }).ToDictionaryAsync(w => w.Id, ct);

        var items = new List<ForecastItemDto>();
        ForecastItemDto Item(PmPlan p, PmSchedule s, DateOnly date, ForecastKind kind, Guid? woId = null, string? number = null, WorkOrderStatus? status = null, bool overdue = false)
        {
            var a = assets.GetValueOrDefault(p.AssetId);
            return new ForecastItemDto(date, kind, s.Id, s.Name, templates.GetValueOrDefault(s.TemplateId), p.AssetId, a?.Code, a?.Name, a?.LocationId, a?.ClassId, woId, number, status, overdue);
        }

        foreach (var p in plans)
        {
            var s = schedules[p.ScheduleId];
            DateOnly? afterOpen = null;
            if (p.OpenWorkOrderId is { } wid && occurrences.TryGetValue(wid, out var occ) && orders.TryGetValue(wid, out var order))
            {
                var overdue = occ.DueOn < today;
                if (occ.DueOn >= start && occ.DueOn <= end || (overdue && start <= today && end >= today))
                    items.Add(Item(p, s, occ.DueOn >= start ? occ.DueOn : start, ForecastKind.Open, wid, WorkOrderRules.DisplayNumber(order.Number), order.Status, overdue));
                afterOpen = occ.DueOn;
            }

            if (s.TimeBasis != PmTimeBasis.None && p.NextDueOn is { } next)
            {
                // With a job open, the next dates follow on from its due date; without one, from the plan's own next date.
                var first = afterOpen is { } served ? Recurrence.Next(s.TimeBasis, s.StartOn, s.IntervalUnit, s.Interval, served, served) : next;
                foreach (var d in Recurrence.Project(s.TimeBasis, s.StartOn, s.IntervalUnit, s.Interval, first, end))
                {
                    if (d >= start) items.Add(Item(p, s, d, ForecastKind.Projected));
                    if (items.Count > MaxForecastItems) break;
                }
            }

            if (s.MeterInterval is { } interval && p.OpenWorkOrderId is null && await EstimateMeterDueAsync(p, interval, today, ct) is { } due && due >= start && due <= end)
                items.Add(Item(p, s, due, ForecastKind.MeterEstimate));
        }

        var truncated = items.Count > MaxForecastItems;
        var ordered = items.OrderBy(i => i.Date).ThenBy(i => i.AssetCode).Take(MaxForecastItems).ToList();
        return Result<ForecastDto>.Ok(new ForecastDto(start, end, today, ordered, truncated));
    }

    /// <summary>When a meter will reach its service interval at the pace of the last 30 days, or null when there is not enough to go on.</summary>
    private async Task<DateOnly?> EstimateMeterDueAsync(PmPlan plan, decimal interval, DateOnly today, CancellationToken ct)
    {
        if (plan.MeterId is not { } meterId || plan.LastServiceMeterValue is not { } baseline) return null;
        var now = clock.GetUtcNow();
        var readings = await facilities.ReadingsSinceAsync(meterId, now.AddDays(-30), ct);
        if (readings.Count < 2) return null;
        var latest = readings[^1];
        var remaining = interval - (latest.Value - baseline);
        if (remaining <= 0) return today;
        var days = (latest.ReadAt - readings[0].ReadAt).TotalDays;
        var rate = days <= 0 ? 0 : (double)(latest.Value - readings[0].Value) / days;
        if (rate <= 0) return null;
        return today.AddDays((int)Math.Ceiling((double)remaining / rate));
    }

    private static PmTemplateDto ToDto(PmTemplate t, string? checklistName) =>
        new(t.Id, t.Name, t.NameAr, t.Instructions, t.Priority, t.EstimatedMinutes, t.CategoryId, t.ChecklistTemplateId, checklistName, t.IsArchived, Convert.ToBase64String(t.RowVersion));

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static bool TryVersion(string? value, out byte[] version)
    {
        version = [];
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { version = Convert.FromBase64String(value); return version.Length > 0; }
        catch (FormatException) { return false; }
    }
}
