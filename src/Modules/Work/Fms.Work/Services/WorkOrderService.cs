using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Messaging;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Work.Domain;
using Fms.Work.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Fms.Work.Services;

public sealed class WorkOrderService(
    WorkDbContext db, IAuditPort audit, ICurrentUser user, ICurrentPermissions permissions, IUserDirectory users, IFacilityLookup facilities,
    SlaClock sla, IOutbox outbox, TimeProvider clock)
{
    private Actor? _actor;
    private WorkflowSettings? _settings;

    private async Task<Actor> ActorAsync(CancellationToken ct) => _actor ??= new Actor(user.UserId, await permissions.GetAsync(ct));

    public async Task<WorkflowSettings> SettingsAsync(CancellationToken ct = default) =>
        _settings ??= await db.Settings.AsNoTracking().Select(s => new WorkflowSettings(s.RequireApproval, s.RequireVerification)).FirstOrDefaultAsync(ct) ?? new WorkflowSettings();

    /// <summary>Everyone with workorders.read sees every work order; everyone else sees only what they raised themselves.</summary>
    private async Task<IQueryable<WorkOrder>> VisibleAsync(bool tracking, CancellationToken ct)
    {
        var actor = await ActorAsync(ct);
        var all = tracking ? db.WorkOrders.AsQueryable() : db.WorkOrders.AsNoTracking();
        if (actor.Has(Permissions.WorkOrdersRead)) return all;
        var me = actor.UserId;
        return all.Where(w => w.RequestedBy == me);
    }

    public async Task<Page<WorkOrderDto>> ListAsync(WorkOrderQuery q, CancellationToken ct = default)
    {
        var (number, size) = Page<WorkOrderDto>.Normalize(q.Page, q.PageSize);
        var actor = await ActorAsync(ct);
        var query = await VisibleAsync(false, ct);
        var me = actor.UserId;
        var now = clock.GetUtcNow();

        if (q.Statuses is { Count: > 0 } statuses) query = query.Where(w => statuses.Contains(w.Status));
        if (q.OpenOnly) query = query.Where(w => w.Status != WorkOrderStatus.Closed && w.Status != WorkOrderStatus.Cancelled);
        if (q.Priority is { } priority) query = query.Where(w => w.Priority == priority);
        if (q.AssetId is { } assetId) query = query.Where(w => w.AssetId == assetId);
        if (q.LocationId is { } locationId) query = query.Where(w => w.LocationId == locationId);
        if (q.CategoryId is { } categoryId) query = query.Where(w => w.CategoryId == categoryId);
        if (q.Mine) query = query.Where(w => w.RequestedBy == me);
        if (q.AssignedToMe) query = query.Where(w => w.AssignedTo == me);
        if (q.OverdueOnly)
            query = query.Where(w => w.DueAt != null && w.DueAt < now && w.Status != WorkOrderStatus.Closed && w.Status != WorkOrderStatus.Cancelled
                                     && w.Status != WorkOrderStatus.Completed && w.Status != WorkOrderStatus.Verified);

        if (q.SlaBreached)
            query = query.Where(w => w.CompletedAt == null && w.Status != WorkOrderStatus.Closed && w.Status != WorkOrderStatus.Cancelled
                                     && ((w.SlaPausedAt == null && w.ResolutionDueAt != null && w.ResolutionDueAt < now)
                                         || (w.RespondedAt == null && w.ResponseDueAt != null && w.ResponseDueAt < now)));

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var term = q.Search.Trim();
            var numeric = int.TryParse(term.Replace("WO-", "", StringComparison.OrdinalIgnoreCase), out var n) ? n : -1;
            query = query.Where(w => w.Title.Contains(term) || (w.Description != null && w.Description.Contains(term)) || w.Number == numeric);
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(w => w.Number).Skip((number - 1) * size).Take(size).ToListAsync(ct);
        return new Page<WorkOrderDto>(await ToDtosAsync(items, ct), total, number, size);
    }

    public async Task<Result<WorkOrderDetailDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var wo = await (await VisibleAsync(false, ct)).FirstOrDefaultAsync(w => w.Id == id, ct);
        if (wo is null) return Result<WorkOrderDetailDto>.NotFound();

        var actor = await ActorAsync(ct);
        var events = await db.WorkOrderEvents.AsNoTracking().Where(e => e.WorkOrderId == id).OrderBy(e => e.At).ThenBy(e => e.Action).ToListAsync(ct);
        var comments = db.WorkOrderComments.AsNoTracking().Where(c => c.WorkOrderId == id);
        if (!actor.Has(Permissions.WorkOrdersRead)) comments = comments.Where(c => !c.IsInternal);
        var commentList = await comments.OrderBy(c => c.At).ToListAsync(ct);

        var names = await users.GetAsync(events.Select(e => e.ActorId).Concat(events.Select(e => e.SubjectId)).Concat(commentList.Select(c => (Guid?)c.AuthorId))
            .Where(g => g.HasValue).Select(g => g!.Value), ct);
        string? Name(Guid? g) => g is { } k && names.TryGetValue(k, out var u) ? u.DisplayName ?? u.Email : null;

        return Result<WorkOrderDetailDto>.Ok(new WorkOrderDetailDto(
            (await ToDtosAsync([wo], ct))[0],
            events.Select(e => new WorkOrderEventDto(e.Id, e.Action, e.FromStatus, e.ToStatus, e.ActorId, Name(e.ActorId), e.At, e.Note, e.SubjectId, Name(e.SubjectId))).ToList(),
            commentList.Select(c => new WorkOrderCommentDto(c.Id, c.AuthorId, Name(c.AuthorId), c.At, c.Body, c.IsInternal)).ToList()));
    }

    public async Task<Result<WorkOrderDto>> CreateAsync(CreateWorkOrderRequest r, CancellationToken ct = default)
    {
        var actor = await ActorAsync(ct);
        if (actor.UserId is not { } me) return Result<WorkOrderDto>.Forbidden("Sign in to raise a request.");

        var errors = new ValidationErrors();
        var title = r.Title?.Trim() ?? "";
        if (title.Length is 0 or > 200) errors.Add("title", "Title is required (max 200 characters).");
        if (r.Description is { Length: > 4000 }) errors.Add("description", "Description is at most 4000 characters.");
        var type = r.Type ?? WorkOrderType.Corrective;
        if (!Enum.IsDefined(type)) errors.Add("type", "Unknown type.");
        if (r.Priority is { } p0 && !Enum.IsDefined(p0)) errors.Add("priority", "Unknown priority.");
        // Emergencies skip approval and planned work is scheduled work: both are for people who dispatch, not for anyone who can report a fault.
        if (type != WorkOrderType.Corrective && !actor.Has(Permissions.WorkOrdersAssign) && !actor.Has(Permissions.WorkOrdersManage))
            errors.Add("type", "Only dispatchers can raise emergency or planned work.");

        Guid? locationId = r.LocationId;
        string? assetName = null;
        if (r.AssetId is { } assetId)
        {
            var asset = await facilities.GetAssetAsync(assetId, ct);
            if (asset is null) errors.Add("assetId", "Asset not found.");
            else
            {
                locationId ??= asset.LocationId;
                assetName = asset.Name;
            }
        }

        if (locationId is null) errors.Add("locationId", "Say where the problem is (a location, or an asset that has one).");
        else if (await facilities.GetLocationAsync(locationId.Value, ct) is null) errors.Add("locationId", "Location not found.");

        WorkCategory? category = null;
        if (r.CategoryId is { } categoryId)
        {
            category = await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == categoryId && !c.IsArchived, ct);
            if (category is null) errors.Add("categoryId", "Category not found.");
        }

        if (errors.Any) return errors.ToResult<WorkOrderDto>();

        var settings = await SettingsAsync(ct);
        var now = clock.GetUtcNow();
        var status = WorkOrderRules.InitialStatus(type, settings);
        var tenantKey = user.TenantId?.ToString() ?? "none";

        // Numbers are handed out one at a time per tenant (an application lock held for the transaction), so parallel requests queue briefly
        // instead of colliding. The unique index remains the backstop. The id is fixed up front so an ambiguous commit can be checked.
        var id = Guid.NewGuid();
        var strategy = db.Database.CreateExecutionStrategy();
        var saved = await strategy.ExecuteAsync(
            0,
            async (_, _, token) =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(token);
                var lockResult = await db.Database
                    .SqlQuery<int>($"DECLARE @r int; EXEC @r = sp_getapplock @Resource = {"fms.wo-number:" + tenantKey}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000; SELECT @r AS Value")
                    .ToListAsync(token);
                if (lockResult.Count != 1 || lockResult[0] < 0) throw new InvalidOperationException("Could not reserve a work order number.");

                var next = (await db.WorkOrders.MaxAsync(w => (int?)w.Number, token) ?? 0) + 1;
                var wo = new WorkOrder
                {
                    Id = id, Number = next, Title = title, Description = Clean(r.Description), Type = type, Priority = r.Priority ?? category?.DefaultPriority ?? WorkPriority.Medium,
                    Status = status, LocationId = locationId!.Value, AssetId = r.AssetId, CategoryId = r.CategoryId, RequestedBy = me, RequestedAt = now, DueAt = r.DueAt, UpdatedAt = now,
                };
                if (status == WorkOrderStatus.Approved) await sla.StartAsync(wo, now, token);
                db.WorkOrders.Add(wo);
                db.WorkOrderEvents.Add(Event(wo, WorkOrderAction.Created, null, status, me, now, null));
                outbox.Enqueue(WorkMessages.Event, new WorkOrderEventMessage(wo.Id, WorkOrderAction.Created, me, null));
                audit.Record("workorder.created", nameof(WorkOrder), wo.Id.ToString(),
                    JsonSerializer.Serialize(new { wo.Number, wo.Title, wo.Type, wo.Priority, wo.LocationId, wo.AssetId, status }));
                await db.SaveChangesAsync(token);
                await tx.CommitAsync(token);
                return wo;
            },
            async (_, _, token) =>
            {
                db.ChangeTracker.Clear();
                var existing = await db.WorkOrders.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, token);
                return new ExecutionResult<WorkOrder>(existing is not null, existing!);
            },
            ct);

        return Result<WorkOrderDto>.Ok((await ToDtosAsync([saved], ct))[0]);
    }

    public async Task<Result<WorkOrderDto>> UpdateAsync(Guid id, UpdateWorkOrderRequest r, CancellationToken ct = default)
    {
        var wo = await (await VisibleAsync(true, ct)).FirstOrDefaultAsync(w => w.Id == id, ct);
        if (wo is null) return Result<WorkOrderDto>.NotFound();
        var actor = await ActorAsync(ct);
        var settings = await SettingsAsync(ct);
        if (!WorkOrderRules.Can(WorkOrderAction.Edited, wo, actor, settings)) return Result<WorkOrderDto>.Conflict(WhyNot(wo, WorkOrderAction.Edited));
        if (!TryVersion(r.RowVersion, out var version)) return Result<WorkOrderDto>.Invalid("rowVersion", "rowVersion is required (as returned by the last read).");

        var errors = new ValidationErrors();
        var title = r.Title?.Trim() ?? "";
        if (title.Length is 0 or > 200) errors.Add("title", "Title is required (max 200 characters).");
        if (r.Description is { Length: > 4000 }) errors.Add("description", "Description is at most 4000 characters.");
        if (r.Priority is { } p && !Enum.IsDefined(p)) errors.Add("priority", "Unknown priority.");

        var locationId = r.LocationId ?? wo.LocationId;
        if (r.AssetId is { } assetId)
        {
            var asset = await facilities.GetAssetAsync(assetId, ct);
            if (asset is null) errors.Add("assetId", "Asset not found.");
            else if (r.LocationId is null && assetId != wo.AssetId) locationId = asset.LocationId ?? locationId;
        }

        if (locationId != wo.LocationId && await facilities.GetLocationAsync(locationId, ct) is null) errors.Add("locationId", "Location not found.");
        if (r.CategoryId is { } categoryId && categoryId != wo.CategoryId && !await db.Categories.AnyAsync(c => c.Id == categoryId && !c.IsArchived, ct)) errors.Add("categoryId", "Category not found.");
        if (errors.Any) return errors.ToResult<WorkOrderDto>();

        var changes = new Dictionary<string, object?>();
        Track(changes, "title", wo.Title, title);
        Track(changes, "description", wo.Description, Clean(r.Description));
        Track(changes, "priority", wo.Priority, r.Priority ?? wo.Priority);
        Track(changes, "locationId", wo.LocationId, locationId);
        Track(changes, "assetId", wo.AssetId, r.AssetId);
        Track(changes, "categoryId", wo.CategoryId, r.CategoryId);
        Track(changes, "dueAt", wo.DueAt, r.DueAt);

        wo.Title = title;
        wo.Description = Clean(r.Description);
        var priorityChanged = r.Priority is { } newPriority && newPriority != wo.Priority;
        wo.Priority = r.Priority ?? wo.Priority;
        wo.LocationId = locationId;
        wo.AssetId = r.AssetId;
        wo.CategoryId = r.CategoryId;
        wo.DueAt = r.DueAt;
        wo.UpdatedAt = clock.GetUtcNow();
        if (priorityChanged) await sla.RecomputeAsync(wo, ct);
        db.Entry(wo).Property(w => w.RowVersion).OriginalValue = version;

        if (changes.Count > 0)
        {
            db.WorkOrderEvents.Add(Event(wo, WorkOrderAction.Edited, wo.Status, wo.Status, actor.UserId, wo.UpdatedAt, string.Join(", ", changes.Keys)));
            audit.Record("workorder.updated", nameof(WorkOrder), id.ToString(), JsonSerializer.Serialize(changes));
        }

        return await SaveAsync(wo, ct);
    }

    public Task<Result<WorkOrderDto>> ApproveAsync(Guid id, CancellationToken ct = default) =>
        ActCoreAsync(id, WorkOrderAction.Approved, null, async (wo, now, token) =>
        {
            wo.Status = WorkOrderStatus.Approved;
            await sla.StartAsync(wo, now, token); // the clocks start when the work is authorised
            return null;
        }, ct: ct);

    public Task<Result<WorkOrderDto>> RejectAsync(Guid id, string? reason, CancellationToken ct = default) =>
        ActAsync(id, WorkOrderAction.Rejected, reason, (wo, _, _) => { wo.Status = WorkOrderStatus.Cancelled; return null; }, ct: ct);

    public Task<Result<WorkOrderDto>> AssignAsync(Guid id, Guid? assigneeId, CancellationToken ct = default) =>
        ActCoreAsync(id, WorkOrderAction.Assigned, null, async (wo, _, token) =>
        {
            if (assigneeId is not { } who) return ("assigneeId", "Choose who does the work.");
            var found = (await users.WithPermissionAsync(Permissions.WorkOrdersWork, token)).FirstOrDefault(u => u.Id == who);
            if (found is null) return ("assigneeId", "That person is not active or cannot do work orders.");
            wo.AssignedTo = who;
            if (wo.Status == WorkOrderStatus.Approved) wo.Status = WorkOrderStatus.Assigned;
            return null;
        }, subject: assigneeId, ct: ct);

    public Task<Result<WorkOrderDto>> StartAsync(Guid id, CancellationToken ct = default) =>
        ActAsync(id, WorkOrderAction.Started, null, (wo, now, _) => { wo.Status = WorkOrderStatus.InProgress; wo.StartedAt ??= now; wo.RespondedAt ??= now; wo.StatusReason = null; return null; }, ct: ct);

    public Task<Result<WorkOrderDto>> HoldAsync(Guid id, string? reason, CancellationToken ct = default) =>
        ActCoreAsync(id, WorkOrderAction.Held, reason, async (wo, now, token) =>
        {
            wo.Status = WorkOrderStatus.OnHold;
            await sla.PauseAsync(wo, now, token); // time on hold does not count against the SLA
            return null;
        }, ct: ct);

    public Task<Result<WorkOrderDto>> ResumeAsync(Guid id, CancellationToken ct = default) =>
        ActCoreAsync(id, WorkOrderAction.Resumed, null, async (wo, now, token) =>
        {
            wo.Status = WorkOrderStatus.InProgress;
            wo.StatusReason = null;
            await sla.ResumeAsync(wo, now, token);
            return null;
        }, ct: ct);

    public Task<Result<WorkOrderDto>> CompleteAsync(Guid id, CompleteRequest r, CancellationToken ct = default) =>
        ActAsync(id, WorkOrderAction.Completed, r.Notes, (wo, now, _) =>
        {
            if (r.Code is not { } code || !Enum.IsDefined(code)) return ("code", "Say how it ended (closure code).");
            if (string.IsNullOrWhiteSpace(r.Notes)) return ("notes", "Describe what was done.");
            wo.Status = WorkOrderStatus.Completed;
            wo.CompletedAt = now;
            wo.CompletedBy = user.UserId;
            wo.ClosureCode = code;
            wo.ClosureNotes = r.Notes.Trim();
            return null;
        }, noteIsReason: false, ct: ct);

    public Task<Result<WorkOrderDto>> VerifyAsync(Guid id, CancellationToken ct = default) =>
        ActAsync(id, WorkOrderAction.Verified, null, (wo, now, _) => { wo.Status = WorkOrderStatus.Verified; wo.VerifiedAt = now; wo.VerifiedBy = user.UserId; return null; }, ct: ct);

    public Task<Result<WorkOrderDto>> CloseAsync(Guid id, CancellationToken ct = default) =>
        ActAsync(id, WorkOrderAction.Closed, null, (wo, now, _) => { wo.Status = WorkOrderStatus.Closed; wo.ClosedAt = now; return null; }, ct: ct);

    /// <summary>Reopens finished work. It returns to the person who did it, or to the approved queue if nobody was assigned, and must be completed and checked again.</summary>
    public Task<Result<WorkOrderDto>> ReopenAsync(Guid id, string? reason, CancellationToken ct = default) =>
        ActAsync(id, WorkOrderAction.Reopened, reason, (wo, _, _) =>
        {
            wo.Status = wo.AssignedTo is null ? WorkOrderStatus.Approved : WorkOrderStatus.Assigned;
            wo.CompletedAt = null;
            wo.CompletedBy = null;
            wo.VerifiedAt = null;
            wo.VerifiedBy = null;
            wo.ClosedAt = null;
            wo.ClosureCode = null;
            wo.ClosureNotes = null;
            return null;
        }, ct: ct);

    public Task<Result<WorkOrderDto>> CancelAsync(Guid id, string? reason, CancellationToken ct = default) =>
        ActAsync(id, WorkOrderAction.Cancelled, reason, (wo, _, _) => { wo.Status = WorkOrderStatus.Cancelled; return null; }, ct: ct);

    public async Task<Result<WorkOrderCommentDto>> AddCommentAsync(Guid id, CommentRequest r, CancellationToken ct = default)
    {
        var wo = await (await VisibleAsync(false, ct)).FirstOrDefaultAsync(w => w.Id == id, ct);
        if (wo is null) return Result<WorkOrderCommentDto>.NotFound();
        var actor = await ActorAsync(ct);
        if (actor.UserId is not { } me) return Result<WorkOrderCommentDto>.Forbidden("Sign in to comment.");

        var body = r.Body?.Trim() ?? "";
        if (body.Length is 0 or > 2000) return Result<WorkOrderCommentDto>.Invalid("body", "Write a comment (max 2000 characters).");
        if (r.IsInternal && !actor.Has(Permissions.WorkOrdersRead)) return Result<WorkOrderCommentDto>.Forbidden("Only staff can leave internal comments.");

        var comment = new WorkOrderComment { WorkOrderId = id, AuthorId = me, At = clock.GetUtcNow(), Body = body, IsInternal = r.IsInternal };
        db.WorkOrderComments.Add(comment);
        audit.Record("workorder.commented", nameof(WorkOrder), id.ToString(), JsonSerializer.Serialize(new { comment.Id, comment.IsInternal }));
        await db.SaveChangesAsync(ct);
        var name = (await users.GetAsync([me], ct)).GetValueOrDefault(me);
        return Result<WorkOrderCommentDto>.Ok(new WorkOrderCommentDto(comment.Id, me, name?.DisplayName ?? name?.Email, comment.At, comment.Body, comment.IsInternal));
    }

    public async Task<IReadOnlyList<AssetChoiceDto>> FindAssetsAsync(string? search, string? tag, CancellationToken ct = default) =>
        (await facilities.FindAssetsAsync(search, tag, 20, ct)).Select(a => new AssetChoiceDto(a.Id, a.Code, a.Name, a.LocationId)).ToList();

    public async Task<IReadOnlyList<AssigneeDto>> AssigneesAsync(CancellationToken ct = default) =>
        (await users.WithPermissionAsync(Permissions.WorkOrdersWork, ct)).Select(u => new AssigneeDto(u.Id, u.DisplayName, u.Email)).ToList();

    /// <summary>
    /// The one path every lifecycle action takes: load what the person may see, ask the rules whether they may do this now, apply it,
    /// and write the history line and the audit event in the same save.
    /// </summary>
    private async Task<Result<WorkOrderDto>> ActAsync(
        Guid id, WorkOrderAction action, string? reason, Func<WorkOrder, DateTimeOffset, CancellationToken, (string Field, string Error)?> apply,
        Guid? subject = null, bool noteIsReason = true, CancellationToken ct = default) =>
        await ActCoreAsync(id, action, reason, (wo, now, token) => Task.FromResult(apply(wo, now, token)), subject, noteIsReason, ct);

    private async Task<Result<WorkOrderDto>> ActCoreAsync(
        Guid id, WorkOrderAction action, string? reason, Func<WorkOrder, DateTimeOffset, CancellationToken, Task<(string Field, string Error)?>> apply,
        Guid? subject = null, bool noteIsReason = true, CancellationToken ct = default)
    {
        var wo = await (await VisibleAsync(true, ct)).FirstOrDefaultAsync(w => w.Id == id, ct);
        if (wo is null) return Result<WorkOrderDto>.NotFound();

        var actor = await ActorAsync(ct);
        var settings = await SettingsAsync(ct);
        if (!WorkOrderRules.Can(action, wo, actor, settings)) return Result<WorkOrderDto>.Conflict(WhyNot(wo, action));

        var cleaned = Clean(reason);
        if (cleaned is { Length: > 500 } && noteIsReason) return Result<WorkOrderDto>.Invalid("reason", "Reason is at most 500 characters.");
        if (cleaned is { Length: > 2000 }) return Result<WorkOrderDto>.Invalid("notes", "Notes are at most 2000 characters.");
        if (noteIsReason && WorkOrderRules.RequiresReason(action) && cleaned is null) return Result<WorkOrderDto>.Invalid("reason", "A reason is required.");

        var from = wo.Status;
        var now = clock.GetUtcNow();
        if (await apply(wo, now, ct) is { } problem) return Result<WorkOrderDto>.Invalid(problem.Field, problem.Error);

        if (noteIsReason && cleaned is not null && action is not WorkOrderAction.Assigned) wo.StatusReason = cleaned;
        wo.UpdatedAt = now;
        db.WorkOrderEvents.Add(Event(wo, action, from, wo.Status, actor.UserId, now, cleaned, subject));
        outbox.Enqueue(WorkMessages.Event, new WorkOrderEventMessage(id, action, actor.UserId, cleaned));
        audit.Record("workorder." + action.ToString().ToLowerInvariant(), nameof(WorkOrder), id.ToString(),
            JsonSerializer.Serialize(new { from, to = wo.Status, reason = cleaned, subject }));
        return await SaveAsync(wo, ct);
    }

    private static string WhyNot(WorkOrder wo, WorkOrderAction action) =>
        $"{action} is not possible on a work order that is {wo.Status}, or you are not allowed to do it.";

    private async Task<Result<WorkOrderDto>> SaveAsync(WorkOrder wo, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return Result<WorkOrderDto>.Ok((await ToDtosAsync([wo], ct))[0]);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<WorkOrderDto>.Conflict("The work order was changed by someone else. Reload it and try again.");
        }
    }

    private static WorkOrderEvent Event(WorkOrder wo, WorkOrderAction action, WorkOrderStatus? from, WorkOrderStatus to, Guid? actor, DateTimeOffset at, string? note, Guid? subject = null) =>
        new() { WorkOrderId = wo.Id, Action = action, FromStatus = from, ToStatus = to, ActorId = actor, At = at, Note = note, SubjectId = subject };

    private async Task<IReadOnlyList<WorkOrderDto>> ToDtosAsync(IReadOnlyList<WorkOrder> orders, CancellationToken ct)
    {
        if (orders.Count == 0) return [];
        var actor = await ActorAsync(ct);
        var settings = await SettingsAsync(ct);
        var now = clock.GetUtcNow();

        var locations = await facilities.LocationsAsync(orders.Select(o => o.LocationId), ct);
        var assets = await facilities.AssetsAsync(orders.Where(o => o.AssetId is not null).Select(o => o.AssetId!.Value), ct);
        var categoryIds = orders.Where(o => o.CategoryId is not null).Select(o => o.CategoryId!.Value).Distinct().ToList();
        var categories = await db.Categories.AsNoTracking().Where(c => categoryIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var people = await users.GetAsync(orders.Select(o => (Guid?)o.RequestedBy).Concat(orders.Select(o => o.AssignedTo)).Where(g => g.HasValue).Select(g => g!.Value), ct);
        string? Person(Guid? g) => g is { } k && people.TryGetValue(k, out var u) ? u.DisplayName ?? u.Email : null;

        return orders.Select(o => new WorkOrderDto(
            o.Id, o.Number, WorkOrderRules.DisplayNumber(o.Number), o.Title, o.Description, o.Type, o.Priority, o.Status,
            o.LocationId, locations.GetValueOrDefault(o.LocationId)?.Name, o.AssetId, o.AssetId is { } a ? assets.GetValueOrDefault(a) is { } ar ? $"{ar.Code} · {ar.Name}" : null : null,
            o.CategoryId, o.CategoryId is { } c ? categories.GetValueOrDefault(c) : null,
            o.RequestedBy, Person(o.RequestedBy), o.RequestedAt, o.AssignedTo, Person(o.AssignedTo),
            o.DueAt, WorkOrderRules.IsOverdue(o, now), o.StartedAt, o.CompletedAt, o.VerifiedAt, o.ClosedAt,
            o.StatusReason, o.ClosureCode, o.ClosureNotes, WorkOrderRules.Allowed(o, actor, settings), Convert.ToBase64String(o.RowVersion), ToSla(o, now))).ToList();
    }

    private static SlaDto ToSla(WorkOrder o, DateTimeOffset now)
    {
        var status = SlaRules.StatusOf(o, now);
        return new SlaDto(status.State, o.ResponseDueAt, o.ResolutionDueAt, o.RespondedAt, status.ResponseBreached, o.SlaPausedAt is not null);
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
