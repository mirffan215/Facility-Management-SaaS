import { useQuery } from '@tanstack/react-query';
import { useAuth } from '../auth/AuthContext';
import type {
  AuditEntry,
  AlarmEvent, AlarmRule, Consumption, EnergyDevice, EnergyMeter, EnergyReading, Tariff,
  ComplianceSummary, Obligation, ObligationDetail, Permit, PermitDetail, PermitType,
  AiSettings, AiStatus, AiUsage, AssignmentSuggestions, RepeatProblem, SimilarRequest, LicenseStatus, ProjectCosts, ProjectDetail, ProjectSummary, Lease, LeaseDocument, LeaseSchedule, LeaseSummary, LeaseTotals,
  SystemInfo,
  Workflow,
  WorkflowVersion,
  Asset, AssetChoice, ChecklistItem, ChecklistTemplate, CostLine, CostSummary, TimeEntry, WorkAttachment, WorkSignature, AssetClass, Assignee, AssetDocument, CustomFieldDefinition, CustomFieldTarget, Location, LocationNode, Me, Meter, MeterReading, NotificationItem, Page, RoleSummary, SlaPolicy, WorkCalendar, UserSummary, WorkCategory, WorkOrder, WorkOrderDetail, WorkSettings, Compliance, ErpItem, ErpSettings, ErpSummary, PurchaseOrder, PurchaseRequest, PurchasingSettings, Contract, ContractDocument, Forecast, Item, Vendor, VendorAssignment, VendorContact, Movement, PmSchedule, PmTemplate, PmTemplatePart, StockLevel, StockTake, Store, WorkParts, Allocation, FloorView, Occupancy, Availability, BookableResource, Booking, CalendarSync, Visit, VisitorSettings, WatchlistEntry, Dashboard, FeedDataset, FeedToken,
} from './types';

/** Typed read hooks. Keys include everything the request depends on so a change refetches. */
export function useMe() {
  const { api, status, offlineSession } = useAuth();
  return useQuery({ queryKey: ['me'], queryFn: () => api.get<Me>('/me'), enabled: status === 'signedIn' && !offlineSession, staleTime: 60_000 });
}

export function usePermissions() {
  const me = useMe();
  const set = new Set(me.data?.permissions ?? []);
  return { loaded: me.isSuccess, can: (permission: string) => set.has(permission) };
}

/** A vendor's person: they have the portal and none of the buyer's own screens (staff may hold the portal permission too, but have no use for it). */
export function usePortalOnly() {
  const { can, loaded } = usePermissions();
  return loaded && can('portal.use') && !can('locations.read') && !can('assets.read') && !can('workorders.read') && !can('workorders.request');
}

export function useLocationTree() {
  const { api } = useAuth();
  return useQuery({ queryKey: ['locations', 'tree'], queryFn: () => api.get<LocationNode[]>('/locations/tree') });
}

export function useLocation(id: string | undefined) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['locations', id], queryFn: () => api.get<Location>(`/locations/${id}`), enabled: !!id });
}

export function useAssetClasses() {
  const { api } = useAuth();
  return useQuery({ queryKey: ['asset-classes'], queryFn: () => api.get<AssetClass[]>('/asset-classes') });
}

export function useCustomFields(target: CustomFieldTarget) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['custom-fields', target], queryFn: () => api.get<CustomFieldDefinition[]>('/custom-fields', { target }) });
}

export function useAsset(id: string | undefined) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['assets', id], queryFn: () => api.get<Asset>(`/assets/${id}`), enabled: !!id });
}

export function useAssets(params: Record<string, string | number | boolean | undefined>) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['assets', 'list', params], queryFn: () => api.get<Page<Asset>>('/assets', params) });
}

export function useMeters(assetId: string | undefined) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['meters', assetId], queryFn: () => api.get<Meter[]>(`/assets/${assetId}/meters`), enabled: !!assetId });
}

export function useReadings(meterId: string | undefined, includeVoided: boolean) {
  const { api } = useAuth();
  return useQuery({
    queryKey: ['readings', meterId, includeVoided],
    queryFn: () => api.get<Page<MeterReading>>(`/meters/${meterId}/readings`, { includeVoided, pageSize: 20 }),
    enabled: !!meterId,
  });
}

