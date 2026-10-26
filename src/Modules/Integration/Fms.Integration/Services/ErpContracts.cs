using Fms.BuildingBlocks.Integration;
using Fms.Integration.Domain;

namespace Fms.Integration.Services;

public sealed record ErpSettingsDto(bool Enabled, string Adapter, string CompanyId, IReadOnlyList<string> AvailableAdapters, string? RowVersion);

public sealed record SaveErpSettingsRequest(bool? Enabled, string? Adapter, string? CompanyId, string? RowVersion);

public sealed record ErpItemDto(
    Guid Id, ErpEntity Entity, Guid EntityId, string? Label, long Revision, ExportStatus Status, int Attempts, DateTimeOffset NextAttemptAt, string? LastError, string? ErpReference,
    DateTimeOffset CreatedAt, DateTimeOffset? SucceededAt, ReconcileState Reconcile, DateTimeOffset? ReconciledAt, string? ReconcileDetail);

public sealed record ErpSummaryDto(bool Enabled, int Pending, int Sending, int Succeeded, int DeadLetter, int Superseded, int Mismatched, DateTimeOffset? OldestPendingAt);

public sealed record ReconcileResultDto(int Checked, int Matched, int Mismatched, int Missing);
