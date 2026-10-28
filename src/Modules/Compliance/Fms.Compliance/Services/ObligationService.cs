using System.Text.Json;
using Microsoft.Extensions.Logging;
using Fms.BuildingBlocks.Identity;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Compliance.Domain;
using Fms.Compliance.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Compliance.Services;

/// <summary>What must be done, by when, and the history of it having been done.</summary>
public sealed class ObligationService(ComplianceDbContext db, IFacilityLookup facilities, IUserDirectory users, ICurrentUser user, IAuditPort audit, TimeProvider clock, IWorkOrderFromSource? work = null, ILogger<ObligationService>? log = null)
{
    public const string WorkSourceKind = "compliance.failed";

    public const int MaxObligations = 5000;
    public const int MaxListed = 2000;

    public async Task<Page<ObligationDto>> ListAsync(ComplianceStatus? status, ObligationCategory? category, Guid? locationId, string? q, bool includeArchived, int? page, int? pageSize, CancellationToken ct = default)
    {
        var query = db.Obligations.AsNoTracking().AsQueryable();
        if (!includeArchived) query = query.Where(o => !o.IsArchived);
        if (category is { } c) query = query.Where(o => o.Category == c);
        if (locationId is { } l) query = query.Where(o => o.LocationId == l);
        if (Common.Clean(q) is { } text) query = query.Where(o => o.Title.Contains(text) || (o.Authority != null && o.Authority.Contains(text)) || (o.LocationName != null && o.LocationName.Contains(text)) || (o.AssetName != null && o.AssetName.Contains(text)));
        var today = Common.Today(clock);
        var all = (await query.Take(MaxListed).ToListAsync(ct)).Select(o => ToDto(o, today)).Where(d => status is null || d.Status == status).ToList();

        // Worst first: failed, then overdue, then by the date it is due.
        var ordered = all.OrderBy(d => d.IsArchived).ThenBy(d => d.Status switch { ComplianceStatus.Failed => 0, ComplianceStatus.Overdue => 1, ComplianceStatus.DueSoon => 2, ComplianceStatus.Scheduled => 3, _ => 4 })
            .ThenBy(d => d.DueOn ?? DateOnly.MaxValue).ThenBy(d => d.Title).ToList();
        var (number, size) = Page<ObligationDto>.Normalize(page, pageSize);
        return new Page<ObligationDto>(ordered.Skip((number - 1) * size).Take(size).ToList(), ordered.Count, number, size);
    }

    public async Task<ComplianceSummaryDto> SummaryAsync(CancellationToken ct = default)
    {
        var today = Common.Today(clock);
        var all = (await db.Obligations.AsNoTracking().Where(o => !o.IsArchived).Take(MaxListed).ToListAsync(ct)).Select(o => ComplianceRules.StatusOf(o, today)).ToList();
        int N(ComplianceStatus s) => all.Count(x => x == s);
        var measurable = all.Count - N(ComplianceStatus.Scheduled);
        return new ComplianceSummaryDto(all.Count, N(ComplianceStatus.Compliant), N(ComplianceStatus.DueSoon), N(ComplianceStatus.Overdue), N(ComplianceStatus.Failed), N(ComplianceStatus.Scheduled),
            measurable == 0 ? null : (int)Math.Round(100.0 * (N(ComplianceStatus.Compliant) + N(ComplianceStatus.DueSoon)) / measurable));
    }

