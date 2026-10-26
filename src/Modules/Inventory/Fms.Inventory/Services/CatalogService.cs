using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Inventory.Domain;
using Fms.Inventory.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Inventory.Services;

/// <summary>Stores, items, and the parts a preventive-maintenance template needs. Reading is for anyone with inventory.read; changing needs inventory.manage.</summary>
public sealed class CatalogService(InventoryDbContext db, IAuditPort audit, IFacilityLookup facilities, IWorkOrderLookup work)
{
    // ---------------- stores ----------------

    public async Task<IReadOnlyList<StoreDto>> StoresAsync(bool includeArchived, CancellationToken ct = default) =>
        (await db.Stores.AsNoTracking().Where(s => includeArchived || !s.IsArchived).OrderBy(s => s.Code).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<Result<StoreDto>> SaveStoreAsync(Guid? id, SaveStoreRequest r, CancellationToken ct = default)
    {
        Store? existing = null;
        if (id is { } sid)
        {
            existing = await db.Stores.FirstOrDefaultAsync(s => s.Id == sid, ct);
            if (existing is null) return Result<StoreDto>.NotFound();
        }

        var errors = new ValidationErrors();
        if (!Common.IsCode(r.Code)) errors.Add("code", "Code is required: 1-50 letters, digits, dot, dash, slash or underscore.");
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 200) errors.Add("name", "Name is required (max 200 characters).");
        if (r.NameAr is { Length: > 200 }) errors.Add("nameAr", "Arabic name is at most 200 characters.");
        if (existing is not null && !Common.TryVersion(r.RowVersion, out _)) errors.Add("rowVersion", "rowVersion is required.");
        Guid? siteId = null;
        if (r.SiteId is { } locationId)
        {
            siteId = await facilities.SiteOfAsync(locationId, ct);
            if (siteId is null) errors.Add("siteId", "Site not found.");
        }

        if (errors.Any) return errors.ToResult<StoreDto>();

        var code = r.Code!.Trim().ToUpperInvariant();
        if (await db.Stores.AnyAsync(s => s.Code == code && s.Id != (existing != null ? existing.Id : Guid.Empty), ct)) return Result<StoreDto>.Conflict("A store with this code already exists.");

        var store = existing ?? new Store();
        store.Code = code;
        store.Name = r.Name!.Trim();
        store.NameAr = Common.Clean(r.NameAr);
        store.SiteId = siteId;
        if (existing is null) db.Stores.Add(store);
        else
        {
            Common.TryVersion(r.RowVersion, out var version);
            db.Entry(store).Property(x => x.RowVersion).OriginalValue = version;
            db.Entry(store).Property(x => x.Name).IsModified = true; // always write the row so a stale version is detected
        }

        audit.Record(existing is null ? "inventory.store.created" : "inventory.store.updated", nameof(Store), store.Id.ToString(), JsonSerializer.Serialize(new { store.Code, store.Name, store.SiteId }));
        return await CommitAsync(store, ToDto, ct);
    }

    public async Task<Result<StoreDto>> SetStoreArchivedAsync(Guid id, bool archived, string? rowVersion, CancellationToken ct = default)
    {
        var store = await db.Stores.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (store is null) return Result<StoreDto>.NotFound();
        if (!Common.TryVersion(rowVersion, out var version)) return Result<StoreDto>.Invalid("rowVersion", "rowVersion is required.");
        if (archived)
        {
            if (await db.Levels.AnyAsync(l => l.StoreId == id && l.OnHand > 0, ct)) return Result<StoreDto>.Conflict("The store still holds stock. Transfer or adjust it to zero first.");
            if (await db.StockTakes.AnyAsync(t => t.StoreId == id && (t.Status == StockTakeStatus.Counting || t.Status == StockTakeStatus.Submitted), ct)) return Result<StoreDto>.Conflict("A stock take of this store is still open.");
        }

        store.IsArchived = archived;
        db.Entry(store).Property(x => x.RowVersion).OriginalValue = version;
        audit.Record(archived ? "inventory.store.archived" : "inventory.store.restored", nameof(Store), id.ToString());
        return await CommitAsync(store, ToDto, ct);
    }

    // ---------------- items ----------------

