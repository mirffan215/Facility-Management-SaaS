import { useQuery } from '@tanstack/react-query';
import { useAuth } from '../auth/AuthContext';
import type {
  Asset, AssetChoice, AssetClass, Assignee, AssetDocument, CustomFieldDefinition, CustomFieldTarget, Location, LocationNode, Me, Meter, MeterReading, Page, RoleSummary, UserSummary, WorkCategory, WorkOrder, WorkOrderDetail, WorkSettings,
} from './types';

/** Typed read hooks. Keys include everything the request depends on so a change refetches. */
export function useMe() {
  const { api, status } = useAuth();
  return useQuery({ queryKey: ['me'], queryFn: () => api.get<Me>('/me'), enabled: status === 'signedIn', staleTime: 60_000 });
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
