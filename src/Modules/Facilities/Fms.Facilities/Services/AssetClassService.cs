using System.Text.Json;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Facilities.Domain;
using Fms.Facilities.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Facilities.Services;

public sealed record AssetClassDto(Guid Id, Guid? ParentId, string Code, string Name, string? NameAr, bool IsArchived, string RowVersion)
{
    public static AssetClassDto From(AssetClass c) => new(c.Id, c.ParentId, c.Code, c.Name, c.NameAr, c.IsArchived, Convert.ToBase64String(c.RowVersion));
}

public sealed record SaveAssetClassRequest(Guid? ParentId, string? Code, string? Name, string? NameAr, string? RowVersion);

public sealed class AssetClassService(FacilitiesDbContext db, IAuditPort audit)
{
    private const int MaxDepth = 5;

    public async Task<IReadOnlyList<AssetClassDto>> ListAsync(bool includeArchived, CancellationToken ct = default)
    {
        var query = db.AssetClasses.AsNoTracking().AsQueryable();
        if (!includeArchived) query = query.Where(c => !c.IsArchived);
        return (await query.OrderBy(c => c.Code).ToListAsync(ct)).Select(AssetClassDto.From).ToList();
    }

    public async Task<Result<AssetClassDto>> CreateAsync(SaveAssetClassRequest r, CancellationToken ct = default)
    {
        var errors = Validate(r);
        AssetClass? parent = null;
        if (r.ParentId is { } parentId)
        {
            parent = await db.AssetClasses.AsNoTracking().FirstOrDefaultAsync(c => c.Id == parentId, ct);
            if (parent is null || parent.IsArchived) errors.Add("parentId", "Parent class not found.");
            else if (await DepthAsync(parent, ct) >= MaxDepth) errors.Add("parentId", $"Classes can be nested at most {MaxDepth} levels deep.");
        }

        if (errors.Any) return errors.ToResult<AssetClassDto>();

        var cls = new AssetClass { ParentId = parent?.Id, Code = r.Code!.Trim(), Name = r.Name!.Trim(), NameAr = Clean(r.NameAr) };
        db.AssetClasses.Add(cls);
        audit.Record("assetclass.created", nameof(AssetClass), cls.Id.ToString(), JsonSerializer.Serialize(new { cls.Code, cls.Name, cls.ParentId }));
        return await SaveAsync(cls, ct);
    }

    /// <summary>Renames or recodes a class. Its parent is fixed once created so asset reporting stays stable.</summary>
    public async Task<Result<AssetClassDto>> UpdateAsync(Guid id, SaveAssetClassRequest r, CancellationToken ct = default)
    {
        var cls = await db.AssetClasses.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (cls is null) return Result<AssetClassDto>.NotFound();
        var errors = Validate(r);
        if (!TryVersion(r.RowVersion, out var version)) errors.Add("rowVersion", "rowVersion is required.");
        if (errors.Any) return errors.ToResult<AssetClassDto>();

        cls.Code = r.Code!.Trim();
        cls.Name = r.Name!.Trim();
        cls.NameAr = Clean(r.NameAr);
        db.Entry(cls).Property(c => c.RowVersion).OriginalValue = version;
        audit.Record("assetclass.updated", nameof(AssetClass), id.ToString(), JsonSerializer.Serialize(new { cls.Code, cls.Name }));
        return await SaveAsync(cls, ct);
    }

    public async Task<Result<AssetClassDto>> SetArchivedAsync(Guid id, bool archived, string? rowVersion, CancellationToken ct = default)
    {
        var cls = await db.AssetClasses.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (cls is null) return Result<AssetClassDto>.NotFound();
        if (!TryVersion(rowVersion, out var version)) return Result<AssetClassDto>.Invalid("rowVersion", "rowVersion is required.");
        if (cls.IsArchived == archived) return Result<AssetClassDto>.Ok(AssetClassDto.From(cls));

        if (archived)
        {
            if (await db.Assets.AnyAsync(a => a.AssetClassId == id && a.Status != AssetStatus.Retired, ct))
                return Result<AssetClassDto>.Conflict("Assets still use this class. Reclassify or retire them first.");
            if (await db.AssetClasses.AnyAsync(c => c.ParentId == id && !c.IsArchived, ct))
                return Result<AssetClassDto>.Conflict("Archive its sub-classes first.");
        }
        else if (cls.ParentId is { } parentId && await db.AssetClasses.AnyAsync(c => c.Id == parentId && c.IsArchived, ct))
        {
            return Result<AssetClassDto>.Conflict("Restore its parent class first.");
        }

        cls.IsArchived = archived;
        db.Entry(cls).Property(c => c.RowVersion).OriginalValue = version;
        audit.Record(archived ? "assetclass.archived" : "assetclass.restored", nameof(AssetClass), id.ToString());
        return await SaveAsync(cls, ct);
    }

    private async Task<int> DepthAsync(AssetClass cls, CancellationToken ct)
    {
        var depth = 1;
        var current = cls;
        while (current.ParentId is { } parentId && depth <= MaxDepth)
        {
            current = await db.AssetClasses.AsNoTracking().FirstAsync(c => c.Id == parentId, ct);
            depth++;
        }

        return depth;
    }

    private static ValidationErrors Validate(SaveAssetClassRequest r)
    {
        var errors = new ValidationErrors();
        if (string.IsNullOrWhiteSpace(r.Code) || !LocationRules.IsValidCode(r.Code.Trim())) errors.Add("code", "Code is required: 1-50 letters, digits, '.', '_' or '-'.");
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 200) errors.Add("name", "Name is required (max 200 characters).");
        if (r.NameAr is { Length: > 200 }) errors.Add("nameAr", "Arabic name is at most 200 characters.");
        return errors;
    }

    private async Task<Result<AssetClassDto>> SaveAsync(AssetClass cls, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return Result<AssetClassDto>.Ok(AssetClassDto.From(cls));
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<AssetClassDto>.Conflict("The class was changed by someone else. Reload it and try again.");
        }
        catch (DbUpdateException ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return Result<AssetClassDto>.Conflict($"The code '{cls.Code}' is already used by another active class.");
        }
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
