using Fms.Energy.Domain;

namespace Fms.Energy.Services;

public sealed record EnergyMeterDto(
    Guid Id, string Code, string Name, EnergyKind Kind, string Unit, Guid? LocationId, string? LocationName, bool IsCumulative, decimal? RolloverAt, bool IsArchived,
    DateTimeOffset? LatestAt, decimal? LatestValue, string RowVersion);

public sealed record SaveEnergyMeterRequest(string? Code, string? Name, EnergyKind? Kind, string? Unit, Guid? LocationId, bool? IsCumulative, decimal? RolloverAt, string? RowVersion);

public sealed record ArchiveEnergyMeterRequest(string? RowVersion);

public sealed record ReadingDto(Guid Id, Guid MeterId, DateTimeOffset At, decimal Value, ReadingSource Source, DateTimeOffset RecordedAt, string? Note, bool IsVoided, string? VoidReason);

public sealed record AddReadingRequest(DateTimeOffset? At, decimal? Value, string? Note);

public sealed record VoidReadingRequest(string? Reason);

public sealed record ImportRowDto(int Line, string? Meter, string Status, string? Message);

public sealed record ImportResultDto(bool DryRun, int Total, int Accepted, int Duplicates, int Rejected, IReadOnlyList<ImportRowDto> Rows);

public sealed record TariffDto(Guid Id, EnergyKind Kind, DateOnly EffectiveFrom, decimal PricePerUnit, string Currency, string RowVersion);

public sealed record SaveTariffRequest(EnergyKind? Kind, DateOnly? EffectiveFrom, decimal? PricePerUnit, string? Currency, string? RowVersion);

public sealed record DeviceDto(Guid Id, string Name, string Prefix, IReadOnlyList<Guid> MeterIds, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt, string State);

public sealed record CreateDeviceRequest(string? Name, IReadOnlyList<Guid>? MeterIds, int? ExpiresInDays);

/// <summary>A new device with its secret. The secret is shown now and cannot be shown again (only a hash is kept).</summary>
public sealed record CreatedDeviceDto(DeviceDto Device, string Secret, string IngestPath);

public sealed record IngestReading(string? Meter, DateTimeOffset? At, decimal? Value);

public sealed record IngestRequest(IReadOnlyList<IngestReading>? Readings);

public sealed record IngestError(int Index, string Message);

public sealed record IngestResultDto(int Accepted, int Duplicates, IReadOnlyList<IngestError> Errors);

public sealed record DeviceAccess(Guid TenantId, Guid DeviceId, IReadOnlyList<Guid> MeterIds);

public sealed record DayUse(DateOnly Day, decimal Amount);

public sealed record MeterUseDto(Guid MeterId, string Code, string Name, EnergyKind Kind, string Unit, decimal Total, decimal? Cost, string? Currency, int Resets, IReadOnlyList<DayUse> Days);

public sealed record KindTotalDto(EnergyKind Kind, string Unit, decimal Total, decimal? Cost, string? Currency, bool MixedUnits);

public sealed record ConsumptionDto(DateOnly From, DateOnly To, IReadOnlyList<MeterUseDto> Meters, IReadOnlyList<KindTotalDto> Totals);

public sealed record AlarmRuleDto(Guid Id, Guid MeterId, string MeterCode, string MeterName, AlarmKind Kind, decimal Threshold, bool IsActive, bool IsRaised, DateTimeOffset? RaisedAt, string RowVersion);

public sealed record SaveAlarmRuleRequest(Guid? MeterId, AlarmKind? Kind, decimal? Threshold, bool? IsActive, string? RowVersion);

public sealed record AlarmEventDto(Guid RuleId, Guid MeterId, string MeterCode, DateTimeOffset At, AlarmEventKind Kind, decimal? Value);
