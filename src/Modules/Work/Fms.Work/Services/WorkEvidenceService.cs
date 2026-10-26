using System.Text.Json;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Work.Domain;
using Fms.Work.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Fms.Work.Services;

/// <summary>Checklists, time and costs of a work order. Everything is checked against what this person may see and do on this work order right now.</summary>
public sealed class WorkEvidenceService(
    WorkDbContext db, IAuditPort audit, ICurrentUser user, ICurrentPermissions permissions, IUserDirectory users, IOptions<WorkOptions> options, TimeProvider clock)
{
    private Actor? _actor;

    private async Task<Actor> ActorAsync(CancellationToken ct) => _actor ??= new Actor(user.UserId, await permissions.GetAsync(ct));

    /// <summary>The work order if this person may see it (everyone with workorders.read, or the requester of their own).</summary>
    private async Task<WorkOrder?> LoadAsync(Guid id, CancellationToken ct)
    {
        var actor = await ActorAsync(ct);
        var query = db.WorkOrders.AsQueryable();
        if (!actor.Has(Permissions.WorkOrdersRead))
        {
            var me = actor.UserId;
            query = query.Where(w => w.RequestedBy == me);
        }

        return await query.FirstOrDefaultAsync(w => w.Id == id, ct);
    }

    // ---------------- checklist ----------------

    public async Task<Result<IReadOnlyList<ChecklistItemDto>>> ChecklistAsync(Guid workOrderId, CancellationToken ct = default)
    {
        if (await LoadAsync(workOrderId, ct) is null) return Result<IReadOnlyList<ChecklistItemDto>>.NotFound();
        return Result<IReadOnlyList<ChecklistItemDto>>.Ok(await ChecklistDtosAsync(workOrderId, ct));
    }

    public async Task<Result<IReadOnlyList<ChecklistItemDto>>> AttachChecklistAsync(Guid workOrderId, Guid? templateId, CancellationToken ct = default)
    {
        var wo = await LoadAsync(workOrderId, ct);
        if (wo is null) return Result<IReadOnlyList<ChecklistItemDto>>.NotFound();
        var actor = await ActorAsync(ct);
        if (!(actor.Has(Permissions.WorkOrdersAssign) || EvidenceRules.DoesTheWork(wo, actor))) return Result<IReadOnlyList<ChecklistItemDto>>.Forbidden("You cannot add a checklist to this work order.");
        if (!EvidenceRules.ChecklistAttachable(wo.Status)) return Result<IReadOnlyList<ChecklistItemDto>>.Conflict($"A checklist cannot be added to work that is {wo.Status}.");
        if (templateId is not { } tid) return Result<IReadOnlyList<ChecklistItemDto>>.Invalid("templateId", "Choose a checklist.");

        var template = await db.ChecklistTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tid && !t.IsArchived, ct);
        if (template is null) return Result<IReadOnlyList<ChecklistItemDto>>.Invalid("templateId", "Checklist not found.");
        var items = await db.ChecklistTemplateItems.AsNoTracking().Where(i => i.TemplateId == tid).OrderBy(i => i.Position).ToListAsync(ct);
        var existing = await db.ChecklistItems.CountAsync(i => i.WorkOrderId == workOrderId, ct);
        if (existing + items.Count > options.Value.MaxChecklistItems) return Result<IReadOnlyList<ChecklistItemDto>>.Conflict($"A work order can have at most {options.Value.MaxChecklistItems} checklist items.");

        foreach (var i in items)
            db.ChecklistItems.Add(new WorkOrderChecklistItem { WorkOrderId = workOrderId, TemplateId = tid, Position = ++existing, Text = i.Text, Required = i.Required });
        audit.Record("workorder.checklist_attached", nameof(WorkOrder), workOrderId.ToString(), JsonSerializer.Serialize(new { templateId = tid, template.Name, items = items.Count }));
        await db.SaveChangesAsync(ct);
        return Result<IReadOnlyList<ChecklistItemDto>>.Ok(await ChecklistDtosAsync(workOrderId, ct));
    }

    public async Task<Result<IReadOnlyList<ChecklistItemDto>>> SetChecklistResultAsync(Guid workOrderId, Guid itemId, SetChecklistResultRequest r, CancellationToken ct = default)
    {
        var wo = await LoadAsync(workOrderId, ct);
        if (wo is null) return Result<IReadOnlyList<ChecklistItemDto>>.NotFound();
        var item = await db.ChecklistItems.FirstOrDefaultAsync(i => i.Id == itemId && i.WorkOrderId == workOrderId, ct);
        if (item is null) return Result<IReadOnlyList<ChecklistItemDto>>.NotFound();

        var actor = await ActorAsync(ct);
        if (!EvidenceRules.DoesTheWork(wo, actor)) return Result<IReadOnlyList<ChecklistItemDto>>.Forbidden("Only the person doing the work (or a manager) fills in the checklist.");
        if (!EvidenceRules.ChecklistOpen(wo.Status)) return Result<IReadOnlyList<ChecklistItemDto>>.Conflict("The checklist can be filled in while the work is in progress or on hold.");

        var note = string.IsNullOrWhiteSpace(r.Note) ? null : r.Note.Trim();
        var errors = new ValidationErrors();
        if (r.Result is not { } result || !Enum.IsDefined(result)) errors.Add("result", "Choose a result.");
        if (note is { Length: > 500 }) errors.Add("note", "Note is at most 500 characters.");
        if (r.Result == ChecklistResult.Failed && note is null) errors.Add("note", "Say what failed.");
        if (errors.Any) return errors.ToResult<IReadOnlyList<ChecklistItemDto>>();

        var from = item.Result;
        item.Result = r.Result!.Value;
        item.Note = note;
        item.ResultBy = item.Result == ChecklistResult.Pending ? null : actor.UserId;
        item.ResultAt = item.Result == ChecklistResult.Pending ? null : clock.GetUtcNow();
        audit.Record("workorder.checklist_result", nameof(WorkOrder), workOrderId.ToString(), JsonSerializer.Serialize(new { itemId, item.Text, from, to = item.Result, note }));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<IReadOnlyList<ChecklistItemDto>>.Conflict("This line was changed by someone else. Reload and try again.");
        }

        return Result<IReadOnlyList<ChecklistItemDto>>.Ok(await ChecklistDtosAsync(workOrderId, ct));
    }

    /// <summary>What still stands between this work order and completion, or null when nothing does.</summary>
    public async Task<string?> CompletionBlockerAsync(Guid workOrderId, CancellationToken ct)
    {
        var pending = await db.ChecklistItems.CountAsync(i => i.WorkOrderId == workOrderId && i.Required && i.Result == ChecklistResult.Pending, ct);
        if (pending > 0) return $"{pending} required checklist item(s) are not answered yet.";

        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (settings is { RequireCompletionSignature: true } && !await db.Signatures.AnyAsync(s => s.WorkOrderId == workOrderId && s.Role == SignatureRole.Technician, ct))
            return "The technician's signature is required before the work can be completed.";
        return null;
    }

    private async Task<IReadOnlyList<ChecklistItemDto>> ChecklistDtosAsync(Guid workOrderId, CancellationToken ct)
    {
        var items = await db.ChecklistItems.AsNoTracking().Where(i => i.WorkOrderId == workOrderId).OrderBy(i => i.Position).ToListAsync(ct);
        var names = await users.GetAsync(items.Where(i => i.ResultBy != null).Select(i => i.ResultBy!.Value), ct);
        return items.Select(i => new ChecklistItemDto(i.Id, i.Position, i.Text, i.Required, i.Result, i.Note,
            i.ResultBy is { } u && names.TryGetValue(u, out var p) ? p.DisplayName ?? p.Email : null, i.ResultAt)).ToList();
    }

    // ---------------- time ----------------

    public async Task<Result<IReadOnlyList<TimeEntryDto>>> TimeAsync(Guid workOrderId, CancellationToken ct = default)
    {
        var gate = await StaffOnlyAsync<IReadOnlyList<TimeEntryDto>>(workOrderId, ct);
        if (gate.Failure is { } f) return f;
        return Result<IReadOnlyList<TimeEntryDto>>.Ok(await TimeDtosAsync(workOrderId, ct));
    }

    public async Task<Result<IReadOnlyList<TimeEntryDto>>> AddTimeAsync(Guid workOrderId, AddTimeRequest r, CancellationToken ct = default)
    {
        var gate = await StaffOnlyAsync<IReadOnlyList<TimeEntryDto>>(workOrderId, ct);
        if (gate.Failure is { } f) return f;
        var wo = gate.Order!;
        var actor = await ActorAsync(ct);
        if (!EvidenceRules.DoesTheWork(wo, actor)) return Result<IReadOnlyList<TimeEntryDto>>.Forbidden("Only the person doing the work (or a manager) records time.");
        if (!EvidenceRules.LedgerOpen(wo.Status)) return Result<IReadOnlyList<TimeEntryDto>>.Conflict($"Time cannot be recorded on work that is {wo.Status}.");

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var note = string.IsNullOrWhiteSpace(r.Note) ? null : r.Note.Trim();
        var errors = new ValidationErrors();
        if (r.Minutes is not (>= 1 and <= 1440)) errors.Add("minutes", "Minutes must be between 1 and 1440 (one day).");
        if (r.WorkedOn is not { } day) errors.Add("workedOn", "Say which day the work was done.");
        else if (day > today.AddDays(1) || day < today.AddDays(-90)) errors.Add("workedOn", "The day must be within the last 90 days and not in the future.");
        if (note is { Length: > 500 }) errors.Add("note", "Note is at most 500 characters.");
        if (errors.Any) return errors.ToResult<IReadOnlyList<TimeEntryDto>>();

        var rate = (await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct))?.LabourRatePerHour ?? 0m;
        var entry = new WorkOrderTimeEntry { WorkOrderId = workOrderId, WorkerId = actor.UserId!.Value, WorkedOn = r.WorkedOn!.Value, Minutes = r.Minutes!.Value, HourlyRate = rate, Note = note, RecordedAt = clock.GetUtcNow() };
        db.TimeEntries.Add(entry);
        audit.Record("workorder.time_recorded", nameof(WorkOrder), workOrderId.ToString(), JsonSerializer.Serialize(new { entry.Id, entry.WorkedOn, entry.Minutes, entry.HourlyRate }));
        await db.SaveChangesAsync(ct);
        return Result<IReadOnlyList<TimeEntryDto>>.Ok(await TimeDtosAsync(workOrderId, ct));
    }

    public Task<Result<IReadOnlyList<TimeEntryDto>>> VoidTimeAsync(Guid workOrderId, Guid entryId, string? reason, CancellationToken ct = default) =>
        VoidAsync(workOrderId, LedgerEntryType.Time, entryId, reason, async () => await TimeDtosAsync(workOrderId, ct),
            async wo => (await db.TimeEntries.AsNoTracking().Where(t => t.Id == entryId && t.WorkOrderId == wo.Id).Select(t => (Guid?)t.WorkerId).FirstOrDefaultAsync(ct), true), ct);

    private async Task<IReadOnlyList<TimeEntryDto>> TimeDtosAsync(Guid workOrderId, CancellationToken ct)
    {
        var entries = await db.TimeEntries.AsNoTracking().Where(t => t.WorkOrderId == workOrderId).OrderBy(t => t.WorkedOn).ThenBy(t => t.RecordedAt).ToListAsync(ct);
        var voids = await db.Voids.AsNoTracking().Where(v => v.WorkOrderId == workOrderId && v.EntryType == LedgerEntryType.Time).ToDictionaryAsync(v => v.EntryId, v => v.Reason, ct);
        var names = await users.GetAsync(entries.Select(e => e.WorkerId), ct);
        return entries.Select(e => new TimeEntryDto(e.Id, e.WorkerId, names.TryGetValue(e.WorkerId, out var p) ? p.DisplayName ?? p.Email : null, e.WorkedOn, e.Minutes, e.HourlyRate,
            EvidenceRules.LabourCost(e.Minutes, e.HourlyRate), e.Note, e.RecordedAt, voids.ContainsKey(e.Id), voids.GetValueOrDefault(e.Id))).ToList();
    }

    // ---------------- costs ----------------

    public async Task<Result<IReadOnlyList<CostLineDto>>> CostsAsync(Guid workOrderId, CancellationToken ct = default)
    {
        var gate = await StaffOnlyAsync<IReadOnlyList<CostLineDto>>(workOrderId, ct);
        if (gate.Failure is { } f) return f;
        return Result<IReadOnlyList<CostLineDto>>.Ok(await CostDtosAsync(workOrderId, ct));
    }

    public async Task<Result<IReadOnlyList<CostLineDto>>> AddCostAsync(Guid workOrderId, AddCostRequest r, CancellationToken ct = default)
    {
        var gate = await StaffOnlyAsync<IReadOnlyList<CostLineDto>>(workOrderId, ct);
        if (gate.Failure is { } f) return f;
        var wo = gate.Order!;
        var actor = await ActorAsync(ct);
        if (!EvidenceRules.DoesTheWork(wo, actor)) return Result<IReadOnlyList<CostLineDto>>.Forbidden("Only the person doing the work (or a manager) records costs.");
        if (!EvidenceRules.LedgerOpen(wo.Status)) return Result<IReadOnlyList<CostLineDto>>.Conflict($"Costs cannot be recorded on work that is {wo.Status}.");

        var description = r.Description?.Trim() ?? "";
        var reference = string.IsNullOrWhiteSpace(r.Reference) ? null : r.Reference.Trim();
        var errors = new ValidationErrors();
        if (r.Kind is not { } kind || !Enum.IsDefined(kind)) errors.Add("kind", "Choose material, contractor or other.");
        if (description.Length is 0 or > 300) errors.Add("description", "Description is required (max 300 characters).");
        if (reference is { Length: > 100 }) errors.Add("reference", "Reference is at most 100 characters.");
        if (r.Quantity is not (> 0 and <= 1_000_000)) errors.Add("quantity", "Quantity must be more than 0 and at most 1,000,000.");
        if (r.UnitCost is not (>= 0 and <= 10_000_000)) errors.Add("unitCost", "Unit cost must be between 0 and 10,000,000.");
        if (errors.Any) return errors.ToResult<IReadOnlyList<CostLineDto>>();

        var line = new WorkOrderCostLine
        {
            WorkOrderId = workOrderId, Kind = r.Kind!.Value, Description = description, Reference = reference, Quantity = r.Quantity!.Value, UnitCost = r.UnitCost!.Value,
            RecordedBy = actor.UserId!.Value, RecordedAt = clock.GetUtcNow(),
        };
        db.CostLines.Add(line);
        audit.Record("workorder.cost_recorded", nameof(WorkOrder), workOrderId.ToString(), JsonSerializer.Serialize(new { line.Id, line.Kind, line.Description, line.Reference, line.Quantity, line.UnitCost, total = EvidenceRules.LineTotal(line) }));
        await db.SaveChangesAsync(ct);
        return Result<IReadOnlyList<CostLineDto>>.Ok(await CostDtosAsync(workOrderId, ct));
    }

    public Task<Result<IReadOnlyList<CostLineDto>>> VoidCostAsync(Guid workOrderId, Guid lineId, string? reason, CancellationToken ct = default) =>
        VoidAsync(workOrderId, LedgerEntryType.Cost, lineId, reason, async () => await CostDtosAsync(workOrderId, ct),
            async wo => (await db.CostLines.AsNoTracking().Where(c => c.Id == lineId && c.WorkOrderId == wo.Id).Select(c => (Guid?)c.RecordedBy).FirstOrDefaultAsync(ct), true), ct);

    public async Task<Result<CostSummaryDto>> SummaryAsync(Guid workOrderId, CancellationToken ct = default)
    {
        var gate = await StaffOnlyAsync<CostSummaryDto>(workOrderId, ct);
        if (gate.Failure is { } f) return f;

        var currency = (await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct))?.CurrencyCode ?? "SAR";
        var time = (await TimeDtosAsync(workOrderId, ct)).Where(t => !t.IsVoided).ToList();
        var costs = (await CostDtosAsync(workOrderId, ct)).Where(c => !c.IsVoided).ToList();
        var labour = time.Sum(t => t.LabourCost);
        decimal Sum(CostKind k) => costs.Where(c => c.Kind == k).Sum(c => c.Total);
        var (material, contractor, other) = (Sum(CostKind.Material), Sum(CostKind.Contractor), Sum(CostKind.Other));
        return Result<CostSummaryDto>.Ok(new CostSummaryDto(currency, time.Sum(t => t.Minutes), labour, material, contractor, other, labour + material + contractor + other));
    }

    private async Task<IReadOnlyList<CostLineDto>> CostDtosAsync(Guid workOrderId, CancellationToken ct)
    {
        var lines = await db.CostLines.AsNoTracking().Where(c => c.WorkOrderId == workOrderId).OrderBy(c => c.RecordedAt).ToListAsync(ct);
        var voids = await db.Voids.AsNoTracking().Where(v => v.WorkOrderId == workOrderId && v.EntryType == LedgerEntryType.Cost).ToDictionaryAsync(v => v.EntryId, v => v.Reason, ct);
        var names = await users.GetAsync(lines.Select(l => l.RecordedBy), ct);
        return lines.Select(l => new CostLineDto(l.Id, l.Kind, l.Description, l.Reference, l.Quantity, l.UnitCost, EvidenceRules.LineTotal(l),
            names.TryGetValue(l.RecordedBy, out var p) ? p.DisplayName ?? p.Email : null, l.RecordedAt, voids.ContainsKey(l.Id), voids.GetValueOrDefault(l.Id))).ToList();
    }

    // ---------------- shared ----------------

    /// <summary>Time and costs are internal: only people who can read all work orders see them.</summary>
    private async Task<(WorkOrder? Order, Result<T>? Failure)> StaffOnlyAsync<T>(Guid workOrderId, CancellationToken ct)
    {
        var wo = await LoadAsync(workOrderId, ct);
        if (wo is null) return (null, Result<T>.NotFound());
        if (!(await ActorAsync(ct)).Has(Permissions.WorkOrdersRead)) return (null, Result<T>.Forbidden("Time and costs are visible to staff only."));
        return (wo, null);
    }

    private async Task<Result<IReadOnlyList<TDto>>> VoidAsync<TDto>(
        Guid workOrderId, LedgerEntryType type, Guid entryId, string? reason, Func<Task<IReadOnlyList<TDto>>> after,
        Func<WorkOrder, Task<(Guid? Owner, bool Found)>> owner, CancellationToken ct)
    {
        var gate = await StaffOnlyAsync<IReadOnlyList<TDto>>(workOrderId, ct);
        if (gate.Failure is { } f) return f;
        var wo = gate.Order!;
        var actor = await ActorAsync(ct);

        var (entryOwner, _) = await owner(wo);
        if (entryOwner is null) return Result<IReadOnlyList<TDto>>.NotFound();
        if (!(actor.Has(Permissions.WorkOrdersManage) || entryOwner == actor.UserId)) return Result<IReadOnlyList<TDto>>.Forbidden("Only the person who recorded it (or a manager) can void it.");
        if (!EvidenceRules.LedgerOpen(wo.Status)) return Result<IReadOnlyList<TDto>>.Conflict($"Entries cannot be voided on work that is {wo.Status}.");

        var why = reason?.Trim() ?? "";
        if (why.Length is 0 or > 500) return Result<IReadOnlyList<TDto>>.Invalid("reason", "A reason is required (max 500 characters).");
        if (await db.Voids.AnyAsync(v => v.EntryType == type && v.EntryId == entryId, ct)) return Result<IReadOnlyList<TDto>>.Conflict("This entry is already voided.");

        db.Voids.Add(new WorkOrderVoid { WorkOrderId = workOrderId, EntryType = type, EntryId = entryId, Reason = why, VoidedBy = actor.UserId!.Value, VoidedAt = clock.GetUtcNow() });
        audit.Record("workorder." + type.ToString().ToLowerInvariant() + "_voided", nameof(WorkOrder), workOrderId.ToString(), JsonSerializer.Serialize(new { entryId, reason = why }));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (Fms.BuildingBlocks.Persistence.SqlErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear(); // voided at the same moment by someone else
            return Result<IReadOnlyList<TDto>>.Conflict("This entry is already voided.");
        }

        return Result<IReadOnlyList<TDto>>.Ok(await after());
    }
}
