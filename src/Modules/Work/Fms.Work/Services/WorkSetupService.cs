using System.Text.Json;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Work.Domain;
using Fms.Work.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Work.Services;

/// <summary>Request categories and the lifecycle switches. Reading is open to anyone who can raise or see requests; changing needs workorders.manage.</summary>
public sealed class WorkSetupService(WorkDbContext db, IAuditPort audit, Fms.BuildingBlocks.Identity.ICurrentUser user, TimeProvider clock)
{
    public async Task<IReadOnlyList<CategoryDto>> CategoriesAsync(bool includeArchived, CancellationToken ct = default)
    {
        var query = db.Categories.AsNoTracking().AsQueryable();
        if (!includeArchived) query = query.Where(c => !c.IsArchived);
        return (await query.OrderBy(c => c.Name).ToListAsync(ct)).Select(ToDto).ToList();
    }

    public async Task<Result<CategoryDto>> CreateCategoryAsync(SaveCategoryRequest r, CancellationToken ct = default)
    {
        var errors = Validate(r);
        if (errors.Any) return errors.ToResult<CategoryDto>();
        var c = new WorkCategory { Code = r.Code!.Trim().ToUpperInvariant(), Name = r.Name!.Trim(), NameAr = Clean(r.NameAr), DefaultPriority = r.DefaultPriority ?? WorkPriority.Medium };
        db.Categories.Add(c);
        audit.Record("workcategory.created", nameof(WorkCategory), c.Id.ToString(), JsonSerializer.Serialize(new { c.Code, c.Name, c.DefaultPriority }));
        return await SaveAsync(c, ct);
    }

    public async Task<Result<CategoryDto>> UpdateCategoryAsync(Guid id, SaveCategoryRequest r, CancellationToken ct = default)
    {
        var c = await db.Categories.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return Result<CategoryDto>.NotFound();
        if (!TryVersion(r.RowVersion, out var version)) return Result<CategoryDto>.Invalid("rowVersion", "rowVersion is required.");
        var errors = Validate(r);
        if (errors.Any) return errors.ToResult<CategoryDto>();

        c.Code = r.Code!.Trim().ToUpperInvariant();
        c.Name = r.Name!.Trim();
        c.NameAr = Clean(r.NameAr);
        c.DefaultPriority = r.DefaultPriority ?? c.DefaultPriority;
        db.Entry(c).Property(x => x.RowVersion).OriginalValue = version;
        audit.Record("workcategory.updated", nameof(WorkCategory), id.ToString(), JsonSerializer.Serialize(new { c.Code, c.Name, c.DefaultPriority }));
        return await SaveAsync(c, ct);
    }

    public async Task<Result<CategoryDto>> SetArchivedAsync(Guid id, bool archived, string? rowVersion, CancellationToken ct = default)
    {
        var c = await db.Categories.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return Result<CategoryDto>.NotFound();
        if (!TryVersion(rowVersion, out var version)) return Result<CategoryDto>.Invalid("rowVersion", "rowVersion is required.");
        c.IsArchived = archived;
        db.Entry(c).Property(x => x.RowVersion).OriginalValue = version;
        audit.Record(archived ? "workcategory.archived" : "workcategory.restored", nameof(WorkCategory), id.ToString());
        return await SaveAsync(c, ct);
    }

