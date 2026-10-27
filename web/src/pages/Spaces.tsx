import { useEffect, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import { useAllocations, useFloorView, useLocationTree, useOccupancy, usePermissions, useUsers } from '../api/hooks';
import type { Allocation, AllocationState, FloorView, OccupantKind, PinKind, SpaceInfo, SpaceKind } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Dialog } from '../components/InventoryBits';
import { Badge, ErrorBanner, Field, QueryState, ReasonDialog, useEnumLabel } from '../components/ui';
import { buildTree, flatten } from '../lib/locations';
import { usePrefs } from '../lib/prefs';

const TABS = ['plans', 'spaces', 'allocations', 'occupancy'] as const;
const KINDS: SpaceKind[] = ['Office', 'Meeting', 'Desk', 'Parking', 'Storage', 'Common', 'Other'];
const asError = (e: unknown) => (e instanceof ApiError ? e : null);

export function SpacesPage() {
  const { t } = useTranslation();
  const [params, setParams] = useSearchParams();
  const tab = TABS.find((x) => x === params.get('tab')) ?? 'plans';
  return (
    <section>
      <h1>{t('spaces.title')}</h1>
      <p className="muted">{t('spaces.intro')}</p>
      <div className="tabs" role="tablist" aria-label={t('spaces.sections')}>
        {TABS.map((x) => (
          <button key={x} role="tab" type="button" aria-selected={tab === x} className={tab === x ? 'active' : ''} onClick={() => setParams({ tab: x })}>
            {t(`spaces.tab.${x}`)}
          </button>
        ))}
      </div>
      <div role="tabpanel">{tab === 'plans' ? <PlansTab /> : tab === 'spaces' ? <SpacesTab /> : tab === 'allocations' ? <AllocationsTab /> : <OccupancyTab />}</div>
    </section>
  );
}

function useRefresh() {
  const client = useQueryClient();
  return () => client.invalidateQueries({ queryKey: ['spaces'] });
}

/** Picks a place from the location hierarchy, indented by depth. */
function PlacePicker({ label, value, onChange, types }: { label: string; value: string; onChange: (id: string) => void; types?: string[] }) {
  const { t } = useTranslation();
  const { name } = usePrefs();
  const tree = useLocationTree();
  const items = useMemo(() => flatten(buildTree(tree.data ?? [])).filter((i) => !types || types.includes(i.type)), [tree.data, types]);
  return (
    <Field label={label}>
      <select value={value} onChange={(e) => onChange(e.target.value)}>
        <option value="">{t('common.choose')}</option>
        {items.map((i) => (
          <option key={i.id} value={i.id}>{`${'  '.repeat(i.depth)}${i.code} · ${name(i)}`}</option>
        ))}
      </select>
    </Field>
  );
}

// ---------------- plans ----------------

function PlansTab() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { can } = usePermissions();
  const refresh = useRefresh();
  const [floorId, setFloorId] = useState('');
  const view = useFloorView(floorId || undefined);
  const upload = useMutation({
    mutationFn: async (file: File) => {
      const form = new FormData();
      form.append('file', file);
      return api.upload(`/spaces/floors/${floorId}/plan`, form);
    },
    onSuccess: () => refresh(),
  });
  return (
    <>
      <div className="filters">
        <PlacePicker label={t('spaces.floor')} value={floorId} onChange={setFloorId} types={['Floor']} />
        {can('spaces.manage') && floorId ? (
          <Field label={view.data?.plan ? t('spaces.replacePlan') : t('spaces.uploadPlan')} hint={t('spaces.planHint')}>
            <input
              type="file"
              accept="image/png,image/jpeg,image/webp"
              onChange={(e) => {
                const f = e.target.files?.[0];
                if (f) upload.mutate(f);
                e.target.value = '';
              }}
            />
          </Field>
        ) : null}
      </div>
      <ErrorBanner error={upload.error} />
      {!floorId ? <p className="muted">{t('spaces.chooseFloor')}</p> : null}
      <QueryState query={view}>{view.data ? <PlanView key={view.data.plan?.id ?? 'none'} view={view.data} /> : null}</QueryState>
    </>
  );
}