    public async Task<Page<ItemDto>> ItemsAsync(string? q, bool includeArchived, int? page, int? pageSize, CancellationToken ct = default)
    {
        var (number, size) = Page<ItemDto>.Normalize(page, pageSize);
        var query = db.Items.AsNoTracking().Where(i => includeArchived || !i.IsArchived);
        if (Common.Clean(q) is { } text) query = query.Where(i => i.Code.Contains(text) || i.Name.Contains(text) || (i.NameAr != null && i.NameAr.Contains(text)) || i.Barcode == text);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(i => i.Code).Skip((number - 1) * size).Take(size).ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var totals = await db.Levels.AsNoTracking().Where(l => ids.Contains(l.ItemId)).GroupBy(l => l.ItemId)
            .Select(g => new { g.Key, OnHand = g.Sum(l => l.OnHand), Reserved = g.Sum(l => l.Reserved) }).ToDictionaryAsync(x => x.Key, ct);
        return new Page<ItemDto>(rows.Select(i => ToDto(i, totals.GetValueOrDefault(i.Id)?.OnHand ?? 0, (totals.GetValueOrDefault(i.Id)?.OnHand ?? 0) - (totals.GetValueOrDefault(i.Id)?.Reserved ?? 0))).ToList(), total, number, size);
    }

    public async Task<Result<ItemDto>> GetItemAsync(Guid id, CancellationToken ct = default)
    {
        var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
        if (item is null) return Result<ItemDto>.NotFound();
        var levels = await db.Levels.AsNoTracking().Where(l => l.ItemId == id).ToListAsync(ct);
        return Result<ItemDto>.Ok(ToDto(item, levels.Sum(l => l.OnHand), levels.Sum(l => l.OnHand - l.Reserved)));
    }

    public async Task<Result<ItemDto>> SaveItemAsync(Guid? id, SaveItemRequest r, CancellationToken ct = default)
    {
        Item? existing = null;
        if (id is { } iid)
        {
            existing = await db.Items.FirstOrDefaultAsync(i => i.Id == iid, ct);
            if (existing is null) return Result<ItemDto>.NotFound();
        }

        var errors = new ValidationErrors();
        if (!Common.IsCode(r.Code)) errors.Add("code", "Code is required: 1-50 letters, digits, dot, dash, slash or underscore.");
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 200) errors.Add("name", "Name is required (max 200 characters).");
        if (r.NameAr is { Length: > 200 }) errors.Add("nameAr", "Arabic name is at most 200 characters.");
        if (r.Description is { Length: > 2000 }) errors.Add("description", "Description is at most 2000 characters.");
        var unit = Common.Clean(r.Unit) ?? "pcs";
        if (unit.Length > 20) errors.Add("unit", "Unit is at most 20 characters.");
        var barcode = Common.Clean(r.Barcode);
        if (barcode is { Length: > 100 }) errors.Add("barcode", "Barcode is at most 100 characters.");
        if (existing is not null && !Common.TryVersion(r.RowVersion, out _)) errors.Add("rowVersion", "rowVersion is required.");
        if (errors.Any) return errors.ToResult<ItemDto>();

        var code = r.Code!.Trim().ToUpperInvariant();
        var self = existing?.Id ?? Guid.Empty;
        if (await db.Items.AnyAsync(i => i.Code == code && i.Id != self, ct)) return Result<ItemDto>.Conflict("An item with this code already exists.");
        if (barcode is not null && await db.Items.AnyAsync(i => i.Barcode == barcode && i.Id != self, ct)) return Result<ItemDto>.Conflict("Another item already has this barcode.");

        var item = existing ?? new Item();
        item.Code = code;
        item.Name = r.Name!.Trim();
        item.NameAr = Common.Clean(r.NameAr);
        item.Description = Common.Clean(r.Description);
        item.Unit = unit;
        item.Barcode = barcode;
        if (existing is null) db.Items.Add(item);
        else
        {
            Common.TryVersion(r.RowVersion, out var version);
            db.Entry(item).Property(x => x.RowVersion).OriginalValue = version;
            db.Entry(item).Property(x => x.Name).IsModified = true;
        }

