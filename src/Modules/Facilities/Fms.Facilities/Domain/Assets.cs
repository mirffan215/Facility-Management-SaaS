using System.ComponentModel.DataAnnotations;
using Fms.BuildingBlocks.Tenancy;

namespace Fms.Facilities.Domain;

/// <summary>A category of asset (HVAC, Electrical, Lift...). Classes can be nested; custom fields can be scoped to a class.</summary>
public sealed class AssetClass : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid? ParentId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? NameAr { get; set; }
    public bool IsArchived { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];
}
