using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Vendors.Domain;
using Fms.Vendors.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Vendors.Services;

/// <summary>Contracts with vendors: dates, value, what they cover and what they promise. Reading needs vendors.read; changing needs vendors.manage.</summary>
public sealed class ContractService(VendorsDbContext db, IAuditPort audit, IFacilityLookup facilities, TimeProvider clock)
{
    public async Task<Page<ContractDto>> ListAsync(Guid? vendorId, ContractStatus? status, string? q, int? page, int? pageSize, CancellationToken ct = default)
    {
        var (number, size) = Page<ContractDto>.Normalize(page, pageSize);
        var today = Common.Today(clock);
        var query = db.Contracts.AsNoTracking().AsQueryable();
        if (vendorId is { } v) query = query.Where(c => c.VendorId == v);
        if (Common.Clean(q) is { } text) query = query.Where(c => c.Number.Contains(text) || c.Title.Contains(text));
        if (status is { } s)
        {
            query = s switch
            {
                ContractStatus.Terminated => query.Where(c => c.TerminatedAt != null),
                ContractStatus.Upcoming => query.Where(c => c.TerminatedAt == null && c.StartOn > today),
                ContractStatus.Expired => query.Where(c => c.TerminatedAt == null && c.EndOn < today),
                ContractStatus.Expiring => query.Where(c => c.TerminatedAt == null && c.StartOn <= today && c.EndOn >= today && c.EndOn.DayNumber - today.DayNumber <= c.RenewalNoticeDays),
                _ => query.Where(c => c.TerminatedAt == null && c.StartOn <= today && c.EndOn >= today && c.EndOn.DayNumber - today.DayNumber > c.RenewalNoticeDays),
            };
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(c => c.EndOn).ThenBy(c => c.Number).Skip((number - 1) * size).Take(size).ToListAsync(ct);
        return new Page<ContractDto>(await ToDtosAsync(rows, ct), total, number, size);
    }

    public async Task<Result<ContractDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var c = await db.Contracts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return c is null ? Result<ContractDto>.NotFound() : Result<ContractDto>.Ok((await ToDtosAsync([c], ct)).Single());
    }

