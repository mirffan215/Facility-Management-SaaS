using System.Text.Json;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Results;
using Fms.BuildingBlocks.Security;
using Fms.Facilities.Domain;
using Fms.Facilities.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fms.Facilities.Services;

public sealed record CustomFieldDto(
    Guid Id, CustomFieldTarget Target, string Key, string LabelEn, string? LabelAr, CustomFieldType DataType, bool IsRequired,
    IReadOnlyList<string> Choices, Guid? AssetClassId, int SortOrder, bool IsArchived)
{
    public static CustomFieldDto From(CustomFieldDefinition d) => new(
        d.Id, d.Target, d.Key, d.LabelEn, d.LabelAr, d.DataType, d.IsRequired, CustomFieldValidator.ChoicesOf(d), d.AssetClassId, d.SortOrder, d.IsArchived);
}

public sealed record CreateCustomFieldRequest(
    CustomFieldTarget Target, string? Key, string? LabelEn, string? LabelAr, CustomFieldType DataType, bool IsRequired,
    List<string>? Choices, Guid? AssetClassId, int SortOrder);

/// <summary>Key, target and type are fixed once created, so stored values never become invalid under their definition.</summary>
public sealed record UpdateCustomFieldRequest(string? LabelEn, string? LabelAr, bool IsRequired, List<string>? Choices, int SortOrder);

public sealed class CustomFieldService(FacilitiesDbContext db, IAuditPort audit)
{
    public async Task<IReadOnlyList<CustomFieldDto>> ListAsync(CustomFieldTarget? target, bool includeArchived, CancellationToken ct = default)
    {
        var query = db.CustomFieldDefinitions.AsNoTracking().AsQueryable();
        if (target is { } t) query = query.Where(d => d.Target == t);
        if (!includeArchived) query = query.Where(d => !d.IsArchived);
        return (await query.OrderBy(d => d.Target).ThenBy(d => d.SortOrder).ThenBy(d => d.Key).ToListAsync(ct)).Select(CustomFieldDto.From).ToList();
    }

    public async Task<Result<CustomFieldDto>> CreateAsync(CreateCustomFieldRequest r, CancellationToken ct = default)
    {
        var errors = new ValidationErrors();
        var key = r.Key?.Trim() ?? "";
        if (!CustomFieldValidator.IsValidKey(key)) errors.Add("key", "Key is required: start with a lowercase letter; lowercase letters, digits and '_' only (max 50).");
        if (!Enum.IsDefined(r.Target)) errors.Add("target", "Unknown target.");
        if (!Enum.IsDefined(r.DataType)) errors.Add("dataType", "Unknown data type.");
        ValidateLabels(errors, r.LabelEn, r.LabelAr, r.DataType, r.Choices);
        if (r.AssetClassId is not null && r.Target != CustomFieldTarget.Asset) errors.Add("assetClassId", "Only asset fields can be limited to an asset class.");
        if (r.AssetClassId is { } classId && !await db.AssetClasses.AnyAsync(c => c.Id == classId && !c.IsArchived, ct)) errors.Add("assetClassId", "Asset class not found.");
        if (errors.Any) return errors.ToResult<CustomFieldDto>();

        var def = new CustomFieldDefinition
        {
            Target = r.Target, Key = key, LabelEn = r.LabelEn!.Trim(), LabelAr = string.IsNullOrWhiteSpace(r.LabelAr) ? null : r.LabelAr.Trim(),
            DataType = r.DataType, IsRequired = r.IsRequired, ChoicesJson = JsonSerializer.Serialize(Choices(r.DataType, r.Choices)),
            AssetClassId = r.AssetClassId, SortOrder = r.SortOrder,
        };
        db.CustomFieldDefinitions.Add(def);
        audit.Record("customfield.created", nameof(CustomFieldDefinition), def.Id.ToString(), JsonSerializer.Serialize(new { def.Target, def.Key, def.DataType, def.IsRequired }));
        return await SaveAsync(def, key, ct);
    }

    public async Task<Result<CustomFieldDto>> UpdateAsync(Guid id, UpdateCustomFieldRequest r, CancellationToken ct = default)
    {
        var def = await db.CustomFieldDefinitions.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (def is null) return Result<CustomFieldDto>.NotFound();

        var errors = new ValidationErrors();
        ValidateLabels(errors, r.LabelEn, r.LabelAr, def.DataType, r.Choices);
        if (errors.Any) return errors.ToResult<CustomFieldDto>();

        def.LabelEn = r.LabelEn!.Trim();
        def.LabelAr = string.IsNullOrWhiteSpace(r.LabelAr) ? null : r.LabelAr.Trim();
        def.IsRequired = r.IsRequired;
        def.SortOrder = r.SortOrder;
        if (def.DataType == CustomFieldType.Choice) def.ChoicesJson = JsonSerializer.Serialize(Choices(def.DataType, r.Choices));
        audit.Record("customfield.updated", nameof(CustomFieldDefinition), id.ToString());
        return await SaveAsync(def, def.Key, ct);
    }

    public async Task<Result<CustomFieldDto>> SetArchivedAsync(Guid id, bool archived, CancellationToken ct = default)
    {
        var def = await db.CustomFieldDefinitions.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (def is null) return Result<CustomFieldDto>.NotFound();
        if (def.IsArchived == archived) return Result<CustomFieldDto>.Ok(CustomFieldDto.From(def));

        def.IsArchived = archived;
        audit.Record(archived ? "customfield.archived" : "customfield.restored", nameof(CustomFieldDefinition), id.ToString());
        return await SaveAsync(def, def.Key, ct);
    }

    private async Task<Result<CustomFieldDto>> SaveAsync(CustomFieldDefinition def, string key, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return Result<CustomFieldDto>.Ok(CustomFieldDto.From(def));
        }
        catch (DbUpdateException ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return Result<CustomFieldDto>.Conflict($"A custom field with key '{key}' already exists for this target.");
        }
    }

    private static List<string> Choices(CustomFieldType type, List<string>? choices) =>
        type == CustomFieldType.Choice ? choices!.Select(c => c.Trim()).ToList() : [];

    private static void ValidateLabels(ValidationErrors errors, string? labelEn, string? labelAr, CustomFieldType type, List<string>? choices)
    {
        if (string.IsNullOrWhiteSpace(labelEn) || labelEn.Trim().Length > 200) errors.Add("labelEn", "English label is required (max 200 characters).");
        if (labelAr is { Length: > 200 }) errors.Add("labelAr", "Arabic label is at most 200 characters.");
        if (type != CustomFieldType.Choice) return;

        var cleaned = (choices ?? []).Select(c => c?.Trim() ?? "").ToList();
        if (cleaned.Count is < 1 or > 100) errors.Add("choices", "A choice field needs 1 to 100 choices.");
        else if (cleaned.Any(c => c.Length is < 1 or > 100)) errors.Add("choices", "Each choice must be 1 to 100 characters.");
        else if (cleaned.Distinct(StringComparer.Ordinal).Count() != cleaned.Count) errors.Add("choices", "Choices must be unique.");
    }
}
