using System.Text.Json;
using Fms.Facilities.Domain;

namespace Fms.Facilities.Services;

public sealed record AssetDto(
    Guid Id, string Code, string Name, string? NameAr, Guid AssetClassId, Guid LocationId, Guid? ParentAssetId,
    AssetStatus Status, string? StatusReason, Criticality Criticality, string? Manufacturer, string? Model, string? SerialNumber,
    DateOnly? InstallDate, DateOnly? WarrantyExpiry, WarrantyState Warranty, string TagCode,
    IReadOnlyDictionary<string, JsonElement> CustomFields, DateTimeOffset? RetiredAt, string RowVersion)
{
    public static AssetDto From(Asset a, DateOnly today) => new(
        a.Id, a.Code, a.Name, a.NameAr, a.AssetClassId, a.LocationId, a.ParentAssetId, a.Status, a.StatusReason, a.Criticality,
        a.Manufacturer, a.Model, a.SerialNumber, a.InstallDate, a.WarrantyExpiry, AssetRules.WarrantyOf(a.WarrantyExpiry, today),
        AssetTag.Format(a.TagCode), JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(a.CustomFieldsJson) ?? [], a.RetiredAt,
        Convert.ToBase64String(a.RowVersion));
}

/// <summary>Fields shared by create and update. Status changes go through their own call so they are always explained and audited.</summary>
public sealed record SaveAssetRequest(
    string? Code, string? Name, string? NameAr, Guid AssetClassId, Guid LocationId, Guid? ParentAssetId, Criticality? Criticality,
    string? Manufacturer, string? Model, string? SerialNumber, DateOnly? InstallDate, DateOnly? WarrantyExpiry,
    Dictionary<string, JsonElement>? CustomFields, string? RowVersion);

/// <summary>Same fields as an update, without a row version, plus whether the asset starts in service (otherwise it starts Planned).</summary>
public sealed record CreateAssetRequest(
    string? Code, string? Name, string? NameAr, Guid AssetClassId, Guid LocationId, Guid? ParentAssetId, Criticality? Criticality,
    string? Manufacturer, string? Model, string? SerialNumber, DateOnly? InstallDate, DateOnly? WarrantyExpiry,
    Dictionary<string, JsonElement>? CustomFields, bool StartActive = false)
{
    internal SaveAssetRequest ToSave() => new(Code, Name, NameAr, AssetClassId, LocationId, ParentAssetId, Criticality, Manufacturer, Model, SerialNumber, InstallDate, WarrantyExpiry, CustomFields, null);
}

public sealed record ChangeAssetStatusRequest(AssetStatus Status, string? Reason, string? RowVersion);

public sealed record AssetQuery(
    Guid? LocationId = null, bool IncludeSublocations = true, Guid? ClassId = null, AssetStatus? Status = null, Criticality? Criticality = null,
    Guid? ParentAssetId = null, string? Search = null, DateOnly? WarrantyExpiresBefore = null, bool IncludeRetired = false, int? Page = null, int? PageSize = null);
