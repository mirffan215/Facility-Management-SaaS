using System.Text.Json;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Facilities.Domain;
using Fms.Facilities.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Facilities.Services;

public sealed class LocationService(FacilitiesDbContext db, IAuditPort audit, TimeProvider clock)
{
    private const int MaxTreeNodes = 5000;

    public async Task<Page<LocationDto>> ListAsync(LocationQuery q, CancellationToken ct = default)
    {
        var (number, size) = Page<LocationDto>.Normalize(q.Page, q.PageSize);
        var query = db.Locations.AsNoTracking().AsQueryable();
        if (!q.IncludeArchived) query = query.Where(l => !l.IsArchived);
        if (q.ParentId is { } parent) query = query.Where(l => l.ParentId == parent);
        if (q.Type is { } type) query = query.Where(l => l.Type == type);
        if (q.UnderId is { } under)
        {
            var root = await db.Locations.AsNoTracking().Where(l => l.Id == under).Select(l => l.Path).FirstOrDefaultAsync(ct);
            if (root is null) return new Page<LocationDto>([], 0, number, size);
            query = query.Where(l => l.Path.StartsWith(root) && l.Id != under);
        }

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var term = q.Search.Trim();
            query = query.Where(l => l.Code.Contains(term) || l.Name.Contains(term) || (l.NameAr != null && l.NameAr.Contains(term)));
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(l => l.Path).Skip((number - 1) * size).Take(size).ToListAsync(ct);
        return new Page<LocationDto>(items.Select(LocationDto.From).ToList(), total, number, size);
    }

    public async Task<IReadOnlyList<LocationNode>> TreeAsync(CancellationToken ct = default) =>
        await db.Locations.AsNoTracking().Where(l => !l.IsArchived).OrderBy(l => l.Path).Take(MaxTreeNodes)
            .Select(l => new LocationNode(l.Id, l.ParentId, l.Type, l.Code, l.Name, l.NameAr)).ToListAsync(ct);

