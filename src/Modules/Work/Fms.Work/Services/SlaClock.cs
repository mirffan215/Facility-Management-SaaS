using Fms.BuildingBlocks.Identity;
using Fms.Work.Domain;
using Fms.Work.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Work.Services;

/// <summary>Finds the policy and calendar that apply to a work order and runs its SLA clocks.</summary>
public sealed class SlaClock(WorkDbContext db, IFacilityLookup facilities)
{
    /// <summary>Starts the clocks if they have not started and a policy applies. Does nothing otherwise.</summary>
    public async Task StartAsync(WorkOrder wo, DateTimeOffset now, CancellationToken ct)
    {
        if (wo.SlaStartedAt is not null) return;
        var (policy, target) = await TargetForAsync(wo.CategoryId, wo.Priority, ct);
        if (policy is null || target is null) return;

        var (calendar, calendarId) = await CalendarForLocationAsync(wo.LocationId, ct);
        SlaRules.Start(wo, target, calendar, policy.Id, calendarId, now);
    }

    /// <summary>A priority change while the clocks run moves the due times to the new targets, counted from when they started.</summary>
    public async Task RecomputeAsync(WorkOrder wo, CancellationToken ct)
    {
        if (wo.SlaStartedAt is not { } started || wo.CompletedAt is not null || wo.SlaPausedAt is not null) return;
        var (policy, target) = await TargetForAsync(wo.CategoryId, wo.Priority, ct);
        if (policy is null || target is null) return;

        var calendar = await CalendarByIdAsync(wo.SlaCalendarId, ct);
        SlaRules.Start(wo, target, calendar, policy.Id, wo.SlaCalendarId, started);
        wo.ResolutionWarnedAt = null;
    }

    public async Task PauseAsync(WorkOrder wo, DateTimeOffset now, CancellationToken ct)
    {
        if (wo.SlaStartedAt is null) return;
        SlaRules.Pause(wo, await CalendarByIdAsync(wo.SlaCalendarId, ct), now);
    }

    public async Task ResumeAsync(WorkOrder wo, DateTimeOffset now, CancellationToken ct)
    {
        if (wo.SlaPausedAt is null) return;
        SlaRules.Resume(wo, await CalendarByIdAsync(wo.SlaCalendarId, ct), now);
    }

    public async Task<(SlaPolicy? Policy, SlaTarget? Target)> TargetForAsync(Guid? categoryId, WorkPriority priority, CancellationToken ct)
    {
        SlaPolicy? policy = null;
        if (categoryId is { } c) policy = await db.SlaPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.CategoryId == c && !p.IsArchived, ct);
        policy ??= await db.SlaPolicies.AsNoTracking().FirstOrDefaultAsync(p => p.IsDefault && !p.IsArchived, ct);
        if (policy is null) return (null, null);
        return (policy, await db.SlaTargets.AsNoTracking().FirstOrDefaultAsync(t => t.PolicyId == policy.Id && t.Priority == priority, ct));
    }

    public async Task<(BusinessCalendar Calendar, Guid? CalendarId)> CalendarForLocationAsync(Guid locationId, CancellationToken ct)
    {
        Guid? calendarId = null;
        if (await facilities.SiteOfAsync(locationId, ct) is { } site)
            calendarId = await db.CalendarSites.AsNoTracking().Where(s => s.SiteId == site).Select(s => (Guid?)s.CalendarId).FirstOrDefaultAsync(ct);
        calendarId ??= await db.Calendars.AsNoTracking().Where(c => c.IsDefault && !c.IsArchived).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
        return (await CalendarByIdAsync(calendarId, ct), calendarId);
    }

    /// <summary>The calendar, or an always-open one when none applies (or it has gone), so a clock never stops for want of configuration.</summary>
    public async Task<BusinessCalendar> CalendarByIdAsync(Guid? id, CancellationToken ct)
    {
        if (id is not { } calendarId) return BusinessCalendar.AlwaysOpen;
        var calendar = await db.Calendars.AsNoTracking().FirstOrDefaultAsync(c => c.Id == calendarId, ct);
        if (calendar is null || WorkingHours.TryParse(calendar.HoursJson, out _) is not { } week) return BusinessCalendar.AlwaysOpen;
        var holidays = (await db.CalendarHolidays.AsNoTracking().Where(h => h.CalendarId == calendarId).Select(h => h.Date).ToListAsync(ct)).ToHashSet();
        try { return new BusinessCalendar(calendar.TimeZoneId, week, holidays); }
        catch (TimeZoneNotFoundException) { return BusinessCalendar.AlwaysOpen; }
    }
}
