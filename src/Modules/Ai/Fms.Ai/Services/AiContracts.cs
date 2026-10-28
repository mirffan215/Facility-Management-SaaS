using Fms.Ai.Domain;

namespace Fms.Ai.Services;

public sealed record AiStatusDto(bool Available, bool Enabled, bool Triage, bool Summary, int DailyLimit, int UsedToday);

public sealed record AiSettingsDto(bool Available, bool Enabled, bool TriageEnabled, bool SummaryEnabled, int DailyLimit, string? RowVersion);

public sealed record SaveAiSettingsRequest(bool? Enabled, bool? TriageEnabled, bool? SummaryEnabled, int? DailyLimit, string? RowVersion);

public sealed record TriageRequest(string? Title, string? Description);

public sealed record TriageSuggestionDto(Guid? CategoryId, string? CategoryCode, string? CategoryName, string? Priority, double Confidence, string Reason, Guid? UsageId = null);

public sealed record SummaryDto(Guid WorkOrderId, string Text, Guid? UsageId = null);

public sealed record FeedbackRequest(Guid? UsageId, bool? Accepted);

public sealed record UsageLineDto(AiFeature Feature, int Ok, int Failed, long InputTokens, long OutputTokens, int Accepted = 0, int Dismissed = 0);

public sealed record UsageDto(int UsedToday, int DailyLimit, IReadOnlyList<UsageLineDto> Last30Days);
