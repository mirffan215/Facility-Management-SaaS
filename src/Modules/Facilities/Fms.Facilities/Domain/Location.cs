using System.ComponentModel.DataAnnotations;
using Fms.BuildingBlocks.Tenancy;

namespace Fms.Facilities.Domain;

public enum LocationType { Site = 1, Building = 2, Floor = 3, Space = 4 }

/// <summary>One node of the tenant's physical hierarchy: Site, Building, Floor or Space.</summary>
public sealed class Location : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? ParentId { get; set; }
    public LocationType Type { get; set; }

    /// <summary>Short unique code people use (e.g. "RUH-HQ-B1-F03"). Unique among the tenant's active locations.</summary>
    public string Code { get; set; } = "";

    public string Name { get; set; } = "";
    public string? NameAr { get; set; }

    /// <summary>Materialised path of ancestor ids, "/a/b/c/", so a whole subtree is one prefix query.</summary>
    public string Path { get; set; } = "";

    public string? Address { get; set; }

    /// <summary>IANA time zone, set on sites; descendants use their site's.</summary>
    public string? TimeZone { get; set; }

    public decimal? AreaSquareMetres { get; set; }
    public string CustomFieldsJson { get; set; } = "{}";
    public bool IsArchived { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Which parent types each location type may sit under. A Site is always a root.</summary>
public static class LocationRules
{
    public static bool CanBeChildOf(LocationType child, LocationType? parent) => (child, parent) switch
    {
        (LocationType.Site, null) => true,
        (LocationType.Building, LocationType.Site) => true,
        (LocationType.Floor, LocationType.Building) => true,
        (LocationType.Space, LocationType.Floor) => true,
        (LocationType.Space, LocationType.Building) => true,
        _ => false,
    };

    public static string PathFor(Location? parent, Guid id) => (parent?.Path ?? "/") + id.ToString("N") + "/";

    public static bool IsValidCode(string code) =>
        code.Length is >= 1 and <= 50 && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
}
