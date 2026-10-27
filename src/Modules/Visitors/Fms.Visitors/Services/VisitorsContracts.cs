using Fms.Visitors.Domain;

namespace Fms.Visitors.Services;

public sealed record VisitDto(
    Guid Id, Guid HostId, string HostName, Guid SiteId, string SiteName, VisitKind Kind, string VisitorName, string? VisitorCompany, string? VisitorEmail, string? VisitorPhone, string? Purpose,
    DateTimeOffset ExpectedAt, DateTimeOffset? ExpectedUntil, VisitStatus Status, DateTimeOffset? CheckedInAt, DateTimeOffset? CheckedOutAt, string? BadgeNumber, DateTimeOffset? InductionAt,
    string? CancelReason, bool CanCheckInNow, bool Anonymised, string RowVersion);

public sealed record CreateVisitRequest(
    Guid? SiteId, VisitKind? Kind, string? VisitorName, string? VisitorCompany, string? VisitorEmail, string? VisitorPhone, string? Purpose, DateTimeOffset? ExpectedAt, DateTimeOffset? ExpectedUntil, Guid? HostId);

/// <summary>A new visit with its pass. The address and the picture are shown now and cannot be shown again (only a hash is kept).</summary>
public sealed record CreatedVisitDto(VisitDto Visit, string PassPath, string PassUrl, string QrSvg);

public sealed record CancelVisitRequest(string? Reason, string? RowVersion);

public sealed record CheckInRequest(string? BadgeNumber, bool? InductionAcknowledged, string? OverrideReason, string? RowVersion);

public sealed record CheckOutRequest(string? RowVersion);

/// <summary>What a visitor sees on their own pass: nothing about anyone else, and nothing about why they are visiting.</summary>
public sealed record PassDto(string VisitorName, string SiteName, string HostName, DateTimeOffset ExpectedAt, DateTimeOffset? ExpectedUntil, VisitStatus Status);

public sealed record WatchlistDto(Guid Id, string Name, string Reason, DateTimeOffset AddedAt);

public sealed record AddWatchlistRequest(string? Name, string? Reason);

public sealed record RemoveWatchlistRequest(string? Reason);

public sealed record VisitorSettingsDto(bool RequireInduction, string? InductionText, int RetentionDays, string? RowVersion);

public sealed record SaveVisitorSettingsRequest(bool? RequireInduction, string? InductionText, int? RetentionDays, string? RowVersion);

public static class VisitorMessages
{
    /// <summary>A visitor arrived. Payload: <see cref="VisitNoticeMessage"/>.</summary>
    public const string Arrived = "visitors.arrived";
}

public sealed record VisitNoticeMessage(Guid VisitId);
