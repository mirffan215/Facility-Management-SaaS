using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Facilities.Domain;
using Fms.Facilities.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Facilities.Services;

public sealed class MeterService(FacilitiesDbContext db, IAuditPort audit, ICurrentUser user, TimeProvider clock)
{
    public const int MaxMetersPerAsset = 50;
    public const int MaxBatchSize = 200;
    private const decimal MaxMagnitude = 1_000_000_000_000m;

    // ---- meters ----

    public async Task<Result<IReadOnlyList<MeterDto>>> ListForAssetAsync(Guid assetId, bool includeArchived, CancellationToken ct = default)
    {
        if (!await db.Assets.AnyAsync(a => a.Id == assetId, ct)) return Result<IReadOnlyList<MeterDto>>.NotFound("Asset not found.");

        var query = db.Meters.AsNoTracking().Where(m => m.AssetId == assetId);
        if (!includeArchived) query = query.Where(m => !m.IsArchived);
        var meters = await query.OrderBy(m => m.Name).ToListAsync(ct);

        var dtos = new List<MeterDto>();
        foreach (var m in meters) dtos.Add(MeterDto.From(m, await LastReadingAsync(m.Id, ct)));
        return Result<IReadOnlyList<MeterDto>>.Ok(dtos);
    }

    public async Task<Result<MeterDto>> GetAsync(Guid meterId, CancellationToken ct = default)
    {
        var meter = await db.Meters.AsNoTracking().FirstOrDefaultAsync(m => m.Id == meterId, ct);
        return meter is null ? Result<MeterDto>.NotFound() : Result<MeterDto>.Ok(MeterDto.From(meter, await LastReadingAsync(meterId, ct)));
    }

    public async Task<Result<MeterDto>> CreateAsync(Guid assetId, CreateMeterRequest r, CancellationToken ct = default)
    {
        var asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == assetId, ct);
        if (asset is null) return Result<MeterDto>.NotFound("Asset not found.");
        if (asset.Status == AssetStatus.Retired) return Result<MeterDto>.Conflict("A retired asset cannot get new meters.");

        var errors = ValidateDefinition(r.Name, r.NameAr, r.MinValue, r.MaxValue);
        var unit = r.Unit?.Trim() ?? "";
        if (!MeterRules.IsValidUnit(unit)) errors.Add("unit", "Unit is required (max 20 characters).");
        if (!Enum.IsDefined(r.Kind)) errors.Add("kind", "Unknown meter kind.");
        if (errors.Any) return errors.ToResult<MeterDto>();
        if (await db.Meters.CountAsync(m => m.AssetId == assetId && !m.IsArchived, ct) >= MaxMetersPerAsset)
            return Result<MeterDto>.Conflict($"An asset can have at most {MaxMetersPerAsset} active meters.");

