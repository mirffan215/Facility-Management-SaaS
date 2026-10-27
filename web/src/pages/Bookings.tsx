import { useMemo, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import { useAvailability, useCalendarFeed, useCalendarSync, useMyBookings, usePendingBookings, usePermissions, useResources } from '../api/hooks';
import type { BookableResource, Booking, BookingStatus, DayName, ResourceKind } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Dialog } from '../components/InventoryBits';
import { Badge, ErrorBanner, Field, QueryState, ReasonDialog, useEnumLabel } from '../components/ui';
import { formatDateTimeInZone, formatTimeInZone, zonedTimeToUtc } from '../lib/zoned';
import { usePrefs } from '../lib/prefs';

const KINDS: ResourceKind[] = ['Room', 'Desk', 'Parking', 'Equipment', 'Other'];
const DAYS: DayName[] = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
const asError = (e: unknown) => (e instanceof ApiError ? e : null);

export function BookingsPage() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const [params, setParams] = useSearchParams();
  const tabs = useMemo(() => ['book', 'mine', ...(can('bookings.approve') ? ['approvals'] : []), ...(can('bookings.manage') ? ['resources'] : [])], [can]);
  const tab = tabs.find((x) => x === params.get('tab')) ?? 'book';
  return (
    <section>
      <h1>{t('bookings.title')}</h1>
      <p className="muted">{t('bookings.intro')}</p>
      <div className="tabs" role="tablist" aria-label={t('bookings.sections')}>
        {tabs.map((x) => (
          <button key={x} role="tab" type="button" aria-selected={tab === x} className={tab === x ? 'active' : ''} onClick={() => setParams({ tab: x })}>
            {t(`bookings.tab.${x}`)}
          </button>
        ))}
      </div>
      <div role="tabpanel">{tab === 'book' ? <BookTab /> : tab === 'mine' ? <MineTab /> : tab === 'approvals' ? <ApprovalsTab /> : <ResourcesTab />}</div>
    </section>
  );
}

function useRefresh() {
  const client = useQueryClient();
  return () => client.invalidateQueries({ queryKey: ['bookings'] });
}

function statusTone(s: BookingStatus): 'ok' | 'warn' | 'bad' | 'neutral' {
  return s === 'Confirmed' || s === 'CheckedIn' ? 'ok' : s === 'Pending' ? 'warn' : s === 'Rejected' || s === 'NoShow' ? 'bad' : 'neutral';
}

// ---------------- book ----------------