export function useDocuments(assetId: string | undefined) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['documents', assetId], queryFn: () => api.get<AssetDocument[]>(`/assets/${assetId}/documents`), enabled: !!assetId });
}

export function useUsers() {
  const { api } = useAuth();
  return useQuery({ queryKey: ['users'], queryFn: () => api.get<UserSummary[]>('/users') });
}

export function useRoles() {
  const { api } = useAuth();
  return useQuery({ queryKey: ['roles'], queryFn: () => api.get<RoleSummary[]>('/roles') });
}

export function useWorkOrders(params: Record<string, string | number | boolean | undefined>) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['work-orders', 'list', params], queryFn: () => api.get<Page<WorkOrder>>('/work-orders', params) });
}

export function useWorkOrder(id: string | undefined) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['work-orders', id], queryFn: () => api.get<WorkOrderDetail>(`/work-orders/${id}`), enabled: !!id });
}

export function useWorkCategories(includeArchived = false) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['work-categories', includeArchived], queryFn: () => api.get<WorkCategory[]>('/work-orders/categories', { includeArchived }) });
}

export function useWorkSettings() {
  const { api } = useAuth();
  return useQuery({ queryKey: ['work-settings'], queryFn: () => api.get<WorkSettings>('/work-orders/settings') });
}

export function useAssignees(enabled: boolean) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['work-assignees'], queryFn: () => api.get<Assignee[]>('/work-orders/assignees'), enabled });
}

/** Assets a person may report a problem against: found by what they type or by a scanned label code. */
export function useAssetChoices(search: string, tag: string) {
  const { api } = useAuth();
  return useQuery({
    queryKey: ['work-assets', search, tag],
    queryFn: () => api.get<AssetChoice[]>('/work-orders/assets', { q: search || undefined, tag: tag || undefined }),
    enabled: search.trim().length >= 2 || tag.length > 0,
  });
}

/** How many unread notices the signed-in person has; refreshed every half minute while the app is open. */
export function useUnreadCount() {
  const { api, status, offlineSession } = useAuth();
  return useQuery({
    queryKey: ['notifications', 'unread'],
    queryFn: () => api.get<{ count: number }>('/notifications/unread-count'),
    enabled: status === 'signedIn' && !offlineSession,
    refetchInterval: 30_000,
  });
}

export function useNotifications(unreadOnly: boolean, page: number) {
  const { api } = useAuth();
  return useQuery({
    queryKey: ['notifications', 'list', unreadOnly, page],
    queryFn: () => api.get<Page<NotificationItem>>('/notifications', { unreadOnly: unreadOnly || undefined, page, pageSize: 25 }),
  });
}

export function useCalendars(includeArchived = true) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['sla-calendars', includeArchived], queryFn: () => api.get<WorkCalendar[]>('/work-orders/sla/calendars', { includeArchived }) });
}

export function useSlaPolicies(includeArchived = true) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['sla-policies', includeArchived], queryFn: () => api.get<SlaPolicy[]>('/work-orders/sla/policies', { includeArchived }) });
}

const evidence = (id: string | undefined, part: string, enabled = true) => ({
  queryKey: ['work-evidence', id, part],
  enabled: !!id && enabled,
});

export function useChecklist(id: string | undefined) {
  const { api } = useAuth();
  return useQuery({ ...evidence(id, 'checklist'), queryFn: () => api.get<ChecklistItem[]>(`/work-orders/${id}/checklist`) });
}

export function useChecklistTemplates(enabled = true, includeArchived = false) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['checklist-templates', includeArchived], enabled, queryFn: () => api.get<ChecklistTemplate[]>('/work-orders/checklists', { includeArchived }) });
}

export function useTimeEntries(id: string | undefined, enabled: boolean) {
  const { api } = useAuth();
  return useQuery({ ...evidence(id, 'time', enabled), queryFn: () => api.get<TimeEntry[]>(`/work-orders/${id}/time`) });
}

export function useCostLines(id: string | undefined, enabled: boolean) {
  const { api } = useAuth();
  return useQuery({ ...evidence(id, 'costs', enabled), queryFn: () => api.get<CostLine[]>(`/work-orders/${id}/costs`) });
}

