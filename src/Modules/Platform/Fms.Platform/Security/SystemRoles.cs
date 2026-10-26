using Fms.BuildingBlocks.Security;

namespace Fms.Platform.Security;

/// <summary>Roles every tenant gets. Permission sets live in code so they stay current as modules add permissions.</summary>
public static class SystemRoles
{
    public const string Administrator = "Administrator";
    public const string FacilityManager = "Facility Manager";
    public const string Technician = "Technician";
    public const string Requester = "Requester";
    public const string Viewer = "Viewer";

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Definitions { get; } = new Dictionary<string, IReadOnlyList<string>>
    {
        [Administrator] = Permissions.All,
        [FacilityManager] =
        [
            Permissions.UsersRead, Permissions.RolesRead,
            Permissions.LocationsRead, Permissions.LocationsManage, Permissions.AssetsRead, Permissions.AssetsManage,
            Permissions.MetersRead, Permissions.MetersRecord, Permissions.MetersManage,
            Permissions.DocumentsRead, Permissions.DocumentsManage, Permissions.CustomFieldsManage,
            Permissions.WorkOrdersRead, Permissions.WorkOrdersRequest, Permissions.WorkOrdersApprove, Permissions.WorkOrdersAssign,
            Permissions.WorkOrdersWork, Permissions.WorkOrdersVerify, Permissions.WorkOrdersManage,
            Permissions.PmRead, Permissions.PmManage,
            Permissions.InventoryRead, Permissions.InventoryManage, Permissions.InventoryIssue, Permissions.InventoryApprove,
            Permissions.VendorsRead, Permissions.VendorsManage,
        ],
        [Technician] =
        [
            Permissions.LocationsRead, Permissions.AssetsRead, Permissions.MetersRead, Permissions.MetersRecord, Permissions.DocumentsRead,
            Permissions.WorkOrdersRead, Permissions.WorkOrdersRequest, Permissions.WorkOrdersWork, Permissions.PmRead,
            Permissions.InventoryRead, Permissions.InventoryIssue,
        ],
        [Requester] = [Permissions.LocationsRead, Permissions.WorkOrdersRequest],
        [Viewer] = [Permissions.LocationsRead, Permissions.AssetsRead, Permissions.MetersRead, Permissions.DocumentsRead, Permissions.WorkOrdersRead, Permissions.PmRead, Permissions.InventoryRead, Permissions.VendorsRead],
    };
}
