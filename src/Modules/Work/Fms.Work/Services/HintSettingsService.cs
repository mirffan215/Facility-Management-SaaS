using System.Text.Json;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Security;
using Fms.BuildingBlocks.Results;
using Fms.Work.Domain;
using Fms.Work.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Work.Services;

public sealed record HintSettingsDto(int HistoryDays, int MinFailures, int ShrinkPercent, int PaceRisePercent, string? RowVersion);

public sealed record SaveHintSettingsRequest(int? HistoryDays, int? MinFailures, int? ShrinkPercent, int? PaceRisePercent, string? RowVersion);

/// <summary>How eagerly suggestions and early warnings speak for one organisation. Reading is for the people who see the hints; changing needs workorders.manage.</summary>
public sealed class HintSettingsService(WorkDbContext db, IAuditPort audit)
{
    public async Task<HintThresholds> LoadAsync(CancellationToken ct = default)
    {
        var s = await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct);
        return s is null ? HintThresholds.Default : new HintThresholds(s.HintHistoryDays, s.HintMinFailures, s.HintShrinkPercent, s.HintPaceRisePercent);
    }

    public async Task<HintSettingsDto> GetAsync(CancellationToken ct = default)
    {
        var s = await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct);
        var t = await LoadAsync(ct);
        return new HintSettingsDto(t.HistoryDays, t.MinFailures, t.ShrinkPercent, t.PaceRisePercent, s is null ? null : Convert.ToBase64String(s.RowVersion));
    }

    public async Task<Result<HintSettingsDto>> SaveAsync(SaveHintSettingsRequest r, CancellationToken ct = default)
    {
        var errors = new ValidationErrors();
        void Check(string field, int? value, (int Min, int Max) range, string what)
        {
            if (value is null) errors.Add(field, $"{what} is required.");
            else if (value < range.Min || value > range.Max) errors.Add(field, $"{what} must be between {range.Min} and {range.Max}.");
        }

        Check("historyDays", r.HistoryDays, HintThresholds.HistoryRange, "The history length (days)");
        Check("minFailures", r.MinFailures, HintThresholds.FailuresRange, "The number of repairs");
        Check("shrinkPercent", r.ShrinkPercent, HintThresholds.ShrinkRange, "The gap percentage");
        Check("paceRisePercent", r.PaceRisePercent, HintThresholds.PaceRange, "The pace percentage");
        if (errors.Any) return errors.ToResult<HintSettingsDto>();

        var s = await db.Settings.FirstOrDefaultAsync(ct);
        if (s is null)
        {
            s = new WorkSettings();
            db.Settings.Add(s);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(r.RowVersion)) return Result<HintSettingsDto>.Invalid("rowVersion", "rowVersion is required.");
            try { db.Entry(s).Property(x => x.RowVersion).OriginalValue = Convert.FromBase64String(r.RowVersion); }
            catch (FormatException) { return Result<HintSettingsDto>.Invalid("rowVersion", "rowVersion is not valid."); }
        }

        var before = new { s.HintHistoryDays, s.HintMinFailures, s.HintShrinkPercent, s.HintPaceRisePercent };
        (s.HintHistoryDays, s.HintMinFailures, s.HintShrinkPercent, s.HintPaceRisePercent) = (r.HistoryDays!.Value, r.MinFailures!.Value, r.ShrinkPercent!.Value, r.PaceRisePercent!.Value);
        audit.Record("hintsettings.changed", nameof(WorkSettings), s.Id.ToString(), JsonSerializer.Serialize(new { from = before, to = new { s.HintHistoryDays, s.HintMinFailures, s.HintShrinkPercent, s.HintPaceRisePercent } }));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException || (ex is DbUpdateException u && SqlErrors.IsUniqueViolation(u)))
        {
            db.ChangeTracker.Clear();
            return Result<HintSettingsDto>.Conflict("The settings were changed by someone else. Reload and try again.");
        }

        return Result<HintSettingsDto>.Ok(await GetAsync(ct));
    }
}