export function useCostSummary(id: string | undefined, enabled: boolean) {
  const { api } = useAuth();
  return useQuery({ ...evidence(id, 'summary', enabled), queryFn: () => api.get<CostSummary>(`/work-orders/${id}/cost-summary`) });
}

export function useSignatures(id: string | undefined) {
  const { api } = useAuth();
  return useQuery({ ...evidence(id, 'signatures'), queryFn: () => api.get<WorkSignature[]>(`/work-orders/${id}/signatures`) });
}

export function useAttachments(id: string | undefined) {
  const { api } = useAuth();
  return useQuery({ ...evidence(id, 'attachments'), queryFn: () => api.get<WorkAttachment[]>(`/work-orders/${id}/attachments`) });
}

export function usePmTemplates(includeArchived = false) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['pm', 'templates', includeArchived], queryFn: () => api.get<PmTemplate[]>('/pm/templates', { includeArchived }) });
}

export function usePmSchedules(includeArchived = false) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['pm', 'schedules', includeArchived], queryFn: () => api.get<PmSchedule[]>('/pm/schedules', { includeArchived }) });
}

export function usePmForecast(params: Record<string, string | undefined>) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['pm', 'forecast', params], queryFn: () => api.get<Forecast>('/pm/forecast', params) });
}

export function usePmCompliance(params: Record<string, string | undefined>) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['pm', 'compliance', params], queryFn: () => api.get<Compliance>('/pm/compliance', params) });
}

// ---------------- inventory ----------------

export function useStores(includeArchived = false, enabled = true) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['inventory', 'stores', includeArchived], enabled, queryFn: () => api.get<Store[]>('/inventory/stores', { includeArchived }) });
}

export function useItems(params: Record<string, string | number | boolean | undefined>, enabled = true) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['inventory', 'items', params], enabled, queryFn: () => api.get<Page<Item>>('/inventory/items', params) });
}

export function useStock(params: Record<string, string | number | boolean | undefined>) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['inventory', 'stock', params], queryFn: () => api.get<Page<StockLevel>>('/inventory/stock', params) });
}

export function useMovements(params: Record<string, string | number | boolean | undefined>, enabled = true) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['inventory', 'movements', params], enabled, queryFn: () => api.get<Page<Movement>>('/inventory/movements', params) });
}

export function useStockTakes() {
  const { api } = useAuth();
  return useQuery({ queryKey: ['inventory', 'takes'], queryFn: () => api.get<StockTake[]>('/inventory/stock-takes') });
}

export function useStockTake(id: string | undefined) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['inventory', 'take', id], enabled: !!id, queryFn: () => api.get<StockTake>(`/inventory/stock-takes/${id}`) });
}

export function useWorkParts(workOrderId: string | undefined, enabled: boolean) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['inventory', 'work-parts', workOrderId], enabled: enabled && !!workOrderId, queryFn: () => api.get<WorkParts>(`/work-orders/${workOrderId}/parts`) });
}

export function usePmTemplateParts(templateId: string | undefined, enabled: boolean) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['inventory', 'pm-parts', templateId], enabled: enabled && !!templateId, queryFn: () => api.get<PmTemplatePart[]>(`/inventory/pm-templates/${templateId}/parts`) });
}

// ---------------- vendors and contracts ----------------

export function useVendors(params: Record<string, string | number | boolean | undefined>, enabled = true) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['vendors', 'list', params], enabled, queryFn: () => api.get<Page<Vendor>>('/vendors', params) });
}

export function useVendorContacts(id: string | undefined) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['vendors', 'contacts', id], enabled: !!id, queryFn: () => api.get<VendorContact[]>(`/vendors/${id}/contacts`) });
}

export function useContracts(params: Record<string, string | number | boolean | undefined>, enabled = true) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['vendors', 'contracts', params], enabled, queryFn: () => api.get<Page<Contract>>('/contracts', params) });
}

export function useContract(id: string | undefined) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['vendors', 'contract', id], enabled: !!id, queryFn: () => api.get<Contract>(`/contracts/${id}`) });
}

export function useContractDocuments(id: string | undefined) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['vendors', 'contract-documents', id], enabled: !!id, queryFn: () => api.get<ContractDocument[]>(`/contracts/${id}/documents`) });
}

