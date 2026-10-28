using System.Text.Json;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Compliance.Domain;
using Fms.Compliance.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Compliance.Services;

/// <summary>The kinds of permit an organisation issues and what each one demands.</summary>
public sealed class PermitTypeService(ComplianceDbContext db, IAuditPort audit)
{
    public const int MaxTypes = 100;
    public const int MaxPrecautions = 30;

    public async Task<IReadOnlyList<PermitTypeDto>> ListAsync(bool includeArchived, CancellationToken ct = default) =>
        (await db.PermitTypes.AsNoTracking().Where(t => includeArchived || !t.IsArchived).OrderBy(t => t.Code).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<Result<PermitTypeDto>> CreateAsync(SavePermitTypeRequest r, CancellationToken ct = default)
    {
        if (await db.PermitTypes.CountAsync(ct) >= MaxTypes) return Result<PermitTypeDto>.Conflict($"At most {MaxTypes} permit types.");
        var t = new PermitType();
        var invalid = Apply(t, r);
        if (invalid is not null) return invalid;
        if (await db.PermitTypes.AnyAsync(x => x.Code == t.Code, ct)) return Result<PermitTypeDto>.Conflict($"A permit type with code {t.Code} already exists.");
        db.PermitTypes.Add(t);
        audit.Record("permit_type.created", nameof(PermitType), t.Id.ToString(), JsonSerializer.Serialize(new { t.Code, t.Name }));
        await db.SaveChangesAsync(ct);
        return Result<PermitTypeDto>.Ok(ToDto(t));
    }

    public async Task<Result<PermitTypeDto>> UpdateAsync(Guid id, SavePermitTypeRequest r, CancellationToken ct = default)
    {
        var t = await db.PermitTypes.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return Result<PermitTypeDto>.NotFound();
        if (!Common.TryVersion(r.RowVersion, out var version)) return Result<PermitTypeDto>.Invalid("rowVersion", "Reload and try again.");
        db.Entry(t).Property(x => x.RowVersion).OriginalValue = version;
        var oldCode = t.Code;
        var invalid = Apply(t, r);
        if (invalid is not null) return invalid;
        if (t.Code != oldCode && await db.PermitTypes.AnyAsync(x => x.Code == t.Code && x.Id != id, ct)) return Result<PermitTypeDto>.Conflict($"A permit type with code {t.Code} already exists.");
        audit.Record("permit_type.updated", nameof(PermitType), id.ToString(), JsonSerializer.Serialize(new { t.Code, t.Name }));
        return await SaveAsync(t, ct);
    }

    public async Task<Result<PermitTypeDto>> ArchiveAsync(Guid id, bool archive, ArchiveObligationRequest r, CancellationToken ct = default)
    {
        var t = await db.PermitTypes.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return Result<PermitTypeDto>.NotFound();
        if (!Common.TryVersion(r.RowVersion, out var version)) return Result<PermitTypeDto>.Invalid("rowVersion", "Reload and try again.");
        db.Entry(t).Property(x => x.RowVersion).OriginalValue = version;
        t.IsArchived = archive;
        audit.Record(archive ? "permit_type.archived" : "permit_type.restored", nameof(PermitType), id.ToString());
        return await SaveAsync(t, ct);
    }

    private static Result<PermitTypeDto>? Apply(PermitType t, SavePermitTypeRequest r)
    {
        var errors = new ValidationErrors();
        var code = Common.Clean(r.Code)?.ToUpperInvariant();
        if (code is null || code.Length > 30 || code.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) errors.Add("code", "A short code of letters, digits, - or _ (at most 30).");
        var name = Common.Clean(r.Name);
        if (name is null || name.Length > 200) errors.Add("name", "Give it a name (at most 200 characters).");
        var nameAr = Common.Clean(r.NameAr);
        if (nameAr is { Length: > 200 }) errors.Add("nameAr", "At most 200 characters.");
        var hours = r.MaxDurationHours ?? 8;
        if (hours < 1 || hours > 720) errors.Add("maxDurationHours", "Between 1 and 720 hours.");
        var precautions = (r.Precautions ?? []).Select(p => Common.Clean(p)).Where(p => p is not null).Select(p => p!).ToList();
        if (precautions.Count > MaxPrecautions) errors.Add("precautions", $"At most {MaxPrecautions} precautions.");
        if (precautions.Any(p => p.Length > 200)) errors.Add("precautions", "Each precaution is at most 200 characters.");
        if (precautions.Distinct(StringComparer.OrdinalIgnoreCase).Count() != precautions.Count) errors.Add("precautions", "Each precaution once.");
        if (errors.Any) return errors.ToResult<PermitTypeDto>();

        t.Code = code!;
        t.Name = name!;
        t.NameAr = nameAr;
        t.MaxDurationHours = hours;
        t.Exclusive = r.Exclusive ?? false;
        t.RequiresIsolation = r.RequiresIsolation ?? false;
        t.PrecautionsJson = JsonSerializer.Serialize(precautions);
        return null;
    }

    private async Task<Result<PermitTypeDto>> SaveAsync(PermitType t, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<PermitTypeDto>.Conflict("Someone else changed this. Reload and try again.");
        }

        return Result<PermitTypeDto>.Ok(ToDto(t));
    }

    public static PermitTypeDto ToDto(PermitType t) =>
        new(t.Id, t.Code, t.Name, t.NameAr, t.MaxDurationHours, t.Exclusive, t.RequiresIsolation, JsonSerializer.Deserialize<List<string>>(t.PrecautionsJson) ?? [], t.IsArchived, Common.Version(t.RowVersion));
}
