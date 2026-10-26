import { useMemo, useState } from 'react';
import { useSearchParams, Link } from 'react-router-dom';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import { useCustomFields, useLocation, useLocationTree } from '../api/hooks';
import type { CustomFieldValues, Location, LocationNode, LocationType } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { CustomFieldInputs } from '../components/CustomFieldInputs';
import { Can, ErrorBanner, Field, QueryState, ReasonDialog, useEnumLabel } from '../components/ui';
import { allowedChildTypes, allowedParentTypes, breadcrumb, buildTree, flatten, subtreeIds, type TreeItem } from '../lib/locations';
import { usePrefs } from '../lib/prefs';

export function LocationsPage() {
  const { t } = useTranslation();
  const tree = useLocationTree();
  const [params, setParams] = useSearchParams();
  const selectedId = params.get('id') ?? undefined;
  const [adding, setAdding] = useState<{ parent: LocationNode | null; type: LocationType } | null>(null);

  const select = (id: string | undefined) => {
    setAdding(null);
    setParams(id ? { id } : {}, { replace: false });
  };

  return (
    <section className="split">
      <aside aria-label={t('locations.tree')}>
        <div className="toolbar">
          <h1>{t('nav.locations')}</h1>
          <Can permission="locations.manage">
            <button type="button" className="primary" onClick={() => setAdding({ parent: null, type: 'Site' })}>
              {t('locations.newSite')}
            </button>
          </Can>
        </div>
        <QueryState query={tree}>
          <TreePanel nodes={tree.data ?? []} selectedId={selectedId} onSelect={select} />
        </QueryState>
      </aside>
      <div className="detail">
        {adding ? (
          <LocationForm
            key={`new-${adding.parent?.id ?? 'root'}-${adding.type}`}
            parent={adding.parent}
            type={adding.type}
            onDone={(created) => {
              setAdding(null);
              if (created) setParams({ id: created.id });
            }}
          />
        ) : selectedId ? (
          <LocationPanel
            key={selectedId}
            id={selectedId}
            nodes={tree.data ?? []}
            onAdd={(parent, type) => setAdding({ parent, type })}
            onGone={() => select(undefined)}
          />
        ) : (
          <p className="muted">{t('locations.selectOne')}</p>
        )}
      </div>
    </section>
  );
}

function TreePanel({ nodes, selectedId, onSelect }: { nodes: LocationNode[]; selectedId?: string; onSelect: (id: string) => void }) {
  const { t } = useTranslation();
  const { name } = usePrefs();
  const typeLabel = useEnumLabel('locationType');
  const roots = useMemo(() => buildTree(nodes), [nodes]);
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());
  const [query, setQuery] = useState('');

  const toggle = (id: string) =>
    setCollapsed((prev) => {
      const next = new Set(prev);
      if (!next.delete(id)) next.add(id);
      return next;
    });

  const term = query.trim().toLowerCase();
  const matches = term
    ? flatten(roots).filter((n) => [n.code, n.name, n.nameAr ?? ''].some((v) => v.toLowerCase().includes(term)))
    : [];

  const row = (item: TreeItem) => (
    <li key={item.id} role="treeitem" aria-expanded={item.children.length ? !collapsed.has(item.id) : undefined} aria-selected={item.id === selectedId}>
      <div className="tree-row" style={{ paddingInlineStart: `${item.depth * 1.25}rem` }}>
        {item.children.length > 0 ? (
          <button type="button" className="icon" aria-label={collapsed.has(item.id) ? t('common.expand') : t('common.collapse')} onClick={() => toggle(item.id)}>
            {collapsed.has(item.id) ? '▸' : '▾'}
          </button>
        ) : (
          <span className="icon-spacer" />
        )}
        <button type="button" className={`link ${item.id === selectedId ? 'selected' : ''}`} onClick={() => onSelect(item.id)}>
          <span className="code">{item.code}</span> {name(item)} <span className="muted">· {typeLabel(item.type)}</span>
        </button>
      </div>
      {item.children.length > 0 && !collapsed.has(item.id) ? <ul role="group">{item.children.map(row)}</ul> : null}
    </li>
  );

  return (
    <>
      <Field label={t('common.search')}>
        <input type="search" value={query} onChange={(e) => setQuery(e.target.value)} />
      </Field>
      {nodes.length === 0 ? <p className="muted">{t('locations.empty')}</p> : null}
      {term ? (
        matches.length === 0 ? (
          <p className="muted">{t('common.noResults')}</p>
        ) : (
          <ul className="plain">
            {matches.map((n) => (
              <li key={n.id}>
                <button type="button" className={`link ${n.id === selectedId ? 'selected' : ''}`} onClick={() => onSelect(n.id)}>
                  <span className="code">{n.code}</span> {name(n)} <span className="muted">· {breadcrumb(nodes, n.id, name)}</span>
                </button>
              </li>
            ))}
          </ul>
        )
      ) : (
        <ul role="tree" className="tree">
          {roots.map(row)}
        </ul>
      )}
    </>
  );
}