export function useVendorAssignment(workOrderId: string | undefined) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['vendors', 'assignment', workOrderId], enabled: !!workOrderId, queryFn: () => api.get<VendorAssignment | undefined>(`/work-orders/${workOrderId}/vendor`) });
}

// ---------------- purchasing ----------------

export function usePurchasingSettings(enabled = true) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['purchasing', 'settings'], enabled, queryFn: () => api.get<PurchasingSettings>('/purchasing/settings') });
}

export function useRequests(params: Record<string, string | number | boolean | undefined>) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['purchasing', 'requests', params], queryFn: () => api.get<Page<PurchaseRequest>>('/purchasing/requests', params) });
}

export function useRequest(id: string | undefined) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['purchasing', 'request', id], enabled: !!id, queryFn: () => api.get<PurchaseRequest>(`/purchasing/requests/${id}`) });
}

export function useOrders(params: Record<string, string | number | boolean | undefined>, enabled = true) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['purchasing', 'orders', params], enabled, queryFn: () => api.get<Page<PurchaseOrder>>('/purchasing/orders', params) });
}

export function useOrder(id: string | undefined) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['purchasing', 'order', id], enabled: !!id, queryFn: () => api.get<PurchaseOrder>(`/purchasing/orders/${id}`) });
}

// ---------------- accounting integration ----------------

export function useErpSettings() {
  const { api } = useAuth();
  return useQuery({ queryKey: ['integration', 'settings'], queryFn: () => api.get<ErpSettings>('/integration/erp/settings') });
}

export function useErpSummary() {
  const { api } = useAuth();
  return useQuery({ queryKey: ['integration', 'summary'], queryFn: () => api.get<ErpSummary>('/integration/erp/summary'), refetchInterval: 15_000 });
}

export function useErpItems(params: Record<string, string | number | boolean | undefined>) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['integration', 'items', params], queryFn: () => api.get<Page<ErpItem>>('/integration/erp/items', params), refetchInterval: 15_000 });
}

export function useFloorView(floorId: string | undefined) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['spaces', 'floor', floorId], enabled: !!floorId, queryFn: () => api.get<FloorView>(`/spaces/floors/${floorId}`) });
}

export function useOccupancy(locationId: string | undefined, on: string) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['spaces', 'occupancy', locationId, on], enabled: !!locationId, queryFn: () => api.get<Occupancy>(`/spaces/occupancy/${locationId}`, { on: on || undefined }) });
}

export function useAllocations(params: Record<string, string | undefined>) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['spaces', 'allocations', params], queryFn: () => api.get<Allocation[]>('/allocations', params) });
}

export function useResources(params: Record<string, string | boolean | undefined> = {}) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['bookings', 'resources', params], queryFn: () => api.get<BookableResource[]>('/resources', params) });
}

export function useAvailability(resourceId: string | undefined, from: string, to: string) {
  const { api } = useAuth();
  return useQuery({
    queryKey: ['bookings', 'availability', resourceId, from, to],
    enabled: !!resourceId,
    queryFn: () => api.get<Availability>('/bookings/availability', { resourceId, from, to }),
  });
}

export function useMyBookings() {
  const { api } = useAuth();
  return useQuery({ queryKey: ['bookings', 'mine'], queryFn: () => api.get<Booking[]>('/bookings/mine') });
}

export function usePendingBookings(enabled: boolean) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['bookings', 'pending'], enabled, queryFn: () => api.get<Booking[]>('/bookings', { pendingOnly: true }) });
}

export function useCalendarSync(enabled: boolean) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['bookings', 'calendar-sync'], enabled, queryFn: () => api.get<CalendarSync>('/bookings/calendar-sync') });
}

export function useCalendarFeed() {
  const { api } = useAuth();
  return useQuery({ queryKey: ['bookings', 'feed'], queryFn: () => api.get<{ exists: boolean; createdAt: string | null; lastUsedAt: string | null }>('/bookings/calendar-feed') });
}

export function useMyVisits() {
  const { api } = useAuth();
  return useQuery({ queryKey: ['visitors', 'mine'], queryFn: () => api.get<Visit[]>('/visits/mine') });
}

export function useDeskVisits(params: Record<string, string | undefined>, enabled: boolean) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['visitors', 'desk', params], enabled, queryFn: () => api.get<Visit[]>('/visits', params), refetchInterval: 30_000 });
}

