namespace Fms.BuildingBlocks.Security;

/// <summary>
/// Every permission the product knows. A permission is "area.action". Modules add their own constants here as they are built
/// and list them in <see cref="All"/>; system roles are defined from these in <c>SystemRoles</c>.
/// </summary>
public static class Permissions
{
    public const string UsersRead = "users.read";
    public const string UsersManage = "users.manage";
    public const string RolesRead = "roles.read";
    public const string AuditRead = "audit.read";

    public const string LocationsRead = "locations.read";
    public const string LocationsManage = "locations.manage";
    public const string AssetsRead = "assets.read";
    public const string AssetsManage = "assets.manage";
    public const string MetersRead = "meters.read";
    public const string MetersRecord = "meters.record";
    public const string MetersManage = "meters.manage";
    public const string DocumentsRead = "documents.read";
    public const string DocumentsManage = "documents.manage";
    public const string CustomFieldsManage = "customfields.manage";

    /// <summary>See every request and work order of the tenant. Without it a person sees only the requests they raised.</summary>
    public const string WorkOrdersRead = "workorders.read";
    public const string WorkOrdersRequest = "workorders.request";
    public const string WorkOrdersApprove = "workorders.approve";
    public const string WorkOrdersAssign = "workorders.assign";
    public const string WorkOrdersWork = "workorders.work";
    public const string WorkOrdersVerify = "workorders.verify";
    public const string WorkOrdersManage = "workorders.manage";

    /// <summary>See preventive maintenance schedules, the forecast calendar and compliance figures.</summary>
    public const string PmRead = "pm.read";
    public const string PmManage = "pm.manage";

    /// <summary>See items, stores, stock and the parts on work orders.</summary>
    public const string InventoryRead = "inventory.read";

    /// <summary>Maintain items and stores, receive and transfer stock, reserve parts, set reorder levels, start and submit stock takes.</summary>
    public const string InventoryManage = "inventory.manage";

    /// <summary>Issue parts to work orders and return unused ones.</summary>
    public const string InventoryIssue = "inventory.issue";

    /// <summary>Approve stock takes and make direct stock adjustments. A stock take cannot be approved by the person who submitted it.</summary>
    public const string InventoryApprove = "inventory.approve";

    /// <summary>See vendors, their contacts, contracts and contract files.</summary>
    public const string VendorsRead = "vendors.read";

    /// <summary>Maintain vendors and contracts, assign work orders to vendors, and invite vendor users.</summary>
    public const string VendorsManage = "vendors.manage";

    /// <summary>See every purchase request and order. Without it a person sees only the requests they raised.</summary>
    public const string PurchasingRead = "purchasing.read";

    public const string PurchasingRequest = "purchasing.request";

    /// <summary>Approve requests up to the first, second and third limit set in the purchasing settings. Each level includes those below it. Nobody approves their own request.</summary>
    public const string PurchasingApprove1 = "purchasing.approve1";

    public const string PurchasingApprove2 = "purchasing.approve2";
    public const string PurchasingApprove3 = "purchasing.approve3";

    /// <summary>Turn an approved request into an order, acknowledge and cancel orders.</summary>
    public const string PurchasingOrder = "purchasing.order";

    public const string PurchasingReceive = "purchasing.receive";

    /// <summary>Set the approval limits.</summary>
    public const string PurchasingManage = "purchasing.manage";

    public static IReadOnlyList<string> All { get; } =
    [
        UsersRead, UsersManage, RolesRead, AuditRead,
        LocationsRead, LocationsManage, AssetsRead, AssetsManage, MetersRead, MetersRecord, MetersManage,
        DocumentsRead, DocumentsManage, CustomFieldsManage,
        WorkOrdersRead, WorkOrdersRequest, WorkOrdersApprove, WorkOrdersAssign, WorkOrdersWork, WorkOrdersVerify, WorkOrdersManage,
        PmRead, PmManage,
        InventoryRead, InventoryManage, InventoryIssue, InventoryApprove,
        VendorsRead, VendorsManage,
        PurchasingRead, PurchasingRequest, PurchasingApprove1, PurchasingApprove2, PurchasingApprove3, PurchasingOrder, PurchasingReceive, PurchasingManage,
    ];

    public static string PolicyName(string permission) => "perm:" + permission;
}

/// <summary>Records who did what, in the same database transaction as the change itself.</summary>
public interface IAuditPort
{
    void Record(string action, string entityType, string? entityId = null, string? changeSummaryJson = null);
}