function LocationPanel({ id, nodes, onAdd, onGone }: { id: string; nodes: LocationNode[]; onAdd: (parent: LocationNode, type: LocationType) => void; onGone: () => void }) {
  const { t } = useTranslation();
  const { date, num, name } = usePrefs();
  const typeLabel = useEnumLabel('locationType');
  const { api } = useAuth();
  const query = useLocation(id);
  const client = useQueryClient();
  const [editing, setEditing] = useState(false);
  const [moving, setMoving] = useState(false);
  const [archiving, setArchiving] = useState(false);

  const refresh = () => Promise.all([client.invalidateQueries({ queryKey: ['locations'] }), client.invalidateQueries({ queryKey: ['assets'] })]);
  const archive = useMutation({
    mutationFn: ({ loc, restore }: { loc: Location; restore: boolean }) => api.post<Location>(`/locations/${loc.id}/${restore ? 'restore' : 'archive'}`, { rowVersion: loc.rowVersion }),
    onSuccess: async () => {
      setArchiving(false);
      await refresh();
    },
  });

  if (query.error instanceof ApiError && query.error.isNotFound) {
    return (
      <div>
        <p>{t('errors.notFound')}</p>
        <button type="button" onClick={onGone}>
          {t('common.back')}
        </button>
      </div>
    );
  }

  return (
    <QueryState query={query}>
      {query.data ? (
        editing ? (
          <LocationForm key={query.data.rowVersion} location={query.data} parent={nodes.find((n) => n.id === query.data!.parentId) ?? null} type={query.data.type} onDone={() => setEditing(false)} />
        ) : (
          <article>
            <header className="toolbar">
              <h2>
                {name(query.data)} {query.data.isArchived ? <span className="badge neutral">{t('common.archived')}</span> : null}
              </h2>
              <div className="actions">
                <Can permission="locations.manage">
                  {!query.data.isArchived ? (
                    <>
                      <button type="button" onClick={() => setEditing(true)}>
                        {t('common.edit')}
                      </button>
                      {query.data.type !== 'Site' ? (
                        <button type="button" onClick={() => setMoving(true)}>
                          {t('locations.move')}
                        </button>
                      ) : null}
                      <button type="button" onClick={() => setArchiving(true)}>
                        {t('common.archive')}
                      </button>
                    </>
                  ) : (
                    <button type="button" onClick={() => archive.mutate({ loc: query.data!, restore: true })}>
                      {t('common.restore')}
                    </button>
                  )}
                </Can>
              </div>
            </header>
            <p className="muted">{breadcrumb(nodes, query.data.id, name)}</p>
            <ErrorBanner error={archive.error} />
            <dl>
              <dt>{t('locations.type')}</dt>
              <dd>{typeLabel(query.data.type)}</dd>
              <dt>{t('locations.code')}</dt>
              <dd>{query.data.code}</dd>
              <dt>{t('locations.name')}</dt>
              <dd>{query.data.name}</dd>
              <dt>{t('locations.nameAr')}</dt>
              <dd {...(query.data.nameAr ? { lang: 'ar', dir: 'rtl' } : {})}>{query.data.nameAr ?? '—'}</dd>
              {query.data.address ? (<><dt>{t('locations.address')}</dt><dd>{query.data.address}</dd></>) : null}
              {query.data.timeZone ? (<><dt>{t('locations.timeZone')}</dt><dd>{query.data.timeZone}</dd></>) : null}
              {query.data.areaSquareMetres !== null ? (<><dt>{t('locations.area')}</dt><dd>{num(query.data.areaSquareMetres)} m²</dd></>) : null}
              <CustomFieldValuesView target="Location" values={query.data.customFields} date={date} />
            </dl>
            <div className="actions">
              <Can permission="locations.manage">
                {!query.data.isArchived
                  ? allowedChildTypes(query.data.type).map((type) => (
                      <button key={type} type="button" onClick={() => onAdd(nodes.find((n) => n.id === id)!, type)}>
                        {t('locations.addChild', { type: typeLabel(type) })}
                      </button>
                    ))
                  : null}
              </Can>
              <Can permission="assets.read">
                <Link to={`/assets?locationId=${id}`} className="button">
                  {t('locations.viewAssets')}
                </Link>
              </Can>
            </div>
            {moving ? <MoveDialog location={query.data} nodes={nodes} onClose={() => setMoving(false)} /> : null}
            {archiving ? (
              <ReasonDialog
                title={t('locations.archiveTitle', { name: name(query.data) })}
                label={t('common.reasonOptional')}
                confirmLabel={t('common.archive')}
                optional
                busy={archive.isPending}
                error={archive.error}
                onCancel={() => setArchiving(false)}
                onConfirm={() => archive.mutate({ loc: query.data!, restore: false })}
              />
            ) : null}
          </article>
        )
      ) : null}
    </QueryState>
  );
}