export function useOnSite(enabled: boolean) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['visitors', 'on-site'], enabled, queryFn: () => api.get<Visit[]>('/visits/on-site'), refetchInterval: 30_000 });
}

export function useWatchlist(enabled: boolean) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['visitors', 'watchlist'], enabled, queryFn: () => api.get<WatchlistEntry[]>('/visits/watchlist') });
}

export function useVisitorSettings(enabled: boolean) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['visitors', 'settings'], enabled, queryFn: () => api.get<VisitorSettings>('/visits/settings') });
}

/** The sites of the organisation, for choosing where a visitor is going. */
export function useSites() {
  const tree = useLocationTree();
  return { ...tree, data: (tree.data ?? []).filter((n) => n.type === 'Site') };
}

export function useDashboard(params: Record<string, string | undefined>) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['reports', 'dashboard', params], queryFn: () => api.get<Dashboard>('/reports/dashboard', params) });
}

export function useFeedTokens() {
  const { api } = useAuth();
  return useQuery({ queryKey: ['reports', 'feed', 'tokens'], queryFn: () => api.get<FeedToken[]>('/reports/feed/tokens') });
}

export function useFeedDatasets() {
  const { api } = useAuth();
  return useQuery({ queryKey: ['reports', 'feed', 'datasets'], queryFn: () => api.get<FeedDataset[]>('/reports/feed/datasets') });
}

export function useWorkflow() {
  const { api } = useAuth();
  return useQuery({ queryKey: ['workflow'], queryFn: () => api.get<Workflow>('/work-orders/workflow') });
}

export function useWorkflowHistory(enabled: boolean) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['workflow', 'history'], enabled, queryFn: () => api.get<WorkflowVersion[]>('/work-orders/workflow/history') });
}

export function useAudit(params: Record<string, string | number | undefined>) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['audit', params], queryFn: () => api.get<Page<AuditEntry>>('/audit', params) });
}

export function useAuditActions(params: Record<string, string | undefined>) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['audit', 'actions', params], queryFn: () => api.get<string[]>('/audit/actions', params) });
}

export function useSystemInfo() {
  const { api } = useAuth();
  return useQuery({ queryKey: ['system-info'], queryFn: () => api.get<SystemInfo>('/system/info'), staleTime: 5 * 60_000 });
}

export function useLicense() {
  const { api } = useAuth();
  return useQuery({ queryKey: ['license'], queryFn: () => api.get<LicenseStatus>('/license') });
}

export function useComplianceSummary() {
  const { api } = useAuth();
  return useQuery({ queryKey: ['compliance', 'summary'], queryFn: () => api.get<ComplianceSummary>('/compliance/summary') });
}

export function useObligations(params: Record<string, string | number | boolean | undefined>) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['compliance', 'obligations', params], queryFn: () => api.get<Page<Obligation>>('/compliance/obligations', params) });
}

export function useObligation(id: string | null) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['compliance', 'obligation', id], enabled: !!id, queryFn: () => api.get<ObligationDetail>(`/compliance/obligations/${id}`) });
}

export function usePermitTypes(includeArchived = false) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['compliance', 'permit-types', includeArchived], queryFn: () => api.get<PermitType[]>('/compliance/permit-types', { includeArchived }) });
}

export function usePermits(params: Record<string, string | number | boolean | undefined>, mine: boolean) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['compliance', 'permits', mine, params], queryFn: () => api.get<Page<Permit>>(mine ? '/permits/mine' : '/permits', params) });
}

export function usePermit(id: string | null) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['compliance', 'permit', id], enabled: !!id, queryFn: () => api.get<PermitDetail>(`/permits/${id}`) });
}

export function useEnergyMeters(params: Record<string, string | number | boolean | undefined>) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['energy', 'meters', params], queryFn: () => api.get<Page<EnergyMeter>>('/energy/meters', params) });
}

export function useEnergyReadings(meterId: string | null, page: number) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['energy', 'readings', meterId, page], enabled: !!meterId, queryFn: () => api.get<Page<EnergyReading>>(`/energy/meters/${meterId}/readings`, { page, pageSize: 20 }) });
}

export function useConsumption(params: Record<string, string | undefined>) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['energy', 'consumption', params], queryFn: () => api.get<Consumption>('/energy/consumption', params) });
}

