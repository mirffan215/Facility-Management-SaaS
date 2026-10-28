import { useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import { useAssetChoices, useLocationTree, usePermissions, useSimilarRequests, useWorkCategories } from '../api/hooks';
import type { WorkOrder, WorkOrderType, WorkPriority } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { ErrorBanner, Field, useEnumLabel } from '../components/ui';
import { breadcrumb, buildTree, flatten } from '../lib/locations';
import { TriageSuggest } from '../components/AiAssist';
import { usePrefs } from '../lib/prefs';
import { PRIORITIES } from './WorkOrders';

/** Raise a request. Arriving from a scanned label (?tag=) finds the asset for the person, who then only describes the problem. */
export function WorkOrderCreatePage() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { name: nameOf } = usePrefs();
  const { can } = usePermissions();
  const navigate = useNavigate();
  const client = useQueryClient();
  const [params] = useSearchParams();
  const priorityLabel = useEnumLabel('workPriority');
  const typeLabel = useEnumLabel('workType');

  const [title, setTitle] = useState('');
  const [description, setDescription] = useState('');
  const [type, setType] = useState<WorkOrderType>('Corrective');
  const [priority, setPriority] = useState<WorkPriority | ''>('');
  const [categoryId, setCategoryId] = useState('');
  const [locationId, setLocationId] = useState(params.get('locationId') ?? '');
  const [dueAt, setDueAt] = useState('');
  const [assetSearch, setAssetSearch] = useState('');
  const [assetId, setAssetId] = useState<string | null>(null);
  const [assetLabel, setAssetLabel] = useState('');
  const tag = params.get('tag') ?? '';

  const tree = useLocationTree();
  const categories = useWorkCategories();
  const choices = useAssetChoices(assetSearch, tag && !assetId ? tag : '');
  const places = useMemo(() => flatten(buildTree(tree.data ?? [])), [tree.data]);
  const dispatcher = can('workorders.assign') || can('workorders.manage');
  const similar = useSimilarRequests({ locationId, assetId, title });

  // A scanned label resolves to exactly one asset: select it and take its place.
  useEffect(() => {
    if (tag && !assetId && choices.data?.length === 1) {
      const a = choices.data[0];
      setAssetId(a.id);
      setAssetLabel(`${a.code} · ${a.name}`);
      if (a.locationId) setLocationId(a.locationId);
    }
  }, [tag, assetId, choices.data]);

  const create = useMutation({
    mutationFn: () =>
      api.post<WorkOrder>('/work-orders', {
        title: title.trim(),
        description: description.trim() || null,
        type: dispatcher ? type : undefined,
        priority: priority || undefined,
        categoryId: categoryId || null,
        locationId: locationId || null,
        assetId,
        dueAt: dueAt ? new Date(dueAt).toISOString() : null,
      }),
    onSuccess: async (created) => {
      await client.invalidateQueries({ queryKey: ['work-orders'] });
      navigate(`/work-orders/${created.id}`);
    },
  });
  const error = create.error instanceof ApiError ? create.error : null;
  const first = (field: string) => error?.field(field);

  const pick = (id: string) => {
    const a = choices.data?.find((x) => x.id === id);
    if (!a) return;
    setAssetId(a.id);
    setAssetLabel(`${a.code} · ${a.name}`);
    if (a.locationId) setLocationId(a.locationId);
    setAssetSearch('');
  };

  return (
    <section className="narrow">
      <h1>{t('work.newTitle')}</h1>
      <form
        onSubmit={(e) => {
          e.preventDefault();
          create.mutate();
        }}
      >
        <Field label={t('work.titleField')} error={first('title')} required hint={t('work.titleHint')}>
          <input value={title} onChange={(e) => setTitle(e.target.value)} maxLength={200} autoFocus />
        </Field>
        {similar.data && similar.data.length > 0 ? (
          <div className="banner" role="group" aria-label={t('work.similar.title')}>
            <strong>{t('work.similar.title')}</strong> <span className="muted">{t('work.similar.help')}</span>
            <ul className="plain">
              {similar.data.map((r) => (
                <li key={r.id}><Link to={`/work-orders/${r.id}`}><bdi>{r.number}</bdi> · <bdi>{r.title}</bdi></Link></li>
              ))}
            </ul>
          </div>
        ) : null}
        <Field label={t('work.description')} error={first('description')}>
          <textarea value={description} onChange={(e) => setDescription(e.target.value)} rows={4} maxLength={4000} />
        </Field>
        <TriageSuggest
          title={title}
          description={description}
          onApply={(s) => {
            if (s.categoryId) setCategoryId(s.categoryId);
            if (s.priority) setPriority(s.priority);
          }}
        />

        {assetId ? (
          <p>
            <strong>{t('work.asset')}:</strong> <bdi>{assetLabel}</bdi>{' '}
            <button type="button" onClick={() => { setAssetId(null); setAssetLabel(''); }}>
              {t('work.changeAsset')}
            </button>
          </p>
        ) : (
          <>
            <Field label={t('work.findAsset')} error={first('assetId')} hint={t('work.findAssetHint')}>
              <input value={assetSearch} onChange={(e) => setAssetSearch(e.target.value)} autoComplete="off" />
            </Field>
            {assetSearch.trim().length >= 2 && choices.data ? (
              choices.data.length === 0 ? (
                <p className="muted">{t('work.noAssetFound')}</p>
              ) : (
                <ul className="choices" aria-label={t('work.findAsset')}>
                  {choices.data.map((a) => (
                    <li key={a.id}>
                      <button type="button" onClick={() => pick(a.id)}>
                        <bdi>{a.code} · {a.name}</bdi>
                      </button>
                    </li>
                  ))}
                </ul>
              )
            ) : null}
          </>
        )}

        <Field label={t('work.where')} error={first('locationId')} required>
          <select value={locationId} onChange={(e) => setLocationId(e.target.value)}>
            <option value="">{t('common.choose')}</option>
            {places.map((n) => (
              <option key={n.id} value={n.id}>
                {breadcrumb(tree.data ?? [], n.id, nameOf)}
              </option>
            ))}
          </select>
        </Field>

        <Field label={t('work.category')} error={first('categoryId')}>
          <select value={categoryId} onChange={(e) => setCategoryId(e.target.value)}>
            <option value="">{t('common.none')}</option>
            {(categories.data ?? []).map((c) => (
              <option key={c.id} value={c.id}>
                {nameOf(c)}
              </option>
            ))}
          </select>
        </Field>
        <Field label={t('work.priority')} error={first('priority')} hint={t('work.priorityHint')}>
          <select value={priority} onChange={(e) => setPriority(e.target.value as WorkPriority | '')}>
            <option value="">{t('work.priorityDefault')}</option>
            {PRIORITIES.map((p) => (
              <option key={p} value={p}>
                {priorityLabel(p)}
              </option>
            ))}
          </select>
        </Field>
        {dispatcher ? (
          <>
            <Field label={t('work.type')} error={first('type')}>
              <select value={type} onChange={(e) => setType(e.target.value as WorkOrderType)}>
                {(['Corrective', 'Emergency', 'Planned'] as WorkOrderType[]).map((x) => (
                  <option key={x} value={x}>
                    {typeLabel(x)}
                  </option>
                ))}
              </select>
            </Field>
            <Field label={t('work.due')} error={first('dueAt')}>
              <input type="datetime-local" value={dueAt} onChange={(e) => setDueAt(e.target.value)} />
            </Field>
          </>
        ) : null}

        <ErrorBanner error={error?.status === 400 ? null : create.error} />
        {error?.status === 400 ? <p className="banner error" role="alert">{t('errors.checkFields')}</p> : null}
        <div className="actions">
          <button type="button" onClick={() => navigate(-1)}>
            {t('common.cancel')}
          </button>
          <button type="submit" className="primary" disabled={create.isPending}>
            {t('work.submit')}
          </button>
        </div>
      </form>
    </section>
  );
}
