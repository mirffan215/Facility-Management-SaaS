using System.ComponentModel.DataAnnotations;
using Fms.BuildingBlocks.Tenancy;

namespace Fms.Work.Domain;

/// <summary>When working time counts for SLA clocks: a time zone, weekly hours and holidays. Sites choose their calendar; one is the default.</summary>
public sealed class WorkCalendar : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";

    /// <summary>IANA zone id, e.g. "Asia/Riyadh".</summary>
    public string TimeZoneId { get; set; } = "UTC";

    /// <summary>See <see cref="WorkingHours"/>.</summary>
    public string HoursJson { get; set; } = "{}";

    public bool IsDefault { get; set; }
    public bool IsArchived { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

public sealed class WorkCalendarHoliday : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid CalendarId { get; set; }
    public DateOnly Date { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>A site that follows a calendar. A site follows at most one.</summary>
public sealed class WorkCalendarSite : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid CalendarId { get; set; }
    public Guid SiteId { get; set; }
}

/// <summary>Response and resolution targets by priority. A policy for a category wins over the tenant's default policy.</summary>
public sealed class SlaPolicy : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public Guid? CategoryId { get; set; }
    public bool IsDefault { get; set; }
    public bool IsArchived { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

public sealed class SlaTarget : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PolicyId { get; set; }
    public WorkPriority Priority { get; set; }

    /// <summary>Working minutes from approval until work starts.</summary>
    public int ResponseMinutes { get; set; }

    /// <summary>Working minutes from approval until the work is completed (paused while on hold).</summary>
    public int ResolutionMinutes { get; set; }
}

public enum SlaState { None = 0, OnTrack = 1, AtRisk = 2, Paused = 3, Met = 4, Breached = 5 }

public sealed record SlaStatus(SlaState State, bool ResponseBreached, bool ResponseMet);

/// <summary>What the clocks say right now. Pure, so the screen, the list filter and the monitor agree.</summary>
public static class SlaRules
{
    /// <summary>Share of the resolution window after which work is "at risk".</summary>
    public const double AtRiskShare = 0.75;

    public const int MaxMinutes = 525_600; // a year

    public static SlaStatus StatusOf(WorkOrder wo, DateTimeOffset now)
    {
        if (wo.SlaStartedAt is null || wo.ResolutionDueAt is null) return new SlaStatus(SlaState.None, false, false);

        var responseMet = wo.RespondedAt is { } r && wo.ResponseDueAt is { } rd && r <= rd;
        var responseBreached = wo.ResponseDueAt is { } due && (wo.RespondedAt is { } at ? at > due : now > due && wo.Status is not (WorkOrderStatus.Cancelled));
        if (wo.Status == WorkOrderStatus.Cancelled) return new SlaStatus(SlaState.None, false, false);

        if (wo.CompletedAt is { } done)
            return new SlaStatus(done <= wo.ResolutionDueAt ? (responseBreached ? SlaState.Breached : SlaState.Met) : SlaState.Breached, responseBreached, responseMet);

        if (wo.SlaPausedAt is not null) return new SlaStatus(responseBreached ? SlaState.Breached : SlaState.Paused, responseBreached, responseMet);
        if (now > wo.ResolutionDueAt || responseBreached) return new SlaStatus(SlaState.Breached, responseBreached, responseMet);

        return new SlaStatus(ResolutionAtRisk(wo, now) ? SlaState.AtRisk : SlaState.OnTrack, false, responseMet);
    }

    /// <summary>True once three quarters of the current resolution stretch has passed and the deadline is still ahead.</summary>
    public static bool ResolutionAtRisk(WorkOrder wo, DateTimeOffset now)
    {
        if (wo.SlaStartedAt is null || wo.ResolutionDueAt is not { } due || wo.SlaPausedAt is not null || now > due) return false;
        var windowStart = wo.ResolutionWindowStartAt ?? wo.SlaStartedAt.Value;
        var window = due - windowStart;
        return window > TimeSpan.Zero && now - windowStart >= window * AtRiskShare;
    }

    /// <summary>Starts the clocks (once, when the work is authorised).</summary>
    public static void Start(WorkOrder wo, SlaTarget target, BusinessCalendar calendar, Guid? policyId, Guid? calendarId, DateTimeOffset now)
    {
        wo.SlaPolicyId = policyId;
        wo.SlaCalendarId = calendarId;
        wo.SlaStartedAt = now;
        wo.ResolutionWindowStartAt = now;
        wo.ResponseDueAt = calendar.AddBusinessMinutes(now, target.ResponseMinutes);
        wo.ResolutionDueAt = calendar.AddBusinessMinutes(now, target.ResolutionMinutes);
    }

    /// <summary>On hold: remember how much working time was left and stop the resolution clock.</summary>
    public static void Pause(WorkOrder wo, BusinessCalendar calendar, DateTimeOffset now)
    {
        if (wo.SlaStartedAt is null || wo.ResolutionDueAt is null || wo.SlaPausedAt is not null || wo.CompletedAt is not null) return;
        wo.SlaPausedAt = now;
        wo.ResolutionRemainingMinutes = calendar.BusinessMinutesBetween(now, wo.ResolutionDueAt.Value);
    }

    public static void Resume(WorkOrder wo, BusinessCalendar calendar, DateTimeOffset now)
    {
        if (wo.SlaPausedAt is null) return;
        wo.ResolutionDueAt = calendar.AddBusinessMinutes(now, wo.ResolutionRemainingMinutes ?? 0);
        wo.SlaPausedAt = null;
        wo.ResolutionWindowStartAt = now;
        wo.ResolutionRemainingMinutes = null;
        wo.ResolutionWarnedAt = null; // a new window: warn again if it gets tight
    }
}