function BookTab() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { can } = usePermissions();
  const { lang } = usePrefs();
  const refresh = useRefresh();
  const kindLabel = useEnumLabel('resourceKind');
  const [kind, setKind] = useState('');
  const [resourceId, setResourceId] = useState('');
  const resources = useResources({ kind: kind || undefined });
  const resource = resources.data?.find((r) => r.id === resourceId);
  const zone = resource?.timeZone ?? 'UTC';
  const [day, setDay] = useState(() => new Date().toISOString().slice(0, 10));
  const from = zonedTimeToUtc(day, '00:00', zone);
  const to = new Date(from.getTime() + 24 * 3600 * 1000);
  const availability = useAvailability(resourceId || undefined, from.toISOString(), to.toISOString());

  const [time, setTime] = useState('09:00');
  const [minutes, setMinutes] = useState(60);
  const [title, setTitle] = useState('');
  const [attendees, setAttendees] = useState('1');
  const [repeat, setRepeat] = useState('1');
  const [notes, setNotes] = useState('');
  const [done, setDone] = useState<{ count: number; pending: boolean } | null>(null);
  const start = zonedTimeToUtc(day, time, zone);
  const durations = [15, 30, 45, 60, 90, 120, 180, 240, 360, 480, 720, 1440].filter((m) => !resource || (m >= resource.minMinutes && m <= resource.maxMinutes));

  const book = useMutation({
    mutationFn: () =>
      api.post<{ bookings: Booking[] }>('/bookings', {
        resourceId, title, startsAt: start.toISOString(), endsAt: new Date(start.getTime() + minutes * 60000).toISOString(), attendees: Number(attendees), repeatWeeks: Number(repeat), notes,
      }),
    onSuccess: async (r) => {
      setDone({ count: r.bookings.length, pending: r.bookings[0]?.status === 'Pending' });
      setTitle('');
      setNotes('');
      await refresh();
    },
  });
  const err = asError(book.error);
  const fields = ['resourceId', 'title', 'startsAt', 'endsAt', 'attendees', 'repeatWeeks', 'notes'];

  return (
    <>
      <div className="filters">
        <Field label={t('bookings.kind')}>
          <select value={kind} onChange={(e) => { setKind(e.target.value); setResourceId(''); }}>
            <option value="">{t('common.all')}</option>
            {KINDS.map((k) => <option key={k} value={k}>{kindLabel(k)}</option>)}
          </select>
        </Field>
        <Field label={t('bookings.resource')}>
          <select value={resourceId} onChange={(e) => { setResourceId(e.target.value); setDone(null); }}>
            <option value="">{t('common.choose')}</option>
            {(resources.data ?? []).map((r) => <option key={r.id} value={r.id}>{r.code} · {r.name}</option>)}
          </select>
        </Field>
        <Field label={t('bookings.day')}><input type="date" value={day} onChange={(e) => { setDay(e.target.value); setDone(null); }} /></Field>
      </div>
      {!resourceId ? <p className="muted">{t('bookings.chooseResource')}</p> : null}
      {resource ? (
        <>
          <p className="muted">
            {t('bookings.rules', { opens: resource.opensAt, closes: resource.closesAt, zone: resource.timeZone, capacity: resource.capacity })}
            {resource.requiresApproval ? ` ${t('bookings.needsApproval')}` : ''}
          </p>
          <h2>{t('bookings.taken')}</h2>
          <QueryState query={availability}>
            {availability.data && availability.data.busy.length === 0 ? <p className="muted">{t('bookings.freeAllDay')}</p> : null}
            <ul className="plain">
              {(availability.data?.busy ?? []).map((b) => (
                <li key={b.startsAt}>
                  <bdi>{formatTimeInZone(b.startsAt, zone, lang)} – {formatTimeInZone(b.endsAt, zone, lang)}</bdi>{' '}
                  {b.mine ? <Badge tone="ok">{t('bookings.yours')}</Badge> : null}{b.pending ? <Badge tone="warn">{t('bookingStatus.Pending')}</Badge> : null}
                </li>
              ))}
            </ul>
          </QueryState>

          {can('bookings.book') ? (
            <form onSubmit={(e) => { e.preventDefault(); book.mutate(); }}>
              <h2>{t('bookings.newBooking')}</h2>
              <Field label={t('bookings.titleField')} required error={err?.field('title')}><input value={title} onChange={(e) => setTitle(e.target.value)} /></Field>
              <Field label={t('bookings.startTime')} hint={t('bookings.inZone', { zone: resource.timeZone })} required error={err?.field('startsAt')}>
                <input type="time" step={300} value={time} onChange={(e) => setTime(e.target.value)} />
              </Field>
              <Field label={t('bookings.length')} error={err?.field('endsAt')}>
                <select value={minutes} onChange={(e) => setMinutes(Number(e.target.value))}>
                  {durations.map((m) => <option key={m} value={m}>{t('bookings.minutes', { count: m })}</option>)}
                </select>
              </Field>
              <Field label={t('bookings.attendees')} error={err?.field('attendees')}><input type="number" min={1} max={resource.capacity} value={attendees} onChange={(e) => setAttendees(e.target.value)} /></Field>
              <Field label={t('bookings.repeat')} hint={t('bookings.repeatHint')} error={err?.field('repeatWeeks')}><input type="number" min={1} max={26} value={repeat} onChange={(e) => setRepeat(e.target.value)} /></Field>
              <Field label={t('spaces.notes')} error={err?.field('notes')}><input value={notes} onChange={(e) => setNotes(e.target.value)} /></Field>
              <ErrorBanner error={err && fields.some((f) => err.field(f)) ? null : book.error} />
              {done ? <p className="banner" role="status">{done.pending ? t('bookings.requested', { count: done.count }) : t('bookings.booked', { count: done.count })}</p> : null}
              <button type="submit" className="primary" disabled={book.isPending}>{resource.requiresApproval ? t('bookings.request') : t('bookings.book')}</button>
            </form>
          ) : <p className="muted">{t('bookings.viewOnly')}</p>}
        </>
      ) : null}
    </>
  );
}

