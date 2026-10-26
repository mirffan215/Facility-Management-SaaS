import { useQuery } from '@tanstack/react-query';
import { useAuth } from '../auth/AuthContext';
import type {
  Asset, AssetChoice, ChecklistItem, ChecklistTemplate, CostLine, CostSummary, TimeEntry, WorkAttachment, WorkSignature, AssetClass, Assignee, AssetDocument, CustomFieldDefinition, CustomFieldTarget, Location, LocationNode, Me, Meter, MeterReading, NotificationItem, Page, RoleSummary, SlaPolicy, WorkCalendar, UserSummary, WorkCategory, WorkOrder, WorkOrderDetail, WorkSettings, Compliance, PurchaseOrder, PurchaseRequest, PurchasingSettings, Contract, ContractDocument, Forecast, Item, Vendor, VendorAssignment, VendorContact, Movement, PmSchedule, PmTemplate, PmTemplatePart, StockLevel, StockTake, Store, WorkParts,
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
