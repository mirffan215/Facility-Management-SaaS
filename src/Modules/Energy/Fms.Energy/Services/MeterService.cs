using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Energy.Domain;
using Fms.Energy.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Energy.Services;

public sealed class MeterService(EnergyDbContext db, IFacilityLookup facilities, ICurrentUser user, IAuditPort audit, TimeProvider clock)
{
    public const int MaxMeters = 2000;

    public async Task<Page<EnergyMeterDto>> ListAsync(EnergyKind? kind, Guid? locationId, string? q, bool includeArchived, int? page, int? pageSize, CancellationToken ct = default)
    {
        var query = db.Meters.AsNoTracking().AsQueryable();
        if (!includeArchived) query = query.Where(m => !m.IsArchived);
        if (kind is { } k) query = query.Where(m => m.Kind == k);
        if (locationId is { } l) query = query.Where(m => m.LocationId == l);
        if (Common.Clean(q) is { } text) query = query.Where(m => m.Code.Contains(text) || m.Name.Contains(text) || (m.LocationName != null && m.LocationName.Contains(text)));
        var total = await query.CountAsync(ct);
        var (number, size) = Page<EnergyMeterDto>.Normalize(page, pageSize);
        var rows = await query.OrderBy(m => m.Code).Skip((number - 1) * size).Take(size).ToListAsync(ct);
        var ids = rows.Select(m => m.Id).ToList();
        var latest = await LatestAsync(ids, ct);
        return new Page<EnergyMeterDto>(rows.Select(m => ToDto(m, latest.GetValueOrDefault(m.Id))).ToList(), total, number, size);
    }

