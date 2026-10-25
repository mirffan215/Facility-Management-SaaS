using System.Text.Json;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Facilities.Domain;
using Fms.Facilities.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Facilities.Services;

public sealed class AssetService(FacilitiesDbContext db, IAuditPort audit, TimeProvider clock)
{
    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public async Task<Page<AssetDto>> ListAsync(AssetQuery q, CancellationToken ct = default)
    {
        var (number, size) = Page<AssetDto>.Normalize(q.Page, q.PageSize);
        var query = db.Assets.AsNoTracking().AsQueryable();

        if (!q.IncludeRetired && q.Status is null) query = query.Where(a => a.Status != AssetStatus.Retired);
        if (q.Status is { } status) query = query.Where(a => a.Status == status);
        if (q.Criticality is { } crit) query = query.Where(a => a.Criticality == crit);
        if (q.ClassId is { } classId) query = query.Where(a => a.AssetClassId == classId);
        if (q.ParentAssetId is { } parent) query = query.Where(a => a.ParentAssetId == parent);
        if (q.WarrantyExpiresBefore is { } before) query = query.Where(a => a.WarrantyExpiry != null && a.WarrantyExpiry < before);

        if (q.LocationId is { } locationId)
        {
            var path = await db.Locations.AsNoTracking().Where(l => l.Id == locationId).Select(l => l.Path).FirstOrDefaultAsync(ct);
            if (path is null) return new Page<AssetDto>([], 0, number, size);
            query = q.IncludeSublocations
                ? query.Where(a => db.Locations.Any(l => l.Id == a.LocationId && l.Path.StartsWith(path)))
                : query.Where(a => a.LocationId == locationId);
        }

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var term = q.Search.Trim();
            query = query.Where(a => a.Code.Contains(term) || a.Name.Contains(term) || (a.NameAr != null && a.NameAr.Contains(term))
                                     || (a.SerialNumber != null && a.SerialNumber.Contains(term)) || (a.Manufacturer != null && a.Manufacturer.Contains(term))
                                     || (a.Model != null && a.Model.Contains(term)));
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(a => a.Code).Skip((number - 1) * size).Take(size).ToListAsync(ct);
        return new Page<AssetDto>(items.Select(a => AssetDto.From(a, Today)).ToList(), total, number, size);
    }

