import { useQuery } from '@tanstack/react-query';
import { useAuth } from '../auth/AuthContext';
import type {
  Asset, AssetClass, AssetDocument, CustomFieldDefinition, CustomFieldTarget, Location, LocationNode, Me, Meter, MeterReading, Page,
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
