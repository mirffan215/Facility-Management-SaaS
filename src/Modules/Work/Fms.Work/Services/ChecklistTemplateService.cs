using System.Text.Json;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Work.Domain;
using Fms.Work.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Fms.Work.Services;

/// <summary>Reusable checklists. Reading is open to anyone who works with work orders; changing needs workorders.manage.</summary>
public sealed class ChecklistTemplateService(WorkDbContext db, IAuditPort audit, IOptions<WorkOptions> options)
{
    public async Task<IReadOnlyList<ChecklistTemplateDto>> ListAsync(bool includeArchived, CancellationToken ct = default)
    {
        var templates = await db.ChecklistTemplates.AsNoTracking().Where(t => includeArchived || !t.IsArchived).OrderBy(t => t.Name).ToListAsync(ct);
        var ids = templates.Select(t => t.Id).ToList();
        var items = await db.ChecklistTemplateItems.AsNoTracking().Where(i => ids.Contains(i.TemplateId)).OrderBy(i => i.Position).ToListAsync(ct);
        return templates.Select(t => ToDto(t, items.Where(i => i.TemplateId == t.Id))).ToList();
    }

    public Task<Result<ChecklistTemplateDto>> CreateAsync(SaveChecklistTemplateRequest r, CancellationToken ct = default) => SaveAsync(null, r, ct);

    public async Task<Result<ChecklistTemplateDto>> UpdateAsync(Guid id, SaveChecklistTemplateRequest r, CancellationToken ct = default)
    {
        var existing = await db.ChecklistTemplates.FirstOrDefaultAsync(t => t.Id == id, ct);
        return existing is null ? Result<ChecklistTemplateDto>.NotFound() : await SaveAsync(existing, r, ct);
    }

    public async Task<Result<ChecklistTemplateDto>> SetArchivedAsync(Guid id, bool archived, string? rowVersion, CancellationToken ct = default)
    {
        var t = await db.ChecklistTemplates.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null) return Result<ChecklistTemplateDto>.NotFound();
        if (!TryVersion(rowVersion, out var version)) return Result<ChecklistTemplateDto>.Invalid("rowVersion", "rowVersion is required.");
        t.IsArchived = archived;
        db.Entry(t).Property(x => x.RowVersion).OriginalValue = version;
        audit.Record(archived ? "checklisttemplate.archived" : "checklisttemplate.restored", nameof(ChecklistTemplate), id.ToString());
        return await CommitAsync(t, ct);
    }

    private async Task<Result<ChecklistTemplateDto>> SaveAsync(ChecklistTemplate? existing, SaveChecklistTemplateRequest r, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 200) errors.Add("name", "Name is required (max 200 characters).");
        if (r.NameAr is { Length: > 200 }) errors.Add("nameAr", "Arabic name is at most 200 characters.");
        var items = (r.Items ?? []).Select(i => new TemplateItemDto(i.Text?.Trim() ?? "", i.Required)).ToList();
        if (items.Count == 0 || items.Count > options.Value.MaxChecklistItems) errors.Add("items", $"A checklist has 1 to {options.Value.MaxChecklistItems} lines.");
        if (items.Any(i => i.Text.Length is 0 or > 300)) errors.Add("items", "Every line needs text (max 300 characters).");
        if (existing is not null && !TryVersion(r.RowVersion, out _)) errors.Add("rowVersion", "rowVersion is required.");
        if (errors.Any) return errors.ToResult<ChecklistTemplateDto>();

        var template = existing ?? new ChecklistTemplate();
        template.Name = r.Name!.Trim();
        template.NameAr = string.IsNullOrWhiteSpace(r.NameAr) ? null : r.NameAr.Trim();
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                if (existing is null) db.ChecklistTemplates.Add(template);
                else
                {
                    TryVersion(r.RowVersion, out var version);
                    db.Entry(template).Property(x => x.RowVersion).OriginalValue = version;
                    db.Entry(template).Property(x => x.Name).IsModified = true; // always write the row so a stale version is detected
                    db.ChecklistTemplateItems.RemoveRange(await db.ChecklistTemplateItems.Where(i => i.TemplateId == template.Id).ToListAsync(ct));
                    await db.SaveChangesAsync(ct);
                }

                var rows = items.Select((i, n) => new ChecklistTemplateItem { TemplateId = template.Id, Position = n + 1, Text = i.Text, Required = i.Required }).ToList();
                db.ChecklistTemplateItems.AddRange(rows);
                audit.Record(existing is null ? "checklisttemplate.created" : "checklisttemplate.updated", nameof(ChecklistTemplate), template.Id.ToString(),
                    JsonSerializer.Serialize(new { template.Name, items = rows.Count }));
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return Result<ChecklistTemplateDto>.Ok(ToDto(template, rows));
            });
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<ChecklistTemplateDto>.Conflict("The checklist was changed by someone else. Reload it and try again.");
        }
        catch (DbUpdateException ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return Result<ChecklistTemplateDto>.Conflict("A checklist with that name already exists.");
        }
    }

    private async Task<Result<ChecklistTemplateDto>> CommitAsync(ChecklistTemplate t, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            var items = await db.ChecklistTemplateItems.AsNoTracking().Where(i => i.TemplateId == t.Id).OrderBy(i => i.Position).ToListAsync(ct);
            return Result<ChecklistTemplateDto>.Ok(ToDto(t, items));
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return Result<ChecklistTemplateDto>.Conflict("The checklist was changed by someone else. Reload it and try again.");
        }
        catch (DbUpdateException ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return Result<ChecklistTemplateDto>.Conflict("A checklist with that name already exists.");
        }
    }

    private static ChecklistTemplateDto ToDto(ChecklistTemplate t, IEnumerable<ChecklistTemplateItem> items) =>
        new(t.Id, t.Name, t.NameAr, t.IsArchived, items.OrderBy(i => i.Position).Select(i => new TemplateItemDto(i.Text, i.Required)).ToList(), Convert.ToBase64String(t.RowVersion));

    private static bool TryVersion(string? value, out byte[] version)
    {
        version = [];
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { version = Convert.FromBase64String(value); return version.Length > 0; }
        catch (FormatException) { return false; }
    }
}
