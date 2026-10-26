using System.Text.Json;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Purchasing.Domain;
using Fms.Purchasing.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Purchasing.Services;

/// <summary>The approval limits. Until a tenant saves its own, the defaults apply.</summary>
public sealed class SettingsService(PurchasingDbContext db, IAuditPort audit)
{
    public async Task<PurchasingSettings> CurrentAsync(CancellationToken ct = default) =>
        await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new PurchasingSettings();

    public async Task<SettingsDto> GetAsync(CancellationToken ct = default)
    {
        var s = await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct);
        return s is null ? new SettingsDto(PurchasingRules.DefaultLevel1Limit, PurchasingRules.DefaultLevel2Limit, PurchasingRules.DefaultTolerancePercent, "SAR", null) : ToDto(s);
    }

    public async Task<Result<SettingsDto>> SaveAsync(SaveSettingsRequest r, CancellationToken ct = default)
    {
        var errors = new ValidationErrors();
        if (r.Level1Limit is not { } l1 || l1 <= 0 || l1 > PurchasingRules.MaxAmount || decimal.Round(l1, 2) != l1) errors.Add("level1Limit", "The first limit must be more than zero (2 decimals).");
        if (r.Level2Limit is not { } l2 || l2 > PurchasingRules.MaxAmount || decimal.Round(l2, 2) != l2) errors.Add("level2Limit", "The second limit must be a valid amount (2 decimals).");
        else if (r.Level1Limit is { } a && l2 < a) errors.Add("level2Limit", "The second limit cannot be below the first.");
        if (r.OverrunTolerancePercent is not { } t || t < 0 || t > 100 || decimal.Round(t, 2) != t) errors.Add("overrunTolerancePercent", "The tolerance is between 0 and 100 percent.");
        var currency = Common.Clean(r.CurrencyCode)?.ToUpperInvariant() ?? "SAR";
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetter)) errors.Add("currencyCode", "Use a three-letter currency code, such as SAR.");
        var existing = await db.Settings.FirstOrDefaultAsync(ct);
        if (existing is not null && !Common.TryVersion(r.RowVersion, out _)) errors.Add("rowVersion", "rowVersion is required.");
        if (errors.Any) return errors.ToResult<SettingsDto>();

        var s = existing ?? new PurchasingSettings();
        s.Level1Limit = r.Level1Limit!.Value;
        s.Level2Limit = r.Level2Limit!.Value;
        s.OverrunTolerancePercent = r.OverrunTolerancePercent!.Value;
        s.CurrencyCode = currency;
        if (existing is null) db.Settings.Add(s);
        else
        {
            Common.TryVersion(r.RowVersion, out var version);
            db.Entry(s).Property(x => x.RowVersion).OriginalValue = version;
            db.Entry(s).Property(x => x.CurrencyCode).IsModified = true;
        }

        audit.Record("purchasing.settings_saved", nameof(PurchasingSettings), s.Id.ToString(), JsonSerializer.Serialize(new { s.Level1Limit, s.Level2Limit, s.OverrunTolerancePercent, s.CurrencyCode }));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<SettingsDto>.Conflict("The limits were changed by someone else. Reload and try again.");
        }
        catch (DbUpdateException ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return Result<SettingsDto>.Conflict("The limits were saved by someone else a moment ago. Reload and try again.");
        }

        return Result<SettingsDto>.Ok(ToDto(s));
    }

    private static SettingsDto ToDto(PurchasingSettings s) => new(s.Level1Limit, s.Level2Limit, s.OverrunTolerancePercent, s.CurrencyCode, Common.Version(s.RowVersion));
}