    public async Task<Result<AssetDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var a = await db.Assets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return a is null ? Result<AssetDto>.NotFound() : Result<AssetDto>.Ok(AssetDto.From(a, Today));
    }

    /// <summary>Looks an asset up by what its label encodes (what a scanner reads). Typos in case/separators/look-alike characters are forgiven.</summary>
    public async Task<Result<AssetDto>> GetByTagAsync(string? tag, CancellationToken ct = default)
    {
        var normalized = AssetTag.Normalize(tag);
        if (normalized is null) return Result<AssetDto>.NotFound();
        var a = await db.Assets.AsNoTracking().FirstOrDefaultAsync(x => x.TagCode == normalized, ct);
        return a is null ? Result<AssetDto>.NotFound() : Result<AssetDto>.Ok(AssetDto.From(a, Today));
    }

    public async Task<Result<AssetDto>> CreateAsync(CreateAssetRequest create, CancellationToken ct = default)
    {
        var r = create.ToSave();
        var checks = await CheckAsync(r, existing: null, ct);
        if (!checks.IsOk) return checks.Map(_ => (AssetDto)null!);

        var now = clock.GetUtcNow();
        var asset = new Asset
        {
            Code = r.Code!.Trim(), Name = r.Name!.Trim(), NameAr = Clean(r.NameAr), AssetClassId = r.AssetClassId, LocationId = r.LocationId,
            ParentAssetId = r.ParentAssetId, Criticality = r.Criticality ?? Criticality.Medium,
            Manufacturer = Clean(r.Manufacturer), Model = Clean(r.Model), SerialNumber = Clean(r.SerialNumber),
            InstallDate = r.InstallDate, WarrantyExpiry = r.WarrantyExpiry, Status = create.StartActive ? AssetStatus.Active : AssetStatus.Planned,
            TagCode = AssetTag.New(), CustomFieldsJson = checks.Value!, CreatedAt = now, UpdatedAt = now,
        };
        db.Assets.Add(asset);
        audit.Record("asset.created", nameof(Asset), asset.Id.ToString(),
            JsonSerializer.Serialize(new { asset.Code, asset.Name, asset.AssetClassId, asset.LocationId, asset.ParentAssetId, asset.Status }));
        return await SaveAsync(asset, ct);
    }

    public async Task<Result<AssetDto>> UpdateAsync(Guid id, SaveAssetRequest r, CancellationToken ct = default)
    {
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (asset is null) return Result<AssetDto>.NotFound();
        if (asset.Status == AssetStatus.Retired) return Result<AssetDto>.Conflict("A retired asset is a closed record and cannot be edited.");
        if (!TryVersion(r.RowVersion, out var version)) return Result<AssetDto>.Invalid("rowVersion", "rowVersion is required (as returned by the last read).");

        var checks = await CheckAsync(r, asset, ct);
        if (!checks.IsOk) return checks.Map(_ => (AssetDto)null!);

        var changes = new Dictionary<string, object?>();
        Track(changes, "code", asset.Code, r.Code!.Trim());
        Track(changes, "name", asset.Name, r.Name!.Trim());
        Track(changes, "nameAr", asset.NameAr, Clean(r.NameAr));
        Track(changes, "assetClassId", asset.AssetClassId, r.AssetClassId);
        Track(changes, "locationId", asset.LocationId, r.LocationId);
        Track(changes, "parentAssetId", asset.ParentAssetId, r.ParentAssetId);
        Track(changes, "criticality", asset.Criticality, r.Criticality ?? asset.Criticality);
        Track(changes, "manufacturer", asset.Manufacturer, Clean(r.Manufacturer));
        Track(changes, "model", asset.Model, Clean(r.Model));
        Track(changes, "serialNumber", asset.SerialNumber, Clean(r.SerialNumber));
        Track(changes, "installDate", asset.InstallDate, r.InstallDate);
        Track(changes, "warrantyExpiry", asset.WarrantyExpiry, r.WarrantyExpiry);
        Track(changes, "customFields", asset.CustomFieldsJson, checks.Value!);

        asset.Code = r.Code.Trim();
        asset.Name = r.Name.Trim();
        asset.NameAr = Clean(r.NameAr);
        asset.AssetClassId = r.AssetClassId;
        asset.LocationId = r.LocationId;
        asset.ParentAssetId = r.ParentAssetId;
        asset.Criticality = r.Criticality ?? asset.Criticality;
        asset.Manufacturer = Clean(r.Manufacturer);
        asset.Model = Clean(r.Model);
        asset.SerialNumber = Clean(r.SerialNumber);
        asset.InstallDate = r.InstallDate;
        asset.WarrantyExpiry = r.WarrantyExpiry;
        asset.CustomFieldsJson = checks.Value!;
        asset.UpdatedAt = clock.GetUtcNow();
        db.Entry(asset).Property(a => a.RowVersion).OriginalValue = version;

        if (changes.Count > 0) audit.Record("asset.updated", nameof(Asset), id.ToString(), JsonSerializer.Serialize(changes));
        return await SaveAsync(asset, ct);
    }

    public async Task<Result<AssetDto>> ChangeStatusAsync(Guid id, ChangeAssetStatusRequest r, CancellationToken ct = default)
    {
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (asset is null) return Result<AssetDto>.NotFound();

        var errors = new ValidationErrors();
        if (!Enum.IsDefined(r.Status)) errors.Add("status", "Unknown status.");
        if (!TryVersion(r.RowVersion, out var version)) errors.Add("rowVersion", "rowVersion is required.");
        var reason = Clean(r.Reason);
        if (reason is { Length: > 500 }) errors.Add("reason", "Reason is at most 500 characters.");
        if (Enum.IsDefined(r.Status) && AssetRules.NeedsReason(r.Status) && reason is null) errors.Add("reason", $"A reason is required to set an asset {r.Status}.");
        if (errors.Any) return errors.ToResult<AssetDto>();

        if (!AssetRules.CanTransition(asset.Status, r.Status))
            return Result<AssetDto>.Conflict($"An asset that is {asset.Status} cannot become {r.Status}.");

        if (r.Status == AssetStatus.Retired && await db.Assets.AnyAsync(a => a.ParentAssetId == id && a.Status != AssetStatus.Retired, ct))
            return Result<AssetDto>.Conflict("Retire or detach the assets that are part of it first.");

        var from = asset.Status;
        var now = clock.GetUtcNow();
        asset.Status = r.Status;
        asset.StatusReason = reason;
        asset.UpdatedAt = now;
        if (r.Status == AssetStatus.Retired) asset.RetiredAt = now;
        db.Entry(asset).Property(a => a.RowVersion).OriginalValue = version;

        audit.Record("asset.status_changed", nameof(Asset), id.ToString(), JsonSerializer.Serialize(new { from, to = r.Status, reason }));
        return await SaveAsync(asset, ct);
    }

    /// <summary>Issues a new label code (lost or damaged label). The old code stops resolving immediately.</summary>
    public async Task<Result<AssetDto>> ReissueTagAsync(Guid id, string? rowVersion, CancellationToken ct = default)
    {
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (asset is null) return Result<AssetDto>.NotFound();
        if (!TryVersion(rowVersion, out var version)) return Result<AssetDto>.Invalid("rowVersion", "rowVersion is required.");
        if (asset.Status == AssetStatus.Retired) return Result<AssetDto>.Conflict("A retired asset's label cannot be reissued.");

        asset.TagCode = AssetTag.New();
        asset.UpdatedAt = clock.GetUtcNow();
        db.Entry(asset).Property(a => a.RowVersion).OriginalValue = version;
        audit.Record("asset.tag_reissued", nameof(Asset), id.ToString());
        return await SaveAsync(asset, ct);
    }

    /// <summary>All the cross-entity checks for a create/update. Returns the normalised custom-field JSON on success.</summary>
    private async Task<Result<string>> CheckAsync(SaveAssetRequest r, Asset? existing, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var code = r.Code?.Trim() ?? "";
        if (!LocationRules.IsValidCode(code)) errors.Add("code", "Code is required: 1-50 letters, digits, '.', '_' or '-'.");
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 200) errors.Add("name", "Name is required (max 200 characters).");
        if (r.NameAr is { Length: > 200 }) errors.Add("nameAr", "Arabic name is at most 200 characters.");
        if (r.Manufacturer is { Length: > 200 }) errors.Add("manufacturer", "Manufacturer is at most 200 characters.");
        if (r.Model is { Length: > 200 }) errors.Add("model", "Model is at most 200 characters.");
        if (r.SerialNumber is { Length: > 100 }) errors.Add("serialNumber", "Serial number is at most 100 characters.");
        if (r.Criticality is { } c && !Enum.IsDefined(c)) errors.Add("criticality", "Unknown criticality.");
        if (r.InstallDate is { } install && r.WarrantyExpiry is { } warranty && warranty < install) errors.Add("warrantyExpiry", "Warranty cannot expire before the install date.");

        var assetClass = await db.AssetClasses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == r.AssetClassId, ct);
        if (assetClass is null || (assetClass.IsArchived && existing?.AssetClassId != r.AssetClassId)) errors.Add("assetClassId", "Asset class not found.");

        var location = await db.Locations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == r.LocationId, ct);
        if (location is null || (location.IsArchived && existing?.LocationId != r.LocationId)) errors.Add("locationId", "Location not found.");

        if (r.ParentAssetId is { } parentId)
        {
            if (existing is not null && parentId == existing.Id) errors.Add("parentAssetId", "An asset cannot be part of itself.");
            else
            {
                var parentError = await CheckParentAsync(parentId, existing?.Id, ct);
                if (parentError is not null) errors.Add("parentAssetId", parentError);
            }
        }

        if (errors.Any) return errors.ToResult<string>();

        var defs = await db.CustomFieldDefinitions.AsNoTracking().Where(d => d.Target == CustomFieldTarget.Asset).ToListAsync(ct);
        var applies = defs.Where(d => d.AssetClassId is null || d.AssetClassId == r.AssetClassId).ToList();
        var archivedKeys = applies.Where(d => d.IsArchived).Select(d => d.Key).ToList();
        return CustomFieldValidator.Validate(applies.Where(d => !d.IsArchived).ToList(), archivedKeys, r.CustomFields, existing?.CustomFieldsJson ?? "{}");
    }

    private async Task<string?> CheckParentAsync(Guid parentId, Guid? self, CancellationToken ct)
    {
        var depth = 1;
        var current = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == parentId, ct);
        if (current is null) return "Parent asset not found.";
        if (current.Status == AssetStatus.Retired) return "A retired asset cannot have parts added.";

        while (current.ParentAssetId is { } next)
        {
            if (next == self) return "That would make an asset part of its own part.";
            if (++depth >= AssetRules.MaxDepth) return $"Assets can be nested at most {AssetRules.MaxDepth} levels deep.";
            current = await db.Assets.AsNoTracking().FirstAsync(a => a.Id == next, ct);
        }

        return null;
    }

    private async Task<Result<AssetDto>> SaveAsync(Asset asset, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return Result<AssetDto>.Ok(AssetDto.From(asset, Today));
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<AssetDto>.Conflict("The asset was changed by someone else. Reload it and try again.");
        }
        catch (DbUpdateException ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return Result<AssetDto>.Conflict($"The asset code '{asset.Code}' is already in use.");
        }
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static void Track(Dictionary<string, object?> changes, string field, object? from, object? to)
    {
        if (!Equals(from, to)) changes[field] = new { from, to };
    }

    private static bool TryVersion(string? value, out byte[] version)
    {
        version = [];
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { version = Convert.FromBase64String(value); return version.Length > 0; }
        catch (FormatException) { return false; }
    }
}