// ---------------- my bookings ----------------

function MineTab() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { lang } = usePrefs();
  const refresh = useRefresh();
  const statusLabel = useEnumLabel('bookingStatus');
  const mine = useMyBookings();
  const resources = useResources({ includeArchived: true });
  const [cancelling, setCancelling] = useState<Booking | null>(null);
  const zoneOf = (id: string) => resources.data?.find((r) => r.id === id)?.timeZone ?? 'UTC';
  const checkIn = useMutation({ mutationFn: (b: Booking) => api.post(`/bookings/${b.id}/check-in`, { rowVersion: b.rowVersion }), onSuccess: () => refresh() });
  const cancel = useMutation({
    mutationFn: (v: { b: Booking; reason: string }) => api.post(`/bookings/${v.b.id}/cancel`, { reason: v.reason, rowVersion: v.b.rowVersion }),
    onSuccess: async () => {
      await refresh();
      setCancelling(null);
    },
  });
  return (
    <>
      <ErrorBanner error={checkIn.error} />
      <QueryState query={mine}>
        {mine.data && mine.data.length === 0 ? <p className="muted">{t('bookings.noneMine')}</p> : null}
        {mine.data && mine.data.length > 0 ? (
          <div className="table-wrap">
            <table>
              <caption className="visually-hidden">{t('bookings.tab.mine')}</caption>
              <thead>
                <tr>
                  <th scope="col">{t('bookings.titleField')}</th>
                  <th scope="col">{t('bookings.resource')}</th>
                  <th scope="col">{t('bookings.when')}</th>
                  <th scope="col">{t('assets.status')}</th>
                  <th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th>
                </tr>
              </thead>
              <tbody>
                {mine.data.map((b) => (
                  <tr key={b.id}>
                    <td><bdi>{b.title}</bdi></td>
                    <td><bdi>{b.resourceCode}</bdi> <span className="muted"><bdi>{b.resourceName}</bdi></span></td>
                    <td><bdi>{formatDateTimeInZone(b.startsAt, zoneOf(b.resourceId), lang)} – {formatTimeInZone(b.endsAt, zoneOf(b.resourceId), lang)}</bdi></td>
                    <td><Badge tone={statusTone(b.status)}>{statusLabel(b.status)}</Badge>{b.decisionNote || b.cancelReason ? <div className="muted"><bdi>{b.decisionNote ?? b.cancelReason}</bdi></div> : null}</td>
                    <td className="actions">
                      {b.canCheckInNow ? <button type="button" disabled={checkIn.isPending} onClick={() => checkIn.mutate(b)}>{t('bookings.checkIn')}</button> : null}
                      {b.status === 'Pending' || b.status === 'Confirmed' || b.status === 'CheckedIn' ? <button type="button" onClick={() => setCancelling(b)}>{t('bookings.cancel')}</button> : null}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : null}
      </QueryState>
      {cancelling ? (
        <ReasonDialog
          title={t('bookings.cancelTitle', { title: cancelling.title })}
          label={t('bookings.cancelReason')}
          confirmLabel={t('bookings.cancel')}
          optional
          busy={cancel.isPending}
          error={cancel.error}
          onConfirm={(reason) => cancel.mutate({ b: cancelling, reason })}
          onCancel={() => setCancelling(null)}
        />
      ) : null}
      <FeedCard />
    </>
  );
}

function FeedCard() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const client = useQueryClient();
  const [address, setAddress] = useState<string | null>(null);
  const status = useCalendarFeed();
  const make = useMutation({
    mutationFn: () => api.post<{ path: string }>('/bookings/calendar-feed'),
    onSuccess: async (r) => {
      setAddress(`${window.location.origin}${r.path}`);
      await client.invalidateQueries({ queryKey: ['bookings', 'feed'] });
    },
  });
  const revoke = useMutation({
    mutationFn: () => api.delete('/bookings/calendar-feed'),
    onSuccess: async () => {
      setAddress(null);
      await client.invalidateQueries({ queryKey: ['bookings', 'feed'] });
    },
  });
  return (
    <div className="panel">
      <h2>{t('bookings.feedTitle')}</h2>
      <p className="muted">{t('bookings.feedHelp')}</p>
      <ErrorBanner error={make.error ?? revoke.error} />
      {address ? (
        <div className="banner" role="status">
          <p>{t('bookings.feedOnce')}</p>
          <input readOnly value={address} aria-label={t('bookings.feedAddress')} dir="ltr" onFocus={(e) => e.currentTarget.select()} />
        </div>
      ) : null}
      <div className="actions">
        <button type="button" disabled={make.isPending} onClick={() => make.mutate()}>{status.data?.exists ? t('bookings.feedNew') : t('bookings.feedMake')}</button>
        {status.data?.exists ? <button type="button" disabled={revoke.isPending} onClick={() => revoke.mutate()}>{t('bookings.feedStop')}</button> : null}
      </div>
    </div>
  );
}

// ---------------- approvals ----------------

function ApprovalsTab() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { lang } = usePrefs();
  const refresh = useRefresh();
  const pending = usePendingBookings(true);
  const resources = useResources({ includeArchived: true });
  const [rejecting, setRejecting] = useState<Booking | null>(null);
  const zoneOf = (id: string) => resources.data?.find((r) => r.id === id)?.timeZone ?? 'UTC';
  const approve = useMutation({ mutationFn: (b: Booking) => api.post(`/bookings/${b.id}/approve`, { rowVersion: b.rowVersion }), onSuccess: () => refresh() });
  const reject = useMutation({
    mutationFn: (v: { b: Booking; note: string }) => api.post(`/bookings/${v.b.id}/reject`, { note: v.note, rowVersion: v.b.rowVersion }),
    onSuccess: async () => {
      await refresh();
      setRejecting(null);
    },
  });
  return (
    <>
      <ErrorBanner error={approve.error} />
      <QueryState query={pending}>
        {pending.data && pending.data.length === 0 ? <p className="muted">{t('bookings.nothingWaiting')}</p> : null}
        {pending.data && pending.data.length > 0 ? (
          <div className="table-wrap">
            <table>
              <caption className="visually-hidden">{t('bookings.tab.approvals')}</caption>
              <thead>
                <tr>
                  <th scope="col">{t('bookings.titleField')}</th>
                  <th scope="col">{t('bookings.resource')}</th>
                  <th scope="col">{t('bookings.when')}</th>
                  <th scope="col">{t('bookings.requestedBy')}</th>
                  <th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th>
                </tr>
              </thead>
              <tbody>
                {pending.data.map((b) => (
                  <tr key={b.id}>
                    <td><bdi>{b.title}</bdi></td>
                    <td><bdi>{b.resourceCode}</bdi> <span className="muted"><bdi>{b.resourceName}</bdi></span></td>
                    <td><bdi>{formatDateTimeInZone(b.startsAt, zoneOf(b.resourceId), lang)} – {formatTimeInZone(b.endsAt, zoneOf(b.resourceId), lang)}</bdi></td>
                    <td><bdi>{b.organizerName ?? '—'}</bdi></td>
                    <td className="actions">
                      {b.mine ? <span className="muted">{t('bookings.notYourOwn')}</span> : (
                        <>
                          <button type="button" disabled={approve.isPending} onClick={() => approve.mutate(b)}>{t('bookings.approve')}</button>
                          <button type="button" onClick={() => setRejecting(b)}>{t('bookings.reject')}</button>
                        </>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : null}
      </QueryState>
      {rejecting ? (
        <ReasonDialog
          title={t('bookings.rejectTitle', { title: rejecting.title })}
          label={t('bookings.rejectReason')}
          confirmLabel={t('bookings.reject')}
          busy={reject.isPending}
          error={reject.error}
          onConfirm={(note) => reject.mutate({ b: rejecting, note })}
          onCancel={() => setRejecting(null)}
        />
      ) : null}
    </>
  );
}

// ---------------- resources ----------------

function ResourcesTab() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { name } = usePrefs();
  const refresh = useRefresh();
  const kindLabel = useEnumLabel('resourceKind');
  const [editing, setEditing] = useState<BookableResource | 'new' | null>(null);
  const resources = useResources({ includeArchived: true });
  const archive = useMutation({
    mutationFn: (r: BookableResource) => api.post(`/resources/${r.id}/${r.isArchived ? 'restore' : 'archive'}`, { rowVersion: r.rowVersion }),
    onSuccess: () => refresh(),
  });
  return (
    <>
      <div className="filters"><button type="button" onClick={() => setEditing('new')}>{t('bookings.newResource')}</button></div>
      <ErrorBanner error={archive.error} />
      <QueryState query={resources}>
        {resources.data && resources.data.length === 0 ? <p className="muted">{t('bookings.noResources')}</p> : null}
        {resources.data && resources.data.length > 0 ? (
          <div className="table-wrap">
            <table>
              <caption className="visually-hidden">{t('bookings.tab.resources')}</caption>
              <thead>
                <tr>
                  <th scope="col">{t('inventory.code')}</th>
                  <th scope="col">{t('assets.name')}</th>
                  <th scope="col">{t('bookings.kind')}</th>
                  <th scope="col">{t('bookings.hours')}</th>
                  <th scope="col">{t('bookings.rulesColumn')}</th>
                  <th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th>
                </tr>
              </thead>
              <tbody>
                {resources.data.map((r) => (
                  <tr key={r.id}>
                    <td><bdi>{r.code}</bdi></td>
                    <td><bdi>{name(r)}</bdi> {r.isArchived ? <Badge tone="neutral">{t('common.archived')}</Badge> : null}</td>
                    <td>{kindLabel(r.kind)}</td>
                    <td><bdi>{r.opensAt}–{r.closesAt}</bdi> <span className="muted"><bdi>{r.timeZone}</bdi></span></td>
                    <td>{[r.requiresApproval ? t('bookings.needsApprovalShort') : null, r.checkInRequired ? t('bookings.checkInShort') : null].filter(Boolean).join(' · ') || '—'}</td>
                    <td className="actions">
                      <button type="button" onClick={() => setEditing(r)}>{t('common.edit')}</button>
                      <button type="button" disabled={archive.isPending} onClick={() => archive.mutate(r)}>{r.isArchived ? t('common.restore') : t('common.archive')}</button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : null}
      </QueryState>
      {editing ? <ResourceDialog resource={editing === 'new' ? null : editing} onClose={() => setEditing(null)} /> : null}
      <SyncCard />
    </>
  );
}

function ResourceDialog({ resource, onClose }: { resource: BookableResource | null; onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const refresh = useRefresh();
  const kindLabel = useEnumLabel('resourceKind');
  const dayLabel = useEnumLabel('dayName');
  const [f, setF] = useState({
    kind: resource?.kind ?? 'Room', code: resource?.code ?? '', name: resource?.name ?? '', nameAr: resource?.nameAr ?? '', capacity: String(resource?.capacity ?? 1), timeZone: resource?.timeZone ?? 'UTC',
    opensAt: resource?.opensAt ?? '07:00', closesAt: resource?.closesAt ?? '19:00', days: resource?.days ?? DAYS, minMinutes: String(resource?.minMinutes ?? 15), maxMinutes: String(resource?.maxMinutes ?? 480),
    maxAdvanceDays: String(resource?.maxAdvanceDays ?? 90), bufferMinutes: String(resource?.bufferMinutes ?? 0), requiresApproval: resource?.requiresApproval ?? false,
    checkInRequired: resource?.checkInRequired ?? false, checkInGraceMinutes: String(resource?.checkInGraceMinutes ?? 15), notes: resource?.notes ?? '',
  });
  const set = <K extends keyof typeof f>(k: K, v: (typeof f)[K]) => setF((x) => ({ ...x, [k]: v }));
  const save = useMutation({
    mutationFn: () => {
      const body = {
        kind: f.kind, code: f.code, name: f.name, nameAr: f.nameAr, capacity: Number(f.capacity), timeZone: f.timeZone, opensAt: f.opensAt, closesAt: f.closesAt, days: f.days, minMinutes: Number(f.minMinutes),
        maxMinutes: Number(f.maxMinutes), maxAdvanceDays: Number(f.maxAdvanceDays), bufferMinutes: Number(f.bufferMinutes), requiresApproval: f.requiresApproval, checkInRequired: f.checkInRequired,
        checkInGraceMinutes: Number(f.checkInGraceMinutes), notes: f.notes, rowVersion: resource?.rowVersion,
      };
      return resource ? api.put(`/resources/${resource.id}`, body) : api.post('/resources', body);
    },
    onSuccess: async () => {
      await refresh();
      onClose();
    },
  });
  const err = asError(save.error);
  const fields = ['kind', 'code', 'name', 'nameAr', 'capacity', 'timeZone', 'opensAt', 'closesAt', 'days', 'minMinutes', 'maxMinutes', 'maxAdvanceDays', 'bufferMinutes', 'checkInGraceMinutes', 'notes', 'spaceId'];
  const num = (k: 'capacity' | 'minMinutes' | 'maxMinutes' | 'maxAdvanceDays' | 'bufferMinutes' | 'checkInGraceMinutes', label: string) => (
    <Field label={label} error={err?.field(k)}><input type="number" value={f[k]} onChange={(e) => set(k, e.target.value)} /></Field>
  );
  return (
    <Dialog title={resource ? t('bookings.editResource', { code: resource.code }) : t('bookings.newResource')} onClose={onClose} wide>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }}>
        <Field label={t('bookings.kind')} error={err?.field('kind')}>
          <select value={f.kind} onChange={(e) => set('kind', e.target.value as ResourceKind)}>{KINDS.map((k) => <option key={k} value={k}>{kindLabel(k)}</option>)}</select>
        </Field>
        <Field label={t('inventory.code')} required error={err?.field('code')}><input value={f.code} onChange={(e) => set('code', e.target.value)} /></Field>
        <Field label={t('assets.name')} required error={err?.field('name')}><input value={f.name} onChange={(e) => set('name', e.target.value)} /></Field>
        <Field label={t('bookings.nameAr')} error={err?.field('nameAr')}><input value={f.nameAr} onChange={(e) => set('nameAr', e.target.value)} dir="rtl" /></Field>
        {num('capacity', t('spaces.capacity'))}
        <Field label={t('bookings.zone')} hint={t('bookings.zoneHint')} error={err?.field('timeZone')}><input value={f.timeZone} onChange={(e) => set('timeZone', e.target.value)} dir="ltr" /></Field>
        <Field label={t('bookings.opens')} error={err?.field('opensAt')}><input type="time" value={f.opensAt} onChange={(e) => set('opensAt', e.target.value)} /></Field>
        <Field label={t('bookings.closes')} hint={t('bookings.closesHint')} error={err?.field('closesAt')}><input value={f.closesAt} onChange={(e) => set('closesAt', e.target.value)} dir="ltr" /></Field>
        <fieldset>
          <legend>{t('bookings.openDays')}</legend>
          {DAYS.map((d) => (
            <label key={d} className="inline">
              <input type="checkbox" checked={f.days.includes(d)} onChange={(e) => set('days', e.target.checked ? [...f.days, d] : f.days.filter((x) => x !== d))} /> {dayLabel(d)}
            </label>
          ))}
          {err?.field('days') ? <p className="field-error" role="alert">{err.field('days')}</p> : null}
        </fieldset>
        {num('minMinutes', t('bookings.shortest'))}
        {num('maxMinutes', t('bookings.longest'))}
        {num('maxAdvanceDays', t('bookings.advance'))}
        {num('bufferMinutes', t('bookings.buffer'))}
        <label className="inline"><input type="checkbox" checked={f.requiresApproval} onChange={(e) => set('requiresApproval', e.target.checked)} /> {t('bookings.requiresApproval')}</label>
        <label className="inline"><input type="checkbox" checked={f.checkInRequired} onChange={(e) => set('checkInRequired', e.target.checked)} /> {t('bookings.checkInRequired')}</label>
        {f.checkInRequired ? num('checkInGraceMinutes', t('bookings.grace')) : null}
        <Field label={t('spaces.notes')} error={err?.field('notes')}><input value={f.notes} onChange={(e) => set('notes', e.target.value)} /></Field>
        <ErrorBanner error={err && fields.some((x) => err.field(x)) ? null : save.error} />
        <div className="actions">
          <button type="button" onClick={onClose}>{t('common.cancel')}</button>
          <button type="submit" className="primary" disabled={save.isPending}>{t('common.save')}</button>
        </div>
      </form>
    </Dialog>
  );
}

function SyncCard() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const client = useQueryClient();
  const sync = useCalendarSync(true);
  const [enabled, setEnabled] = useState<boolean | null>(null);
  const [adapter, setAdapter] = useState<string | null>(null);
  const save = useMutation({
    mutationFn: () => api.put('/bookings/calendar-sync', { enabled: enabled ?? sync.data?.enabled, adapter: adapter ?? sync.data?.adapter, rowVersion: sync.data?.rowVersion ?? undefined }),
    onSuccess: async () => {
      setEnabled(null);
      setAdapter(null);
      await client.invalidateQueries({ queryKey: ['bookings', 'calendar-sync'] });
    },
  });
  return (
    <div className="panel">
      <h2>{t('bookings.syncTitle')}</h2>
      <p className="muted">{t('bookings.syncHelp')}</p>
      <QueryState query={sync}>
        {sync.data ? (
          sync.data.availableAdapters.length === 0 ? <p className="muted">{t('bookings.syncNone')}</p> : (
            <>
              <label className="inline"><input type="checkbox" checked={enabled ?? sync.data.enabled} onChange={(e) => setEnabled(e.target.checked)} /> {t('bookings.syncEnable')}</label>
              <Field label={t('bookings.syncAdapter')} error={asError(save.error)?.field('adapter')}>
                <select value={adapter ?? sync.data.adapter} onChange={(e) => setAdapter(e.target.value)}>
                  <option value="">{t('common.choose')}</option>
                  {sync.data.availableAdapters.map((a) => <option key={a} value={a}>{a}</option>)}
                </select>
              </Field>
              <ErrorBanner error={asError(save.error)?.field('adapter') ? null : save.error} />
              <button type="button" disabled={save.isPending} onClick={() => save.mutate()}>{t('common.save')}</button>
            </>
          )
        ) : null}
      </QueryState>
    </div>
  );
}