export function CustomFieldValuesView({ target, values, date }: { target: 'Location' | 'Asset'; values: CustomFieldValues; date: (v: string) => string }) {
  const { i18n, t } = useTranslation();
  const defs = useCustomFields(target);
  const arabic = i18n.language.startsWith('ar');
  const shown = (defs.data ?? []).filter((d) => values[d.key] !== undefined);
  return (
    <>
      {shown.map((d) => {
        const v = values[d.key];
        const text = d.dataType === 'Boolean' ? (v ? t('common.yes') : t('common.no')) : d.dataType === 'Date' ? date(String(v)) : String(v);
        return (
          <div key={d.key} className="kv">
            <dt>{arabic && d.labelAr ? d.labelAr : d.labelEn}</dt>
            <dd>{text}</dd>
          </div>
        );
      })}
    </>
  );
}

function MoveDialog({ location, nodes, onClose }: { location: Location; nodes: LocationNode[]; onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { name } = usePrefs();
  const client = useQueryClient();
  const [target, setTarget] = useState(location.parentId ?? '');
  const roots = useMemo(() => buildTree(nodes), [nodes]);
  const blocked = useMemo(() => subtreeIds(roots, location.id), [roots, location.id]);
  const options = flatten(roots).filter((n) => allowedParentTypes(location.type).includes(n.type) && !blocked.has(n.id));

  const move = useMutation({
    mutationFn: () => api.post<Location>(`/locations/${location.id}/move`, { newParentId: target, rowVersion: location.rowVersion }),
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: ['locations'] });
      onClose();
    },
  });

  return (
    <div className="overlay" onKeyDown={(e) => e.key === 'Escape' && onClose()}>
      <div className="dialog" role="dialog" aria-modal="true" aria-label={t('locations.move')}>
        <h2>{t('locations.moveTitle', { name: name(location) })}</h2>
        <Field label={t('locations.newParent')} error={move.error instanceof ApiError ? move.error.field('newParentId') : undefined}>
          <select value={target} onChange={(e) => setTarget(e.target.value)} autoFocus>
            <option value="" disabled>
              {t('common.choose')}
            </option>
            {options.map((n) => (
              <option key={n.id} value={n.id}>
                {breadcrumb(nodes, n.id, name)}
              </option>
            ))}
          </select>
        </Field>
        <ErrorBanner error={move.error instanceof ApiError && move.error.field('newParentId') ? null : move.error} />
        <div className="actions">
          <button type="button" onClick={onClose}>
            {t('common.cancel')}
          </button>
          <button type="button" className="primary" disabled={!target || target === location.parentId || move.isPending} onClick={() => move.mutate()}>
            {t('locations.move')}
          </button>
        </div>
      </div>
    </div>
  );
}

interface FormProps {
  location?: Location;
  parent: LocationNode | null;
  type: LocationType;
  onDone: (saved?: Location) => void;
}

