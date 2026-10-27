using System.ComponentModel.DataAnnotations;
using Fms.BuildingBlocks.Tenancy;

namespace Fms.Spaces.Domain;

public enum SpaceKind { Office = 1, Meeting = 2, Desk = 3, Parking = 4, Storage = 5, Common = 6, Other = 7 }

/// <summary>What the space management module adds to a space (a Space-type location owned by Facilities): what it is used for and how many people it holds.</summary>
public sealed class SpaceProfile : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid LocationId { get; set; }
    public SpaceKind Kind { get; set; } = SpaceKind.Office;

    /// <summary>People it holds (seats, desks, bays). Zero means not set, and nothing can be allocated to it.</summary>
    public int Capacity { get; set; }

    public string? Notes { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>An image of a floor. Uploading a new one supersedes the old (kept for history); pins are positions as fractions of the image, so they survive a new drawing of the same floor.</summary>
public sealed class FloorPlan : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid FloorId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string StorageKey { get; set; } = "";
    public DateTimeOffset UploadedAt { get; set; }
    public Guid? UploadedBy { get; set; }
    public DateTimeOffset? SupersededAt { get; set; }
}

public enum PinKind { Space = 1, Asset = 2 }

/// <summary>Where something is on a floor's plan. X and Y are fractions (0 to 1) of the image width and height, from the top left.</summary>
public sealed class PlanPin : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid FloorId { get; set; }
    public PinKind Kind { get; set; }
    public Guid TargetId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
}

public enum OccupantKind { Person = 1, Team = 2 }

/// <summary>Space given to a person or a team for a period. Ended early (released) or cancelled (never started) allocations stay as history and count for nothing.</summary>
public sealed class Allocation : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid SpaceId { get; set; }
    public OccupantKind Kind { get; set; }
    public Guid? UserId { get; set; }
    public string? TeamName { get; set; }

    /// <summary>People it takes up: one for a person, the team's size for a team.</summary>
    public int Headcount { get; set; } = 1;

    public DateOnly StartOn { get; set; }

    /// <summary>The last day. Null means until further notice.</summary>
    public DateOnly? EndOn { get; set; }

    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public Guid? EndedBy { get; set; }
    public string? EndReason { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>The arithmetic of allocation, free of storage so it can be tested exhaustively.</summary>
public static class SpaceRules
{
    public const int MaxCapacity = 100_000;
    public static readonly DateOnly Forever = new(9999, 12, 31);

    public static bool IsFraction(double v) => !double.IsNaN(v) && v is >= 0 and <= 1;

    public static bool Overlap(DateOnly aStart, DateOnly? aEnd, DateOnly bStart, DateOnly? bEnd) =>
        aStart <= (bEnd ?? Forever) && bStart <= (aEnd ?? Forever);

    /// <summary>The most people in the space on any one day of the period, counting the given allocations. Used both to check a new allocation fits and to report peaks.</summary>
    public static int Peak(IEnumerable<(DateOnly Start, DateOnly? End, int Headcount)> allocations, DateOnly from, DateOnly? to)
    {
        var inside = allocations.Where(a => Overlap(a.Start, a.End, from, to)).ToList();
        if (inside.Count == 0) return 0;

        // Occupancy only rises on a start date, so the peak is on the period's first day or on a start date within it.
        var days = inside.Select(a => a.Start).Where(s => s > from).Append(from).Distinct();
        return days.Max(d => inside.Where(a => a.Start <= d && d <= (a.End ?? Forever)).Sum(a => a.Headcount));
    }

    public static decimal? UtilisationPercent(int capacity, int allocated) => capacity <= 0 ? null : Math.Round(allocated * 100m / capacity, 1);

    public static bool IsActiveOn(DateOnly start, DateOnly? end, DateOnly day) => start <= day && day <= (end ?? Forever);
}