    public async Task<Result<ObligationDetailDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var o = await db.Obligations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (o is null) return Result<ObligationDetailDto>.NotFound();
        var completions = await db.Completions.AsNoTracking().Where(c => c.ObligationId == id).OrderByDescending(c => c.PerformedOn).ThenByDescending(c => c.RecordedAt).Take(200).ToListAsync(ct);
        var ids = completions.Select(c => c.Id).ToList();
        var docs = await db.Documents.AsNoTracking().Where(d => ids.Contains(d.CompletionId) && d.DeletedAt == null).ToListAsync(ct);
        var orders = work is null ? new Dictionary<Guid, WorkFromSourceResult>() : (await work.FindAsync(WorkSourceKind, completions.Where(c => c.Result == CompletionResult.Failed).Select(c => c.Id), ct)).ToDictionary(x => x.Key, x => x.Value);
        var dtos = completions.Select(c => ToDto(c, docs.Where(d => d.CompletionId == c.Id).Select(ToDto).ToList()) with { WorkOrderId = orders.GetValueOrDefault(c.Id)?.WorkOrderId, WorkOrderNumber = orders.GetValueOrDefault(c.Id)?.Number }).ToList();
        return Result<ObligationDetailDto>.Ok(new ObligationDetailDto(ToDto(o, Common.Today(clock)), dtos));
    }

    public async Task<Result<ObligationDto>> CreateAsync(SaveObligationRequest r, CancellationToken ct = default)
    {
        if (await db.Obligations.CountAsync(ct) >= MaxObligations) return Result<ObligationDto>.Conflict($"At most {MaxObligations} obligations can be kept.");
        var o = new Obligation { CreatedAt = clock.GetUtcNow(), CreatedBy = user.UserId };
        var invalid = await ApplyAsync(o, r, ct);
        if (invalid is not null) return invalid;
        db.Obligations.Add(o);
        audit.Record("compliance.obligation_created", nameof(Obligation), o.Id.ToString(), JsonSerializer.Serialize(new { o.Title, o.Category }));
        await db.SaveChangesAsync(ct);
        return Result<ObligationDto>.Ok(ToDto(o, Common.Today(clock)));
    }

    public async Task<Result<ObligationDto>> UpdateAsync(Guid id, SaveObligationRequest r, CancellationToken ct = default)
    {
        var o = await db.Obligations.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (o is null) return Result<ObligationDto>.NotFound();
        if (!Common.TryVersion(r.RowVersion, out var version)) return Result<ObligationDto>.Invalid("rowVersion", "Reload and try again.");
        db.Entry(o).Property(x => x.RowVersion).OriginalValue = version;
        var invalid = await ApplyAsync(o, r, ct);
        if (invalid is not null) return invalid;
        audit.Record("compliance.obligation_updated", nameof(Obligation), id.ToString(), JsonSerializer.Serialize(new { o.Title }));
        return await SaveAsync(o, ct);
    }

    public async Task<Result<ObligationDto>> ArchiveAsync(Guid id, bool archive, ArchiveObligationRequest r, CancellationToken ct = default)
    {
        var o = await db.Obligations.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (o is null) return Result<ObligationDto>.NotFound();
        if (!Common.TryVersion(r.RowVersion, out var version)) return Result<ObligationDto>.Invalid("rowVersion", "Reload and try again.");
        db.Entry(o).Property(x => x.RowVersion).OriginalValue = version;
        o.IsArchived = archive;
        audit.Record(archive ? "compliance.obligation_archived" : "compliance.obligation_restored", nameof(Obligation), id.ToString());
        return await SaveAsync(o, ct);
    }

    public async Task<Result<CompletionDto>> RecordAsync(Guid id, RecordCompletionRequest r, CancellationToken ct = default)
    {
        var o = await db.Obligations.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (o is null) return Result<CompletionDto>.NotFound();
        if (o.IsArchived) return Result<CompletionDto>.Conflict("This obligation is archived. Restore it first.");
        var today = Common.Today(clock);
        var errors = new ValidationErrors();
        if (r.PerformedOn is not { } performed) errors.Add("performedOn", "Say when it was done.");
        else if (performed > today || performed < today.AddYears(-10)) errors.Add("performedOn", "The date cannot be in the future or more than ten years ago.");
        if (r.Result is not { } result || !Enum.IsDefined(result)) errors.Add("result", "Say how it went.");
        var reference = Common.Clean(r.Reference);
        var provider = Common.Clean(r.ProviderName);
        var notes = Common.Clean(r.Notes);
        if (reference is { Length: > 100 }) errors.Add("reference", "The reference is at most 100 characters.");
        if (provider is { Length: > 200 }) errors.Add("providerName", "The name is at most 200 characters.");
        if (notes is { Length: > 1000 }) errors.Add("notes", "Notes are at most 1000 characters.");
        if (r.Result == CompletionResult.Failed && notes is null) errors.Add("notes", "Say what failed.");
        DateOnly? validUntil = null;
        if (r.Result != CompletionResult.Failed && r.PerformedOn is { } p)
        {
            validUntil = r.ValidUntil ?? ComplianceRules.DefaultValidUntil(p, o.IntervalMonths);
            if (validUntil is { } v && (v <= p || v > p.AddMonths(ComplianceRules.MaxIntervalMonths))) errors.Add("validUntil", "The certificate must be valid for a period after it was done (at most 20 years).");
        }

        if (errors.Any) return errors.ToResult<CompletionDto>();

        var c = new Completion
        {
            ObligationId = id, PerformedOn = r.PerformedOn!.Value, ValidUntil = validUntil, Result = r.Result!.Value, Reference = reference, ProviderName = provider, Notes = notes,
            RecordedAt = clock.GetUtcNow(), RecordedBy = user.UserId, RecordedByName = await Common.NameOfAsync(users, user.UserId, ct),
        };
        db.Completions.Add(c);

        // Only the newest piece of work decides where the obligation stands; recording an older one later just completes the history.
        if (o.LastCompletedOn is null || c.PerformedOn >= o.LastCompletedOn)
        {
            o.LastCompletedOn = c.PerformedOn;
            o.LastResult = c.Result;
            if (c.Result != CompletionResult.Failed) o.ValidUntil = c.ValidUntil;
            o.LastAlertStatus = null; // the state has changed on purpose: warn afresh if it needs warning
        }

        audit.Record("compliance.completion_recorded", nameof(Completion), c.Id.ToString(), JsonSerializer.Serialize(new { obligationId = id, c.PerformedOn, c.Result, c.ValidUntil, c.Reference }));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<CompletionDto>.Conflict("Someone else changed this obligation. Reload and try again.");
        }

        var dto = ToDto(c, []);
        if (c.Result == CompletionResult.Failed && o.CreateWorkOrderOnFail && user.UserId is { } me && work is not null)
        {
            // The failure is on record; a work order that cannot be raised now can be raised from the failure by hand.
            try
            {
                var raised = await work.EnsureAsync(FailureWork(o, c), me, ct);
                if (raised is not null) dto = dto with { WorkOrderId = raised.WorkOrderId, WorkOrderNumber = raised.Number };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log?.LogError(ex, "A work order for a failed inspection could not be raised");
            }
        }

        return Result<CompletionDto>.Ok(dto);
    }

    /// <summary>A manager raises the work order for a failed result by hand. Asking again for the same failure returns the same work order.</summary>
    public async Task<Result<WorkFromSourceResult>> RaiseWorkOrderAsync(Guid completionId, CancellationToken ct = default)
    {
        if (work is null) return Result<WorkFromSourceResult>.Conflict("Work orders are not available.");
        if (user.UserId is not { } me) return Result<WorkFromSourceResult>.Forbidden("Sign in first.");
        var c = await db.Completions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == completionId, ct);
        if (c is null) return Result<WorkFromSourceResult>.NotFound();
        if (c.Result != CompletionResult.Failed) return Result<WorkFromSourceResult>.Conflict("Only a failed result can have a work order.");
        var o = await db.Obligations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == c.ObligationId, ct);
        if (o is null) return Result<WorkFromSourceResult>.NotFound();
        if (o.LocationId is null && o.AssetId is null) return Result<WorkFromSourceResult>.Conflict("The obligation has no place or asset, so a work order cannot be raised for it.");
        var result = await work.EnsureAsync(FailureWork(o, c), me, ct);
        return result is null ? Result<WorkFromSourceResult>.Conflict("The place or asset no longer exists.") : Result<WorkFromSourceResult>.Ok(result);
    }

    private static WorkFromSource FailureWork(Obligation o, Completion c) =>
        new(WorkSourceKind, c.Id, $"Failed inspection: {o.Title}", $"{o.Title} failed on {c.PerformedOn:yyyy-MM-dd}. {c.Notes}".Trim(), o.LocationId, o.AssetId, "High", false);

    private async Task<Result<ObligationDto>?> ApplyAsync(Obligation o, SaveObligationRequest r, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var title = Common.Clean(r.Title);
        if (title is null || title.Length > 200) errors.Add("title", "Give it a title (at most 200 characters).");
        if (r.Category is not { } category || !Enum.IsDefined(category)) errors.Add("category", "Choose a category.");
        var authority = Common.Clean(r.Authority);
        if (authority is { Length: > 200 }) errors.Add("authority", "At most 200 characters.");
        var notes = Common.Clean(r.Notes);
        if (notes is { Length: > 1000 }) errors.Add("notes", "Notes are at most 1000 characters.");
        if (r.IntervalMonths is { } m && (m < 1 || m > ComplianceRules.MaxIntervalMonths)) errors.Add("intervalMonths", "Between 1 and 240 months, or leave empty for a one-off.");
        var lead = r.LeadDays ?? 30;
        if (lead < 0 || lead > ComplianceRules.MaxLeadDays) errors.Add("leadDays", "Between 0 and 365 days.");
        var today = Common.Today(clock);
        if (r.FirstDueOn is { } due && (due < today.AddYears(-10) || due > today.AddYears(30))) errors.Add("firstDueOn", "That date is too far away.");

        FacilityRef? location = null, asset = null;
        if (r.LocationId is { } lid && (location = await facilities.GetLocationAsync(lid, ct)) is null) errors.Add("locationId", "That place does not exist.");
        if (r.AssetId is { } aid && (asset = await facilities.GetAssetAsync(aid, ct)) is null) errors.Add("assetId", "That asset does not exist.");
        DirectoryUser? owner = null;
        if (r.OwnerId is { } oid && ((owner = (await users.GetAsync([oid], ct)).GetValueOrDefault(oid)) is null || !owner.IsActive)) errors.Add("ownerId", "Choose an active person.");
        var raiseOnFail = r.CreateWorkOrderOnFail ?? false;
        if (raiseOnFail && location is null && asset is null) errors.Add("createWorkOrderOnFail", "Give a place or an asset first: a work order needs to know where.");
        if (errors.Any) return errors.ToResult<ObligationDto>();

        o.CreateWorkOrderOnFail = raiseOnFail;
        o.Title = title!;
        o.Category = r.Category!.Value;
        o.Authority = authority;
        o.LocationId = location?.Id;
        o.LocationName = location?.Name;
        o.AssetId = asset?.Id;
        o.AssetName = asset?.Name;
        o.IntervalMonths = r.IntervalMonths;
        o.LeadDays = lead;
        o.FirstDueOn = r.FirstDueOn;
        o.OwnerId = owner?.Id;
        o.OwnerName = owner is null ? null : Common.NameOf(owner);
        o.Notes = notes;
        return null;
    }

    private async Task<Result<ObligationDto>> SaveAsync(Obligation o, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<ObligationDto>.Conflict("Someone else changed this. Reload and try again.");
        }

        return Result<ObligationDto>.Ok(ToDto(o, Common.Today(clock)));
    }

    public static ObligationDto ToDto(Obligation o, DateOnly today)
    {
        var due = ComplianceRules.DueOn(o);
        return new ObligationDto(o.Id, o.Title, o.Category, o.Authority, o.LocationId, o.LocationName, o.AssetId, o.AssetName, o.IntervalMonths, o.LeadDays, o.FirstDueOn, due,
            due is { } d ? d.DayNumber - today.DayNumber : null, o.OwnerId, o.OwnerName, o.Notes, o.IsArchived, o.LastCompletedOn, o.ValidUntil, o.LastResult, ComplianceRules.StatusOf(o, today), Common.Version(o.RowVersion), o.CreateWorkOrderOnFail);
    }

    private static CompletionDto ToDto(Completion c, IReadOnlyList<CompletionDocumentDto> docs) =>
        new(c.Id, c.ObligationId, c.PerformedOn, c.ValidUntil, c.Result, c.Reference, c.ProviderName, c.Notes, c.RecordedAt, c.RecordedByName, docs);

    private static CompletionDocumentDto ToDto(CompletionDocument d) => new(d.Id, d.CompletionId, d.FileName, d.ContentType, d.SizeBytes, d.Sha256, d.UploadedAt);
}
