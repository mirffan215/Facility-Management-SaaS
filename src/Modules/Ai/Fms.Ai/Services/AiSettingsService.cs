using System.Text.Json;
using Fms.Ai.Domain;
using Fms.Ai.Persistence;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;

namespace Fms.Ai.Services;

/// <summary>Whether AI is on for the organisation, which suggestions it offers, the daily limit, and what has been used.</summary>
public sealed class AiSettingsService(AiDbContext db, IAiProvider provider, ICurrentUser user, IAuditPort audit, TimeProvider clock)
{
    public async Task<AiSettingsDto> GetAsync(CancellationToken ct = default)
    {
        var s = await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct);
        return s is null ? new AiSettingsDto(provider.IsConfigured, false, true, true, AiRules.DefaultDailyLimit, null) : ToDto(s);
    }

    public async Task<AiStatusDto> StatusAsync(CancellationToken ct = default)
    {
        var s = await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct);
        var used = await UsedTodayAsync(ct);
        var on = provider.IsConfigured && s is { Enabled: true };
        return new AiStatusDto(provider.IsConfigured, on, on && s!.TriageEnabled, on && s!.SummaryEnabled, s?.DailyLimit ?? AiRules.DefaultDailyLimit, used);
    }

    public async Task<Result<AiSettingsDto>> SaveAsync(SaveAiSettingsRequest r, CancellationToken ct = default)
    {
        var errors = new ValidationErrors();
        if (r.Enabled is null) errors.Add("enabled", "Say whether AI is on.");
        if (r.DailyLimit is < 0 or > AiRules.MaxDailyLimit) errors.Add("dailyLimit", $"The daily limit is from 0 to {AiRules.MaxDailyLimit}.");
        if (r.Enabled == true && !provider.IsConfigured) return Result<AiSettingsDto>.Conflict("AI is not set up on this installation, so it cannot be turned on. Ask whoever runs the installation to configure a provider.");
        if (errors.Any) return errors.ToResult<AiSettingsDto>();

        var s = await db.Settings.FirstOrDefaultAsync(ct);
        var isNew = s is null;
        if (s is null) db.Settings.Add(s = new AiSettings());
        else
        {
            if (!Common.TryVersion(r.RowVersion, out var version)) return Result<AiSettingsDto>.Invalid("rowVersion", "Reload and try again.");
            db.Entry(s).Property(x => x.RowVersion).OriginalValue = version;
        }

        var before = new { s.Enabled, s.TriageEnabled, s.SummaryEnabled, s.DailyLimit };
        s.Enabled = r.Enabled!.Value;
        s.TriageEnabled = r.TriageEnabled ?? s.TriageEnabled;
        s.SummaryEnabled = r.SummaryEnabled ?? s.SummaryEnabled;
        s.DailyLimit = r.DailyLimit ?? s.DailyLimit;
        s.UpdatedAt = clock.GetUtcNow();
        s.UpdatedBy = user.UserId;
        audit.Record("ai.settings_changed", nameof(AiSettings), s.Id.ToString(), JsonSerializer.Serialize(new { before = isNew ? null : before, after = new { s.Enabled, s.TriageEnabled, s.SummaryEnabled, s.DailyLimit } }));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<AiSettingsDto>.Conflict("Someone else changed this. Reload and try again.");
        }
        catch (DbUpdateException) when (isNew)
        {
            db.ChangeTracker.Clear();
            return Result<AiSettingsDto>.Conflict("Someone else changed this. Reload and try again.");
        }

        return Result<AiSettingsDto>.Ok(ToDto(s));
    }

    public async Task<UsageDto> UsageAsync(CancellationToken ct = default)
    {
        var since = clock.GetUtcNow().AddDays(-30);
        var rows = await db.Usage.AsNoTracking().Where(u => u.At >= since).GroupBy(u => new { u.Feature, u.Outcome })
            .Select(g => new { g.Key.Feature, g.Key.Outcome, N = g.Count(), In = g.Sum(x => (long)x.InputTokens), Out = g.Sum(x => (long)x.OutputTokens) }).ToListAsync(ct);
        var lines = rows.GroupBy(r => r.Feature).OrderBy(g => g.Key)
            .Select(g => new UsageLineDto(g.Key, g.Where(x => x.Outcome == AiOutcome.Ok).Sum(x => x.N), g.Where(x => x.Outcome == AiOutcome.Failed).Sum(x => x.N), g.Sum(x => x.In), g.Sum(x => x.Out))).ToList();
        var answers = await db.Feedback.AsNoTracking().Where(f => f.At >= since).GroupBy(f => new { f.Feature, f.Accepted }).Select(g => new { g.Key.Feature, g.Key.Accepted, N = g.Count() }).ToListAsync(ct);
        lines = lines.Select(l => l with { Accepted = answers.Where(a => a.Feature == l.Feature && a.Accepted).Sum(a => a.N), Dismissed = answers.Where(a => a.Feature == l.Feature && !a.Accepted).Sum(a => a.N) }).ToList();
        var limit = (await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct))?.DailyLimit ?? AiRules.DefaultDailyLimit;
        return new UsageDto(await UsedTodayAsync(ct), limit, lines);
    }

    private Task<int> UsedTodayAsync(CancellationToken ct)
    {
        var start = new DateTimeOffset(clock.GetUtcNow().UtcDateTime.Date, TimeSpan.Zero);
        return db.Usage.CountAsync(u => u.At >= start, ct);
    }

    private AiSettingsDto ToDto(AiSettings s) => new(provider.IsConfigured, s.Enabled, s.TriageEnabled, s.SummaryEnabled, s.DailyLimit, Common.Version(s.RowVersion));
}
