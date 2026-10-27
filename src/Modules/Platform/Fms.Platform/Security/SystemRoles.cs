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

    /// <summary>People from a vendor, invited to the vendor portal. Holds no staff permission at all.</summary>
    public const string Vendor = "Vendor";

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
            Permissions.PurchasingRead, Permissions.PurchasingRequest, Permissions.PurchasingApprove1, Permissions.PurchasingApprove2,
            Permissions.PurchasingOrder, Permissions.PurchasingReceive, Permissions.PurchasingManage,
            Permissions.IntegrationRead,
            Permissions.SpacesRead, Permissions.SpacesManage,
            Permissions.BookingsRead, Permissions.BookingsBook, Permissions.BookingsApprove, Permissions.BookingsManage,
        ],
        [Technician] =
        [
            Permissions.LocationsRead, Permissions.AssetsRead, Permissions.MetersRead, Permissions.MetersRecord, Permissions.DocumentsRead,
            Permissions.WorkOrdersRead, Permissions.WorkOrdersRequest, Permissions.WorkOrdersWork, Permissions.PmRead,
            Permissions.InventoryRead, Permissions.InventoryIssue, Permissions.PurchasingRequest,
            Permissions.BookingsRead, Permissions.BookingsBook,
        ],
        [Requester] = [Permissions.LocationsRead, Permissions.WorkOrdersRequest, Permissions.BookingsRead, Permissions.BookingsBook],
        [Vendor] = [Permissions.PortalUse],
        [Viewer] = [Permissions.LocationsRead, Permissions.AssetsRead, Permissions.MetersRead, Permissions.DocumentsRead, Permissions.WorkOrdersRead, Permissions.PmRead, Permissions.InventoryRead, Permissions.VendorsRead, Permissions.SpacesRead, Permissions.BookingsRead],
    };
}