        audit.Record(existing is null ? "inventory.item.created" : "inventory.item.updated", nameof(Item), item.Id.ToString(), JsonSerializer.Serialize(new { item.Code, item.Name, item.Unit }));
        var saved = await CommitAsync(item, i => ToDto(i, 0, 0), ct);
        return saved.IsOk ? await GetItemAsync(item.Id, ct) : saved;
    }

    public async Task<Result<ItemDto>> SetItemArchivedAsync(Guid id, bool archived, string? rowVersion, CancellationToken ct = default)
    {
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (item is null) return Result<ItemDto>.NotFound();
        if (!Common.TryVersion(rowVersion, out var version)) return Result<ItemDto>.Invalid("rowVersion", "rowVersion is required.");
        if (archived && await db.Levels.AnyAsync(l => l.ItemId == id && l.OnHand > 0, ct)) return Result<ItemDto>.Conflict("There is still stock of this item. Use it up or adjust it to zero first.");
        item.IsArchived = archived;
        db.Entry(item).Property(x => x.RowVersion).OriginalValue = version;
        audit.Record(archived ? "inventory.item.archived" : "inventory.item.restored", nameof(Item), id.ToString());
        var saved = await CommitAsync(item, i => ToDto(i, 0, 0), ct);
        return saved.IsOk ? await GetItemAsync(id, ct) : saved;
    }

    // ---------------- parts a PM template needs ----------------

    public async Task<IReadOnlyList<PmTemplatePartDto>> PmPartsAsync(Guid templateId, CancellationToken ct = default)
    {
        var parts = await db.PmTemplateParts.AsNoTracking().Where(p => p.PmTemplateId == templateId).ToListAsync(ct);
        return await ToPartDtosAsync(parts, ct);
    }

    public async Task<Result<IReadOnlyList<PmTemplatePartDto>>> SavePmPartsAsync(Guid templateId, SavePmPartsRequest r, CancellationToken ct = default)
    {
        if (!await work.PmTemplateExistsAsync(templateId, ct)) return Result<IReadOnlyList<PmTemplatePartDto>>.NotFound();
        var input = r.Parts ?? [];
        var errors = new ValidationErrors();
        if (input.Count > 50) errors.Add("parts", "A template lists at most 50 parts.");
        if (input.Any(p => p.ItemId is null || p.Quantity is not { } q || !StockRules.IsValidQuantity(q))) errors.Add("parts", "Every part needs an item and a quantity greater than zero (up to 4 decimals).");
        if (input.GroupBy(p => p.ItemId).Any(g => g.Count() > 1)) errors.Add("parts", "An item can be listed once.");
        if (errors.Any) return errors.ToResult<IReadOnlyList<PmTemplatePartDto>>();

        var itemIds = input.Select(p => p.ItemId!.Value).ToList();
        var storeIds = input.Where(p => p.StoreId != null).Select(p => p.StoreId!.Value).Distinct().ToList();
        if (await db.Items.CountAsync(i => itemIds.Contains(i.Id) && !i.IsArchived, ct) != itemIds.Count) errors.Add("parts", "An item was not found or is archived.");
        if (await db.Stores.CountAsync(s => storeIds.Contains(s.Id) && !s.IsArchived, ct) != storeIds.Count) errors.Add("parts", "A store was not found or is archived.");
        if (errors.Any) return errors.ToResult<IReadOnlyList<PmTemplatePartDto>>();

        db.PmTemplateParts.RemoveRange(await db.PmTemplateParts.Where(p => p.PmTemplateId == templateId).ToListAsync(ct));
        foreach (var p in input) db.PmTemplateParts.Add(new PmTemplatePart { PmTemplateId = templateId, ItemId = p.ItemId!.Value, Quantity = p.Quantity!.Value, StoreId = p.StoreId });
        audit.Record("inventory.pmparts.saved", "PmTemplate", templateId.ToString(), JsonSerializer.Serialize(input.Select(p => new { p.ItemId, p.Quantity, p.StoreId })));
        await db.SaveChangesAsync(ct);
        return Result<IReadOnlyList<PmTemplatePartDto>>.Ok(await PmPartsAsync(templateId, ct));
    }

    private async Task<IReadOnlyList<PmTemplatePartDto>> ToPartDtosAsync(List<PmTemplatePart> parts, CancellationToken ct)
    {
        var itemIds = parts.Select(p => p.ItemId).ToList();
        var storeIds = parts.Where(p => p.StoreId != null).Select(p => p.StoreId!.Value).ToList();
        var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var stores = await db.Stores.AsNoTracking().Where(s => storeIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        return parts.Where(p => items.ContainsKey(p.ItemId)).OrderBy(p => items[p.ItemId].Code)
            .Select(p => new PmTemplatePartDto(p.ItemId, items[p.ItemId].Code, items[p.ItemId].Name, items[p.ItemId].Unit, p.Quantity, p.StoreId, p.StoreId is { } s ? stores.GetValueOrDefault(s)?.Name : null)).ToList();
    }

    // ---------------- helpers ----------------

    private async Task<Result<T>> CommitAsync<TEntity, T>(TEntity entity, Func<TEntity, T> map, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return Result<T>.Ok(map(entity));
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<T>.Conflict("This record was changed by someone else. Reload and try again.");
        }
        catch (DbUpdateException ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return Result<T>.Conflict("Another record with the same code already exists.");
        }
    }

    private static StoreDto ToDto(Store s) => new(s.Id, s.Code, s.Name, s.NameAr, s.SiteId, s.IsArchived, Common.Version(s.RowVersion));

    private static ItemDto ToDto(Item i, decimal onHand, decimal available) =>
        new(i.Id, i.Code, i.Name, i.NameAr, i.Description, i.Unit, i.Barcode, i.IsArchived, onHand, available, Common.Version(i.RowVersion));
}