export function useTariffs() {
  const { api } = useAuth();
  return useQuery({ queryKey: ['energy', 'tariffs'], queryFn: () => api.get<Tariff[]>('/energy/tariffs') });
}

export function useEnergyDevices(enabled: boolean) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['energy', 'devices'], enabled, queryFn: () => api.get<EnergyDevice[]>('/energy/devices') });
}

export function useAlarmRules() {
  const { api } = useAuth();
  return useQuery({ queryKey: ['energy', 'alarm-rules'], queryFn: () => api.get<AlarmRule[]>('/energy/alarms/rules') });
}

export function useAlarmEvents() {
  const { api } = useAuth();
  return useQuery({ queryKey: ['energy', 'alarm-events'], queryFn: () => api.get<Page<AlarmEvent>>('/energy/alarms/events', { pageSize: 20 }) });
}

export function useProjects(params: Record<string, string | number | boolean | undefined>) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['projects', 'list', params], queryFn: () => api.get<Page<ProjectSummary>>('/projects', params) });
}

export function useProject(id: string | null) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['projects', 'detail', id], enabled: !!id, queryFn: () => api.get<ProjectDetail>(`/projects/${id}`) });
}

export function useProjectCosts(id: string | null) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['projects', 'costs', id], enabled: !!id, queryFn: () => api.get<ProjectCosts>(`/projects/${id}/costs`) });
}

export function useLeaseTotals() {
  const { api } = useAuth();
  return useQuery({ queryKey: ['leases', 'totals'], queryFn: () => api.get<LeaseTotals>('/leases/summary') });
}

export function useLeases(params: Record<string, string | number | boolean | undefined>) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['leases', 'list', params], queryFn: () => api.get<Page<LeaseSummary>>('/leases', params) });
}

export function useLease(id: string | null) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['leases', 'detail', id], enabled: !!id, queryFn: () => api.get<Lease>(`/leases/${id}`) });
}

export function useLeaseSchedule(id: string | null) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['leases', 'schedule', id], enabled: !!id, queryFn: () => api.get<LeaseSchedule>(`/leases/${id}/schedule`) });
}

/** Whether AI suggestions are offered to this person. Quiet when they have no right to ask: the answer is then simply "off". */
export function useAiStatus() {
  const { api } = useAuth();
  const { can } = usePermissions();
  return useQuery({ queryKey: ['ai', 'status'], enabled: can('ai.use') || can('ai.manage'), queryFn: () => api.get<AiStatus>('/ai/status'), staleTime: 60_000 });
}

export function useAiSettings() {
  const { api } = useAuth();
  return useQuery({ queryKey: ['ai', 'settings'], queryFn: () => api.get<AiSettings>('/ai/settings') });
}

export function useAiUsage() {
  const { api } = useAuth();
  return useQuery({ queryKey: ['ai', 'usage'], queryFn: () => api.get<AiUsage>('/ai/usage') });
}

export function useRepeatProblems(enabled: boolean) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['work-orders', 'repeat-problems'], enabled, queryFn: () => api.get<RepeatProblem[]>('/work-orders/repeat-problems') });
}

export function useLeaseDocuments(id: string | null) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['leases', 'documents', id], enabled: !!id, queryFn: () => api.get<LeaseDocument[]>(`/leases/${id}/documents`) });
}

export function useSimilarRequests(params: { locationId: string; assetId: string | null; title: string }) {
  const { api } = useAuth();
  const { can } = usePermissions();
  const title = params.title.trim();
  const enabled = can('workorders.read') && title.length >= 5 && (!!params.locationId || !!params.assetId);
  return useQuery({
    queryKey: ['work-orders', 'similar', params.locationId, params.assetId, title],
    enabled,
    staleTime: 30_000,
    queryFn: () => api.get<SimilarRequest[]>('/work-orders/similar', { title, locationId: params.locationId || undefined, assetId: params.assetId ?? undefined }),
  });
}

export function useAssignmentSuggestions(id: string, enabled: boolean) {
  const { api } = useAuth();
  return useQuery({ queryKey: ['work-orders', 'suggestions', id], enabled, staleTime: 30_000, queryFn: () => api.get<AssignmentSuggestions>(`/work-orders/${id}/suggestions`) });
}
