using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Notifications;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.BuildingBlocks.Tenancy;
using Fms.Compliance.Domain;
using Fms.Compliance.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fms.Compliance.Services;

/// <summary>
/// Permit to work. The lifecycle is fixed: Requested, then Approved (or Rejected), Issued, Started, Closed; Cancelled before it starts.
/// People are kept apart: the approver and the issuer are never the requester, and the person who starts the work is never the one who issued the permit.
/// A permit lasts no longer than its type allows, an exclusive permit stands alone in its place, and closing needs a statement that the area is safe.
/// </summary>
public sealed class PermitService(
    ComplianceDbContext db, IFacilityLookup facilities, IWorkOrderLookup workOrders, IUserDirectory users, INotificationSink notices, ICurrentUser user, ICurrentPermissions permissions, ITenantContext tenant,
    IAuditPort audit, TimeProvider clock, ILogger<PermitService> log)
{
    public const int MaxListed = 500;
    public const int MaxWindowDaysAhead = 90;

    public async Task<Page<PermitDto>> ListAsync(PermitStatus? status, Guid? locationId, bool? overdue, bool mine, string? q, int? page, int? pageSize, CancellationToken ct = default)
    {
        var query = db.Permits.AsNoTracking().AsQueryable();
        if (status is { } s) query = query.Where(p => p.Status == s);
        if (locationId is { } l) query = query.Where(p => p.LocationId == l);
        if (mine && user.UserId is { } me) query = query.Where(p => p.RequestedBy == me);
        if (Common.Clean(q) is { } text) query = query.Where(p => p.TypeName.Contains(text) || p.LocationName.Contains(text) || p.Description.Contains(text) || (p.ContractorName != null && p.ContractorName.Contains(text)));
        var now = clock.GetUtcNow();
        if (overdue == true) query = query.Where(p => (p.Status == PermitStatus.Issued || p.Status == PermitStatus.Started) && p.PlannedEnd < now);
        var total = await query.CountAsync(ct);
        var (number, size) = Page<PermitDto>.Normalize(page, pageSize);
        var rows = await query.OrderByDescending(p => p.Number).Skip((number - 1) * size).Take(size).ToListAsync(ct);
        return new Page<PermitDto>(rows.Select(p => ToDto(p, now)).ToList(), total, number, size);
    }

    public async Task<Result<PermitDetailDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var p = await db.Permits.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return Result<PermitDetailDto>.NotFound();
        var events = await db.PermitEvents.AsNoTracking().Where(e => e.PermitId == id).OrderBy(e => e.At).ToListAsync(ct);
        return Result<PermitDetailDto>.Ok(new PermitDetailDto(ToDto(p, clock.GetUtcNow()), events.Select(e => new PermitEventDto(e.At, e.ByName, e.Action, e.Note)).ToList()));
    }

    public async Task<Result<PermitDto>> RequestAsync(RequestPermitRequest r, CancellationToken ct = default)
    {
        if (user.UserId is not { } me) return Result<PermitDto>.Forbidden("Sign in to ask for a permit.");
        var now = clock.GetUtcNow();
        var errors = new ValidationErrors();
        PermitType? type = null;
        if (r.TypeId is not { } typeId || (type = await db.PermitTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == typeId && !t.IsArchived, ct)) is null) errors.Add("typeId", "Choose a permit type.");
        FacilityRef? location = null;
        if (r.LocationId is not { } locationId || (location = await facilities.GetLocationAsync(locationId, ct)) is null) errors.Add("locationId", "Choose the place the work is in.");
        else if (!location.Active) errors.Add("locationId", "That place is archived.");
        var description = Common.Clean(r.Description);
        if (description is null || description.Length > 1000) errors.Add("description", "Describe the work (at most 1000 characters).");
        var contractor = Common.Clean(r.ContractorName);
        if (contractor is { Length: > 200 }) errors.Add("contractorName", "At most 200 characters.");
        if (r.PlannedStart is not { } start || r.PlannedEnd is not { } end) errors.Add("plannedStart", "Say when the work starts and ends.");
        else
        {
            if (end <= start) errors.Add("plannedEnd", "The end must be after the start.");
            else if (end <= now) errors.Add("plannedEnd", "The work cannot end in the past.");
            else if (start > now.AddDays(MaxWindowDaysAhead)) errors.Add("plannedStart", $"Ask for permits at most {MaxWindowDaysAhead} days ahead.");
            else if (type is not null && end - start > TimeSpan.FromHours(type.MaxDurationHours)) errors.Add("plannedEnd", $"This kind of permit lasts at most {type.MaxDurationHours} hours. Ask for a new one when it ends.");
        }

        WorkOrderRef? workOrder = null;
        if (r.WorkOrderId is { } woId && (workOrder = await workOrders.GetAsync(woId, ct)) is null) errors.Add("workOrderId", "That work order does not exist.");
        if (errors.Any) return errors.ToResult<PermitDto>();

        var requester = await Common.NameOfAsync(users, me, ct);
        var precautions = (JsonSerializer.Deserialize<List<string>>(type!.PrecautionsJson) ?? []).Select(t => new PermitPrecautionDto(t, false)).ToList();
        var permit = new Permit
        {
            TypeId = type.Id, TypeCode = type.Code, TypeName = type.Name, TypeExclusive = type.Exclusive, TypeRequiresIsolation = type.RequiresIsolation,
            WorkOrderId = workOrder?.Id, WorkOrderNumber = workOrder?.Number, LocationId = location!.Id, LocationName = location.Name, Description = description!, ContractorName = contractor,
            PlannedStart = r.PlannedStart!.Value, PlannedEnd = r.PlannedEnd!.Value, PrecautionsJson = JsonSerializer.Serialize(precautions),
            RequestedBy = me, RequestedByName = requester, RequestedAt = now,
        };

        var tenantKey = tenant.RequireTenantId().ToString("N");
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            db.Permits.Add(permit);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await Common.LockAsync(db, "permit-number:" + tenantKey, ct);
            permit.Number = (await db.Permits.MaxAsync(x => (int?)x.Number, ct) ?? 0) + 1;
            AddEvent(permit, "requested", requester, null);
            audit.Record("permit.requested", nameof(Permit), permit.Id.ToString(), JsonSerializer.Serialize(new { permit.Number, permit.TypeCode, permit.LocationId, permit.PlannedStart, permit.PlannedEnd }));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });

        await NotifyAsync(permit, await users.WithPermissionAsync(Permissions.PermitsApprove, ct), "permit.requested", $"Permit PTW-{permit.Number:D4} needs approval", $"{permit.RequestedByName} asked for a {permit.TypeName} permit at {permit.LocationName}.\n\nطلب {permit.RequestedByName} تصريح عمل ({permit.TypeName}) في {permit.LocationName}.", exclude: me, ct);
        return Result<PermitDto>.Ok(ToDto(permit, now));
    }

    public Task<Result<PermitDto>> ApproveAsync(Guid id, PermitDecisionRequest r, CancellationToken ct = default) =>
        Transition(id, r.RowVersion, ct, (p, me, name, now) =>
        {
            if (p.Status != PermitStatus.Requested) return Task.FromResult<Failure?>(Fail("Only a requested permit can be approved."));
            if (p.RequestedBy == me) return Task.FromResult<Failure?>(Fail("You cannot approve your own request.", forbidden: true));
            if (p.PlannedEnd <= now) return Task.FromResult<Failure?>(Fail("The time asked for has already passed. Ask for a new permit."));
            p.Status = PermitStatus.Approved;
            p.ApprovedBy = me;
            p.ApprovedByName = name;
            p.ApprovedAt = now;
            AddEvent(p, "approved", name, Common.Clean(r.Reason));
            return Task.FromResult<Failure?>(null);
        }, notify: async p => await NotifyAsync(p, await users.WithPermissionAsync(Permissions.PermitsIssue, ct), "permit.approved", $"Permit PTW-{p.Number:D4} is ready to issue", $"{p.TypeName} at {p.LocationName} was approved.\n\nتمت الموافقة على تصريح العمل ({p.TypeName}) في {p.LocationName}.", exclude: p.RequestedBy, ct));

    public Task<Result<PermitDto>> RejectAsync(Guid id, PermitDecisionRequest r, CancellationToken ct = default) =>
        Transition(id, r.RowVersion, ct, (p, me, name, now) =>
        {
            if (p.Status != PermitStatus.Requested) return Task.FromResult<Failure?>(Fail("Only a requested permit can be rejected."));
            if (p.RequestedBy == me) return Task.FromResult<Failure?>(Fail("You cannot decide your own request. Cancel it instead.", forbidden: true));
            var reason = Common.Clean(r.Reason);
            if (reason is null || reason.Length > 500) return Task.FromResult<Failure?>(Fail("A reason is required (at most 500 characters).", field: "reason"));
            p.Status = PermitStatus.Rejected;
            p.RejectReason = reason;
            AddEvent(p, "rejected", name, reason);
            return Task.FromResult<Failure?>(null);
        }, notify: p => NotifyAsync(p, [new DirectoryUser(p.RequestedBy, null, null, true)], "permit.rejected", $"Permit PTW-{p.Number:D4} was rejected", $"Reason: {p.RejectReason}\n\nالسبب: {p.RejectReason}", exclude: null, ct));

    public Task<Result<PermitDto>> IssueAsync(Guid id, IssuePermitRequest r, CancellationToken ct = default) =>
        Transition(id, r.RowVersion, ct, async (p, me, name, now) =>
        {
            if (p.Status != PermitStatus.Approved) return Fail("Only an approved permit can be issued.");
            if (p.RequestedBy == me) return Fail("You cannot issue a permit you asked for.", forbidden: true);
            if (p.PlannedEnd <= now) return Fail("The time asked for has passed. Ask for a new permit.");

            var precautions = JsonSerializer.Deserialize<List<PermitPrecautionDto>>(p.PrecautionsJson) ?? [];
            var confirmed = new HashSet<string>(r.ConfirmedPrecautions ?? [], StringComparer.OrdinalIgnoreCase);
            var missing = precautions.Where(x => !confirmed.Contains(x.Text)).Select(x => x.Text).ToList();
            if (missing.Count > 0) return Fail("Every precaution must be confirmed before the permit is issued: " + string.Join("; ", missing), field: "confirmedPrecautions");
            var isolation = Common.Clean(r.IsolationReference);
            if (isolation is { Length: > 200 }) return Fail("At most 200 characters.", field: "isolationReference");
            if (p.TypeRequiresIsolation && isolation is null) return Fail("Give the reference of the isolation (lock-out) that makes this work safe.", field: "isolationReference");

            // Only one permit at a time in a place where either of them is exclusive. Checked under a lock on the place so two issuers cannot both pass.
            await Common.LockAsync(db, "permit-place:" + p.LocationId.ToString("N"), ct);
            var others = await db.Permits.AsNoTracking()
                .Where(x => x.Id != p.Id && x.LocationId == p.LocationId && (x.Status == PermitStatus.Issued || x.Status == PermitStatus.Started) && (x.TypeExclusive || p.TypeExclusive)).ToListAsync(ct);
            var clash = others.FirstOrDefault(x => x.PlannedEnd <= now || ComplianceRules.Overlaps(x.PlannedStart, x.PlannedEnd, p.PlannedStart, p.PlannedEnd));
            if (clash is not null)
                return Fail($"Permit PTW-{clash.Number:D4} ({clash.TypeName}) is in force in this place" + (clash.PlannedEnd <= now ? " and has run past its end; it must be closed first." : " at the same time."), conflict: true);

            p.PrecautionsJson = JsonSerializer.Serialize(precautions.Select(x => x with { Confirmed = true }).ToList());
            p.IsolationReference = isolation;
            p.Status = PermitStatus.Issued;
            p.IssuedBy = me;
            p.IssuedByName = name;
            p.IssuedAt = now;
            AddEvent(p, "issued", name, isolation is null ? null : "Isolation: " + isolation);
            return null;
        }, notify: p => NotifyAsync(p, [new DirectoryUser(p.RequestedBy, null, null, true)], "permit.issued", $"Permit PTW-{p.Number:D4} has been issued", $"{p.TypeName} at {p.LocationName} may start at the planned time.\n\nصدر تصريح العمل ({p.TypeName}) في {p.LocationName}.", exclude: null, ct), useTransaction: true);

    public Task<Result<PermitDto>> StartAsync(Guid id, StartPermitRequest r, CancellationToken ct = default) =>
        Transition(id, r.RowVersion, ct, (p, me, name, now) =>
        {
            if (p.Status != PermitStatus.Issued) return Task.FromResult<Failure?>(Fail("Only an issued permit can be started."));
            if (p.IssuedBy == me) return Task.FromResult<Failure?>(Fail("The person who issued the permit cannot also accept it and start the work.", forbidden: true));
            if (now >= p.PlannedEnd) return Task.FromResult<Failure?>(Fail("The permit has run past its end. Close it and ask for a new one."));
            if (now < p.PlannedStart.AddMinutes(-60)) return Task.FromResult<Failure?>(Fail("It is too early: work may start at most an hour before the planned start."));
            p.Status = PermitStatus.Started;
            p.StartedBy = me;
            p.StartedByName = name;
            p.StartedAt = now;
            AddEvent(p, "started", name, null);
            return Task.FromResult<Failure?>(null);
        });

    public Task<Result<PermitDto>> CloseAsync(Guid id, ClosePermitRequest r, CancellationToken ct = default) =>
        Transition(id, r.RowVersion, ct, (p, me, name, now) =>
        {
            if (p.Status is not (PermitStatus.Issued or PermitStatus.Started)) return Task.FromResult<Failure?>(Fail("Only a permit in force can be closed."));
            if (r.AreaSafe != true) return Task.FromResult<Failure?>(Fail("Confirm that the work is finished (or stopped) and the area is safe.", field: "areaSafe"));
            var note = Common.Clean(r.Note);
            if (note is { Length: > 1000 }) return Task.FromResult<Failure?>(Fail("Notes are at most 1000 characters.", field: "note"));
            p.Status = PermitStatus.Closed;
            p.ClosedBy = me;
            p.ClosedByName = name;
            p.ClosedAt = now;
            p.AreaSafe = true;
            p.ClosureNote = note;
            AddEvent(p, "closed", name, note);
            return Task.FromResult<Failure?>(null);
        });

    public Task<Result<PermitDto>> CancelAsync(Guid id, PermitDecisionRequest r, CancellationToken ct = default) =>
        Transition(id, r.RowVersion, ct, async (p, me, name, now) =>
        {
            if (p.Status is not (PermitStatus.Requested or PermitStatus.Approved or PermitStatus.Issued)) return Fail("A permit can be cancelled until work starts. After that, close it.");
            var reason = Common.Clean(r.Reason);
            if (reason is null || reason.Length > 500) return Fail("A reason is required (at most 500 characters).", field: "reason");
            // The person who asked may withdraw early; once issued, only someone who may issue permits can take it back.
            var perms = await permissions.GetAsync(ct);
            var mayDecide = perms.Contains(Permissions.PermitsApprove) || perms.Contains(Permissions.PermitsIssue);
            if (p.RequestedBy != me && !mayDecide) return Fail("Only the person who asked, or an approver, can cancel a permit.", forbidden: true);
            if (p.Status == PermitStatus.Issued && !perms.Contains(Permissions.PermitsIssue)) return Fail("An issued permit can only be cancelled by someone who may issue permits.", forbidden: true);
            p.Status = PermitStatus.Cancelled;
            p.CancelReason = reason;
            AddEvent(p, "cancelled", name, reason);
            return null;
        });

    // ---- plumbing ----

    private sealed record Failure(string Message, string? Field, bool Forbidden, bool Conflict);

    private static Failure Fail(string message, string? field = null, bool forbidden = false, bool conflict = false) => new(message, field, forbidden, conflict);

    private Task<Result<PermitDto>> Transition(Guid id, string? rowVersion, CancellationToken ct, Func<Permit, Guid, string, DateTimeOffset, Task<Failure?>> change, Func<Permit, Task>? notify = null, bool useTransaction = false)
    {
        return Run();

        async Task<Result<PermitDto>> Run()
        {
            if (user.UserId is not { } me) return Result<PermitDto>.Forbidden("Sign in first.");
            var name = await Common.NameOfAsync(users, me, ct);
            Permit? saved = null;
            Result<PermitDto>? outcome = null;

            async Task Attempt()
            {
                db.ChangeTracker.Clear();
                var p = await db.Permits.FirstOrDefaultAsync(x => x.Id == id, ct);
                if (p is null) { outcome = Result<PermitDto>.NotFound(); return; }
                if (Common.TryVersion(rowVersion, out var version)) db.Entry(p).Property(x => x.RowVersion).OriginalValue = version;
                var failure = await change(p, me, name, clock.GetUtcNow());
                if (failure is not null)
                {
                    outcome = failure.Forbidden ? Result<PermitDto>.Forbidden(failure.Message)
                        : failure.Conflict ? Result<PermitDto>.Conflict(failure.Message)
                        : failure.Field is not null ? Result<PermitDto>.Invalid(failure.Field, failure.Message)
                        : Result<PermitDto>.Conflict(failure.Message);
                    return;
                }

                audit.Record("permit." + p.Status.ToString().ToLowerInvariant(), nameof(Permit), id.ToString(), JsonSerializer.Serialize(new { p.Number, p.Status }));
                await db.SaveChangesAsync(ct);
                saved = p;
            }

            try
            {
                if (useTransaction)
                {
                    await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
                    {
                        saved = null;
                        outcome = null;
                        await using var tx = await db.Database.BeginTransactionAsync(ct);
                        await Attempt();
                        if (saved is not null) await tx.CommitAsync(ct);
                    });
                }
                else
                {
                    await Attempt();
                }
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();
                return Result<PermitDto>.Conflict("Someone else changed this permit. Reload and look at where it stands now.");
            }

            if (outcome is not null) return outcome;
            if (notify is not null && saved is not null) await notify(saved);
            return Result<PermitDto>.Ok(ToDto(saved!, clock.GetUtcNow()));
        }
    }

    private void AddEvent(Permit p, string action, string by, string? note) =>
        db.PermitEvents.Add(new PermitEvent { PermitId = p.Id, At = clock.GetUtcNow(), ById = user.UserId, ByName = by, Action = action, Note = note });

    private async Task NotifyAsync(Permit p, IReadOnlyCollection<DirectoryUser> people, string type, string subject, string body, Guid? exclude, CancellationToken ct)
    {
        try
        {
            var requests = people.Where(u => u.Id != exclude).Select(u => new NotificationRequest(u.Id, type, new { permitId = p.Id, number = p.Number, type = p.TypeName, place = p.LocationName }, "/compliance?tab=permits", subject, body)).ToList();
            if (requests.Count > 0) await notices.NotifyAsync(Guid.NewGuid(), requests, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Could not send a permit notice; the permit itself was saved");
        }
    }

    public static PermitDto ToDto(Permit p, DateTimeOffset now) =>
        new(p.Id, p.Number, p.TypeId, p.TypeCode, p.TypeName, p.TypeExclusive, p.TypeRequiresIsolation, p.WorkOrderId, p.WorkOrderNumber, p.LocationId, p.LocationName, p.Description, p.ContractorName,
            p.PlannedStart, p.PlannedEnd, p.Status, ComplianceRules.IsOverdue(p.Status, p.PlannedEnd, now), JsonSerializer.Deserialize<List<PermitPrecautionDto>>(p.PrecautionsJson) ?? [], p.RequestedBy,
            p.RequestedByName, p.RequestedAt, p.ApprovedByName, p.ApprovedAt, p.RejectReason, p.IssuedByName, p.IssuedAt, p.IsolationReference, p.StartedByName, p.StartedAt, p.ClosedByName, p.ClosedAt,
            p.ClosureNote, p.AreaSafe, p.CancelReason, Common.Version(p.RowVersion));
}
