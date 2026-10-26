using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Integration;
using Fms.BuildingBlocks.Messaging;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.BuildingBlocks.Tenancy;
using Fms.Purchasing.Domain;
using Fms.Purchasing.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Purchasing.Services;

/// <summary>
/// Purchase orders: made from an approved request, sent to a vendor, acknowledged, and received against. Receiving puts stock into Inventory through the outbox,
/// once per receipt. Orders are for people with purchasing.read; ordering, receiving and cancelling have their own permissions.
/// </summary>
public sealed class OrderService(
    PurchasingDbContext db, IAuditPort audit, ICurrentUser user, IUserDirectory users, SettingsService settings, IOutbox outbox, ITenantContext tenant, TimeProvider clock,
    IInventoryCatalog? inventory = null, IVendorLookup? vendors = null)
{
    // ---------------- reading ----------------

    public async Task<Page<OrderDto>> ListAsync(OrderStatus? status, Guid? vendorId, string? q, int? page, int? pageSize, CancellationToken ct = default)
    {
        var (number, size) = Page<OrderDto>.Normalize(page, pageSize);
        var query = db.Orders.AsNoTracking().AsQueryable();
        if (status is { } s) query = query.Where(o => o.Status == s);
        if (vendorId is { } v) query = query.Where(o => o.VendorId == v);
        if (Common.Clean(q) is { } text) query = query.Where(o => o.VendorName.Contains(text) || o.VendorCode.Contains(text));
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(o => o.Number).Skip((number - 1) * size).Take(size).ToListAsync(ct);
        return new Page<OrderDto>(await ToDtosAsync(rows, withDetail: false, ct), total, number, size);
    }

    public async Task<Result<OrderDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var o = await db.Orders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return o is null ? Result<OrderDto>.NotFound() : Result<OrderDto>.Ok((await ToDtosAsync([o], withDetail: true, ct)).Single());
    }

    // ---------------- ordering ----------------

    public async Task<Result<OrderDto>> OrderFromRequestAsync(Guid requestId, OrderFromRequestRequest r, CancellationToken ct = default)
    {
        if (user.UserId is not { } me) return Result<OrderDto>.Forbidden("Sign in first.");
        var request = await db.Requests.FirstOrDefaultAsync(x => x.Id == requestId, ct);
        if (request is null) return Result<OrderDto>.NotFound();
        if (request.Status != RequestStatus.Approved) return Result<OrderDto>.Conflict("Only an approved request can be ordered.");
        var errors = new ValidationErrors();
        VendorRef? vendor = null;
        if (r.VendorId is not { } vid || vendors is null || (vendor = await vendors.GetVendorAsync(vid, ct)) is null) errors.Add("vendorId", "Choose a vendor.");
        else if (!vendor.CanReceiveWork) errors.Add("vendorId", "This vendor is on hold, blocked or archived and cannot be given orders.");
        if (r.Notes is { Length: > 2000 }) errors.Add("notes", "Notes are at most 2000 characters.");
        if (r.ExpectedOn is { } due && due < DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime)) errors.Add("expectedOn", "The expected date cannot be in the past.");
        var lines = await db.RequestLines.AsNoTracking().Where(l => l.RequestId == requestId).OrderBy(l => l.Position).ToListAsync(ct);
        var prices = (r.Prices ?? []).Where(p => p.LineId is not null).ToDictionary(p => p.LineId!.Value, p => p.UnitPrice);
        if (prices.Keys.Any(k => lines.All(l => l.Id != k))) errors.Add("prices", "A price is for a line that is not on the request.");
        if (prices.Values.Any(p => p is not { } x || !Common.IsPrice(x))) errors.Add("prices", "Prices must be zero or more (up to 4 decimals).");
        if (errors.Any) return errors.ToResult<OrderDto>();

        var s = await settings.CurrentAsync(ct);
        var orderLines = lines.Select(l => new PurchaseOrderLine
        {
            Position = l.Position, ItemId = l.ItemId, StoreId = l.StoreId, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity,
            UnitPrice = prices.TryGetValue(l.Id, out var p) && p is { } price ? price : l.EstimatedUnitPrice,
        }).ToList();
        var total = orderLines.Sum(l => PurchasingRules.LineTotal(l.Quantity, l.UnitPrice));
        if (!PurchasingRules.WithinTolerance(total, request.Total, s.OverrunTolerancePercent))
            return Result<OrderDto>.Invalid("prices", $"The order comes to {total}, more than the approved {request.Total} plus {s.OverrunTolerancePercent}%. Make a new request for the difference.");

        var tenantKey = tenant.TenantId?.ToString() ?? "none";
        var now = clock.GetUtcNow();
        try
        {
            var strategy = db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var req = await db.Requests.FirstAsync(x => x.Id == requestId, ct);
                if (req.Status != RequestStatus.Approved) return Result<OrderDto>.Conflict("Only an approved request can be ordered.");
                await Common.AcquireNumberLockAsync(db, "po", tenantKey, ct);
                var order = new PurchaseOrder
                {
                    Number = (await db.Orders.MaxAsync(x => (int?)x.Number, ct) ?? 0) + 1, RequestId = requestId, VendorId = vendor!.Id, VendorCode = vendor.Code, VendorName = vendor.Name,
                    IssuedBy = me, IssuedAt = now, ExpectedOn = r.ExpectedOn, CurrencyCode = s.CurrencyCode, Total = total, Notes = Common.Clean(r.Notes),
                };
                db.Orders.Add(order);
                foreach (var l in orderLines) { l.OrderId = order.Id; db.OrderLines.Add(l); }
                req.Status = RequestStatus.Ordered;
                outbox.Enqueue(PurchasingMessages.Event, new PurchasingEventMessage(requestId, PurchasingEvent.Ordered, me));
                AnnounceOrder(order, orderLines);
                audit.Record("purchaseorder.issued", nameof(PurchaseOrder), order.Id.ToString(), JsonSerializer.Serialize(new { order.Number, requestId, order.VendorId, vendor.Name, order.Total, order.CurrencyCode }));
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return await GetAsync(order.Id, ct);
            });
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException || (ex is DbUpdateException dbEx && Fms.BuildingBlocks.Persistence.SqlErrors.IsUniqueViolation(dbEx)))
        {
            db.ChangeTracker.Clear();
            return Result<OrderDto>.Conflict("This request was ordered by someone else a moment ago.");
        }
    }

    public async Task<Result<OrderDto>> AcknowledgeAsync(Guid id, CancellationToken ct = default)
    {
        var o = await db.Orders.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (o is null) return Result<OrderDto>.NotFound();
        if (o.Status != OrderStatus.Issued) return Result<OrderDto>.Conflict("Only an order that has been sent and not yet acknowledged can be acknowledged.");
        o.Status = OrderStatus.Acknowledged;
        o.AcknowledgedAt = clock.GetUtcNow();
        o.AcknowledgedBy = user.UserId;
        audit.Record("purchaseorder.acknowledged", nameof(PurchaseOrder), id.ToString());
        return await SaveThenGetAsync(o, ct);
    }

    public async Task<Result<OrderDto>> CancelAsync(Guid id, CancelOrderRequest r, CancellationToken ct = default)
    {
        var o = await db.Orders.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (o is null) return Result<OrderDto>.NotFound();
        var reason = Common.Clean(r.Reason);
        if (reason is null || reason.Length > 500) return Result<OrderDto>.Invalid("reason", "A reason is required (max 500 characters).");
        if (o.Status is not (OrderStatus.Issued or OrderStatus.Acknowledged)) return Result<OrderDto>.Conflict(o.Status == OrderStatus.Cancelled ? "This order is already cancelled." : "Goods have been received against this order, so it cannot be cancelled.");
        o.Status = OrderStatus.Cancelled;
        o.CancelledAt = clock.GetUtcNow();
        o.CancelReason = reason;
        var request = await db.Requests.FirstOrDefaultAsync(x => x.Id == o.RequestId, ct);
        if (request is { Status: RequestStatus.Ordered }) request.Status = RequestStatus.Approved; // the request can be ordered again, from another vendor perhaps
        audit.Record("purchaseorder.cancelled", nameof(PurchaseOrder), id.ToString(), JsonSerializer.Serialize(new { reason }));
        AnnounceOrder(o, await db.OrderLines.AsNoTracking().Where(l => l.OrderId == id).ToListAsync(ct));
        return await SaveThenGetAsync(o, ct);
    }

    // ---------------- receiving ----------------

    public async Task<Result<OrderDto>> ReceiveAsync(Guid id, ReceiveRequest r, CancellationToken ct = default)
    {
        var input = (r.Lines ?? []).Where(l => l.Quantity is > 0).ToList();
        var errors = new ValidationErrors();
        if (input.Count == 0) errors.Add("lines", "Enter the quantity received for at least one line.");
        if (input.Any(l => l.LineId is null || l.Quantity is not { } q || !Common.IsQuantity(q))) errors.Add("lines", "Every quantity must be more than zero (up to 4 decimals).");
        if (input.GroupBy(l => l.LineId).Any(g => g.Count() > 1)) errors.Add("lines", "A line is listed twice.");
        if (r.DeliveryNote is { Length: > 100 }) errors.Add("deliveryNote", "The delivery note is at most 100 characters.");
        if (errors.Any) return errors.ToResult<OrderDto>();

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                return await ReceiveOnceAsync(id, r, input, ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();
                if (attempt < 5) await Task.Delay(Random.Shared.Next(5, 40 * attempt), ct);
            }
        }

        return Result<OrderDto>.Conflict("Someone else received goods on this order at the same moment. Reload and try again.");
    }

    private async Task<Result<OrderDto>> ReceiveOnceAsync(Guid id, ReceiveRequest r, List<ReceiveLineInput> input, CancellationToken ct)
    {
        var order = await db.Orders.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (order is null) return Result<OrderDto>.NotFound();
        if (order.Status is OrderStatus.Cancelled or OrderStatus.Received) return Result<OrderDto>.Conflict(order.Status == OrderStatus.Cancelled ? "This order is cancelled." : "Everything on this order has been received.");
        var lines = await db.OrderLines.Where(l => l.OrderId == id).ToListAsync(ct);
        foreach (var l in input)
        {
            var line = lines.FirstOrDefault(x => x.Id == l.LineId);
            if (line is null) return Result<OrderDto>.Invalid("lines", "A line is not on this order.");
            if (l.Quantity!.Value > line.Quantity - line.ReceivedQty) return Result<OrderDto>.Invalid("lines", $"Line {line.Position}: only {line.Quantity - line.ReceivedQty} {line.Unit} still to come.");
        }

        var now = clock.GetUtcNow();
        var note = Common.Clean(r.DeliveryNote);
        foreach (var l in input)
        {
            var line = lines.First(x => x.Id == l.LineId);
            var receipt = new GoodsReceipt { OrderId = id, LineId = line.Id, Quantity = l.Quantity!.Value, ReceivedAt = now, ReceivedBy = user.UserId, DeliveryNote = note };
            db.Receipts.Add(receipt);
            line.ReceivedQty += l.Quantity.Value;
            AnnounceReceipt(order, line, receipt);
            if (line.ItemId is { } item && line.StoreId is { } store)
                outbox.Enqueue(GoodsReceived.MessageType, new GoodsReceivedMessage(receipt.Id, PurchasingRules.DisplayOrder(order.Number), store, item, l.Quantity.Value, line.UnitPrice));
        }

        order.Status = PurchasingRules.StatusAfterReceiving(lines.Select(l => (l.Quantity, l.ReceivedQty)));
        db.Entry(order).Property(x => x.Notes).IsModified = true; // writes the order row, so two people receiving at once cannot both succeed on stale figures
        audit.Record("purchaseorder.received", nameof(PurchaseOrder), id.ToString(), JsonSerializer.Serialize(new { order.Number, deliveryNote = note, lines = input.Select(l => new { l.LineId, l.Quantity }), status = order.Status }));
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    // ---------------- accounting ----------------

    /// <summary>Tells the accounting integration about this order as it stands now. Sent in the same save as the change.</summary>
    private void AnnounceOrder(PurchaseOrder o, IEnumerable<PurchaseOrderLine> lines) =>
        outbox.Enqueue(ErpMessages.Export, new ErpExportMessage(ErpEntity.PurchaseOrder, o.Id, clock.GetUtcNow().UtcTicks, JsonSerializer.Serialize(new
        {
            label = $"{PurchasingRules.DisplayOrder(o.Number)} {o.VendorName}", orderId = o.Id, number = PurchasingRules.DisplayOrder(o.Number), vendorId = o.VendorId, o.VendorCode, o.VendorName,
            currency = o.CurrencyCode, o.Total, o.ExpectedOn, status = o.Status.ToString(), o.IssuedAt,
            lines = lines.OrderBy(l => l.Position).Select(l => new { l.Position, l.Description, l.Unit, l.Quantity, l.UnitPrice }),
        })));

    private void AnnounceReceipt(PurchaseOrder o, PurchaseOrderLine line, GoodsReceipt r) =>
        outbox.Enqueue(ErpMessages.Export, new ErpExportMessage(ErpEntity.GoodsReceipt, r.Id, 1, JsonSerializer.Serialize(new
        {
            label = $"{PurchasingRules.DisplayOrder(o.Number)} line {line.Position}", receiptId = r.Id, orderId = o.Id, orderNumber = PurchasingRules.DisplayOrder(o.Number), vendorId = o.VendorId,
            line = line.Position, description = line.Description, r.Quantity, r.ReceivedAt, r.DeliveryNote,
        })));

    // ---------------- shaping ----------------

    private async Task<Result<OrderDto>> SaveThenGetAsync(PurchaseOrder o, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<OrderDto>.Conflict("Someone else changed this order first. Reload to see where it stands.");
        }

        return await GetAsync(o.Id, ct);
    }

    private async Task<IReadOnlyList<OrderDto>> ToDtosAsync(List<PurchaseOrder> rows, bool withDetail, CancellationToken ct)
    {
        var ids = rows.Select(r => r.Id).ToList();
        var requestIds = rows.Select(r => r.RequestId).ToList();
        var requestNumbers = await db.Requests.AsNoTracking().Where(r => requestIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Number, ct);
        var people = await users.GetAsync(rows.Select(r => (Guid?)r.IssuedBy).Concat(rows.Select(r => r.AcknowledgedBy)).OfType<Guid>(), ct);
        var lines = withDetail ? await db.OrderLines.AsNoTracking().Where(l => ids.Contains(l.OrderId)).OrderBy(l => l.Position).ToListAsync(ct) : [];
        var receipts = withDetail ? await db.Receipts.AsNoTracking().Where(x => ids.Contains(x.OrderId)).OrderBy(x => x.ReceivedAt).ToListAsync(ct) : [];
        var receivers = await users.GetAsync(receipts.Select(x => x.ReceivedBy).OfType<Guid>(), ct);
        IReadOnlyDictionary<Guid, ItemRef> items = inventory is null || lines.Count == 0 ? new Dictionary<Guid, ItemRef>() : await inventory.ItemsAsync(lines.Where(l => l.ItemId != null).Select(l => l.ItemId!.Value), ct);
        IReadOnlyDictionary<Guid, StoreRef> stores = inventory is null || lines.Count == 0 ? new Dictionary<Guid, StoreRef>() : await inventory.StoresAsync(lines.Where(l => l.StoreId != null).Select(l => l.StoreId!.Value), ct);
        string? Name(IReadOnlyDictionary<Guid, DirectoryUser> d, Guid? g) => g is { } x && d.TryGetValue(x, out var p) ? p.DisplayName ?? p.Email : null;

        return rows.Select(o => new OrderDto(
            o.Id, o.Number, PurchasingRules.DisplayOrder(o.Number), o.RequestId, requestNumbers.TryGetValue(o.RequestId, out var rn) ? PurchasingRules.DisplayRequest(rn) : null, o.VendorId, o.VendorCode, o.VendorName,
            o.Status, Name(people, o.IssuedBy), o.IssuedAt, o.ExpectedOn, o.CurrencyCode, o.Total, o.Notes, o.AcknowledgedAt, o.CancelledAt, o.CancelReason, o.ErpReference,
            withDetail ? lines.Where(l => l.OrderId == o.Id).Select(l => new OrderLineDto(l.Id, l.Position, l.ItemId, l.ItemId is { } i ? items.GetValueOrDefault(i)?.Code : null, l.StoreId,
                l.StoreId is { } s ? stores.GetValueOrDefault(s)?.Name : null, l.Description, l.Unit, l.Quantity, l.UnitPrice, PurchasingRules.LineTotal(l.Quantity, l.UnitPrice), l.ReceivedQty, l.Quantity - l.ReceivedQty)).ToList() : null,
            withDetail ? receipts.Where(x => x.OrderId == o.Id).Select(x => new ReceiptDto(x.Id, x.LineId, x.Quantity, x.ReceivedAt, Name(receivers, x.ReceivedBy), x.DeliveryNote)).ToList() : null,
            Common.Version(o.RowVersion))).ToList();
    }
}
