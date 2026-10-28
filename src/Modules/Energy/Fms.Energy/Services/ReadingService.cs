using System.Globalization;
using System.Text;
using System.Text.Json;
using Fms.BuildingBlocks.Csv;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Energy.Domain;
using Fms.Energy.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Energy.Services;

/// <summary>Adding, listing and voiding readings, from a person, a CSV file or a device. Readings are only ever added; a wrong one is voided with a reason.</summary>
public sealed class ReadingService(EnergyDbContext db, ICurrentUser user, IAuditPort audit, TimeProvider clock)
{
    public const int MaxBatch = 500;
    public const int MaxImportRows = 5000;
    public const int MaxImportBytes = 2 * 1024 * 1024;
    public static readonly TimeSpan FutureSlack = TimeSpan.FromMinutes(5);
    public const int MaxAgeDays = 730;

    public enum Outcome { Added, Duplicate, Rejected }

    public sealed record AddResult(Outcome Outcome, string? Message = null, EnergyReading? Reading = null);

    public async Task<Result<Page<ReadingDto>>> ListAsync(Guid meterId, DateOnly? from, DateOnly? to, int? page, int? pageSize, CancellationToken ct = default)
    {
        if (!await db.Meters.AnyAsync(m => m.Id == meterId, ct)) return Result<Page<ReadingDto>>.NotFound();
        var query = db.Readings.AsNoTracking().Where(r => r.MeterId == meterId);
        if (from is { } f) query = query.Where(r => r.At >= new DateTimeOffset(f.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        if (to is { } t) query = query.Where(r => r.At < new DateTimeOffset(t.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        var total = await query.CountAsync(ct);
        var (number, size) = Page<ReadingDto>.Normalize(page, pageSize);
        var rows = await query.OrderByDescending(r => r.At).Skip((number - 1) * size).Take(size).ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var voids = await db.Voids.AsNoTracking().Where(v => ids.Contains(v.ReadingId)).ToDictionaryAsync(v => v.ReadingId, v => v.Reason, ct);
        return Result<Page<ReadingDto>>.Ok(new Page<ReadingDto>(rows.Select(r => new ReadingDto(r.Id, r.MeterId, r.At, r.Value, r.Source, r.RecordedAt, r.Note, voids.ContainsKey(r.Id), voids.GetValueOrDefault(r.Id))).ToList(), total, number, size));
    }

    public async Task<Result<ReadingDto>> AddAsync(Guid meterId, AddReadingRequest r, CancellationToken ct = default)
    {
        var meter = await db.Meters.AsNoTracking().FirstOrDefaultAsync(m => m.Id == meterId, ct);
        if (meter is null) return Result<ReadingDto>.NotFound();
        if (meter.IsArchived) return Result<ReadingDto>.Conflict("This meter is archived.");
        var errors = new ValidationErrors();
        if (r.Value is null) errors.Add("value", "Enter the reading.");
        var note = Common.Clean(r.Note);
        if (note is { Length: > 200 }) errors.Add("note", "The note is at most 200 characters.");
        if (errors.Any) return errors.ToResult<ReadingDto>();

        var result = await TryAddAsync(meter, r.At ?? clock.GetUtcNow(), r.Value!.Value, ReadingSource.Manual, null, note, strict: true, ct);
        if (result.Outcome == Outcome.Rejected) return Result<ReadingDto>.Invalid(result.Message!.Contains("time", StringComparison.OrdinalIgnoreCase) || result.Message.Contains("already", StringComparison.OrdinalIgnoreCase) ? "at" : "value", result.Message);
        if (result.Outcome == Outcome.Duplicate) return Result<ReadingDto>.Conflict(result.Message ?? "That reading is already recorded.");
        audit.Record("energy.reading_added", nameof(EnergyReading), result.Reading!.Id.ToString(), JsonSerializer.Serialize(new { meterId, result.Reading.At, result.Reading.Value }));
        await db.SaveChangesAsync(ct);
        return Result<ReadingDto>.Ok(ToDto(result.Reading));
    }

    public async Task<Result<ReadingDto>> VoidAsync(Guid readingId, VoidReadingRequest r, CancellationToken ct = default)
    {
        var reading = await db.Readings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == readingId, ct);
        if (reading is null) return Result<ReadingDto>.NotFound();
        var reason = Common.Clean(r.Reason);
        if (reason is null || reason.Length > 500) return Result<ReadingDto>.Invalid("reason", "A reason is required (at most 500 characters).");
        if (await db.Voids.AnyAsync(v => v.ReadingId == readingId, ct)) return Result<ReadingDto>.Conflict("That reading is already voided.");
        db.Voids.Add(new ReadingVoid { ReadingId = readingId, At = clock.GetUtcNow(), By = user.UserId, Reason = reason });
        audit.Record("energy.reading_voided", nameof(EnergyReading), readingId.ToString(), JsonSerializer.Serialize(new { reading.MeterId, reading.At, reading.Value, reason }));
        await db.SaveChangesAsync(ct);
        return Result<ReadingDto>.Ok(ToDto(reading) with { IsVoided = true, VoidReason = reason });
    }

    /// <summary>Readings from a device. Each row is judged on its own; a bad row never stops the good ones. Devices are trusted to say what the meter showed, so a lower value is taken as a reset, not refused.</summary>
    public async Task<Result<IngestResultDto>> IngestAsync(DeviceAccess access, IngestRequest r, CancellationToken ct = default)
    {
        var rows = r.Readings ?? [];
        if (rows.Count == 0) return Result<IngestResultDto>.Invalid("readings", "Send at least one reading.");
        if (rows.Count > MaxBatch) return Result<IngestResultDto>.Invalid("readings", $"Send at most {MaxBatch} readings at a time.");

        var meters = await db.Meters.AsNoTracking().Where(m => access.MeterIds.Contains(m.Id) && !m.IsArchived).ToListAsync(ct);
        var byKey = meters.ToDictionary(m => m.Code, StringComparer.OrdinalIgnoreCase);
        foreach (var m in meters) byKey[m.Id.ToString()] = m;

        var errors = new List<IngestError>();
        int accepted = 0, duplicates = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.Meter is null || !byKey.TryGetValue(row.Meter.Trim(), out var meter)) { errors.Add(new IngestError(i, "This device may not send readings for that meter.")); continue; }
            if (row.At is null || row.Value is null) { errors.Add(new IngestError(i, "Each reading needs a time and a value.")); continue; }
            var result = await TryAddAsync(meter, row.At.Value, row.Value.Value, ReadingSource.Device, access.DeviceId, null, strict: false, ct);
            switch (result.Outcome)
            {
                case Outcome.Added: accepted++; break;
                case Outcome.Duplicate: duplicates++; break;
                default: errors.Add(new IngestError(i, result.Message!)); break;
            }
        }

        if (!await SaveBatchAsync(ct)) return Result<IngestResultDto>.Conflict("Another upload was saving the same readings. Send the batch again.");
        return Result<IngestResultDto>.Ok(new IngestResultDto(accepted, duplicates, errors));
    }

    /// <summary>Readings from a CSV file with the columns meter, at and value (a header row). With dryRun nothing is saved and every row is reported, so mistakes can be fixed first.</summary>
    public async Task<Result<ImportResultDto>> ImportAsync(Stream content, bool dryRun, CancellationToken ct = default)
    {
        string text;
        using (var buffer = new MemoryStream())
        {
            var chunk = new byte[81920];
            int read;
            while ((read = await content.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > MaxImportBytes) return new Result<ImportResultDto>(ResultStatus.TooLarge, Message: $"The file can be at most {MaxImportBytes / (1024 * 1024)} MB.");
                buffer.Write(chunk, 0, read);
            }

            text = new UTF8Encoding(false, throwOnInvalidBytes: false).GetString(buffer.ToArray());
        }

        IReadOnlyList<string[]> table;
        try { table = Csv.Parse(text); }
        catch (FormatException ex) { return Result<ImportResultDto>.Invalid("file", ex.Message); }
        if (table.Count == 0) return Result<ImportResultDto>.Invalid("file", "The file is empty.");
        var header = table[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(string name) => header.IndexOf(name);
        var (cMeter, cAt, cValue, cNote) = (Col("meter"), Col("at"), Col("value"), Col("note"));
        if (cMeter < 0 || cAt < 0 || cValue < 0) return Result<ImportResultDto>.Invalid("file", "The first row must name the columns: meter, at, value (and optionally note).");
        if (table.Count - 1 > MaxImportRows) return Result<ImportResultDto>.Invalid("file", $"At most {MaxImportRows} rows per file.");

        var codes = table.Skip(1).Select(r => r.ElementAtOrDefault(cMeter)?.Trim() ?? "").Where(c => c.Length > 0).Select(c => c.ToUpperInvariant()).Distinct().ToList();
        var meters = await db.Meters.AsNoTracking().Where(m => codes.Contains(m.Code) && !m.IsArchived).ToDictionaryAsync(m => m.Code, ct);

        var report = new List<ImportRowDto>();
        int accepted = 0, duplicates = 0, rejected = 0;
        foreach (var (row, index) in table.Skip(1).Select((r, i) => (r, i)))
        {
            var line = index + 2;
            string? Cell(int c) => c >= 0 && c < row.Length ? row[c].Trim() : null;
            var code = Cell(cMeter)?.ToUpperInvariant();
            if (string.IsNullOrEmpty(code) || !meters.TryGetValue(code, out var meter)) { report.Add(new ImportRowDto(line, code, "Rejected", "Unknown or archived meter.")); rejected++; continue; }
            if (!DateTimeOffset.TryParse(Cell(cAt), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) { report.Add(new ImportRowDto(line, code, "Rejected", "The time is not a date and time (use 2027-03-01T08:00:00Z).")); rejected++; continue; }
            if (!decimal.TryParse(Csv.NormalizeDigits(Cell(cValue) ?? ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)) { report.Add(new ImportRowDto(line, code, "Rejected", "The value is not a number.")); rejected++; continue; }
            var note = Cell(cNote);
            var result = await TryAddAsync(meter, at, value, ReadingSource.Import, null, note is { Length: > 200 } ? note[..200] : note, strict: true, ct);
            switch (result.Outcome)
            {
                case Outcome.Added: accepted++; report.Add(new ImportRowDto(line, code, "Accepted", null)); break;
                case Outcome.Duplicate: duplicates++; report.Add(new ImportRowDto(line, code, "Duplicate", result.Message)); break;
                default: rejected++; report.Add(new ImportRowDto(line, code, "Rejected", result.Message)); break;
            }
        }

        if (dryRun) db.ChangeTracker.Clear();
        else if (accepted > 0)
        {
            audit.Record("energy.readings_imported", nameof(EnergyReading), null, JsonSerializer.Serialize(new { accepted, duplicates, rejected }));
            if (!await SaveBatchAsync(ct)) return Result<ImportResultDto>.Conflict("Someone else added some of the same readings while this file was being read. Upload it again.");
        }

        return Result<ImportResultDto>.Ok(new ImportResultDto(dryRun, report.Count, accepted, duplicates, rejected, report));
    }

    /// <summary>
    /// Judges one reading and, when it is acceptable, adds it to the context (not yet saved). Strict mode (people and files) refuses a value below the previous reading of a counting meter
    /// or above the next one, because that is nearly always a typing mistake; devices are taken at their word.
    /// </summary>
    internal async Task<AddResult> TryAddAsync(EnergyMeter meter, DateTimeOffset at, decimal value, ReadingSource source, Guid? deviceId, string? note, bool strict, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (at > now + FutureSlack) return new AddResult(Outcome.Rejected, "The time is in the future.");
        if (at < now.AddDays(-MaxAgeDays)) return new AddResult(Outcome.Rejected, $"The time is more than {MaxAgeDays} days ago.");
        if (value < 0 || value > EnergyMath.MaxValue) return new AddResult(Outcome.Rejected, "The value is out of range.");
        value = Math.Round(value, 4);
        at = at.ToUniversalTime();
        at = new DateTimeOffset(at.Ticks - at.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);

        var pendingSame = db.ChangeTracker.Entries<EnergyReading>().FirstOrDefault(e => e.State == EntityState.Added && e.Entity.MeterId == meter.Id && e.Entity.At == at);
        if (pendingSame is not null) return pendingSame.Entity.Value == value ? new AddResult(Outcome.Duplicate, "Already in this batch.") : new AddResult(Outcome.Rejected, "Two different values for the same time in this batch.");

        var existing = await db.Readings.AsNoTracking().FirstOrDefaultAsync(r => r.MeterId == meter.Id && r.At == at, ct);
        if (existing is not null)
        {
            if (await db.Voids.AnyAsync(v => v.ReadingId == existing.Id, ct)) return new AddResult(Outcome.Rejected, "A reading at that exact time was voided. Use a time a second later.");
            return existing.Value == value ? new AddResult(Outcome.Duplicate, "Already recorded.") : new AddResult(Outcome.Rejected, $"A different value ({existing.Value}) is already recorded for that time. Void it first if it was wrong.");
        }

        if (strict && meter.IsCumulative)
        {
            var live = db.Readings.AsNoTracking().Where(r => r.MeterId == meter.Id && !db.Voids.Any(v => v.ReadingId == r.Id));
            var before = await live.Where(r => r.At < at).OrderByDescending(r => r.At).Select(r => new { r.At, r.Value }).FirstOrDefaultAsync(ct);
            if (before is not null && value < before.Value && meter.RolloverAt is null) return new AddResult(Outcome.Rejected, $"The reading is lower than the previous one ({before.Value} on {before.At:yyyy-MM-dd}). If the meter was replaced, add it as a new meter.");
            var after = await live.Where(r => r.At > at).OrderBy(r => r.At).Select(r => new { r.At, r.Value }).FirstOrDefaultAsync(ct);
            if (after is not null && value > after.Value && meter.RolloverAt is null) return new AddResult(Outcome.Rejected, $"The reading is higher than a later one ({after.Value} on {after.At:yyyy-MM-dd}).");
        }

        var reading = new EnergyReading { MeterId = meter.Id, At = at, Value = value, Source = source, DeviceId = deviceId, RecordedAt = now, RecordedBy = user.UserId, Note = note };
        db.Readings.Add(reading);
        return new AddResult(Outcome.Added, null, reading);
    }

    private async Task<bool> SaveBatchAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }

    internal static ReadingDto ToDto(EnergyReading r) => new(r.Id, r.MeterId, r.At, r.Value, r.Source, r.RecordedAt, r.Note, false, null);
}