    public async Task<WorkSettingsDto> GetSettingsAsync(CancellationToken ct = default)
    {
        var s = await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct);
        return s is null ? new WorkSettingsDto(true, true, null) : ToDto(s);
    }

    public async Task<Result<WorkSettingsDto>> SaveSettingsAsync(SaveWorkSettingsRequest r, CancellationToken ct = default)
    {
        var s = await db.Settings.FirstOrDefaultAsync(ct);
        if (s is null)
        {
            s = new WorkSettings();
            db.Settings.Add(s);
        }
        else
        {
            if (!TryVersion(r.RowVersion, out var version)) return Result<WorkSettingsDto>.Invalid("rowVersion", "rowVersion is required.");
            db.Entry(s).Property(x => x.RowVersion).OriginalValue = version;
        }

        var errors = new ValidationErrors();
        var currency = r.CurrencyCode?.Trim().ToUpperInvariant();
        if (currency is not null && (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))) errors.Add("currencyCode", "Use a three-letter currency code, for example SAR.");
        if (r.LabourRatePerHour is { } rate && (rate < 0 || rate > 100_000)) errors.Add("labourRatePerHour", "The hourly rate must be between 0 and 100,000.");
        if (errors.Any) { db.ChangeTracker.Clear(); return errors.ToResult<WorkSettingsDto>(); }

        var before = new { s.RequireApproval, s.RequireVerification, s.RequireCompletionSignature, s.CurrencyCode, s.LabourRatePerHour };
        var flagsBefore = (s.RequireApproval, s.RequireVerification, s.RequireCompletionSignature);
        s.RequireApproval = r.RequireApproval;
        s.RequireVerification = r.RequireVerification;
        if (r.RequireCompletionSignature is { } sig) s.RequireCompletionSignature = sig;
        if (currency is not null) s.CurrencyCode = currency;
        if (r.LabourRatePerHour is { } newRate) s.LabourRatePerHour = Math.Round(newRate, 2);
        if (db.Entry(s).State != EntityState.Added && flagsBefore != (s.RequireApproval, s.RequireVerification, s.RequireCompletionSignature))
            WorkflowDesigner.Snapshot(db, s, user.UserId, clock.GetUtcNow(), await db.WorkflowVersions.MaxAsync(v => (int?)v.Version, ct) ?? 0, "Changed on the settings screen");
        audit.Record("worksettings.changed", nameof(WorkSettings), s.Id.ToString(),
            JsonSerializer.Serialize(new { from = before, to = new { s.RequireApproval, s.RequireVerification, s.RequireCompletionSignature, s.CurrencyCode, s.LabourRatePerHour } }));
        try
        {
            await db.SaveChangesAsync(ct);
            return Result<WorkSettingsDto>.Ok(ToDto(s));
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException || (ex is DbUpdateException u && SqlErrors.IsUniqueViolation(u)))
        {
            db.ChangeTracker.Clear();
            return Result<WorkSettingsDto>.Conflict("The settings were changed by someone else. Reload and try again.");
        }
    }

    private static WorkSettingsDto ToDto(WorkSettings s) =>
        new(s.RequireApproval, s.RequireVerification, Convert.ToBase64String(s.RowVersion), s.CurrencyCode, s.LabourRatePerHour, s.RequireCompletionSignature);

    private static ValidationErrors Validate(SaveCategoryRequest r)
    {
        var errors = new ValidationErrors();
        var code = r.Code?.Trim() ?? "";
        if (code.Length is 0 or > 50 || !code.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')) errors.Add("code", "Code is required: 1-50 letters, digits, '.', '_' or '-'.");
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 200) errors.Add("name", "Name is required (max 200 characters).");
        if (r.NameAr is { Length: > 200 }) errors.Add("nameAr", "Arabic name is at most 200 characters.");
        if (r.DefaultPriority is { } p && !Enum.IsDefined(p)) errors.Add("defaultPriority", "Unknown priority.");
        return errors;
    }

    private async Task<Result<CategoryDto>> SaveAsync(WorkCategory c, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return Result<CategoryDto>.Ok(ToDto(c));
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<CategoryDto>.Conflict("The category was changed by someone else. Reload it and try again.");
        }
        catch (DbUpdateException ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return Result<CategoryDto>.Conflict($"The category code '{c.Code}' is already in use.");
        }
    }

    private static CategoryDto ToDto(WorkCategory c) => new(c.Id, c.Code, c.Name, c.NameAr, c.DefaultPriority, c.IsArchived, Convert.ToBase64String(c.RowVersion));

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static bool TryVersion(string? value, out byte[] version)
    {
        version = [];
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { version = Convert.FromBase64String(value); return version.Length > 0; }
        catch (FormatException) { return false; }
    }
}
