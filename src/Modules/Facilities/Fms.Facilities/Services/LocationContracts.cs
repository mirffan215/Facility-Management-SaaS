using System.Text.Json;
using Fms.Facilities.Domain;

namespace Fms.Facilities.Services;

public sealed record LocationDto(
    Guid Id, Guid? ParentId, LocationType Type, string Code, string Name, string? NameAr, string? Address, string? TimeZone,
    decimal? AreaSquareMetres, IReadOnlyDictionary<string, JsonElement> CustomFields, bool IsArchived, string RowVersion)
{
    public static LocationDto From(Location l) => new(
        l.Id, l.ParentId, l.Type, l.Code, l.Name, l.NameAr, l.Address, l.TimeZone, l.AreaSquareMetres,
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(l.CustomFieldsJson) ?? [], l.IsArchived, Convert.ToBase64String(l.RowVersion));
}

/// <summary>A light node for drawing the whole hierarchy in one call.</summary>
public sealed record LocationNode(Guid Id, Guid? ParentId, LocationType Type, string Code, string Name, string? NameAr);

public sealed record CreateLocationRequest(
    LocationType Type, Guid? ParentId, string? Code, string? Name, string? NameAr, string? Address, string? TimeZone,
    decimal? AreaSquareMetres, Dictionary<string, JsonElement>? CustomFields);

public sealed record UpdateLocationRequest(
    string? Code, string? Name, string? NameAr, string? Address, string? TimeZone, decimal? AreaSquareMetres,
    Dictionary<string, JsonElement>? CustomFields, string? RowVersion);

public sealed record MoveLocationRequest(Guid? NewParentId, string? RowVersion);

public sealed record LocationQuery(Guid? ParentId = null, Guid? UnderId = null, LocationType? Type = null, string? Search = null,
    bool IncludeArchived = false, int? Page = null, int? PageSize = null);
