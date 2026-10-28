using System.ComponentModel.DataAnnotations;
using Fms.BuildingBlocks.Audit;
using Fms.BuildingBlocks.Tenancy;

namespace Fms.Energy.Domain;

public enum EnergyKind { Electricity = 1, Water = 2, Gas = 3, Cooling = 4, Heat = 5, Other = 6 }

public enum ReadingSource { Manual = 1, Device = 2, Import = 3 }

/// <summary>A utility meter: the electricity supply of a building, a water main, a chilled-water loop.</summary>
public sealed class EnergyMeter : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public EnergyKind Kind { get; set; }
    public string Unit { get; set; } = "";
    public Guid? LocationId { get; set; }
    public string? LocationName { get; set; }

    /// <summary>True for a register that only counts up (a reading is the total so far); false when each reading is the amount used in its period.</summary>
    public bool IsCumulative { get; set; } = true;

    /// <summary>The value at which a cumulative register wraps back to zero, if it does.</summary>
    public decimal? RolloverAt { get; set; }

    public bool IsArchived { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>One reading. Never edited or deleted: a wrong one is voided with a reason and a right one added.</summary>
public sealed class EnergyReading : ITenantOwned, IAppendOnly
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid MeterId { get; set; }

    /// <summary>When the meter showed this value.</summary>
    public DateTimeOffset At { get; set; }

    public decimal Value { get; set; }
    public ReadingSource Source { get; set; }
    public Guid? DeviceId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public Guid? RecordedBy { get; set; }
    public string? Note { get; set; }
}

public sealed class ReadingVoid : ITenantOwned, IAppendOnly
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ReadingId { get; set; }
    public DateTimeOffset At { get; set; }
    public Guid? By { get; set; }
    public string Reason { get; set; } = "";
}

/// <summary>The price of one unit of a kind of energy from a date. The newest one starting on or before a day applies to that day.</summary>
public sealed class Tariff : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public EnergyKind Kind { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public decimal PricePerUnit { get; set; }
    public string Currency { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>A data logger or gateway allowed to post readings for named meters. Only a hash of its secret is kept.</summary>
public sealed class Device : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string TokenHash { get; set; } = "";

    /// <summary>JSON list of the meter ids it may post for.</summary>
    public string MeterIdsJson { get; set; } = "[]";

    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? RevokedBy { get; set; }
}

public enum AlarmKind { HighDailyUse = 1, NoReading = 2 }

/// <summary>A condition worth telling someone about: a meter using more than a limit in a day, or sending nothing for too long.</summary>
public sealed class AlarmRule : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid MeterId { get; set; }
    public AlarmKind Kind { get; set; }

    /// <summary>Units per day for HighDailyUse; hours for NoReading.</summary>
    public decimal Threshold { get; set; }

    public bool IsActive { get; set; } = true;
    public bool IsRaised { get; set; }
    public DateTimeOffset? RaisedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

public enum AlarmEventKind { Raised = 1, Cleared = 2 }

public sealed class AlarmEvent : ITenantOwned, IAppendOnly
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid RuleId { get; set; }
    public Guid MeterId { get; set; }
    public DateTimeOffset At { get; set; }
    public AlarmEventKind Kind { get; set; }
    public decimal? Value { get; set; }
}

/// <summary>An amount used over an interval, worked out from readings.</summary>
public sealed record Usage(DateTimeOffset From, DateTimeOffset To, decimal Amount);

/// <summary>What turning readings into use found that a person should look at.</summary>
public sealed record UsageResult(IReadOnlyList<Usage> Intervals, int Resets);

/// <summary>Turns readings into use. Pure, so the arithmetic is tested without a database.</summary>
public static class EnergyMath
{
    public const decimal MaxValue = 1_000_000_000_000m;

    /// <summary>
    /// For a cumulative meter, the use between each pair of consecutive readings. A reading lower than the one before is a wrap-around when the meter has a rollover
    /// value (use = rollover - previous + current), otherwise a reset or replacement, which is counted and used as a new starting point rather than as negative use.
    /// For a meter whose readings are amounts, each reading is the use of the period since the previous one (or of an unknown start, counted as a point).
    /// </summary>
    public static UsageResult UsageFrom(IReadOnlyList<(DateTimeOffset At, decimal Value)> readings, bool cumulative, decimal? rolloverAt)
    {
        var ordered = readings.OrderBy(r => r.At).ToList();
        var intervals = new List<Usage>();
        var resets = 0;
        if (!cumulative)
        {
            DateTimeOffset? previous = null;
            foreach (var (at, value) in ordered)
            {
                intervals.Add(new Usage(previous ?? at, at, value));
                previous = at;
            }

            return new UsageResult(intervals, 0);
        }

        for (var i = 1; i < ordered.Count; i++)
        {
            var (fromAt, fromValue) = ordered[i - 1];
            var (toAt, toValue) = ordered[i];
            if (toValue >= fromValue) intervals.Add(new Usage(fromAt, toAt, toValue - fromValue));
            else if (rolloverAt is { } wrap && wrap > fromValue) intervals.Add(new Usage(fromAt, toAt, wrap - fromValue + toValue));
            else resets++;
        }

        return new UsageResult(intervals, resets);
    }

    /// <summary>Spreads the use of each interval evenly over the days it covers (in UTC) and returns the use of each day. A one-instant interval lands on its day.</summary>
    public static IReadOnlyDictionary<DateOnly, decimal> ByDay(IEnumerable<Usage> intervals)
    {
        var days = new SortedDictionary<DateOnly, decimal>();
        foreach (var u in intervals)
        {
            var from = u.From.UtcDateTime;
            var to = u.To.UtcDateTime;
            if (to <= from)
            {
                Add(days, DateOnly.FromDateTime(to), u.Amount);
                continue;
            }

            var totalSeconds = (decimal)(to - from).TotalSeconds;
            var cursor = from;
            while (cursor < to)
            {
                var nextMidnight = cursor.Date.AddDays(1);
                var end = nextMidnight < to ? nextMidnight : to;
                Add(days, DateOnly.FromDateTime(cursor), u.Amount * (decimal)(end - cursor).TotalSeconds / totalSeconds);
                cursor = end;
            }
        }

        return days;
    }

    private static void Add(SortedDictionary<DateOnly, decimal> days, DateOnly day, decimal amount) => days[day] = days.GetValueOrDefault(day) + amount;

    /// <summary>The price in force on a day: the newest tariff starting on or before it, or null when none has started yet.</summary>
    public static Tariff? TariffOn(IEnumerable<Tariff> tariffs, DateOnly day) => tariffs.Where(t => t.EffectiveFrom <= day).OrderByDescending(t => t.EffectiveFrom).FirstOrDefault();
}
