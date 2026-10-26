using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Messaging;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Security;
using Fms.BuildingBlocks.Tenancy;
using Fms.Work.Domain;
using Fms.Work.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Work.Services;

public sealed record PmRunResult(int PlansCreated, int PlansRetired, int WorkOrdersCreated);

/// <summary>
/// Turns schedules into plans (one per asset) and plans into work orders when they fall due, and keeps the plans in step when the work is done.
/// Safe to run often and from several workers at once: a plan carries a version, so only one run can turn a given due date into a work order.
/// </summary>
public sealed class PmEngine(
    WorkDbContext db, IFacilityLookup facilities, IUserDirectory users, IOutbox outbox, IAuditPort audit, ITenantContext tenant, TimeProvider clock)
{
    private TimeZoneInfo? _zone;

    /// <summary>Dates are the tenant's: the time zone of its default working calendar, or UTC when none is set.</summary>
    public async Task<TimeZoneInfo> ZoneAsync(CancellationToken ct)
    {
        if (_zone is not null) return _zone;
        var id = await db.Calendars.AsNoTracking().Where(c => c.IsDefault && !c.IsArchived).Select(c => c.TimeZoneId).FirstOrDefaultAsync(ct);
        return _zone = id is not null && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var z) ? z : TimeZoneInfo.Utc;
    }

    public async Task<DateOnly> TodayAsync(CancellationToken ct) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), await ZoneAsync(ct)).DateTime);

    public async Task<PmRunResult> RunAsync(CancellationToken ct = default)
    {
        var (created, retired) = await MaterializeAsync(ct);
        return new PmRunResult(created, retired, await GenerateAsync(ct));
    }

    // ---------------- plans ----------------

    public async Task<(int Created, int Retired)> MaterializeAsync(CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        var today = await TodayAsync(ct);
        var schedules = await db.PmSchedules.AsNoTracking().ToListAsync(ct);
        var plans = await db.PmPlans.ToListAsync(ct);
        int created = 0, retired = 0;
        var now = clock.GetUtcNow();

        foreach (var s in schedules)
        {
            var assets = s.IsArchived ? [] : await AssetsOfAsync(s, ct);
            var mine = plans.Where(p => p.ScheduleId == s.Id).ToList();

            foreach (var asset in assets)
            {
                var plan = mine.FirstOrDefault(p => p.AssetId == asset.Id);
                if (plan is null)
                {
                    plan = new PmPlan { ScheduleId = s.Id, AssetId = asset.Id, CreatedAt = now };
                    if (s.TimeBasis != PmTimeBasis.None) plan.NextDueOn = Recurrence.First(s.TimeBasis, s.StartOn, s.IntervalUnit, s.Interval, today);
                    await BindMeterAsync(s, plan, ct);
                    db.PmPlans.Add(plan);
                    plans.Add(plan);
                    created++;
                }
                else if (!plan.IsActive)
                {
                    plan.IsActive = true;
                    if (plan.OpenWorkOrderId is null && s.TimeBasis != PmTimeBasis.None && plan.NextDueOn is null) plan.NextDueOn = Recurrence.First(s.TimeBasis, s.StartOn, s.IntervalUnit, s.Interval, today);
                }
            }

            var keep = assets.Select(a => a.Id).ToHashSet();
            foreach (var p in mine.Where(p => p.IsActive && !keep.Contains(p.AssetId)))
            {
                p.IsActive = false;
                retired++;
            }
        }

        try
        {
            if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || SqlErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear(); // another run did the same work at the same moment
        }

        return (created, retired);
    }

    private async Task<IReadOnlyList<FacilityRef>> AssetsOfAsync(PmSchedule s, CancellationToken ct)
    {
        if (s.TargetKind == PmTargetKind.AssetClass) return s.AssetClassId is { } c ? await facilities.AssetsInClassAsync(c, ct) : [];
        var asset = s.AssetId is { } a ? await facilities.GetAssetAsync(a, ct) : null;
        return asset is { Active: true } ? [asset] : [];
    }

    private async Task BindMeterAsync(PmSchedule s, PmPlan plan, CancellationToken ct)
    {
        if (s.MeterInterval is null || string.IsNullOrWhiteSpace(s.MeterName)) return;
        var meter = await facilities.FindMeterAsync(plan.AssetId, s.MeterName, ct);
        plan.MeterId = meter?.Id;
        if (meter is not null && await facilities.LatestReadingAsync(meter.Id, ct) is { } latest) plan.LastServiceMeterValue = latest.Value;
    }

    // ---------------- generation ----------------

    public async Task<int> GenerateAsync(CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        var today = await TodayAsync(ct);
        var ids = await db.PmPlans.AsNoTracking().Where(p => p.IsActive && p.OpenWorkOrderId == null).Select(p => p.Id).ToListAsync(ct);
        var created = 0;
        foreach (var id in ids)
        {
            if (await TryGenerateAsync(id, today, ct)) created++;
        }

        return created;
    }

    private async Task<bool> TryGenerateAsync(Guid planId, DateOnly today, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var plan = await db.PmPlans.FirstOrDefaultAsync(p => p.Id == planId && p.IsActive && p.OpenWorkOrderId == null, ct);
        if (plan is null) return false;
        var schedule = await db.PmSchedules.AsNoTracking().FirstOrDefaultAsync(s => s.Id == plan.ScheduleId && !s.IsArchived, ct);
        var template = schedule is null ? null : await db.PmTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == schedule.TemplateId && !t.IsArchived, ct);
        var asset = await facilities.GetAssetAsync(plan.AssetId, ct);
        if (schedule is null || template is null || asset is not { Active: true, LocationId: not null }) return false;

        PmTrigger? trigger = null;
        var due = today;
        if (schedule.TimeBasis != PmTimeBasis.None && plan.NextDueOn is { } next && today >= next.AddDays(-schedule.LeadDays))
        {
            trigger = PmTrigger.Time;
            due = next;
        }
        else if (schedule.MeterInterval is { } interval)
        {
            if (await MeterDueAsync(schedule, plan, interval, ct)) trigger = PmTrigger.Meter;
            else
            {
                if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct); // a baseline was set or reset
                return false;
            }
        }

        if (trigger is null) return false;
        return await CreateAsync(planId, schedule, template, asset, due, trigger.Value, ct);
    }

    /// <summary>Has the meter run the interval since the last service? Also fixes up the baseline: first reading, or a replaced meter reading lower.</summary>
    private async Task<bool> MeterDueAsync(PmSchedule schedule, PmPlan plan, decimal interval, CancellationToken ct)
    {
        if (plan.MeterId is null)
        {
            var meter = string.IsNullOrWhiteSpace(schedule.MeterName) ? null : await facilities.FindMeterAsync(plan.AssetId, schedule.MeterName, ct);
            if (meter is null) return false;
            plan.MeterId = meter.Id;
        }

        if (await facilities.LatestReadingAsync(plan.MeterId.Value, ct) is not { } latest) return false;
        if (plan.LastServiceMeterValue is not { } baseline)
        {
            plan.LastServiceMeterValue = latest.Value;
            return false;
        }

        if (latest.Value < baseline)
        {
            plan.LastServiceMeterValue = latest.Value; // the meter was replaced or rolled over: count from here
            return false;
        }

        return latest.Value - baseline >= interval;
    }

    private async Task<bool> CreateAsync(Guid planId, PmSchedule schedule, PmTemplate template, FacilityRef asset, DateOnly due, PmTrigger trigger, CancellationToken ct)
    {
        var zone = await ZoneAsync(ct);
        var assignee = schedule.AssignToUserId is { } a && (await users.WithPermissionAsync(Permissions.WorkOrdersWork, ct)).Any(u => u.Id == a) ? a : (Guid?)null;
        var templateItems = template.ChecklistTemplateId is { } ct0
            ? await db.ChecklistTemplateItems.AsNoTracking().Where(i => i.TemplateId == ct0).OrderBy(i => i.Position).ToListAsync(ct)
            : [];
        var now = clock.GetUtcNow();
        var tenantKey = tenant.TenantId?.ToString() ?? "none";

        try
        {
            var strategy = db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var plan = await db.PmPlans.FirstOrDefaultAsync(p => p.Id == planId && p.IsActive && p.OpenWorkOrderId == null, ct);
                if (plan is null) return false;

                var number = await WorkOrderNumbers.NextAsync(db, tenantKey, ct);
                var title = $"{template.Name} · {asset.Code}";
                var wo = new WorkOrder
                {
                    Number = number, Title = title.Length <= 200 ? title : title[..200], Description = template.Instructions, Type = WorkOrderType.Planned, Priority = template.Priority,
                    Status = assignee is null ? WorkOrderStatus.Approved : WorkOrderStatus.Assigned, LocationId = asset.LocationId!.Value, AssetId = asset.Id, CategoryId = template.CategoryId,
                    RequestedBy = schedule.CreatedBy, RequestedAt = now, AssignedTo = assignee, DueAt = EndOfDay(due, zone), UpdatedAt = now,
                };
                db.WorkOrders.Add(wo);
                db.WorkOrderEvents.Add(new WorkOrderEvent { WorkOrderId = wo.Id, Action = WorkOrderAction.Created, ToStatus = WorkOrderStatus.Approved, At = now, Note = $"Preventive: {schedule.Name}" });
                if (assignee is not null)
                    db.WorkOrderEvents.Add(new WorkOrderEvent { WorkOrderId = wo.Id, Action = WorkOrderAction.Assigned, FromStatus = WorkOrderStatus.Approved, ToStatus = WorkOrderStatus.Assigned, At = now, SubjectId = assignee });
                var position = 0;
                foreach (var item in templateItems)
                    db.ChecklistItems.Add(new WorkOrderChecklistItem { WorkOrderId = wo.Id, TemplateId = template.ChecklistTemplateId, Position = ++position, Text = item.Text, Required = item.Required });

                db.PmOccurrences.Add(new PmOccurrence { PlanId = plan.Id, ScheduleId = schedule.Id, AssetId = asset.Id, WorkOrderId = wo.Id, DueOn = due, Trigger = trigger, GeneratedAt = now });
                plan.OpenWorkOrderId = wo.Id;

                outbox.Enqueue(WorkMessages.Event, new WorkOrderEventMessage(wo.Id, WorkOrderAction.Created, null, null));
                audit.Record("workorder.created", nameof(WorkOrder), wo.Id.ToString(),
                    JsonSerializer.Serialize(new { wo.Number, wo.Title, wo.Type, wo.Priority, wo.LocationId, wo.AssetId, status = wo.Status, preventive = true, scheduleId = schedule.Id, trigger, dueOn = due }));
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return true;
            });
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || SqlErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear(); // another run generated it first
            return false;
        }
    }

    private static DateTimeOffset EndOfDay(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(new TimeOnly(23, 59, 0));
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone.GetUtcOffset(local)).ToUniversalTime();
    }

    // ---------------- when the work is done ----------------

    /// <summary>
    /// Called by the work order service, in its transaction, when a work order is completed or cancelled. If it was generated by a plan, records
    /// how it ended and moves the plan to its next due date. A reopened job that is completed again does not move the plan a second time.
    /// </summary>
    public async Task OnFinishedAsync(WorkOrder wo, PmOutcome outcome, DateTimeOffset at, CancellationToken ct)
    {
        var plan = await db.PmPlans.FirstOrDefaultAsync(p => p.OpenWorkOrderId == wo.Id, ct);
        if (plan is null) return;
        var occurrence = await db.PmOccurrences.FirstOrDefaultAsync(o => o.WorkOrderId == wo.Id, ct);
        var schedule = await db.PmSchedules.AsNoTracking().FirstOrDefaultAsync(s => s.Id == plan.ScheduleId, ct);
        if (occurrence is null || schedule is null) return;

        var finishedOn = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, await ZoneAsync(ct)).DateTime);
        occurrence.Outcome = outcome;
        occurrence.FinishedOn = finishedOn;
        occurrence.OnTime = outcome == PmOutcome.Completed ? finishedOn <= occurrence.DueOn : null;
        plan.OpenWorkOrderId = null;
        if (outcome == PmOutcome.Completed) plan.LastCompletedOn = finishedOn;

        if (schedule.TimeBasis != PmTimeBasis.None)
        {
            plan.NextDueOn = occurrence.Trigger == PmTrigger.Time || schedule.TimeBasis == PmTimeBasis.Floating || plan.NextDueOn is null || plan.NextDueOn <= finishedOn
                ? Recurrence.Next(schedule.TimeBasis, schedule.StartOn, schedule.IntervalUnit, schedule.Interval, occurrence.DueOn, finishedOn)
                : plan.NextDueOn; // a meter-triggered visit does not disturb the fixed dates still ahead
        }

        if (schedule.MeterInterval is not null && plan.MeterId is { } meterId && await facilities.LatestReadingAsync(meterId, ct) is { } latest) plan.LastServiceMeterValue = latest.Value;
    }
}
