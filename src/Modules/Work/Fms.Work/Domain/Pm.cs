using System.ComponentModel.DataAnnotations;
using Fms.BuildingBlocks.Tenancy;

namespace Fms.Work.Domain;

/// <summary>What gets done on a preventive visit, written once and used by many schedules.</summary>
public sealed class PmTemplate : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public string? NameAr { get; set; }
    public string? Instructions { get; set; }
    public WorkPriority Priority { get; set; } = WorkPriority.Medium;
    public int? EstimatedMinutes { get; set; }
    public Guid? CategoryId { get; set; }

    /// <summary>The checklist copied onto each generated work order.</summary>
    public Guid? ChecklistTemplateId { get; set; }

    public bool IsArchived { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

public enum PmTargetKind { Asset = 1, AssetClass = 2 }

/// <summary>Fixed: on the same calendar dates whatever happens (statutory inspections). Floating: a set time after the last one was done (lubrication).</summary>
public enum PmTimeBasis { None = 0, Fixed = 1, Floating = 2 }

public enum PmIntervalUnit { Day = 1, Week = 2, Month = 3, Year = 4 }

/// <summary>The rule: which template, on which assets, how often (by time, by meter, or both: whichever comes first).</summary>
public sealed class PmSchedule : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public Guid TemplateId { get; set; }
    public PmTargetKind TargetKind { get; set; }
    public Guid? AssetId { get; set; }
    public Guid? AssetClassId { get; set; }

    public PmTimeBasis TimeBasis { get; set; } = PmTimeBasis.Fixed;
    public PmIntervalUnit IntervalUnit { get; set; } = PmIntervalUnit.Month;
    public int Interval { get; set; } = 1;

    /// <summary>The first date for a fixed schedule, and the anchor all later dates are counted from.</summary>
    public DateOnly StartOn { get; set; }

    /// <summary>How many days before it is due the work order is created.</summary>
    public int LeadDays { get; set; } = 7;

    /// <summary>Meter trigger (optional, with or without a time rule): the meter of this name on each asset, serviced every <see cref="MeterInterval"/> units.</summary>
    public string? MeterName { get; set; }

    public decimal? MeterInterval { get; set; }
    public Guid? AssignToUserId { get; set; }
    public bool IsArchived { get; set; }
    public Guid CreatedBy { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>One schedule on one asset: when it is next due and what is open. A schedule for a class has one per asset in the class.</summary>
public sealed class PmPlan : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ScheduleId { get; set; }
    public Guid AssetId { get; set; }

    /// <summary>The next date by time, or null when the schedule has no time rule.</summary>
    public DateOnly? NextDueOn { get; set; }

    public DateOnly? LastCompletedOn { get; set; }

    /// <summary>The generated work order that is not finished yet. While it is set nothing more is generated for this plan.</summary>
    public Guid? OpenWorkOrderId { get; set; }

    public Guid? MeterId { get; set; }

    /// <summary>The meter value at the last service (or when the plan started): the next service is due this much later.</summary>
    public decimal? LastServiceMeterValue { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

public enum PmTrigger { Time = 1, Meter = 2 }

public enum PmOutcome { Open = 0, Completed = 1, Skipped = 2 }

/// <summary>The history of a plan: every work order generated for it and how it ended. The source of compliance figures.</summary>
public sealed class PmOccurrence : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PlanId { get; set; }
    public Guid ScheduleId { get; set; }
    public Guid AssetId { get; set; }
    public Guid WorkOrderId { get; set; }
    public DateOnly DueOn { get; set; }
    public PmTrigger Trigger { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
    public PmOutcome Outcome { get; set; }
    public DateOnly? FinishedOn { get; set; }

    /// <summary>Finished on or before the due date. Null while open or skipped.</summary>
    public bool? OnTime { get; set; }
}

/// <summary>Calendar arithmetic for schedules. Pure.</summary>
public static class Recurrence
{
    public const int MaxInterval = 3650;

    public static DateOnly Add(DateOnly from, PmIntervalUnit unit, int count) => unit switch
    {
        PmIntervalUnit.Day => from.AddDays(count),
        PmIntervalUnit.Week => from.AddDays(7 * count),
        PmIntervalUnit.Month => from.AddMonths(count),
        _ => from.AddYears(count),
    };

    /// <summary>The n-th date of a fixed schedule. Counted from the start each time (not from the previous date), so "the 31st" does not drift to the 28th for good after February.</summary>
    public static DateOnly Nth(DateOnly startOn, PmIntervalUnit unit, int interval, int n) => Add(startOn, unit, interval * n);

    /// <summary>The first fixed date strictly after <paramref name="after"/>.</summary>
    public static DateOnly FixedAfter(DateOnly startOn, PmIntervalUnit unit, int interval, DateOnly after)
    {
        if (startOn > after) return startOn;
        // Jump close with an estimate, then walk the last steps exactly.
        var days = after.DayNumber - startOn.DayNumber;
        var approx = unit switch { PmIntervalUnit.Day => 1.0, PmIntervalUnit.Week => 7.0, PmIntervalUnit.Month => 30.4375, _ => 365.25 } * interval;
        var n = Math.Max(0, (int)(days / approx) - 1);
        while (Nth(startOn, unit, interval, n) <= after) n++;
        return Nth(startOn, unit, interval, n);
    }

    /// <summary>The first date a new plan is due: the first fixed date from today on, or the start date (never in the past) for a floating schedule.</summary>
    public static DateOnly First(PmTimeBasis basis, DateOnly startOn, PmIntervalUnit unit, int interval, DateOnly today) =>
        basis == PmTimeBasis.Fixed
            ? (startOn >= today ? startOn : FixedAfter(startOn, unit, interval, today.AddDays(-1)))
            : (startOn >= today ? startOn : today);

    /// <summary>
    /// The next due date once a job was finished (or skipped) on <paramref name="finishedOn"/>. Fixed schedules move on to the next date after both the
    /// one just served and today's finish, so a late job never leaves an already-overdue duplicate behind. Floating schedules count from the finish.
    /// </summary>
    public static DateOnly Next(PmTimeBasis basis, DateOnly startOn, PmIntervalUnit unit, int interval, DateOnly servedDue, DateOnly finishedOn) =>
        basis == PmTimeBasis.Fixed
            ? FixedAfter(startOn, unit, interval, servedDue > finishedOn ? servedDue : finishedOn)
            : Add(finishedOn, unit, interval);

    /// <summary>Upcoming due dates from <paramref name="next"/> up to and including <paramref name="until"/>, assuming each job is done on its due date.</summary>
    public static IEnumerable<DateOnly> Project(PmTimeBasis basis, DateOnly startOn, PmIntervalUnit unit, int interval, DateOnly next, DateOnly until, int max = 400)
    {
        var due = next;
        for (var i = 0; i < max && due <= until; i++)
        {
            yield return due;
            due = Next(basis, startOn, unit, interval, due, due);
        }
    }
}
