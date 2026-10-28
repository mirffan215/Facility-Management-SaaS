using Fms.Ai.Domain;

namespace Fms.Ai.Services;

public sealed record AiStatusDto(bool Available, bool Enabled, bool Triage, bool Summary, int DailyLimit, int UsedToday);

public sealed record AiSettingsDto(bool Available, bool Enabled, bool TriageEnabled, bool SummaryEnabled, int DailyLimit, string? RowVersion);

public sealed record SaveAiSettingsRequest(bool? Enabled, bool? TriageEnabled, bool? SummaryEnabled, int? DailyLimit, string? RowVersion);

public sealed record TriageRequest(string? Title, string? Description);

public sealed record TriageSuggestionDto(Guid? CategoryId, string? CategoryCode, string? CategoryName, string? Priority, double Confidence, string Reason);

public sealed record SummaryDto(Guid WorkOrderId, string Text);

public sealed record UsageLineDto(AiFeature Feature, int Ok, int Failed, long InputTokens, long OutputTokens);

public sealed record UsageDto(int UsedToday, int DailyLimit, IReadOnlyList<UsageLineDto> Last30Days);