interface DraftPin {
  kind: PinKind;
  targetId: string;
  code: string;
  name: string;
  x: number;
  y: number;
}

function PlanView({ view }: { view: FloorView }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { can } = usePermissions();
  const { name } = usePrefs();
  const refresh = useRefresh();
  const [src, setSrc] = useState<string | null>(null);
  const [pins, setPins] = useState<DraftPin[]>(view.pins);
  const [picked, setPicked] = useState('');
  const [selected, setSelected] = useState<string | null>(null);
  const manage = can('spaces.manage');
  const dirty = JSON.stringify(pins.map(({ kind, targetId, x, y }) => ({ kind, targetId, x, y }))) !== JSON.stringify(view.pins.map(({ kind, targetId, x, y }) => ({ kind, targetId, x, y })));

  useEffect(() => {
    if (!view.plan) return;
    let url: string | null = null;
    let cancelled = false;
    api.blob(`/spaces/plans/${view.plan.id}/image`).then(({ blob }) => {
      if (cancelled) return;
      url = URL.createObjectURL(blob);
      setSrc(url);
    }).catch(() => setSrc(null));
    return () => {
      cancelled = true;
      if (url) URL.revokeObjectURL(url);
    };
  }, [api, view.plan]);

  const save = useMutation({
    mutationFn: () => api.put(`/spaces/floors/${view.floorId}/pins`, { pins: pins.map(({ kind, targetId, x, y }) => ({ kind, targetId, x, y })) }),
    onSuccess: () => refresh(),
  });
  const free = view.spaces.filter((s) => !pins.some((p) => p.kind === 'Space' && p.targetId === s.locationId));
  const clamp = (v: number) => Math.min(1, Math.max(0, v));
  const set = (target: string, patch: Partial<DraftPin>) => setPins((all) => all.map((p) => (p.targetId === target ? { ...p, ...patch } : p)));

  if (!view.plan) return <p className="muted">{t('spaces.noPlan')}</p>;
  return (
    <div className="plan-layout">
      <div className="plan-frame">
        {src ? (
          <div className="plan-canvas" style={{ position: 'relative', display: 'inline-block' }}>
            <img
              src={src}
              alt={t('spaces.planAlt', { floor: view.floorName })}
              style={{ maxWidth: '100%', display: 'block' }}
              onClick={(e) => {
                if (!manage || !selected) return;
                const box = e.currentTarget.getBoundingClientRect();
                set(selected, { x: clamp((e.clientX - box.left) / box.width), y: clamp((e.clientY - box.top) / box.height) });
              }}
            />
            {pins.map((p) => (
              <button
                key={`${p.kind}-${p.targetId}`}
                type="button"
                className={selected === p.targetId ? 'pin selected' : 'pin'}
                style={{ position: 'absolute', left: `${p.x * 100}%`, top: `${p.y * 100}%`, transform: 'translate(-50%, -50%)' }}
                aria-pressed={selected === p.targetId}
                onClick={() => setSelected(selected === p.targetId ? null : p.targetId)}
              >
                {p.code}
              </button>
            ))}
          </div>
        ) : (
          <p className="muted">{t('common.loading')}</p>
        )}
      </div>
      <div>
        <h2>{t('spaces.pins')}</h2>
        <ErrorBanner error={save.error} />
        {manage ? (
          <div className="filters">
            <Field label={t('spaces.addPin')}>
              <select value={picked} onChange={(e) => setPicked(e.target.value)}>
                <option value="">{t('common.choose')}</option>
                {free.map((s) => <option key={s.locationId} value={s.locationId}>{s.code} · {s.name}</option>)}
              </select>
            </Field>
            <button
              type="button"
              disabled={!picked}
              onClick={() => {
                const s = view.spaces.find((x) => x.locationId === picked);
                if (!s) return;
                setPins((all) => [...all, { kind: 'Space', targetId: s.locationId, code: s.code, name: s.name, x: 0.5, y: 0.5 }]);
                setSelected(s.locationId);
                setPicked('');
              }}
            >
              {t('spaces.placeIt')}
            </button>
          </div>
        ) : null}
        {manage && selected ? <p className="muted">{t('spaces.clickPlan')}</p> : null}
        {pins.length === 0 ? <p className="muted">{t('spaces.noPins')}</p> : (
          <div className="table-wrap">
            <table>
              <caption className="visually-hidden">{t('spaces.pins')}</caption>
              <thead>
                <tr>
                  <th scope="col">{t('inventory.code')}</th>
                  <th scope="col">{t('spaces.across')}</th>
                  <th scope="col">{t('spaces.down')}</th>
                  <th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th>
                </tr>
              </thead>
              <tbody>
                {pins.map((p) => (
                  <tr key={`${p.kind}-${p.targetId}`}>
                    <td><bdi>{p.code}</bdi> <span className="muted"><bdi>{name({ name: p.name, nameAr: null })}</bdi></span></td>
                    <td>
                      <input type="number" min={0} max={100} aria-label={t('spaces.acrossOf', { code: p.code })} disabled={!manage} value={Math.round(p.x * 100)} onChange={(e) => set(p.targetId, { x: clamp(Number(e.target.value) / 100) })} />
                    </td>
                    <td>
                      <input type="number" min={0} max={100} aria-label={t('spaces.downOf', { code: p.code })} disabled={!manage} value={Math.round(p.y * 100)} onChange={(e) => set(p.targetId, { y: clamp(Number(e.target.value) / 100) })} />
                    </td>
                    <td className="actions">
                      {manage ? <button type="button" onClick={() => setPins((all) => all.filter((x) => x.targetId !== p.targetId))}>{t('spaces.removePin')}</button> : null}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {manage ? <button type="button" className="primary" disabled={!dirty || save.isPending} onClick={() => save.mutate()}>{t('common.save')}</button> : null}
      </div>
    </div>
  );
}

// ---------------- spaces ----------------

function SpacesTab() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const { name, num } = usePrefs();
  const kindLabel = useEnumLabel('spaceKind');
  const [placeId, setPlaceId] = useState('');
  const [on] = useState('');
  const occupancy = useOccupancy(placeId || undefined, on);
  const [editing, setEditing] = useState<{ id: string; code: string; name: string } | null>(null);
  return (
    <>
      <div className="filters">
        <PlacePicker label={t('spaces.place')} value={placeId} onChange={setPlaceId} types={['Site', 'Building', 'Floor']} />
      </div>
      {!placeId ? <p className="muted">{t('spaces.choosePlace')}</p> : null}
      <QueryState query={occupancy}>
        {occupancy.data && occupancy.data.rows.length === 0 ? <p className="muted">{t('spaces.noSpaces')}</p> : null}
        {occupancy.data && occupancy.data.rows.length > 0 ? (
          <div className="table-wrap">
            <table>
              <caption className="visually-hidden">{t('spaces.tab.spaces')}</caption>
              <thead>
                <tr>
                  <th scope="col">{t('inventory.code')}</th>
                  <th scope="col">{t('assets.name')}</th>
                  <th scope="col">{t('spaces.kind')}</th>
                  <th scope="col">{t('spaces.capacity')}</th>
                  <th scope="col">{t('spaces.allocated')}</th>
                  <th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th>
                </tr>
              </thead>
              <tbody>
                {occupancy.data.rows.map((r) => (
                  <tr key={r.spaceId}>
                    <td><bdi>{r.code}</bdi></td>
                    <td><bdi>{name({ name: r.name, nameAr: null })}</bdi></td>
                    <td>{r.kind ? kindLabel(r.kind) : <span className="muted">{t('spaces.notSet')}</span>}</td>
                    <td>{r.capacity > 0 ? num(r.capacity) : <span className="muted">{t('spaces.notSet')}</span>}</td>
                    <td>{num(r.allocated)}</td>
                    <td className="actions">{can('spaces.manage') ? <button type="button" onClick={() => setEditing({ id: r.spaceId, code: r.code, name: r.name })}>{t('common.edit')}</button> : null}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : null}
      </QueryState>
      {editing ? <SpaceDialog space={editing} onClose={() => setEditing(null)} /> : null}
    </>
  );
}

function SpaceDialog({ space, onClose }: { space: { id: string; code: string; name: string }; onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const refresh = useRefresh();
  const kindLabel = useEnumLabel('spaceKind');
  const [info, setInfo] = useState<SpaceInfo | null>(null);
  const [kind, setKind] = useState<SpaceKind>('Office');
  const [capacity, setCapacity] = useState('0');
  const [notes, setNotes] = useState('');
  useEffect(() => {
    void api.get<SpaceInfo>(`/spaces/${space.id}`).then((s) => {
      setInfo(s);
      setKind(s.kind ?? 'Office');
      setCapacity(String(s.capacity));
      setNotes(s.notes ?? '');
    });
  }, [api, space.id]);
  const save = useMutation({
    mutationFn: () => api.put(`/spaces/${space.id}`, { kind, capacity: Number(capacity), notes, rowVersion: info?.rowVersion ?? undefined }),
    onSuccess: async () => {
      await refresh();
      onClose();
    },
  });
  const err = asError(save.error);
  return (
    <Dialog title={t('spaces.editSpace', { code: space.code })} onClose={onClose}>
      {!info ? <p role="status">{t('common.loading')}</p> : null}
      <form hidden={!info} onSubmit={(e) => { e.preventDefault(); save.mutate(); }}>
        <Field label={t('spaces.kind')} error={err?.field('kind')}>
          <select value={kind} onChange={(e) => setKind(e.target.value as SpaceKind)}>
            {KINDS.map((k) => <option key={k} value={k}>{kindLabel(k)}</option>)}
          </select>
        </Field>
        <Field label={t('spaces.capacity')} hint={t('spaces.capacityHint')} error={err?.field('capacity')}>
          <input type="number" min={0} value={capacity} onChange={(e) => setCapacity(e.target.value)} />
        </Field>
        <Field label={t('spaces.notes')} error={err?.field('notes')}>
          <textarea value={notes} onChange={(e) => setNotes(e.target.value)} rows={2} />
        </Field>
        <ErrorBanner error={err && (err.field('kind') || err.field('capacity') || err.field('notes')) ? null : save.error} />
        <div className="actions">
          <button type="button" onClick={onClose}>{t('common.cancel')}</button>
          <button type="submit" className="primary" disabled={save.isPending}>{t('common.save')}</button>
        </div>
      </form>
    </Dialog>
  );
}

// ---------------- allocations ----------------

function stateTone(s: AllocationState): 'ok' | 'warn' | 'bad' | 'neutral' {
  return s === 'Current' ? 'ok' : s === 'Upcoming' ? 'warn' : 'neutral';
}

function AllocationsTab() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { can } = usePermissions();
  const { date, num } = usePrefs();
  const refresh = useRefresh();
  const stateLabel = useEnumLabel('allocationState');
  const [state, setState] = useState('');
  const [creating, setCreating] = useState(false);
  const [releasing, setReleasing] = useState<Allocation | null>(null);
  const list = useAllocations({ state: state || undefined });
  const release = useMutation({
    mutationFn: (v: { a: Allocation; reason: string }) => api.post(`/allocations/${v.a.id}/release`, { reason: v.reason, rowVersion: v.a.rowVersion }),
    onSuccess: async () => {
      await refresh();
      setReleasing(null);
    },
  });
  return (
    <>
      <div className="filters">
        <Field label={t('assets.status')}>
          <select value={state} onChange={(e) => setState(e.target.value)}>
            <option value="">{t('spaces.inForce')}</option>
            {(['Current', 'Upcoming', 'Ended', 'Cancelled'] as const).map((s) => <option key={s} value={s}>{stateLabel(s)}</option>)}
          </select>
        </Field>
        {can('spaces.manage') ? <button type="button" onClick={() => setCreating(true)}>{t('spaces.newAllocation')}</button> : null}
      </div>
      <QueryState query={list}>
        {list.data && list.data.length === 0 ? <p className="muted">{t('spaces.noAllocations')}</p> : null}
        {list.data && list.data.length > 0 ? (
          <div className="table-wrap">
            <table>
              <caption className="visually-hidden">{t('spaces.tab.allocations')}</caption>
              <thead>
                <tr>
                  <th scope="col">{t('spaces.space')}</th>
                  <th scope="col">{t('spaces.occupant')}</th>
                  <th scope="col">{t('spaces.period')}</th>
                  <th scope="col">{t('assets.status')}</th>
                  <th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th>
                </tr>
              </thead>
              <tbody>
                {list.data.map((a) => (
                  <tr key={a.id}>
                    <td><bdi>{a.spaceCode}</bdi> <span className="muted"><bdi>{a.spaceName}</bdi></span></td>
                    <td>
                      <bdi>{a.kind === 'Person' ? (a.userName ?? '—') : a.teamName}</bdi>
                      {a.kind === 'Team' ? <div className="muted">{t('spaces.people', { count: a.headcount, n: num(a.headcount) })}</div> : null}
                    </td>
                    <td>{date(a.startOn)} – {a.endOn ? date(a.endOn) : t('spaces.openEnded')}</td>
                    <td><Badge tone={stateTone(a.state)}>{stateLabel(a.state)}</Badge>{a.endReason ? <div className="muted"><bdi>{a.endReason}</bdi></div> : null}</td>
                    <td className="actions">
                      {can('spaces.manage') && (a.state === 'Current' || a.state === 'Upcoming') ? <button type="button" onClick={() => setReleasing(a)}>{t('spaces.release')}</button> : null}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : null}
      </QueryState>
      {creating ? <AllocationDialog onClose={() => setCreating(false)} /> : null}
      {releasing ? (
        <ReasonDialog
          title={t('spaces.releaseTitle', { space: releasing.spaceCode })}
          label={t('spaces.releaseReason')}
          confirmLabel={t('spaces.release')}
          busy={release.isPending}
          error={release.error}
          onConfirm={(reason) => release.mutate({ a: releasing, reason })}
          onCancel={() => setReleasing(null)}
        />
      ) : null}
    </>
  );
}

function AllocationDialog({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const refresh = useRefresh();
  const [floorId, setFloorId] = useState('');
  const view = useFloorView(floorId || undefined);
  const users = useUsers();
  const today = new Date().toISOString().slice(0, 10);
  const [spaceId, setSpaceId] = useState('');
  const [kind, setKind] = useState<OccupantKind>('Person');
  const [userId, setUserId] = useState('');
  const [team, setTeam] = useState('');
  const [headcount, setHeadcount] = useState('1');
  const [startOn, setStartOn] = useState(today);
  const [endOn, setEndOn] = useState('');
  const [note, setNote] = useState('');
  const save = useMutation({
    mutationFn: () => api.post('/allocations', { spaceId, kind, userId: kind === 'Person' ? userId || undefined : undefined, teamName: kind === 'Team' ? team : undefined, headcount: kind === 'Team' ? Number(headcount) : undefined, startOn, endOn: endOn || undefined, note }),
    onSuccess: async () => {
      await refresh();
      onClose();
    },
  });
  const err = asError(save.error);
  const fields = ['spaceId', 'kind', 'userId', 'teamName', 'headcount', 'startOn', 'endOn', 'note'];
  return (
    <Dialog title={t('spaces.newAllocation')} onClose={onClose} wide>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }}>
        <PlacePicker label={t('spaces.floor')} value={floorId} onChange={(id) => { setFloorId(id); setSpaceId(''); }} types={['Floor']} />
        <Field label={t('spaces.space')} required error={err?.field('spaceId')}>
          <select value={spaceId} onChange={(e) => setSpaceId(e.target.value)}>
            <option value="">{t('common.choose')}</option>
            {(view.data?.spaces ?? []).map((s) => <option key={s.locationId} value={s.locationId}>{s.code} · {s.name} ({s.allocatedToday}/{s.capacity})</option>)}
          </select>
        </Field>
        <Field label={t('spaces.for')} error={err?.field('kind')}>
          <select value={kind} onChange={(e) => setKind(e.target.value as OccupantKind)}>
            <option value="Person">{t('spaces.person')}</option>
            <option value="Team">{t('spaces.team')}</option>
          </select>
        </Field>
        {kind === 'Person' ? (
          <Field label={t('spaces.person')} required error={err?.field('userId')}>
            <select value={userId} onChange={(e) => setUserId(e.target.value)}>
              <option value="">{t('common.choose')}</option>
              {(users.data ?? []).filter((u) => u.isActive).map((u) => <option key={u.id} value={u.id}>{u.displayName ?? u.email ?? u.id}</option>)}
            </select>
          </Field>
        ) : (
          <>
            <Field label={t('spaces.teamName')} required error={err?.field('teamName')}><input value={team} onChange={(e) => setTeam(e.target.value)} /></Field>
            <Field label={t('spaces.headcount')} required error={err?.field('headcount')}><input type="number" min={1} value={headcount} onChange={(e) => setHeadcount(e.target.value)} /></Field>
          </>
        )}
        <Field label={t('spaces.from')} required error={err?.field('startOn')}><input type="date" value={startOn} onChange={(e) => setStartOn(e.target.value)} /></Field>
        <Field label={t('spaces.until')} hint={t('spaces.untilHint')} error={err?.field('endOn')}><input type="date" value={endOn} onChange={(e) => setEndOn(e.target.value)} /></Field>
        <Field label={t('spaces.notes')} error={err?.field('note')}><input value={note} onChange={(e) => setNote(e.target.value)} /></Field>
        <ErrorBanner error={err && fields.some((f) => err.field(f)) ? null : save.error} />
        <div className="actions">
          <button type="button" onClick={onClose}>{t('common.cancel')}</button>
          <button type="submit" className="primary" disabled={save.isPending}>{t('spaces.allocate')}</button>
        </div>
      </form>
    </Dialog>
  );
}

// ---------------- occupancy ----------------

function OccupancyTab() {
  const { t } = useTranslation();
  const { name, num } = usePrefs();
  const kindLabel = useEnumLabel('spaceKind');
  const [placeId, setPlaceId] = useState('');
  const [on, setOn] = useState('');
  const occupancy = useOccupancy(placeId || undefined, on);
  const pct = (v: number | null) => (v === null ? '—' : `${num(v)}%`);
  return (
    <>
      <div className="filters">
        <PlacePicker label={t('spaces.place')} value={placeId} onChange={setPlaceId} types={['Site', 'Building', 'Floor', 'Space']} />
        <Field label={t('spaces.onDay')}><input type="date" value={on} onChange={(e) => setOn(e.target.value)} /></Field>
      </div>
      {!placeId ? <p className="muted">{t('spaces.choosePlace')}</p> : null}
      <QueryState query={occupancy}>
        {occupancy.data ? (
          <>
            <dl className="tiles">
              <div><dt>{t('spaces.capacity')}</dt><dd>{num(occupancy.data.capacity)}</dd></div>
              <div><dt>{t('spaces.allocated')}</dt><dd>{num(occupancy.data.allocated)}</dd></div>
              <div><dt>{t('spaces.utilisation')}</dt><dd>{pct(occupancy.data.utilisationPercent)}</dd></div>
            </dl>
            {occupancy.data.spacesWithoutCapacity > 0 ? <p className="muted">{t('spaces.withoutCapacity', { count: occupancy.data.spacesWithoutCapacity })}</p> : null}
            <div className="table-wrap">
              <table>
                <caption className="visually-hidden">{t('spaces.tab.occupancy')}</caption>
                <thead>
                  <tr>
                    <th scope="col">{t('inventory.code')}</th>
                    <th scope="col">{t('spaces.kind')}</th>
                    <th scope="col">{t('spaces.capacity')}</th>
                    <th scope="col">{t('spaces.allocated')}</th>
                    <th scope="col">{t('spaces.utilisation')}</th>
                  </tr>
                </thead>
                <tbody>
                  {occupancy.data.rows.map((r) => (
                    <tr key={r.spaceId}>
                      <td><bdi>{r.code}</bdi> <span className="muted"><bdi>{name({ name: r.name, nameAr: null })}</bdi></span></td>
                      <td>{r.kind ? kindLabel(r.kind) : '—'}</td>
                      <td>{r.capacity > 0 ? num(r.capacity) : '—'}</td>
                      <td>{num(r.allocated)}</td>
                      <td>{pct(r.utilisationPercent)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </>
        ) : null}
      </QueryState>
    </>
  );
}
