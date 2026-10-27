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

    /// <summary>See the state of the connection to the accounting system and what has been sent.</summary>
    public const string IntegrationRead = "integration.read";

    /// <summary>Turn the accounting connection on or off, retry what failed and run a reconciliation.</summary>
    public const string IntegrationManage = "integration.manage";

    /// <summary>See floor plans, space profiles, allocations and occupancy.</summary>
    public const string SpacesRead = "spaces.read";

    /// <summary>Set space capacity, upload and pin floor plans, allocate and release space.</summary>
    public const string SpacesManage = "spaces.manage";

    /// <summary>See bookable resources and when they are free; see one's own bookings.</summary>
    public const string BookingsRead = "bookings.read";

    /// <summary>Book resources, and cancel and check in to one's own bookings.</summary>
    public const string BookingsBook = "bookings.book";

    /// <summary>Approve or reject bookings of resources that need approval (never one's own), and see everyone's bookings.</summary>
    public const string BookingsApprove = "bookings.approve";

    /// <summary>Set up resources and their rules, cancel anyone's booking, and run the calendar connection.</summary>
    public const string BookingsManage = "bookings.manage";

    /// <summary>Expect visitors and manage one's own: pre-register, cancel, issue a new pass.</summary>
    public const string VisitorsHost = "visitors.host";

    /// <summary>Work the front desk: see everyone's visitors, check them in and out, see who is on site, expect a visitor for someone else.</summary>
    public const string VisitorsReception = "visitors.reception";

    /// <summary>Keep the watchlist, let a watchlisted visitor in with a recorded reason, and set the visiting rules.</summary>
    public const string VisitorsSecurity = "visitors.security";

    /// <summary>See dashboards and the KPI pack (each group still needs its own module's read permission) and export them.</summary>
    public const string ReportsRead = "reports.read";

    /// <summary>Manage the BI feed: make and revoke access tokens for outside tools.</summary>
    public const string ReportsManage = "reports.manage";

    /// <summary>Use the vendor portal: see the orders, work and contracts of one's own vendor. Held by nobody but vendor users.</summary>
    public const string PortalUse = "portal.use";

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
        IntegrationRead, IntegrationManage,
        SpacesRead, SpacesManage,
        BookingsRead, BookingsBook, BookingsApprove, BookingsManage,
        VisitorsHost, VisitorsReception, VisitorsSecurity,
        ReportsRead, ReportsManage,
        PortalUse,
    ];

    public static string PolicyName(string permission) => "perm:" + permission;
}

/// <summary>Records who did what, in the same database transaction as the change itself.</summary>
public interface IAuditPort
{
    void Record(string action, string entityType, string? entityId = null, string? changeSummaryJson = null);
}
