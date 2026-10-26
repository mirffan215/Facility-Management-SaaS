using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Messaging;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.BuildingBlocks.Tenancy;
using Fms.Purchasing.Domain;
using Fms.Purchasing.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Purchasing.Services;

/// <summary>
/// Purchase requests: drafted by anyone who may request, approved at the level the total needs by someone other than the requester, then turned into an order.
/// People without purchasing.read see only their own requests.
/// </summary>
public sealed class RequestService(
    PurchasingDbContext db, IAuditPort audit, ICurrentUser user, ICurrentPermissions permissions, IUserDirectory users, SettingsService settings, IOutbox outbox, ITenantContext tenant,
    TimeProvider clock, IInventoryCatalog? inventory = null, IVendorLookup? vendors = null, IWorkOrderLookup? work = null)
{
    // ---------------- reading ----------------

    public async Task<Page<RequestDto>> ListAsync(RequestStatus? status, bool awaitingMe, bool mine, string? q, int? page, int? pageSize, CancellationToken ct = default)
    {
        var (number, size) = Page<RequestDto>.Normalize(page, pageSize);
        var perms = await permissions.GetAsync(ct);
        var me = user.UserId;
        var query = db.Requests.AsNoTracking().AsQueryable();
        if (!perms.Contains(Permissions.PurchasingRead) || mine) query = query.Where(r => r.RequestedBy == me);
        if (status is { } s) query = query.Where(r => r.Status == s);
        if (awaitingMe)
        {
            var held = PurchasingRules.LevelHeld(perms);
            query = query.Where(r => r.Status == RequestStatus.Submitted && r.RequiredLevel <= held && r.RequestedBy != me);
        }

        if (Common.Clean(q) is { } text) query = query.Where(r => r.Title.Contains(text));
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(r => r.Number).Skip((number - 1) * size).Take(size).ToListAsync(ct);
        return new Page<RequestDto>(await ToDtosAsync(rows, withLines: false, perms, ct), total, number, size);
    }

    public async Task<Result<RequestDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var perms = await permissions.GetAsync(ct);
        var r = await VisibleAsync(perms).FirstOrDefaultAsync(x => x.Id == id, ct);
        return r is null ? Result<RequestDto>.NotFound() : Result<RequestDto>.Ok((await ToDtosAsync([r], withLines: true, perms, ct)).Single());
    }

    private IQueryable<PurchaseRequest> VisibleAsync(IReadOnlySet<string> perms)
    {
        var me = user.UserId;
        var query = db.Requests.AsNoTracking();
        return perms.Contains(Permissions.PurchasingRead) ? query : query.Where(r => r.RequestedBy == me);
    }

    // ---------------- drafting ----------------

    public async Task<Result<RequestDto>> SaveAsync(Guid? id, SaveRequestRequest r, CancellationToken ct = default)
    {
        if (user.UserId is not { } me) return Result<RequestDto>.Forbidden("Sign in to make a request.");
        PurchaseRequest? existing = null;
        if (id is { } rid)
        {
            existing = await db.Requests.FirstOrDefaultAsync(x => x.Id == rid && x.RequestedBy == me, ct);
            if (existing is null) return Result<RequestDto>.NotFound();
            if (existing.Status != RequestStatus.Draft) return Result<RequestDto>.Conflict("Only a draft can be changed. Cancel this request and make a new one.");
        }

        var errors = new ValidationErrors();
        if (string.IsNullOrWhiteSpace(r.Title) || r.Title.Trim().Length > 200) errors.Add("title", "Title is required (max 200 characters).");
        if (r.Justification is { Length: > 2000 }) errors.Add("justification", "Justification is at most 2000 characters.");
        if (existing is not null && !Common.TryVersion(r.RowVersion, out _)) errors.Add("rowVersion", "rowVersion is required.");
        if (r.PreferredVendorId is { } v && (vendors is null || await vendors.GetVendorAsync(v, ct) is null)) errors.Add("preferredVendorId", "Vendor not found.");
        if (r.WorkOrderId is { } w && (work is null || await work.GetAsync(w, ct) is null)) errors.Add("workOrderId", "Work order not found.");
        var lines = await ValidateLinesAsync(r.Lines ?? [], errors, ct);
        if (errors.Any) return errors.ToResult<RequestDto>();

        var tenantKey = tenant.TenantId?.ToString() ?? "none";
        var now = clock.GetUtcNow();
        try
        {
            var strategy = db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                PurchaseRequest request;
                if (existing is null)
                {
                    await Common.AcquireNumberLockAsync(db, "pr", tenantKey, ct);
                    request = new PurchaseRequest { Number = (await db.Requests.MaxAsync(x => (int?)x.Number, ct) ?? 0) + 1, RequestedBy = me, RequestedAt = now };
                    db.Requests.Add(request);
                }
                else
                {
                    request = await db.Requests.FirstAsync(x => x.Id == existing.Id, ct);
                    Common.TryVersion(r.RowVersion, out var version);
                    db.Entry(request).Property(x => x.RowVersion).OriginalValue = version;
                    db.Entry(request).Property(x => x.Title).IsModified = true; // always write the row so a stale version is detected
                    db.RequestLines.RemoveRange(await db.RequestLines.Where(l => l.RequestId == request.Id).ToListAsync(ct));
                }

                request.Title = r.Title!.Trim();
                request.Justification = Common.Clean(r.Justification);
                request.PreferredVendorId = r.PreferredVendorId;
                request.WorkOrderId = r.WorkOrderId;
                request.Total = lines.Sum(l => PurchasingRules.LineTotal(l.Quantity, l.EstimatedUnitPrice));
                var position = 0;
                foreach (var l in lines) { l.RequestId = request.Id; l.Position = ++position; db.RequestLines.Add(l); }
                audit.Record(existing is null ? "purchaserequest.created" : "purchaserequest.updated", nameof(PurchaseRequest), request.Id.ToString(), JsonSerializer.Serialize(new { request.Number, request.Title, request.Total, lines = lines.Count }));
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return await GetAsync(request.Id, ct);
            });
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<RequestDto>.Conflict("This request was changed by someone else. Reload and try again.");
        }
    }

    private async Task<List<PurchaseRequestLine>> ValidateLinesAsync(IReadOnlyList<RequestLineInput> input, ValidationErrors errors, CancellationToken ct)
    {
        var result = new List<PurchaseRequestLine>();
        if (input.Count > 100) { errors.Add("lines", "A request has at most 100 lines."); return result; }
        var itemIds = input.Where(l => l.ItemId != null).Select(l => l.ItemId!.Value).Distinct().ToList();
        var storeIds = input.Where(l => l.StoreId != null).Select(l => l.StoreId!.Value).Distinct().ToList();
        if ((itemIds.Count > 0 || storeIds.Count > 0) && inventory is null) { errors.Add("lines", "Stock items are not available."); return result; }
        var items = inventory is null ? new Dictionary<Guid, ItemRef>() : (await inventory.ItemsAsync(itemIds, ct)).ToDictionary(x => x.Key, x => x.Value);
        var stores = inventory is null ? new Dictionary<Guid, StoreRef>() : (await inventory.StoresAsync(storeIds, ct)).ToDictionary(x => x.Key, x => x.Value);

        foreach (var (l, n) in input.Select((l, n) => (l, n + 1)))
        {
            var item = l.ItemId is { } iid ? items.GetValueOrDefault(iid) : null;
            if (l.ItemId is not null && (item is null || !item.Active)) errors.Add("lines", $"Line {n}: the item was not found or is archived.");
            if (l.StoreId is { } sid && (!stores.TryGetValue(sid, out var store) || !store.Active)) errors.Add("lines", $"Line {n}: the store was not found or is archived.");
            if (l.ItemId is not null && l.StoreId is null) errors.Add("lines", $"Line {n}: choose the store the goods will go into.");
            if (l.ItemId is null && l.StoreId is not null) errors.Add("lines", $"Line {n}: a store only goes with a stock item.");
            var description = Common.Clean(l.Description) ?? item?.Name;
            if (description is null || description.Length > 300) errors.Add("lines", $"Line {n}: describe what is needed (max 300 characters).");
            if (l.Quantity is not { } q || !Common.IsQuantity(q)) errors.Add("lines", $"Line {n}: quantity must be more than zero (up to 4 decimals).");
            if (l.EstimatedUnitPrice is not { } p || !Common.IsPrice(p)) errors.Add("lines", $"Line {n}: price must be zero or more (up to 4 decimals).");
            var unit = item?.Unit ?? Common.Clean(l.Unit) ?? "pcs";
            if (unit.Length > 20) errors.Add("lines", $"Line {n}: unit is at most 20 characters.");
            if (errors.Any) continue;
            result.Add(new PurchaseRequestLine { ItemId = l.ItemId, StoreId = l.StoreId, Description = description!, Unit = unit, Quantity = l.Quantity!.Value, EstimatedUnitPrice = l.EstimatedUnitPrice!.Value });
        }

        return result;
    }

    // ---------------- decisions ----------------

    public async Task<Result<RequestDto>> SubmitAsync(Guid id, CancellationToken ct = default)
    {
        if (user.UserId is not { } me) return Result<RequestDto>.Forbidden("Sign in first.");
        var r = await db.Requests.FirstOrDefaultAsync(x => x.Id == id && x.RequestedBy == me, ct);
        if (r is null) return Result<RequestDto>.NotFound();
        if (r.Status != RequestStatus.Draft) return Result<RequestDto>.Conflict("Only a draft can be submitted.");
        var lines = await db.RequestLines.AsNoTracking().Where(l => l.RequestId == id).ToListAsync(ct);
        if (lines.Count == 0) return Result<RequestDto>.Invalid("lines", "Add at least one line before submitting.");
        var total = lines.Sum(l => PurchasingRules.LineTotal(l.Quantity, l.EstimatedUnitPrice));
        if (total <= 0) return Result<RequestDto>.Invalid("lines", "The request has no value. Enter prices before submitting.");

        var s = await settings.CurrentAsync(ct);
        r.Total = total;
        r.RequiredLevel = PurchasingRules.RequiredLevel(total, s.Level1Limit, s.Level2Limit);
        r.Status = RequestStatus.Submitted;
        r.SubmittedAt = clock.GetUtcNow();
        outbox.Enqueue(PurchasingMessages.Event, new PurchasingEventMessage(id, PurchasingEvent.Submitted, me));
        audit.Record("purchaserequest.submitted", nameof(PurchaseRequest), id.ToString(), JsonSerializer.Serialize(new { r.Number, r.Total, r.RequiredLevel }));
        return await SaveThenGetAsync(r, ct);
    }

    public async Task<Result<RequestDto>> ApproveAsync(Guid id, DecisionRequest d, CancellationToken ct = default)
    {
        var (r, failure) = await LoadForDecisionAsync(id, ct);
        if (failure is not null) return failure;
        r!.Status = RequestStatus.Approved;
        Decide(r, d.Note);
        outbox.Enqueue(PurchasingMessages.Event, new PurchasingEventMessage(id, PurchasingEvent.Approved, user.UserId));
        audit.Record("purchaserequest.approved", nameof(PurchaseRequest), id.ToString(), JsonSerializer.Serialize(new { r.Number, r.Total, r.RequiredLevel, note = r.DecisionNote }));
        return await SaveThenGetAsync(r, ct);
    }

    public async Task<Result<RequestDto>> RejectAsync(Guid id, DecisionRequest d, CancellationToken ct = default)
    {
        if (Common.Clean(d.Note) is not { Length: <= 500 }) return Result<RequestDto>.Invalid("note", "Say why it is rejected (max 500 characters).");
        var (r, failure) = await LoadForDecisionAsync(id, ct);
        if (failure is not null) return failure;
        r!.Status = RequestStatus.Rejected;
        Decide(r, d.Note);
        outbox.Enqueue(PurchasingMessages.Event, new PurchasingEventMessage(id, PurchasingEvent.Rejected, user.UserId));
        audit.Record("purchaserequest.rejected", nameof(PurchaseRequest), id.ToString(), JsonSerializer.Serialize(new { r.Number, note = r.DecisionNote }));
        return await SaveThenGetAsync(r, ct);
    }

    public async Task<Result<RequestDto>> CancelAsync(Guid id, DecisionRequest d, CancellationToken ct = default)
    {
        if (user.UserId is not { } me) return Result<RequestDto>.Forbidden("Sign in first.");
        var perms = await permissions.GetAsync(ct);
        var r = await db.Requests.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null || (r.RequestedBy != me && !perms.Contains(Permissions.PurchasingManage))) return Result<RequestDto>.NotFound();
        if (r.Status is not (RequestStatus.Draft or RequestStatus.Submitted or RequestStatus.Approved)) return Result<RequestDto>.Conflict("This request can no longer be cancelled.");
        var note = Common.Clean(d.Note);
        if (r.Status != RequestStatus.Draft && (note is null || note.Length > 500)) return Result<RequestDto>.Invalid("note", "Say why it is cancelled (max 500 characters).");
        r.Status = RequestStatus.Cancelled;
        r.DecisionNote = note;
        r.DecidedBy = me;
        r.DecidedAt = clock.GetUtcNow();
        audit.Record("purchaserequest.cancelled", nameof(PurchaseRequest), id.ToString(), JsonSerializer.Serialize(new { r.Number, note }));
        return await SaveThenGetAsync(r, ct);
    }

    private async Task<(PurchaseRequest? Request, Result<RequestDto>? Failure)> LoadForDecisionAsync(Guid id, CancellationToken ct)
    {
        if (user.UserId is not { } me) return (null, Result<RequestDto>.Forbidden("Sign in first."));
        var perms = await permissions.GetAsync(ct);
        var r = await db.Requests.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return (null, Result<RequestDto>.NotFound());
        if (r.Status != RequestStatus.Submitted) return (null, Result<RequestDto>.Conflict("This request is not waiting for a decision."));
        if (r.RequestedBy == me) return (null, Result<RequestDto>.Forbidden("Nobody decides on their own request."));
        var held = PurchasingRules.LevelHeld(perms);
        if (!PurchasingRules.CanApprove(held, r.RequiredLevel, me, r.RequestedBy)) return (null, Result<RequestDto>.Forbidden($"This request needs approval at level {r.RequiredLevel}; yours is {held}."));
        return (r, null);
    }

    private void Decide(PurchaseRequest r, string? note)
    {
        r.DecidedBy = user.UserId;
        r.DecidedAt = clock.GetUtcNow();
        r.DecisionNote = Common.Clean(note);
    }

    private async Task<Result<RequestDto>> SaveThenGetAsync(PurchaseRequest r, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<RequestDto>.Conflict("Someone else changed this request first. Reload to see where it stands.");
        }

        return await GetAsync(r.Id, ct);
    }

    // ---------------- shaping ----------------

    private async Task<IReadOnlyList<RequestDto>> ToDtosAsync(List<PurchaseRequest> rows, bool withLines, IReadOnlySet<string> perms, CancellationToken ct)
    {
        var ids = rows.Select(r => r.Id).ToList();
        var people = await users.GetAsync(rows.SelectMany(r => new[] { (Guid?)r.RequestedBy, r.DecidedBy }).OfType<Guid>(), ct);
        string? Name(Guid? g) => g is { } x && people.TryGetValue(x, out var p) ? p.DisplayName ?? p.Email : null;
        IReadOnlyDictionary<Guid, VendorRef> vendorNames = vendors is null ? new Dictionary<Guid, VendorRef>() : await vendors.VendorsAsync(rows.Where(r => r.PreferredVendorId != null).Select(r => r.PreferredVendorId!.Value), ct);
        var orders = await db.Orders.AsNoTracking().Where(o => ids.Contains(o.RequestId) && o.Status != OrderStatus.Cancelled).ToDictionaryAsync(o => o.RequestId, o => new { o.Id, o.Number }, ct);
        var lineRows = withLines ? await db.RequestLines.AsNoTracking().Where(l => ids.Contains(l.RequestId)).OrderBy(l => l.Position).ToListAsync(ct) : [];
        IReadOnlyDictionary<Guid, ItemRef> items = inventory is null || lineRows.Count == 0 ? new Dictionary<Guid, ItemRef>() : await inventory.ItemsAsync(lineRows.Where(l => l.ItemId != null).Select(l => l.ItemId!.Value), ct);
        IReadOnlyDictionary<Guid, StoreRef> stores = inventory is null || lineRows.Count == 0 ? new Dictionary<Guid, StoreRef>() : await inventory.StoresAsync(lineRows.Where(l => l.StoreId != null).Select(l => l.StoreId!.Value), ct);
        var held = PurchasingRules.LevelHeld(perms);

        return rows.Select(r => new RequestDto(
            r.Id, r.Number, PurchasingRules.DisplayRequest(r.Number), r.Title, r.Justification, r.Status, r.RequestedBy, Name(r.RequestedBy), r.RequestedAt, r.PreferredVendorId,
            r.PreferredVendorId is { } v ? vendorNames.GetValueOrDefault(v)?.Name : null, r.WorkOrderId, r.Total, r.RequiredLevel, r.SubmittedAt, Name(r.DecidedBy), r.DecidedAt, r.DecisionNote,
            orders.TryGetValue(r.Id, out var o) ? o.Id : null, o is null ? null : PurchasingRules.DisplayOrder(o.Number),
            r.Status == RequestStatus.Submitted && user.UserId is { } me && PurchasingRules.CanApprove(held, r.RequiredLevel, me, r.RequestedBy),
            withLines ? lineRows.Where(l => l.RequestId == r.Id).Select(l => new RequestLineDto(l.Id, l.Position, l.ItemId, l.ItemId is { } i ? items.GetValueOrDefault(i)?.Code : null, l.StoreId,
                l.StoreId is { } s ? stores.GetValueOrDefault(s)?.Name : null, l.Description, l.Unit, l.Quantity, l.EstimatedUnitPrice, PurchasingRules.LineTotal(l.Quantity, l.EstimatedUnitPrice))).ToList() : null,
            Common.Version(r.RowVersion))).ToList();
    }
}
