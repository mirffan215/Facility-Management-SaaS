using System.ComponentModel.DataAnnotations;
using Fms.BuildingBlocks.Tenancy;

namespace Fms.Leases.Domain;

/// <summary>Whether the organisation rents the premises from a landlord, or lets them out to someone else.</summary>
public enum LeaseKind { Occupied = 1, LetOut = 2 }

/// <summary>What is stored. Whether an active lease has run out is worked out from its dates.</summary>
public enum LeaseStatus { Draft = 1, Active = 2, Terminated = 3 }

/// <summary>What a lease is today, from its stored status and its dates.</summary>
public enum LeaseState { Draft = 1, Active = 2, Expired = 3, Terminated = 4 }

/// <summary>The number is how many months one rent instalment covers.</summary>
public enum RentFrequency { Monthly = 1, Quarterly = 3, HalfYearly = 6, Annual = 12 }

public enum DeadlineKind { Notice = 1, Break = 2, Expiry = 3 }

/// <summary>A lease: the premises, the other party, the term, the notice periods and the rent.</summary>
public sealed class Lease : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Reference { get; set; } = "";
    public string Title { get; set; } = "";
    public LeaseKind Kind { get; set; } = LeaseKind.Occupied;
    public LeaseStatus Status { get; set; } = LeaseStatus.Draft;

    /// <summary>The landlord (when the organisation rents) or the occupier (when it lets out).</summary>
    public string Counterparty { get; set; } = "";

    public Guid? LocationId { get; set; }
    public string? LocationName { get; set; }
    public decimal? AreaSquareMetres { get; set; }
    public DateOnly StartOn { get; set; }
    public DateOnly EndOn { get; set; }

    /// <summary>Days before the end by which notice of non-renewal must be given. Zero when no notice is needed.</summary>
    public int NoticeDays { get; set; }

    /// <summary>A date from which either side may leave early, and the notice it needs.</summary>
    public DateOnly? BreakOn { get; set; }
    public int BreakNoticeDays { get; set; }

    /// <summary>How many months a renewal option adds, when the lease has one.</summary>
    public int? RenewalMonths { get; set; }

    public string CurrencyCode { get; set; } = "SAR";
    public decimal BaseRent { get; set; }
    public RentFrequency Frequency { get; set; } = RentFrequency.Monthly;
    public decimal EscalationPercent { get; set; }
    public int EscalationEveryMonths { get; set; } = 12;
    public int RentFreeMonths { get; set; }
    public decimal? Deposit { get; set; }

    /// <summary>How many days ahead of a deadline people are warned.</summary>
    public int AlertLeadDays { get; set; } = 90;

    public string? Notes { get; set; }
    public DateOnly? ActivatedOn { get; set; }
    public DateOnly? TerminatedOn { get; set; }
    public string? TerminationReason { get; set; }

    /// <summary>The deadline last warned about (kind and date), and the day, so the same warning is not repeated every run.</summary>
    public string? LastAlertKey { get; set; }
    public DateOnly? LastAlertOn { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

public sealed record RentInstallment(int Number, DateOnly DueOn, DateOnly PeriodEnd, decimal Amount, int EscalationStep, bool IsRentFree, bool IsPartial);

public sealed record Deadline(DeadlineKind Kind, DateOnly On);

/// <summary>Dates, notice periods and rent for a lease. Pure, so the arithmetic is tested without a database.</summary>
public static class LeaseMath
{
    public const int MaxTermMonths = 600;
    public static readonly IReadOnlyList<int> Frequencies = [1, 3, 6, 12];

    public static LeaseState StateOf(Lease l, DateOnly today) => l.Status switch
    {
        LeaseStatus.Draft => LeaseState.Draft,
        LeaseStatus.Terminated => LeaseState.Terminated,
        _ => today > l.EndOn ? LeaseState.Expired : LeaseState.Active,
    };

    public static DateOnly NoticeDeadline(Lease l) => l.EndOn.AddDays(-l.NoticeDays);

    public static DateOnly? BreakDeadline(Lease l) => l.BreakOn?.AddDays(-l.BreakNoticeDays);

    /// <summary>The dates that still matter, soonest first: the notice date, the break notice date, the end. Only those not yet passed.</summary>
    public static IReadOnlyList<Deadline> Upcoming(Lease l, DateOnly today)
    {
        var list = new List<Deadline>();
        if (l.NoticeDays > 0 && NoticeDeadline(l) >= today) list.Add(new(DeadlineKind.Notice, NoticeDeadline(l)));
        if (BreakDeadline(l) is { } b && b >= today) list.Add(new(DeadlineKind.Break, b));
        if (l.EndOn >= today) list.Add(new(DeadlineKind.Expiry, l.EndOn));
        return list.OrderBy(d => d.On).ThenBy(d => d.Kind).ToList();
    }

    /// <summary>
    /// What to warn about today, as a stable key (kind and date), or null. An expired lease gives "expired"; otherwise the first upcoming date that falls within the lead time.
    /// The date is in the key so that extending a lease starts the warnings afresh.
    /// </summary>
    public static string? AlertKey(Lease l, DateOnly today)
    {
        if (l.Status != LeaseStatus.Active) return null;
        if (today > l.EndOn) return $"expired:{l.EndOn:yyyy-MM-dd}";
        return Upcoming(l, today).Where(d => d.On.DayNumber - today.DayNumber <= l.AlertLeadDays).Select(d => $"{d.Kind.ToString().ToLowerInvariant()}:{d.On:yyyy-MM-dd}").FirstOrDefault();
    }

    /// <summary>
    /// The rent instalments for the whole term. One falls due at the start of each period. The rent rises by the escalation percentage (compounded, rounded to cents each time) at every
    /// escalation step; periods that start within the rent-free months cost nothing; the last period is pro-rated by days when the term does not divide evenly.
    /// </summary>
    public static IReadOnlyList<RentInstallment> Schedule(Lease l)
    {
        var months = (int)l.Frequency;
        var list = new List<RentInstallment>();
        for (var i = 0; ; i++)
        {
            var due = l.StartOn.AddMonths(i * months);
            if (due > l.EndOn) break;
            var next = l.StartOn.AddMonths((i + 1) * months);
            var periodEnd = next.AddDays(-1);
            var partial = periodEnd > l.EndOn;
            if (partial) periodEnd = l.EndOn;
            var step = l.EscalationPercent > 0 && l.EscalationEveryMonths > 0 ? i * months / l.EscalationEveryMonths : 0;
            var free = i * months < l.RentFreeMonths;
            var amount = free ? 0 : RentAtStep(l, step);
            if (partial && !free)
            {
                var full = next.DayNumber - due.DayNumber;
                var used = periodEnd.DayNumber - due.DayNumber + 1;
                amount = Math.Round(amount * used / full, 2, MidpointRounding.AwayFromZero);
            }

            list.Add(new RentInstallment(i + 1, due, periodEnd, amount, step, free, partial));
        }

        return list;
    }

    public static decimal RentAtStep(Lease l, int step)
    {
        var rent = l.BaseRent;
        for (var s = 0; s < step; s++) rent = Math.Round(rent * (1 + l.EscalationPercent / 100m), 2, MidpointRounding.AwayFromZero);
        return rent;
    }

    /// <summary>The rent due in the twelve months from a date, for reports and the summary.</summary>
    public static decimal NextTwelveMonths(Lease l, DateOnly from) =>
        Schedule(l).Where(i => i.DueOn >= from && i.DueOn < from.AddMonths(12)).Sum(i => i.Amount);

    public static int TermMonths(Lease l) => (l.EndOn.Year - l.StartOn.Year) * 12 + l.EndOn.Month - l.StartOn.Month;
}