        var meter = new Meter
        {
            AssetId = assetId, Name = r.Name!.Trim(), NameAr = Clean(r.NameAr), Unit = unit, Kind = r.Kind,
            MinValue = r.MinValue, MaxValue = r.MaxValue, CreatedAt = clock.GetUtcNow(),
        };
        db.Meters.Add(meter);
        audit.Record("meter.created", nameof(Meter), meter.Id.ToString(), JsonSerializer.Serialize(new { meter.AssetId, meter.Name, meter.Unit, meter.Kind }));
        return await SaveMeterAsync(meter, ct);
    }

    public async Task<Result<MeterDto>> UpdateAsync(Guid meterId, UpdateMeterRequest r, CancellationToken ct = default)
    {
        var meter = await db.Meters.FirstOrDefaultAsync(m => m.Id == meterId, ct);
        if (meter is null) return Result<MeterDto>.NotFound();

        var errors = ValidateDefinition(r.Name, r.NameAr, r.MinValue, r.MaxValue);
        if (!TryVersion(r.RowVersion, out var version)) errors.Add("rowVersion", "rowVersion is required.");
        if (errors.Any) return errors.ToResult<MeterDto>();

        meter.Name = r.Name!.Trim();
        meter.NameAr = Clean(r.NameAr);
        meter.MinValue = r.MinValue;
        meter.MaxValue = r.MaxValue;
        db.Entry(meter).Property(m => m.RowVersion).OriginalValue = version;
        audit.Record("meter.updated", nameof(Meter), meterId.ToString(), JsonSerializer.Serialize(new { meter.Name, meter.MinValue, meter.MaxValue }));
        return await SaveMeterAsync(meter, ct);
    }

    public async Task<Result<MeterDto>> SetArchivedAsync(Guid meterId, bool archived, string? rowVersion, CancellationToken ct = default)
    {
        var meter = await db.Meters.FirstOrDefaultAsync(m => m.Id == meterId, ct);
        if (meter is null) return Result<MeterDto>.NotFound();
        if (!TryVersion(rowVersion, out var version)) return Result<MeterDto>.Invalid("rowVersion", "rowVersion is required.");
        if (meter.IsArchived == archived) return Result<MeterDto>.Ok(MeterDto.From(meter, await LastReadingAsync(meterId, ct)));

        meter.IsArchived = archived;
        db.Entry(meter).Property(m => m.RowVersion).OriginalValue = version;
        audit.Record(archived ? "meter.archived" : "meter.restored", nameof(Meter), meterId.ToString());
        return await SaveMeterAsync(meter, ct);
    }

    // ---- readings ----

    public async Task<Result<RecordedReading>> RecordAsync(Guid meterId, RecordReadingRequest r, CancellationToken ct = default)
    {
        var meter = await db.Meters.AsNoTracking().FirstOrDefaultAsync(m => m.Id == meterId, ct);
        if (meter is null) return Result<RecordedReading>.NotFound("Meter not found.");

        // A retry of something already stored is not an error: hand back the original, whatever has happened since.
        if (r.ClientId is { } retryOf && await FindByClientIdAsync(meterId, retryOf, ct) is { } existing)
            return Result<RecordedReading>.Ok(new RecordedReading(MeterReadingDto.From(existing, await IsVoidedAsync(existing.Id, ct)), true));

        if (meter.IsArchived) return Result<RecordedReading>.Conflict("This meter is archived.");
        var asset = await db.Assets.AsNoTracking().FirstAsync(a => a.Id == meter.AssetId, ct);
        if (asset.Status == AssetStatus.Retired) return Result<RecordedReading>.Conflict("The asset is retired.");

        var now = clock.GetUtcNow();
        var readAt = r.ReadAt ?? now;
        var errors = new ValidationErrors();
        if (Math.Abs(r.Value) > MaxMagnitude) errors.Add("value", "Value is out of range.");
        if (meter.MinValue is { } min && r.Value < min) errors.Add("value", $"Value is below the meter's plausible minimum ({min}).");
        if (meter.MaxValue is { } max && r.Value > max) errors.Add("value", $"Value is above the meter's plausible maximum ({max}).");
        if (readAt > now + MeterRules.FutureTolerance) errors.Add("readAt", "A reading cannot be dated in the future.");
        if (readAt < now - MeterRules.MaxAge) errors.Add("readAt", "A reading cannot be older than ten years.");
        if (r.Note is { Length: > 500 }) errors.Add("note", "Note is at most 500 characters.");
        if (r.IsReset && meter.Kind != MeterKind.Cumulative) errors.Add("isReset", "Only a cumulative meter can be reset.");
        var source = r.Source ?? ReadingSource.Manual;
        if (source is not (ReadingSource.Manual or ReadingSource.Mobile)) errors.Add("source", "Source must be Manual or Mobile.");
        if (errors.Any) return errors.ToResult<RecordedReading>();

        if (meter.Kind == MeterKind.Cumulative)
        {
            var live = LiveReadings(meterId);
            var previous = await live.Where(x => x.ReadAt <= readAt).OrderByDescending(x => x.ReadAt).ThenByDescending(x => x.RecordedAt)
                .Select(x => (decimal?)x.Value).FirstOrDefaultAsync(ct);
            var nextRow = await live.Where(x => x.ReadAt > readAt).OrderBy(x => x.ReadAt).ThenBy(x => x.RecordedAt)
                .Select(x => new { x.Value, x.IsReset }).FirstOrDefaultAsync(ct);

            if (MeterRules.CheckCumulative(r.Value, r.IsReset, previous, nextRow is null ? null : (nextRow.Value, nextRow.IsReset)) is { } problem)
                return Result<RecordedReading>.Invalid("value", problem);
        }

        var reading = new MeterReading
        {
            MeterId = meterId, Value = r.Value, ReadAt = readAt, RecordedAt = now, RecordedBy = user.UserId, Source = source,
            Note = Clean(r.Note), IsReset = r.IsReset, ClientId = r.ClientId,
        };
        db.MeterReadings.Add(reading);
        try
        {
            await db.SaveChangesAsync(ct);
            return Result<RecordedReading>.Ok(new RecordedReading(MeterReadingDto.From(reading, false), false));
        }
        catch (DbUpdateException ex) when (SqlErrors.IsUniqueViolation(ex) && r.ClientId is { } clientId)
        {
            // Two submissions of the same ClientId raced; the unique index let one win.
            db.ChangeTracker.Clear();
            var winner = await FindByClientIdAsync(meterId, clientId, ct);
            return winner is null
                ? Result<RecordedReading>.Conflict("The reading could not be stored. Try again.")
                : Result<RecordedReading>.Ok(new RecordedReading(MeterReadingDto.From(winner, false), true));
        }
    }

    /// <summary>Stores many readings (offline sync). Each is judged on its own: one bad item never blocks the rest, and the result says what happened to each.</summary>
    public async Task<Result<IReadOnlyList<BatchReadingResult>>> RecordBatchAsync(IReadOnlyList<BatchReadingItem>? items, CancellationToken ct = default)
    {
        if (items is null || items.Count == 0) return Result<IReadOnlyList<BatchReadingResult>>.Invalid("items", "At least one reading is required.");
        if (items.Count > MaxBatchSize) return Result<IReadOnlyList<BatchReadingResult>>.Invalid("items", $"At most {MaxBatchSize} readings per call.");

        var results = new List<BatchReadingResult>();
        // Oldest first, so a cumulative sequence recorded offline validates in the order it happened.
        foreach (var (item, index) in items.Select((item, i) => (item, i)).OrderBy(x => x.item.ReadAt ?? DateTimeOffset.MaxValue))
        {
            var result = await RecordAsync(item.MeterId, new RecordReadingRequest(item.Value, item.ReadAt, item.Note, item.IsReset, item.ClientId, item.Source), ct);
            results.Add(result.IsOk
                ? new BatchReadingResult(index, item.MeterId, result.Value!.WasDuplicate ? "Duplicate" : "Created", result.Value.Reading.Id, null)
                : new BatchReadingResult(index, item.MeterId, "Rejected", null, result.Message ?? string.Join(" ", result.Errors?.SelectMany(e => e.Value) ?? [])));
        }

        return Result<IReadOnlyList<BatchReadingResult>>.Ok(results.OrderBy(r => r.Index).ToList());
    }

    public async Task<Result<Page<MeterReadingDto>>> ListReadingsAsync(Guid meterId, ReadingQuery q, CancellationToken ct = default)
    {
        if (!await db.Meters.AnyAsync(m => m.Id == meterId, ct)) return Result<Page<MeterReadingDto>>.NotFound("Meter not found.");

        var (number, size) = Page<MeterReadingDto>.Normalize(q.Page, q.PageSize);
        var query = db.MeterReadings.AsNoTracking().Where(r => r.MeterId == meterId);
        if (q.From is { } from) query = query.Where(r => r.ReadAt >= from);
        if (q.To is { } to) query = query.Where(r => r.ReadAt <= to);
        if (!q.IncludeVoided) query = query.Where(r => !db.MeterReadingVoids.Any(v => v.ReadingId == r.Id));

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(r => r.ReadAt).ThenByDescending(r => r.RecordedAt).Skip((number - 1) * size).Take(size).ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var voided = (await db.MeterReadingVoids.AsNoTracking().Where(v => ids.Contains(v.ReadingId)).Select(v => v.ReadingId).ToListAsync(ct)).ToHashSet();
        return Result<Page<MeterReadingDto>>.Ok(new Page<MeterReadingDto>(rows.Select(r => MeterReadingDto.From(r, voided.Contains(r.Id))).ToList(), total, number, size));
    }

    /// <summary>Marks a reading wrong so it stops affecting checks, trends and the meter's latest value. The reading itself stays on record.</summary>
    public async Task<Result<MeterReadingDto>> VoidAsync(Guid readingId, VoidReadingRequest r, CancellationToken ct = default)
    {
        var reading = await db.MeterReadings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == readingId, ct);
        if (reading is null) return Result<MeterReadingDto>.NotFound();
        var reason = Clean(r.Reason);
        if (reason is null || reason.Length > 500) return Result<MeterReadingDto>.Invalid("reason", "A reason is required (max 500 characters).");
        if (await IsVoidedAsync(readingId, ct)) return Result<MeterReadingDto>.Conflict("This reading is already voided.");

        db.MeterReadingVoids.Add(new MeterReadingVoid { ReadingId = readingId, VoidedAt = clock.GetUtcNow(), VoidedBy = user.UserId, Reason = reason });
        audit.Record("meter.reading_voided", nameof(MeterReading), readingId.ToString(), JsonSerializer.Serialize(new { reading.MeterId, reading.Value, reading.ReadAt, reason }));
        try
        {
            await db.SaveChangesAsync(ct);
            return Result<MeterReadingDto>.Ok(MeterReadingDto.From(reading, true));
        }
        catch (DbUpdateException ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return Result<MeterReadingDto>.Conflict("This reading is already voided.");
        }
    }

    // ---- helpers ----

    private IQueryable<MeterReading> LiveReadings(Guid meterId) =>
        db.MeterReadings.Where(r => r.MeterId == meterId && !db.MeterReadingVoids.Any(v => v.ReadingId == r.Id));

    private Task<MeterReading?> LastReadingAsync(Guid meterId, CancellationToken ct) =>
        LiveReadings(meterId).AsNoTracking().OrderByDescending(r => r.ReadAt).ThenByDescending(r => r.RecordedAt).FirstOrDefaultAsync(ct);

    private Task<MeterReading?> FindByClientIdAsync(Guid meterId, Guid clientId, CancellationToken ct) =>
        db.MeterReadings.AsNoTracking().FirstOrDefaultAsync(r => r.MeterId == meterId && r.ClientId == clientId, ct);

    private Task<bool> IsVoidedAsync(Guid readingId, CancellationToken ct) => db.MeterReadingVoids.AnyAsync(v => v.ReadingId == readingId, ct);

    private async Task<Result<MeterDto>> SaveMeterAsync(Meter meter, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return Result<MeterDto>.Ok(MeterDto.From(meter, await LastReadingAsync(meter.Id, ct)));
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<MeterDto>.Conflict("The meter was changed by someone else. Reload it and try again.");
        }
        catch (DbUpdateException ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return Result<MeterDto>.Conflict($"This asset already has an active meter named '{meter.Name}'.");
        }
    }

    private static ValidationErrors ValidateDefinition(string? name, string? nameAr, decimal? min, decimal? max)
    {
        var errors = new ValidationErrors();
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200) errors.Add("name", "Name is required (max 200 characters).");
        if (nameAr is { Length: > 200 }) errors.Add("nameAr", "Arabic name is at most 200 characters.");
        if (min is { } lo && max is { } hi && lo > hi) errors.Add("maxValue", "Maximum cannot be below the minimum.");
        return errors;
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static bool TryVersion(string? value, out byte[] version)
    {
        version = [];
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { version = Convert.FromBase64String(value); return version.Length > 0; }
        catch (FormatException) { return false; }
    }
}