    public async Task<Result<LocationDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var l = await db.Locations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return l is null ? Result<LocationDto>.NotFound() : Result<LocationDto>.Ok(LocationDto.From(l));
    }

    public async Task<Result<LocationDto>> CreateAsync(CreateLocationRequest r, CancellationToken ct = default)
    {
        var errors = new ValidationErrors();
        var code = r.Code?.Trim() ?? "";
        var name = r.Name?.Trim() ?? "";
        if (!LocationRules.IsValidCode(code)) errors.Add("code", "Code is required: 1-50 letters, digits, '.', '_' or '-'.");
        ValidateCommon(errors, r.Type, name, r.NameAr, r.Address, r.TimeZone, r.AreaSquareMetres);

        Location? parent = null;
        if (r.ParentId is { } parentId)
        {
            parent = await db.Locations.AsNoTracking().FirstOrDefaultAsync(l => l.Id == parentId, ct);
            if (parent is null) errors.Add("parentId", "Parent location not found.");
            else if (parent.IsArchived) errors.Add("parentId", "Parent location is archived.");
        }

        if (errors.Any) return errors.ToResult<LocationDto>();
        if (!LocationRules.CanBeChildOf(r.Type, parent?.Type))
        {
            return Result<LocationDto>.Invalid("parentId", parent is null ? $"A {r.Type} needs a parent." : $"A {r.Type} cannot be placed under a {parent.Type}.");
        }

        var fields = await ValidateCustomFieldsAsync(r.CustomFields, "{}", ct);
        if (!fields.IsOk) return fields.Map(_ => (LocationDto)null!);

        var now = clock.GetUtcNow();
        var location = new Location
        {
            ParentId = parent?.Id, Type = r.Type, Code = code, Name = name, NameAr = Clean(r.NameAr), Address = Clean(r.Address),
            TimeZone = Clean(r.TimeZone), AreaSquareMetres = r.AreaSquareMetres, CustomFieldsJson = fields.Value!, CreatedAt = now, UpdatedAt = now,
        };
        location.Path = LocationRules.PathFor(parent, location.Id);
        db.Locations.Add(location);
        audit.Record("location.created", nameof(Location), location.Id.ToString(), JsonSerializer.Serialize(new { location.Type, location.Code, location.Name, location.ParentId }));

        return await SaveAsync(location, ct);
    }

    public async Task<Result<LocationDto>> UpdateAsync(Guid id, UpdateLocationRequest r, CancellationToken ct = default)
    {
        var location = await db.Locations.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (location is null) return Result<LocationDto>.NotFound();
        if (location.IsArchived) return Result<LocationDto>.Conflict("An archived location cannot be edited. Restore it first.");

        var errors = new ValidationErrors();
        var code = r.Code?.Trim() ?? "";
        var name = r.Name?.Trim() ?? "";
        if (!LocationRules.IsValidCode(code)) errors.Add("code", "Code is required: 1-50 letters, digits, '.', '_' or '-'.");
        ValidateCommon(errors, location.Type, name, r.NameAr, r.Address, r.TimeZone, r.AreaSquareMetres);
        if (!TryVersion(r.RowVersion, out var version)) errors.Add("rowVersion", "rowVersion is required (as returned by the last read).");
        if (errors.Any) return errors.ToResult<LocationDto>();

        var fields = await ValidateCustomFieldsAsync(r.CustomFields, location.CustomFieldsJson, ct);
        if (!fields.IsOk) return fields.Map(_ => (LocationDto)null!);

        var changes = new Dictionary<string, object?>();
        Track(changes, "code", location.Code, code);
        Track(changes, "name", location.Name, name);
        Track(changes, "nameAr", location.NameAr, Clean(r.NameAr));
        Track(changes, "address", location.Address, Clean(r.Address));
        Track(changes, "timeZone", location.TimeZone, Clean(r.TimeZone));
        Track(changes, "areaSquareMetres", location.AreaSquareMetres, r.AreaSquareMetres);
        Track(changes, "customFields", location.CustomFieldsJson, fields.Value!);

        location.Code = code;
        location.Name = name;
        location.NameAr = Clean(r.NameAr);
        location.Address = Clean(r.Address);
        location.TimeZone = Clean(r.TimeZone);
        location.AreaSquareMetres = r.AreaSquareMetres;
        location.CustomFieldsJson = fields.Value!;
        location.UpdatedAt = clock.GetUtcNow();
        db.Entry(location).Property(l => l.RowVersion).OriginalValue = version;

        if (changes.Count > 0) audit.Record("location.updated", nameof(Location), id.ToString(), JsonSerializer.Serialize(changes));
        return await SaveAsync(location, ct);
    }

    /// <summary>Moves a location (and everything beneath it) to a new parent of a valid type. Sites are roots and cannot be moved.</summary>
    public async Task<Result<LocationDto>> MoveAsync(Guid id, MoveLocationRequest r, CancellationToken ct = default)
    {
        var node = await db.Locations.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (node is null) return Result<LocationDto>.NotFound();
        if (!TryVersion(r.RowVersion, out var version)) return Result<LocationDto>.Invalid("rowVersion", "rowVersion is required.");
        if (node.IsArchived) return Result<LocationDto>.Conflict("An archived location cannot be moved.");

        Location? parent = null;
        if (r.NewParentId is { } parentId)
        {
            parent = await db.Locations.FirstOrDefaultAsync(l => l.Id == parentId, ct);
            if (parent is null) return Result<LocationDto>.Invalid("newParentId", "Parent location not found.");
            if (parent.IsArchived) return Result<LocationDto>.Invalid("newParentId", "Parent location is archived.");
            if (parent.Path.StartsWith(node.Path, StringComparison.Ordinal)) return Result<LocationDto>.Invalid("newParentId", "A location cannot be moved under itself or one of its descendants.");
        }

        if (!LocationRules.CanBeChildOf(node.Type, parent?.Type))
        {
            return Result<LocationDto>.Invalid("newParentId", parent is null ? $"A {node.Type} needs a parent." : $"A {node.Type} cannot be placed under a {parent.Type}.");
        }

        var oldPath = node.Path;
        var oldParent = node.ParentId;
        var newPath = LocationRules.PathFor(parent, node.Id);
        var now = clock.GetUtcNow();

        var descendants = await db.Locations.Where(l => l.Path.StartsWith(oldPath) && l.Id != node.Id).ToListAsync(ct);
        foreach (var d in descendants)
        {
            d.Path = newPath + d.Path[oldPath.Length..];
            d.UpdatedAt = now;
        }

        node.ParentId = parent?.Id;
        node.Path = newPath;
        node.UpdatedAt = now;
        db.Entry(node).Property(l => l.RowVersion).OriginalValue = version;

        audit.Record("location.moved", nameof(Location), id.ToString(),
            JsonSerializer.Serialize(new { fromParent = oldParent, toParent = node.ParentId, affectedDescendants = descendants.Count }));
        return await SaveAsync(node, ct);
    }

    public async Task<Result<LocationDto>> ArchiveAsync(Guid id, string? rowVersion, CancellationToken ct = default)
    {
        var location = await db.Locations.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (location is null) return Result<LocationDto>.NotFound();
        if (!TryVersion(rowVersion, out var version)) return Result<LocationDto>.Invalid("rowVersion", "rowVersion is required.");
        if (location.IsArchived) return Result<LocationDto>.Ok(LocationDto.From(location));

        if (await db.Locations.AnyAsync(l => l.ParentId == id && !l.IsArchived, ct))
            return Result<LocationDto>.Conflict("Archive or move the locations inside it first.");

        location.IsArchived = true;
        location.UpdatedAt = clock.GetUtcNow();
        db.Entry(location).Property(l => l.RowVersion).OriginalValue = version;
        audit.Record("location.archived", nameof(Location), id.ToString());
        return await SaveAsync(location, ct);
    }

    public async Task<Result<LocationDto>> RestoreAsync(Guid id, string? rowVersion, CancellationToken ct = default)
    {
        var location = await db.Locations.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (location is null) return Result<LocationDto>.NotFound();
        if (!TryVersion(rowVersion, out var version)) return Result<LocationDto>.Invalid("rowVersion", "rowVersion is required.");
        if (!location.IsArchived) return Result<LocationDto>.Ok(LocationDto.From(location));

        if (location.ParentId is { } parentId && await db.Locations.AnyAsync(l => l.Id == parentId && l.IsArchived, ct))
            return Result<LocationDto>.Conflict("Restore its parent location first.");

        location.IsArchived = false;
        location.UpdatedAt = clock.GetUtcNow();
        db.Entry(location).Property(l => l.RowVersion).OriginalValue = version;
        audit.Record("location.restored", nameof(Location), id.ToString());
        return await SaveAsync(location, ct);
    }

    private async Task<Result<LocationDto>> SaveAsync(Location location, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return Result<LocationDto>.Ok(LocationDto.From(location));
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<LocationDto>.Conflict("The location was changed by someone else. Reload it and try again.");
        }
        catch (DbUpdateException ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return Result<LocationDto>.Conflict($"The code '{location.Code}' is already used by another active location.");
        }
    }

    private async Task<Result<string>> ValidateCustomFieldsAsync(Dictionary<string, JsonElement>? values, string existingJson, CancellationToken ct)
    {
        var defs = await db.CustomFieldDefinitions.AsNoTracking().Where(d => d.Target == CustomFieldTarget.Location).ToListAsync(ct);
        return CustomFieldValidator.Validate(defs.Where(d => !d.IsArchived).ToList(), defs.Where(d => d.IsArchived).Select(d => d.Key).ToList(), values, existingJson);
    }

    private static void ValidateCommon(ValidationErrors errors, LocationType type, string name, string? nameAr, string? address, string? timeZone, decimal? area)
    {
        if (name.Length is < 1 or > 200) errors.Add("name", "Name is required (max 200 characters).");
        if (nameAr is { Length: > 200 }) errors.Add("nameAr", "Arabic name is at most 200 characters.");
        if (address is { Length: > 500 }) errors.Add("address", "Address is at most 500 characters.");
        if (area is < 0 or > 100_000_000) errors.Add("areaSquareMetres", "Area must be between 0 and 100,000,000.");
        if (!string.IsNullOrWhiteSpace(timeZone))
        {
            if (type != LocationType.Site) errors.Add("timeZone", "Only a site has a time zone; others use their site's.");
            else if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZone.Trim(), out _)) errors.Add("timeZone", "Unknown time zone. Use an IANA id such as Asia/Riyadh.");
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
