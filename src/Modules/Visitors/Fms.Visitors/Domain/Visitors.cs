using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using Fms.BuildingBlocks.Tenancy;

namespace Fms.Visitors.Domain;

public enum VisitKind { Guest = 1, Contractor = 2, Delivery = 3, Interview = 4 }

public enum VisitStatus { Expected = 1, CheckedIn = 2, CheckedOut = 3, Cancelled = 4, NoShow = 5 }

/// <summary>One expected, arrived or departed visitor. Names of the host and the site are copied in, so the record of who was on site stays true if people or places change later.</summary>
public sealed class Visit : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid HostId { get; set; }
    public string HostName { get; set; } = "";
    public Guid SiteId { get; set; }
    public string SiteName { get; set; } = "";
    public VisitKind Kind { get; set; }
    public string VisitorName { get; set; } = "";
    public string? VisitorCompany { get; set; }
    public string? VisitorEmail { get; set; }
    public string? VisitorPhone { get; set; }
    public string? Purpose { get; set; }
    public DateTimeOffset ExpectedAt { get; set; }
    public DateTimeOffset? ExpectedUntil { get; set; }
    public VisitStatus Status { get; set; } = VisitStatus.Expected;

    /// <summary>SHA-256 of the pass code the visitor shows. The code itself is shown once and never stored.</summary>
    public string PassHash { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelReason { get; set; }
    public DateTimeOffset? CheckedInAt { get; set; }
    public Guid? CheckedInBy { get; set; }
    public string? BadgeNumber { get; set; }
    public DateTimeOffset? InductionAt { get; set; }

    /// <summary>The visitor's name matched the watchlist when they were checked in (and security let them in).</summary>
    public bool WatchlistMatch { get; set; }

    public string? OverrideReason { get; set; }
    public Guid? OverrideBy { get; set; }
    public DateTimeOffset? CheckedOutAt { get; set; }
    public Guid? CheckedOutBy { get; set; }

    /// <summary>Set when the visitor's personal details were removed at the end of the retention period.</summary>
    public DateTimeOffset? AnonymisedAt { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>A person who must not be let in without security's say. Matched on name only (see <see cref="VisitorRules.Normalize"/>); a match asks for a person's judgement, it does not decide.</summary>
public sealed class WatchlistEntry : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public string NameKey { get; set; } = "";
    public string Reason { get; set; } = "";
    public Guid? AddedBy { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }
    public Guid? RemovedBy { get; set; }
    public string? RemoveReason { get; set; }
}

/// <summary>Rules of visiting for one organisation. One row per tenant.</summary>
public sealed class VisitorSettings : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }

    /// <summary>Contractors must acknowledge the site induction at check-in.</summary>
    public bool RequireInduction { get; set; }

    public string? InductionText { get; set; }

    /// <summary>Days after the visit that the visitor's personal details are removed.</summary>
    public int RetentionDays { get; set; } = 365;

    public DateTimeOffset UpdatedAt { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>The rules of visiting, free of storage so they can be tested exhaustively.</summary>
public static class VisitorRules
{
    public const int MinRetentionDays = 30;
    public const int MaxRetentionDays = 3650;
    public static readonly TimeSpan CheckInOpensBefore = TimeSpan.FromHours(12);
    public static readonly TimeSpan DefaultStay = TimeSpan.FromHours(24);
    public static readonly TimeSpan MaxStay = TimeSpan.FromDays(14);

    /// <summary>
    /// What two spellings of a name must share to count as the same: lower case, accents and Arabic vowel marks removed, the Arabic letters that are written several ways
    /// folded together, and spaces collapsed. Deliberately generous, so a match is a prompt for a person to look, never a verdict.
    /// </summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var decomposed = name.Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var lastSpace = false;
        foreach (var raw in decomposed)
        {
            var c = raw;
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue; // accents, Arabic harakat, shadda
            if (c == 'ـ') continue; // tatweel (stretching)
            c = c switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا', // alef with hamza, madda, wasla -> alef
                'ى' => 'ي', // alef maqsura -> ya
                'ة' => 'ه', // ta marbuta -> ha
                _ => char.ToLowerInvariant(c),
            };
            if (char.IsWhiteSpace(c) || c is '-' or '.' or '\'' or '’')
            {
                if (!lastSpace && sb.Length > 0) sb.Append(' ');
                lastSpace = true;
                continue;
            }

            sb.Append(c);
            lastSpace = false;
        }

        return sb.ToString().Trim().Normalize(NormalizationForm.FormC);
    }

    public static DateTimeOffset EndOf(Visit v) => v.ExpectedUntil ?? v.ExpectedAt + DefaultStay;

    /// <summary>Checking in opens half a day before the expected time and closes when the visit's time is over.</summary>
    public static bool CanCheckIn(VisitStatus status, DateTimeOffset expectedAt, DateTimeOffset? expectedUntil, DateTimeOffset now) =>
        status == VisitStatus.Expected && now >= expectedAt - CheckInOpensBefore && now <= (expectedUntil ?? expectedAt + DefaultStay);

    /// <summary>An expected visit whose time is over without the visitor arriving.</summary>
    public static bool IsNoShow(VisitStatus status, DateTimeOffset expectedAt, DateTimeOffset? expectedUntil, DateTimeOffset now) =>
        status == VisitStatus.Expected && now > (expectedUntil ?? expectedAt + DefaultStay);

    public static bool IsDueForRemoval(DateTimeOffset expectedAt, int retentionDays, DateTimeOffset now) => expectedAt < now.AddDays(-retentionDays);

    /// <summary>The pass code from what a scanner or a person typed: the code itself, or the address a QR code holds (its last path segment).</summary>
    public static string? CodeFrom(string? scanned)
    {
        if (string.IsNullOrWhiteSpace(scanned)) return null;
        var text = scanned.Trim();
        var q = text.IndexOfAny(['?', '#']);
        if (q >= 0) text = text[..q];
        var last = text.TrimEnd('/').Split('/').Last();
        return last.Length is >= 16 and <= 64 && last.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? last : null;
    }
}