    public async Task<Result<EnergyMeterDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var m = await db.Meters.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null) return Result<EnergyMeterDto>.NotFound();
        return Result<EnergyMeterDto>.Ok(ToDto(m, (await LatestAsync([id], ct)).GetValueOrDefault(id)));
    }

    public async Task<Result<EnergyMeterDto>> CreateAsync(SaveEnergyMeterRequest r, CancellationToken ct = default)
    {
        if (await db.Meters.CountAsync(ct) >= MaxMeters) return Result<EnergyMeterDto>.Conflict($"At most {MaxMeters} meters.");
        var m = new EnergyMeter { CreatedAt = clock.GetUtcNow(), CreatedBy = user.UserId };
        var invalid = await ApplyAsync(m, r, ct);
        if (invalid is not null) return invalid;
        if (await db.Meters.AnyAsync(x => x.Code == m.Code, ct)) return Result<EnergyMeterDto>.Conflict($"A meter with code {m.Code} already exists.");
        db.Meters.Add(m);
        audit.Record("energy.meter_created", nameof(EnergyMeter), m.Id.ToString(), JsonSerializer.Serialize(new { m.Code, m.Kind, m.Unit }));
        await db.SaveChangesAsync(ct);
        return Result<EnergyMeterDto>.Ok(ToDto(m, null));
    }

    public async Task<Result<EnergyMeterDto>> UpdateAsync(Guid id, SaveEnergyMeterRequest r, CancellationToken ct = default)
    {
        var m = await db.Meters.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null) return Result<EnergyMeterDto>.NotFound();
        if (!Common.TryVersion(r.RowVersion, out var version)) return Result<EnergyMeterDto>.Invalid("rowVersion", "Reload and try again.");
        db.Entry(m).Property(x => x.RowVersion).OriginalValue = version;
        var hasReadings = await db.Readings.AnyAsync(x => x.MeterId == id, ct);
        var (kind, unit, cumulative) = (m.Kind, m.Unit, m.IsCumulative);
        var invalid = await ApplyAsync(m, r, ct);
        if (invalid is not null) return invalid;
        if (hasReadings && (m.Kind != kind || m.Unit != unit || m.IsCumulative != cumulative))
        {
            db.ChangeTracker.Clear();
            return Result<EnergyMeterDto>.Conflict("The kind, unit and type of a meter cannot change once it has readings. Archive it and add a new meter.");
        }

        if (await db.Meters.AnyAsync(x => x.Code == m.Code && x.Id != id, ct)) { db.ChangeTracker.Clear(); return Result<EnergyMeterDto>.Conflict($"A meter with code {m.Code} already exists."); }
        audit.Record("energy.meter_updated", nameof(EnergyMeter), id.ToString(), JsonSerializer.Serialize(new { m.Code }));
        return await SaveAsync(m, ct);
    }

    public async Task<Result<EnergyMeterDto>> ArchiveAsync(Guid id, bool archive, ArchiveEnergyMeterRequest r, CancellationToken ct = default)
    {
        var m = await db.Meters.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null) return Result<EnergyMeterDto>.NotFound();
        if (!Common.TryVersion(r.RowVersion, out var version)) return Result<EnergyMeterDto>.Invalid("rowVersion", "Reload and try again.");
        db.Entry(m).Property(x => x.RowVersion).OriginalValue = version;
        m.IsArchived = archive;
        audit.Record(archive ? "energy.meter_archived" : "energy.meter_restored", nameof(EnergyMeter), id.ToString());
        return await SaveAsync(m, ct);
    }

    private async Task<Result<EnergyMeterDto>?> ApplyAsync(EnergyMeter m, SaveEnergyMeterRequest r, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var code = Common.Clean(r.Code)?.ToUpperInvariant();
        if (code is null || code.Length > 50 || code.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))) errors.Add("code", "A short code of letters, digits, - _ or . (at most 50).");
        var name = Common.Clean(r.Name);
        if (name is null || name.Length > 200) errors.Add("name", "Give it a name (at most 200 characters).");
        if (r.Kind is not { } kind || !Enum.IsDefined(kind)) errors.Add("kind", "Choose a kind.");
        var unit = Common.Clean(r.Unit);
        if (unit is null || unit.Length > 20) errors.Add("unit", "Give the unit, such as kWh or m3 (at most 20 characters).");
        if (r.RolloverAt is { } wrap && (wrap <= 0 || wrap > EnergyMath.MaxValue)) errors.Add("rolloverAt", "The wrap-around value must be above zero.");
        if (r.RolloverAt is not null && r.IsCumulative == false) errors.Add("rolloverAt", "Only a counting register can wrap around.");
        FacilityRef? location = null;
        if (r.LocationId is { } lid && (location = await facilities.GetLocationAsync(lid, ct)) is null) errors.Add("locationId", "That place does not exist.");
        if (errors.Any) return errors.ToResult<EnergyMeterDto>();

        m.Code = code!;
        m.Name = name!;
        m.Kind = r.Kind!.Value;
        m.Unit = unit!;
        m.LocationId = location?.Id;
        m.LocationName = location?.Name;
        m.IsCumulative = r.IsCumulative ?? true;
        m.RolloverAt = r.RolloverAt;
        return null;
    }

    private async Task<Result<EnergyMeterDto>> SaveAsync(EnergyMeter m, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<EnergyMeterDto>.Conflict("Someone else changed this. Reload and try again.");
        }

        return Result<EnergyMeterDto>.Ok(ToDto(m, (await LatestAsync([m.Id], ct)).GetValueOrDefault(m.Id)));
    }

    private async Task<Dictionary<Guid, (DateTimeOffset At, decimal Value)>> LatestAsync(IReadOnlyCollection<Guid> meterIds, CancellationToken ct)
    {
        var rows = await db.Readings.AsNoTracking().Where(r => meterIds.Contains(r.MeterId) && !db.Voids.Any(v => v.ReadingId == r.Id))
            .GroupBy(r => r.MeterId).Select(g => g.OrderByDescending(r => r.At).Select(r => new { r.MeterId, r.At, r.Value }).First()).ToListAsync(ct);
        return rows.ToDictionary(r => r.MeterId, r => (r.At, r.Value));
    }

    internal static EnergyMeterDto ToDto(EnergyMeter m, (DateTimeOffset At, decimal Value)? latest) =>
        new(m.Id, m.Code, m.Name, m.Kind, m.Unit, m.LocationId, m.LocationName, m.IsCumulative, m.RolloverAt, m.IsArchived, latest?.At, latest?.Value, Common.Version(m.RowVersion));
}
