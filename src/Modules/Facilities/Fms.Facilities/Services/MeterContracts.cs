using Fms.Facilities.Domain;

namespace Fms.Facilities.Services;

public sealed record MeterDto(
    Guid Id, Guid AssetId, string Name, string? NameAr, string Unit, MeterKind Kind, decimal? MinValue, decimal? MaxValue, bool IsArchived,
    decimal? LastValue, DateTimeOffset? LastReadAt, string RowVersion)
{
    public static MeterDto From(Meter m, MeterReading? last) => new(
        m.Id, m.AssetId, m.Name, m.NameAr, m.Unit, m.Kind, m.MinValue, m.MaxValue, m.IsArchived, last?.Value, last?.ReadAt, Convert.ToBase64String(m.RowVersion));
}

public sealed record CreateMeterRequest(string? Name, string? NameAr, string? Unit, MeterKind Kind, decimal? MinValue, decimal? MaxValue);

/// <summary>Unit and kind are fixed once created: changing them would reinterpret every stored reading.</summary>
public sealed record UpdateMeterRequest(string? Name, string? NameAr, decimal? MinValue, decimal? MaxValue, string? RowVersion);

public sealed record MeterReadingDto(
    Guid Id, Guid MeterId, decimal Value, DateTimeOffset ReadAt, DateTimeOffset RecordedAt, Guid? RecordedBy, ReadingSource Source,
    string? Note, bool IsReset, Guid? ClientId, bool IsVoided)
{
    public static MeterReadingDto From(MeterReading r, bool voided) =>
        new(r.Id, r.MeterId, r.Value, r.ReadAt, r.RecordedAt, r.RecordedBy, r.Source, r.Note, r.IsReset, r.ClientId, voided);
}

public sealed record RecordReadingRequest(decimal Value, DateTimeOffset? ReadAt, string? Note, bool IsReset, Guid? ClientId, ReadingSource? Source);

/// <summary>The outcome of recording one reading: stored now, or recognised as an earlier submission of the same ClientId.</summary>
public sealed record RecordedReading(MeterReadingDto Reading, bool WasDuplicate);

public sealed record BatchReadingItem(Guid MeterId, decimal Value, DateTimeOffset? ReadAt, string? Note, bool IsReset, Guid? ClientId, ReadingSource? Source);

public sealed record BatchReadingResult(int Index, Guid MeterId, string Outcome, Guid? ReadingId, string? Error);

public sealed record VoidReadingRequest(string? Reason);

public sealed record ReadingQuery(DateTimeOffset? From = null, DateTimeOffset? To = null, bool IncludeVoided = false, int? Page = null, int? PageSize = null);
