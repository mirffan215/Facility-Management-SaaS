using System.ComponentModel.DataAnnotations;
using Fms.BuildingBlocks.Audit;
using Fms.BuildingBlocks.Tenancy;

namespace Fms.Facilities.Domain;

public enum MeterKind
{
    /// <summary>A running total that only goes up (energy kWh, water m3, run hours). A lower reading means a replaced or rolled-over meter.</summary>
    Cumulative = 1,

    /// <summary>An instantaneous value that moves both ways (temperature, pressure).</summary>
    Gauge = 2,
}

public enum ReadingSource { Manual = 1, Mobile = 2, Import = 3, Device = 4 }

/// <summary>Something measured on an asset: run hours, energy, temperature...</summary>
public sealed class Meter : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid AssetId { get; set; }
    public string Name { get; set; } = "";
    public string? NameAr { get; set; }

    /// <summary>Unit label, e.g. "kWh", "h", "°C". Fixed once readings exist.</summary>
    public string Unit { get; set; } = "";

    public MeterKind Kind { get; set; }

    /// <summary>Optional plausible range. A reading outside it is rejected as a probable typo.</summary>
    public decimal? MinValue { get; set; }

    public decimal? MaxValue { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>One measurement. Append-only: a wrong reading is voided (see <see cref="MeterReadingVoid"/>), never edited.</summary>
public sealed class MeterReading : ITenantOwned, IAppendOnly
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid MeterId { get; set; }
    public decimal Value { get; set; }

    /// <summary>When the value was measured (may be earlier than <see cref="RecordedAt"/> for offline entry).</summary>
    public DateTimeOffset ReadAt { get; set; }

    public DateTimeOffset RecordedAt { get; set; }
    public Guid? RecordedBy { get; set; }
    public ReadingSource Source { get; set; }
    public string? Note { get; set; }

    /// <summary>Cumulative meters only: the meter was replaced or rolled over, so a lower value is legitimate.</summary>
    public bool IsReset { get; set; }

    /// <summary>Client-generated id so a retried or offline-synced submission is stored once.</summary>
    public Guid? ClientId { get; set; }
}

/// <summary>Marks a reading as wrong. Voided readings are ignored by every check and report but remain on record.</summary>
public sealed class MeterReadingVoid : ITenantOwned, IAppendOnly
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ReadingId { get; set; }
    public DateTimeOffset VoidedAt { get; set; }
    public Guid? VoidedBy { get; set; }
    public string Reason { get; set; } = "";
}

public static class MeterRules
{
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(3650);

    public static bool IsValidUnit(string unit) => unit.Length is >= 1 and <= 20 && unit.All(c => !char.IsControl(c));

    /// <summary>
    /// Checks a cumulative meter's value against its neighbours in time. <paramref name="previous"/> is the closest earlier reading,
    /// <paramref name="next"/> the closest later one. A reset only has to follow its predecessor's time; it restarts the sequence,
    /// so a later reading is compared to the reset, not to what came before it.
    /// </summary>
    public static string? CheckCumulative(decimal value, bool isReset, decimal? previous, (decimal Value, bool IsReset)? next)
    {
        if (!isReset && previous is { } p && value < p)
            return $"A cumulative meter cannot go down (previous reading {p}). If the meter was replaced, record it as a reset.";
        if (next is { IsReset: false } n && value > n.Value)
            return $"This value is higher than a later reading ({n.Value}). Void the wrong reading first.";
        return null;
    }
}