    public async Task<Result<ContractDto>> SaveAsync(Guid? id, SaveContractRequest r, CancellationToken ct = default)
    {
        Contract? existing = null;
        if (id is { } cid)
        {
            existing = await db.Contracts.FirstOrDefaultAsync(c => c.Id == cid, ct);
            if (existing is null) return Result<ContractDto>.NotFound();
        }

        var errors = new ValidationErrors();
        if (!Common.IsCode(r.Number)) errors.Add("number", "Number is required: 1-50 letters, digits, dot, dash, slash or underscore.");
        if (string.IsNullOrWhiteSpace(r.Title) || r.Title.Trim().Length > 200) errors.Add("title", "Title is required (max 200 characters).");
        if (r.Type is not { } type || !Enum.IsDefined(type)) errors.Add("type", "Choose a contract type.");
        if (r.StartOn is null) errors.Add("startOn", "Start date is required.");
        if (r.EndOn is null) errors.Add("endOn", "End date is required.");
        if (r.StartOn is { } s && r.EndOn is { } e && e < s) errors.Add("endOn", "The end date cannot be before the start date.");
        if (r.Value is { } value && (value < 0 || value > 1_000_000_000_000m || decimal.Round(value, 2) != value)) errors.Add("value", "Value must be zero or more, with at most 2 decimals.");
        var currency = Common.Clean(r.CurrencyCode)?.ToUpperInvariant() ?? "SAR";
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetter)) errors.Add("currencyCode", "Use a three-letter currency code, such as SAR.");
        var notice = r.RenewalNoticeDays ?? 60;
        if (notice is < 0 or > 365) errors.Add("renewalNoticeDays", "The renewal reminder is between 0 and 365 days before the end.");
        if (r.Notes is { Length: > 2000 }) errors.Add("notes", "Notes are at most 2000 characters.");
        if (existing is not null && !Common.TryVersion(r.RowVersion, out _)) errors.Add("rowVersion", "rowVersion is required.");
        if (existing is not null && existing.TerminatedAt is not null) return Result<ContractDto>.Conflict("A terminated contract cannot be changed.");
        Vendor? vendor = null;
        var vendorId = existing?.VendorId ?? r.VendorId;
        if (vendorId is not { } vid || (vendor = await db.Vendors.AsNoTracking().FirstOrDefaultAsync(v => v.Id == vid, ct)) is null) errors.Add("vendorId", "Choose a vendor.");
        else if (existing is not null && r.VendorId is { } changed && changed != existing.VendorId) errors.Add("vendorId", "A contract cannot be moved to another vendor. Terminate it and make a new one.");
        else if (existing is null && vendor!.IsArchived) errors.Add("vendorId", "This vendor is archived.");
        if (errors.Any) return errors.ToResult<ContractDto>();

        var number = r.Number!.Trim().ToUpperInvariant();
        var self = existing?.Id ?? Guid.Empty;
        if (await db.Contracts.AnyAsync(c => c.Number == number && c.Id != self, ct)) return Result<ContractDto>.Conflict("A contract with this number already exists.");

        var contract = existing ?? new Contract { VendorId = vendorId!.Value, CreatedAt = clock.GetUtcNow() };
        var endChanged = existing is not null && existing.EndOn != r.EndOn!.Value;
        contract.Number = number;
        contract.Title = r.Title!.Trim();
        contract.Type = r.Type!.Value;
        contract.StartOn = r.StartOn!.Value;
        contract.EndOn = r.EndOn!.Value;
        contract.Value = r.Value;
        contract.CurrencyCode = currency;
        contract.RenewalNoticeDays = notice;
        contract.Notes = Common.Clean(r.Notes);
        if (endChanged) { contract.ExpiringNoticedAt = null; contract.ExpiredNoticedAt = null; } // an extended contract is reminded again
        if (existing is null) db.Contracts.Add(contract);
        else
        {
            Common.TryVersion(r.RowVersion, out var version);
            db.Entry(contract).Property(x => x.RowVersion).OriginalValue = version;
            db.Entry(contract).Property(x => x.Title).IsModified = true;
        }

        audit.Record(existing is null ? "contract.created" : "contract.updated", nameof(Contract), contract.Id.ToString(),
            JsonSerializer.Serialize(new { contract.Number, contract.VendorId, contract.StartOn, contract.EndOn, contract.Value, contract.CurrencyCode }));
        return await CommitAsync(contract, ct);
    }

    public async Task<Result<ContractDto>> TerminateAsync(Guid id, TerminateContractRequest r, CancellationToken ct = default)
    {
        var contract = await db.Contracts.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (contract is null) return Result<ContractDto>.NotFound();
        if (contract.TerminatedAt is not null) return Result<ContractDto>.Conflict("This contract is already terminated.");
        var reason = Common.Clean(r.Reason);
        if (reason is null || reason.Length > 500) return Result<ContractDto>.Invalid("reason", "A reason is required (max 500 characters).");
        if (!Common.TryVersion(r.RowVersion, out var version)) return Result<ContractDto>.Invalid("rowVersion", "rowVersion is required.");
        contract.TerminatedAt = clock.GetUtcNow();
        contract.TerminationReason = reason;
        db.Entry(contract).Property(x => x.RowVersion).OriginalValue = version;
        audit.Record("contract.terminated", nameof(Contract), id.ToString(), JsonSerializer.Serialize(new { reason }));
        return await CommitAsync(contract, ct);
    }

    public async Task<Result<ContractDto>> SaveCoverageAsync(Guid id, SaveCoverageRequest r, CancellationToken ct = default)
    {
        var contract = await db.Contracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (contract is null) return Result<ContractDto>.NotFound();
        if (contract.TerminatedAt is not null) return Result<ContractDto>.Conflict("A terminated contract cannot be changed.");
        var input = r.Coverage ?? [];
        var errors = new ValidationErrors();
        if (input.Count > 200) errors.Add("coverage", "A contract covers at most 200 things.");
        if (input.Any(c => c.Kind is not { } k || !Enum.IsDefined(k) || c.TargetId is null)) errors.Add("coverage", "Every line needs a kind and a target.");
        if (input.GroupBy(c => (c.Kind, c.TargetId)).Any(g => g.Count() > 1)) errors.Add("coverage", "The same thing is listed twice.");
        if (errors.Any) return errors.ToResult<ContractDto>();

        foreach (var c in input)
        {
            var found = c.Kind!.Value switch
            {
                CoverageKind.Site => await facilities.GetLocationAsync(c.TargetId!.Value, ct) is { Kind: "Site" },
                CoverageKind.AssetClass => (await facilities.AssetClassNamesAsync([c.TargetId!.Value], ct)).ContainsKey(c.TargetId!.Value),
                _ => await facilities.GetAssetAsync(c.TargetId!.Value, ct) is not null,
            };
            if (!found) return Result<ContractDto>.Invalid("coverage", $"A covered {c.Kind.ToString()!.ToLowerInvariant()} was not found.");
        }

        db.Coverage.RemoveRange(await db.Coverage.Where(x => x.ContractId == id).ToListAsync(ct));
        foreach (var c in input) db.Coverage.Add(new ContractCoverage { ContractId = id, Kind = c.Kind!.Value, TargetId = c.TargetId!.Value });
        audit.Record("contract.coverage_saved", nameof(Contract), id.ToString(), JsonSerializer.Serialize(input.Select(c => new { c.Kind, c.TargetId })));
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<Result<ContractDto>> SaveSlaAsync(Guid id, SaveSlaRequest r, CancellationToken ct = default)
    {
        var contract = await db.Contracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (contract is null) return Result<ContractDto>.NotFound();
        if (contract.TerminatedAt is not null) return Result<ContractDto>.Conflict("A terminated contract cannot be changed.");
        var input = r.Targets ?? [];
        var errors = new ValidationErrors();
        if (input.Any(t => t.Priority is not (>= 1 and <= 4))) errors.Add("targets", "Priority is 1 (low) to 4 (urgent).");
        if (input.GroupBy(t => t.Priority).Any(g => g.Count() > 1)) errors.Add("targets", "Each priority can be set once.");
        if (input.Any(t => t.ResponseMinutes is not (> 0 and <= 525_600) || t.ResolutionMinutes is not (> 0 and <= 525_600))) errors.Add("targets", "Times are in minutes, from 1 up to a year.");
        if (input.Any(t => t.ResolutionMinutes < t.ResponseMinutes)) errors.Add("targets", "Resolution cannot be sooner than response.");
        if (errors.Any) return errors.ToResult<ContractDto>();

        db.SlaTargets.RemoveRange(await db.SlaTargets.Where(x => x.ContractId == id).ToListAsync(ct));
        foreach (var t in input) db.SlaTargets.Add(new ContractSla { ContractId = id, Priority = t.Priority!.Value, ResponseMinutes = t.ResponseMinutes!.Value, ResolutionMinutes = t.ResolutionMinutes!.Value });
        audit.Record("contract.sla_saved", nameof(Contract), id.ToString(), JsonSerializer.Serialize(input));
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    private async Task<Result<ContractDto>> CommitAsync(Contract contract, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<ContractDto>.Conflict("This contract was changed by someone else. Reload and try again.");
        }
        catch (DbUpdateException ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return Result<ContractDto>.Conflict("A contract with this number already exists.");
        }

        return await GetAsync(contract.Id, ct);
    }

    private async Task<IReadOnlyList<ContractDto>> ToDtosAsync(List<Contract> contracts, CancellationToken ct)
    {
        var today = Common.Today(clock);
        var ids = contracts.Select(c => c.Id).ToList();
        var vendorIds = contracts.Select(c => c.VendorId).Distinct().ToList();
        var vendors = await db.Vendors.AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Name, ct);
        var coverage = await db.Coverage.AsNoTracking().Where(x => ids.Contains(x.ContractId)).ToListAsync(ct);
        var sla = await db.SlaTargets.AsNoTracking().Where(x => ids.Contains(x.ContractId)).OrderBy(x => x.Priority).ToListAsync(ct);
        var docs = await db.Documents.AsNoTracking().Where(d => ids.Contains(d.ContractId) && d.DeletedAt == null).GroupBy(d => d.ContractId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);

        var names = new Dictionary<(CoverageKind, Guid), string?>();
        foreach (var c in coverage.DistinctBy(x => (x.Kind, x.TargetId)))
        {
            names[(c.Kind, c.TargetId)] = c.Kind switch
            {
                CoverageKind.Site => (await facilities.GetLocationAsync(c.TargetId, ct))?.Name,
                CoverageKind.AssetClass => (await facilities.AssetClassNamesAsync([c.TargetId], ct)).GetValueOrDefault(c.TargetId),
                _ => await facilities.GetAssetAsync(c.TargetId, ct) is { } a ? $"{a.Code} {a.Name}" : null,
            };
        }

        return contracts.Select(c => new ContractDto(
            c.Id, c.VendorId, vendors.GetValueOrDefault(c.VendorId), c.Number, c.Title, c.Type, c.StartOn, c.EndOn, c.Value, c.CurrencyCode, c.RenewalNoticeDays, c.Notes,
            ContractRules.StatusOn(c, today), ContractRules.DaysLeft(c, today), c.TerminatedAt, c.TerminationReason,
            coverage.Where(x => x.ContractId == c.Id).Select(x => new CoverageDto(x.Kind, x.TargetId, names.GetValueOrDefault((x.Kind, x.TargetId)))).ToList(),
            sla.Where(x => x.ContractId == c.Id).Select(x => new ContractSlaDto(x.Priority, x.ResponseMinutes, x.ResolutionMinutes)).ToList(),
            docs.GetValueOrDefault(c.Id), Common.Version(c.RowVersion))).ToList();
    }
}
