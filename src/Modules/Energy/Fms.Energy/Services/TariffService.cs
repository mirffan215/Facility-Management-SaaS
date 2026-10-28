using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Energy.Domain;
using Fms.Energy.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Energy.Services;

/// <summary>What a unit of each kind of energy costs, from a date. Costs in the consumption figures are worked out from these; a day with no tariff has no cost, not a zero cost.</summary>
public sealed class TariffService(EnergyDbContext db, ICurrentUser user, IAuditPort audit, TimeProvider clock)
{
    public const int MaxTariffs = 500;

    public async Task<IReadOnlyList<TariffDto>> ListAsync(CancellationToken ct = default) =>
        (await db.Tariffs.AsNoTracking().OrderBy(t => t.Kind).ThenByDescending(t => t.EffectiveFrom).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<Result<TariffDto>> CreateAsync(SaveTariffRequest r, CancellationToken ct = default)
    {
        if (await db.Tariffs.CountAsync(ct) >= MaxTariffs) return Result<TariffDto>.Conflict($"At most {MaxTariffs} tariffs.");
        var t = new Tariff();
        var invalid = Apply(t, r);
        if (invalid is not null) return invalid;
        if (await db.Tariffs.AnyAsync(x => x.Kind == t.Kind && x.EffectiveFrom == t.EffectiveFrom, ct)) return Result<TariffDto>.Conflict("There is already a price for that kind from that date. Change it instead.");
        db.Tariffs.Add(t);
        audit.Record("energy.tariff_created", nameof(Tariff), t.Id.ToString(), JsonSerializer.Serialize(new { t.Kind, t.EffectiveFrom, t.PricePerUnit, t.Currency }));
        await db.SaveChangesAsync(ct);
        return Result<TariffDto>.Ok(ToDto(t));
    }

    public async Task<Result<TariffDto>> UpdateAsync(Guid id, SaveTariffRequest r, CancellationToken ct = default)
    {
        var t = await db.Tariffs.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return Result<TariffDto>.NotFound();
        if (!Common.TryVersion(r.RowVersion, out var version)) return Result<TariffDto>.Invalid("rowVersion", "Reload and try again.");
        db.Entry(t).Property(x => x.RowVersion).OriginalValue = version;
        var before = new { t.PricePerUnit, t.Currency };
        var invalid = Apply(t, r);
        if (invalid is not null) return invalid;
        if (await db.Tariffs.AnyAsync(x => x.Kind == t.Kind && x.EffectiveFrom == t.EffectiveFrom && x.Id != id, ct)) { db.ChangeTracker.Clear(); return Result<TariffDto>.Conflict("There is already a price for that kind from that date."); }
        audit.Record("energy.tariff_updated", nameof(Tariff), id.ToString(), JsonSerializer.Serialize(new { before, after = new { t.PricePerUnit, t.Currency } }));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<TariffDto>.Conflict("Someone else changed this. Reload and try again.");
        }

        return Result<TariffDto>.Ok(ToDto(t));
    }

    private Result<TariffDto>? Apply(Tariff t, SaveTariffRequest r)
    {
        var errors = new ValidationErrors();
        if (r.Kind is not { } kind || !Enum.IsDefined(kind)) errors.Add("kind", "Choose a kind.");
        var today = Common.Today(clock);
        if (r.EffectiveFrom is not { } from) errors.Add("effectiveFrom", "Say from when it applies.");
        else if (from < today.AddYears(-10) || from > today.AddYears(2)) errors.Add("effectiveFrom", "That date is too far away.");
        if (r.PricePerUnit is not { } price || price < 0 || price > 1_000_000) errors.Add("pricePerUnit", "Enter a price of zero or more.");
        var currency = Common.Clean(r.Currency)?.ToUpperInvariant();
        if (currency is null || currency.Length != 3 || !currency.All(char.IsAsciiLetter)) errors.Add("currency", "Use a three-letter currency code, such as SAR.");
        if (errors.Any) return errors.ToResult<TariffDto>();
        t.Kind = r.Kind!.Value;
        t.EffectiveFrom = r.EffectiveFrom!.Value;
        t.PricePerUnit = Math.Round(r.PricePerUnit!.Value, 6);
        t.Currency = currency!;
        t.UpdatedAt = clock.GetUtcNow();
        t.UpdatedBy = user.UserId;
        return null;
    }

    private static TariffDto ToDto(Tariff t) => new(t.Id, t.Kind, t.EffectiveFrom, t.PricePerUnit, t.Currency, Common.Version(t.RowVersion));
}
