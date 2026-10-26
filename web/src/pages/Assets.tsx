import { useMemo, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useAssetClasses, useAssets, useLocationTree } from '../api/hooks';
import type { Asset, AssetStatus } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Badge, Can, ErrorBanner, Field, Pager, QueryState, useEnumLabel } from '../components/ui';
import { CRITICALITIES } from '../components/AssetForm';
import { downloadText } from '../lib/csv';
import { breadcrumb, buildTree, flatten } from '../lib/locations';
import { usePrefs } from '../lib/prefs';

export const STATUSES: AssetStatus[] = ['Planned', 'Active', 'UnderMaintenance', 'OutOfService', 'Retired'];

export function statusTone(status: AssetStatus): 'ok' | 'warn' | 'bad' | 'neutral' {
  return status === 'Active' ? 'ok' : status === 'UnderMaintenance' ? 'warn' : status === 'OutOfService' ? 'bad' : 'neutral';
}

export function AssetsPage() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { name, date } = usePrefs();
  const statusLabel = useEnumLabel('assetStatus');
  const critLabel = useEnumLabel('criticality');
  const warrantyLabel = useEnumLabel('warranty');
  const [params, setParams] = useSearchParams();
  const [exportError, setExportError] = useState<unknown>(null);

  const filters = {
    q: params.get('q') ?? '',
    status: params.get('status') ?? '',
    classId: params.get('classId') ?? '',
    criticality: params.get('criticality') ?? '',
    locationId: params.get('locationId') ?? '',
    includeRetired: params.get('includeRetired') === 'true',
    page: Number(params.get('page') ?? '1') || 1,
  };

  const set = (patch: Record<string, string | boolean | number | undefined>) => {
    const next = new URLSearchParams(params);
    for (const [k, v] of Object.entries(patch)) {
      if (v === undefined || v === '' || v === false || (k === 'page' && v === 1)) next.delete(k);
      else next.set(k, String(v));
    }
    if (!('page' in patch)) next.delete('page');
    setParams(next);
  };

  const query = useAssets({
    q: filters.q || undefined, status: filters.status || undefined, classId: filters.classId || undefined, criticality: filters.criticality || undefined,
    locationId: filters.locationId || undefined, includeRetired: filters.includeRetired || undefined, page: filters.page, pageSize: 25,
  });
  const classes = useAssetClasses();
  const tree = useLocationTree();
  const places = useMemo(() => flatten(buildTree(tree.data ?? [])), [tree.data]);
  const classById = useMemo(() => new Map((classes.data ?? []).map((c) => [c.id, c])), [classes.data]);

  const exportCsv = async () => {
    setExportError(null);
    try {
      downloadText('assets.csv', await api.text('/assets/export.csv'));
    } catch (e) {
      setExportError(e);
    }
  };

  return (
    <section>
      <div className="toolbar">
        <h1>{t('nav.assets')}</h1>
        <div className="actions">
          <button type="button" onClick={() => void exportCsv()}>
            {t('assets.export')}
          </button>
          <Can permission="assets.manage">
            <Link to={filters.locationId ? `/assets/new?locationId=${filters.locationId}` : '/assets/new'} className="button primary">
              {t('assets.new')}
            </Link>
          </Can>
        </div>
      </div>
      <ErrorBanner error={exportError} />
      <form className="filters" role="search" onSubmit={(e) => e.preventDefault()}>
        <Field label={t('common.search')}>
          <input type="search" value={filters.q} onChange={(e) => set({ q: e.target.value })} placeholder={t('assets.searchHint')} />
        </Field>
        <Field label={t('assets.status')}>
          <select value={filters.status} onChange={(e) => set({ status: e.target.value })}>
            <option value="">{t('common.all')}</option>
            {STATUSES.map((s) => (
              <option key={s} value={s}>
                {statusLabel(s)}
              </option>
            ))}
          </select>
        </Field>
        <Field label={t('assets.class')}>
          <select value={filters.classId} onChange={(e) => set({ classId: e.target.value })}>
            <option value="">{t('common.all')}</option>
            {(classes.data ?? []).map((c) => (
              <option key={c.id} value={c.id}>
                {name(c)}
              </option>
            ))}
          </select>
        </Field>
        <Field label={t('assets.criticality')}>
          <select value={filters.criticality} onChange={(e) => set({ criticality: e.target.value })}>
            <option value="">{t('common.all')}</option>
            {CRITICALITIES.map((c) => (
              <option key={c} value={c}>
                {critLabel(c)}
              </option>
            ))}
          </select>
        </Field>
        <Field label={t('assets.location')}>
          <select value={filters.locationId} onChange={(e) => set({ locationId: e.target.value })}>
            <option value="">{t('common.all')}</option>
            {places.map((n) => (
              <option key={n.id} value={n.id}>
                {breadcrumb(tree.data ?? [], n.id, name)}
              </option>
            ))}
          </select>
        </Field>
        <label className="check">
          <input type="checkbox" checked={filters.includeRetired} onChange={(e) => set({ includeRetired: e.target.checked })} /> {t('assets.includeRetired')}
        </label>
      </form>
      <QueryState query={query}>
        {query.data && query.data.items.length === 0 ? (
          <p className="muted">{t('assets.empty')}</p>
        ) : (
          <div className="table-wrap">
            <table>
              <caption className="visually-hidden">{t('nav.assets')}</caption>
              <thead>
                <tr>
                  <th scope="col">{t('assets.code')}</th>
                  <th scope="col">{t('assets.name')}</th>
                  <th scope="col">{t('assets.class')}</th>
                  <th scope="col">{t('assets.location')}</th>
                  <th scope="col">{t('assets.status')}</th>
                  <th scope="col">{t('assets.criticality')}</th>
                  <th scope="col">{t('assets.warranty')}</th>
                </tr>
              </thead>
              <tbody>
                {query.data?.items.map((a: Asset) => (
                  <tr key={a.id}>
                    <td>
                      <Link to={`/assets/${a.id}`}>{a.code}</Link>
                    </td>
                    <td>{name(a)}</td>
                    <td>{classById.get(a.assetClassId) ? name(classById.get(a.assetClassId)!) : ''}</td>
                    <td>{breadcrumb(tree.data ?? [], a.locationId, name)}</td>
                    <td>
                      <Badge tone={statusTone(a.status)}>{statusLabel(a.status)}</Badge>
                    </td>
                    <td>{critLabel(a.criticality)}</td>
                    <td>
                      {a.warranty === 'None' ? '—' : `${warrantyLabel(a.warranty)} · ${date(a.warrantyExpiry)}`}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {query.data ? <Pager page={filters.page} pageSize={query.data.pageSize} total={query.data.total} onChange={(page) => set({ page })} /> : null}
        {query.data ? <p className="muted">{t('common.results', { count: query.data.total })}</p> : null}
      </QueryState>
    </section>
  );
}