function LocationForm({ location, parent, type, onDone }: FormProps) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const client = useQueryClient();
  const typeLabel = useEnumLabel('locationType');
  const defs = useCustomFields('Location');
  const [code, setCode] = useState(location?.code ?? '');
  const [name, setName] = useState(location?.name ?? '');
  const [nameAr, setNameAr] = useState(location?.nameAr ?? '');
  const [address, setAddress] = useState(location?.address ?? '');
  const [timeZone, setTimeZone] = useState(location?.timeZone ?? '');
  const [area, setArea] = useState(location?.areaSquareMetres?.toString() ?? '');
  const [custom, setCustom] = useState<CustomFieldValues>(location?.customFields ?? {});

  const zones = useMemo(() => (typeof Intl.supportedValuesOf === 'function' ? Intl.supportedValuesOf('timeZone') : []), []);

  const save = useMutation({
    mutationFn: () => {
      const body = {
        code: code.trim(), name: name.trim(), nameAr: nameAr.trim() || null, address: address.trim() || null,
        timeZone: type === 'Site' ? timeZone.trim() || null : null, areaSquareMetres: area.trim() === '' ? null : Number(area), customFields: custom,
      };
      return location
        ? api.put<Location>(`/locations/${location.id}`, { ...body, rowVersion: location.rowVersion })
        : api.post<Location>('/locations', { ...body, type, parentId: parent?.id ?? null });
    },
    onSuccess: async (saved) => {
      await client.invalidateQueries({ queryKey: ['locations'] });
      onDone(saved);
    },
  });

  const errors = save.error instanceof ApiError ? save.error.fieldErrors : {};
  const first = (field: string) => errors[field]?.[0];
  const hasFieldErrors = Object.keys(errors).length > 0;

  return (
    <form
      className="form"
      onSubmit={(e) => {
        e.preventDefault();
        save.mutate();
      }}
      noValidate
    >
      <h2>{location ? t('locations.editTitle') : t('locations.newTitle', { type: typeLabel(type) })}</h2>
      {parent ? <p className="muted">{t('locations.under', { parent: parent.name })}</p> : null}
      <Field label={t('locations.code')} error={first('code')} required hint={t('locations.codeHint')}>
        <input value={code} onChange={(e) => setCode(e.target.value)} maxLength={50} autoComplete="off" />
      </Field>
      <Field label={t('locations.name')} error={first('name')} required>
        <input value={name} onChange={(e) => setName(e.target.value)} maxLength={200} />
      </Field>
      <Field label={t('locations.nameAr')} error={first('nameAr')}>
        <input value={nameAr} onChange={(e) => setNameAr(e.target.value)} maxLength={200} lang="ar" dir="rtl" />
      </Field>
      {type === 'Site' || type === 'Building' ? (
        <Field label={t('locations.address')} error={first('address')}>
          <input value={address} onChange={(e) => setAddress(e.target.value)} maxLength={500} />
        </Field>
      ) : null}
      {type === 'Site' ? (
        <Field label={t('locations.timeZone')} error={first('timeZone')} hint={t('locations.timeZoneHint')}>
          <input value={timeZone} onChange={(e) => setTimeZone(e.target.value)} list="time-zones" />
        </Field>
      ) : null}
      <datalist id="time-zones">
        {zones.map((z) => (
          <option key={z} value={z} />
        ))}
      </datalist>
      <Field label={t('locations.area')} error={first('areaSquareMetres')}>
        <input type="number" min="0" step="any" value={area} onChange={(e) => setArea(e.target.value)} />
      </Field>
      <CustomFieldInputs definitions={defs.data ?? []} values={custom} errors={errors} onChange={setCustom} />
      <ErrorBanner error={hasFieldErrors && save.error instanceof ApiError && save.error.status === 400 ? null : save.error} />
      {hasFieldErrors ? (
        <p className="banner error" role="alert">
          {t('errors.checkFields')}
        </p>
      ) : null}
      {save.error instanceof ApiError && save.error.isConflict ? (
        <button type="button" onClick={() => void client.invalidateQueries({ queryKey: ['locations'] }).then(() => onDone())}>
          {t('common.reload')}
        </button>
      ) : null}
      <div className="actions">
        <button type="button" onClick={() => onDone()}>
          {t('common.cancel')}
        </button>
        <button type="submit" className="primary" disabled={save.isPending}>
          {t('common.save')}
        </button>
      </div>
    </form>
  );
}
