import { useMemo, useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import { useCalendars, useLocationTree, useSlaPolicies, useWorkCategories } from '../api/hooks';
import type { DayHours, Holiday, SlaPolicy, SlaTarget, WorkCalendar, WorkPriority } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Badge, ErrorBanner, Field, QueryState, useEnumLabel } from '../components/ui';
import { usePrefs } from '../lib/prefs';

const DAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
const PRIORITIES: WorkPriority[] = ['Low', 'Medium', 'High', 'Urgent'];

const week = (open: string[], start = '08:00', end = '17:00'): Record<string, DayHours | null> =>
  Object.fromEntries(DAYS.map((d) => [d, open.includes(d) ? { start, end } : null]));

const SAUDI = week(['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday']);
const ALWAYS = week(DAYS, '00:00', '24:00');

function timeZones(): string[] {
  try {
    return (Intl as unknown as { supportedValuesOf?: (k: string) => string[] }).supportedValuesOf?.('timeZone') ?? ['UTC', 'Asia/Riyadh'];
  } catch {
    return ['UTC', 'Asia/Riyadh'];
  }
}

export function SlaSection() {
  const { t } = useTranslation();
  return (
    <>
      <p className="muted">{t('sla.intro')}</p>
      <CalendarsPart />
      <PoliciesPart />
    </>
  );
}

// ---------------- calendars ----------------

function CalendarsPart() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const client = useQueryClient();
  const calendars = useCalendars();
  const [editing, setEditing] = useState<WorkCalendar | 'new' | null>(null);

  const archive = useMutation({
    mutationFn: (c: WorkCalendar) => api.post(`/work-orders/sla/calendars/${c.id}/${c.isArchived ? 'restore' : 'archive'}`, { rowVersion: c.rowVersion }),
    onSuccess: () => client.invalidateQueries({ queryKey: ['sla-calendars'] }),
  });

  return (
    <>
      <div className="toolbar">
        <h2>{t('sla.calendars')}</h2>
        <button type="button" onClick={() => setEditing('new')}>{t('sla.newCalendar')}</button>
      </div>
      <QueryState query={calendars}>
        {calendars.data && calendars.data.length === 0 ? <p className="muted">{t('sla.noCalendars')}</p> : null}
        <ul className="plain">
          {calendars.data?.map((c) => (
            <li key={c.id}>
              <strong><bdi>{c.name}</bdi></strong> <span className="muted">· <bdi>{c.timeZoneId}</bdi></span>{' '}
              {c.isDefault ? <Badge tone="ok">{t('sla.default')}</Badge> : null} {c.isArchived ? <Badge tone="neutral">{t('common.archived')}</Badge> : null}{' '}
              <button type="button" onClick={() => setEditing(c)}>{t('common.edit')}</button>{' '}
              <button type="button" disabled={archive.isPending} onClick={() => archive.mutate(c)}>{c.isArchived ? t('common.restore') : t('common.archive')}</button>
            </li>
          ))}
        </ul>
        <ErrorBanner error={archive.error} />
      </QueryState>
      {editing ? <CalendarDialog calendar={editing === 'new' ? null : editing} onClose={() => setEditing(null)} /> : null}
    </>
  );
}

function CalendarDialog({ calendar, onClose }: { calendar: WorkCalendar | null; onClose: () => void }) {
  const { t, i18n } = useTranslation();
  const { api } = useAuth();
  const { name: nameOf } = usePrefs();
  const client = useQueryClient();
  const tree = useLocationTree();
  const zones = useMemo(timeZones, []);
  const [name, setName] = useState(calendar?.name ?? '');
  const [zone, setZone] = useState(calendar?.timeZoneId ?? 'Asia/Riyadh');
  const [hours, setHours] = useState<Record<string, DayHours | null>>(calendar?.hours ?? SAUDI);
  const [isDefault, setIsDefault] = useState(calendar?.isDefault ?? false);
  const [holidays, setHolidays] = useState<Holiday[]>(calendar?.holidays ?? []);
  const [sites, setSites] = useState<Set<string>>(new Set(calendar?.siteIds ?? []));
  const [newDate, setNewDate] = useState('');
  const [newName, setNewName] = useState('');

  const weekday = (d: string) => new Intl.DateTimeFormat(i18n.language, { weekday: 'long' }).format(new Date(2026, 10, 1 + DAYS.indexOf(d))); // 2026-11-01 is a Sunday
  const siteNodes = (tree.data ?? []).filter((n) => n.type === 'Site');

  const save = useMutation({
    mutationFn: () => {
      const body = { name: name.trim(), timeZoneId: zone.trim(), hours, isDefault, holidays, siteIds: [...sites], rowVersion: calendar?.rowVersion };
      return calendar ? api.put(`/work-orders/sla/calendars/${calendar.id}`, body) : api.post('/work-orders/sla/calendars', body);
    },
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: ['sla-calendars'] });
      onClose();
    },
  });
  const error = save.error instanceof ApiError ? save.error : null;
  const setDay = (d: string, value: DayHours | null) => setHours((prev) => ({ ...prev, [d]: value }));

  return (
    <div className="overlay" onKeyDown={(e) => e.key === 'Escape' && onClose()}>
      <div className="dialog wide" role="dialog" aria-modal="true" aria-label={calendar ? t('sla.editCalendar') : t('sla.newCalendar')}>
        <h2>{calendar ? t('sla.editCalendar') : t('sla.newCalendar')}</h2>
        <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }}>
          <div className="row">
            <Field label={t('assets.name')} error={error?.field('name')} required>
              <input value={name} onChange={(e) => setName(e.target.value)} maxLength={200} autoFocus />
            </Field>
            <Field label={t('sla.timeZone')} error={error?.field('timeZoneId')} required hint={t('sla.timeZoneHint')}>
              <input value={zone} onChange={(e) => setZone(e.target.value)} list="zones" autoComplete="off" />
            </Field>
          </div>
          <datalist id="zones">{zones.map((z) => <option key={z} value={z} />)}</datalist>

          <fieldset>
            <legend>{t('sla.hours')}</legend>
            <div className="actions">
              <button type="button" onClick={() => setHours(SAUDI)}>{t('sla.presetSaudi')}</button>
              <button type="button" onClick={() => setHours(ALWAYS)}>{t('sla.presetAlways')}</button>
            </div>
            {DAYS.map((d) => {
              const h = hours[d];
              return (
                <div className="hours-row" key={d}>
                  <span>{weekday(d)}</span>
                  <label className="check">
                    <input type="checkbox" checked={h !== null} onChange={(e) => setDay(d, e.target.checked ? { start: '08:00', end: '17:00' } : null)} />
                    <span>{t('sla.open')}</span>
                  </label>
                  {h ? (
                    <>
                      <label><span className="visually-hidden">{weekday(d)} {t('sla.from')}</span><input type="time" value={h.start ?? ''} onChange={(e) => setDay(d, { ...h, start: e.target.value })} /></label>
                      <label><span className="visually-hidden">{weekday(d)} {t('sla.to')}</span><input type="time" value={h.end === '24:00' ? '23:59' : (h.end ?? '')} onChange={(e) => setDay(d, { ...h, end: e.target.value })} /></label>
                    </>
                  ) : <span className="muted">{t('sla.closed')}</span>}
                </div>
              );
            })}
            {error?.field('hours') ? <small className="error" role="alert">{error.field('hours')}</small> : null}
          </fieldset>

          <fieldset>
            <legend>{t('sla.holidays')}</legend>
            <ul className="plain">
              {holidays.map((h) => (
                <li key={h.date}><bdi>{h.date}</bdi> · <bdi>{h.name}</bdi> <button type="button" onClick={() => setHolidays(holidays.filter((x) => x.date !== h.date))}>{t('common.delete')}</button></li>
              ))}
            </ul>
            <div className="row">
              <Field label={t('sla.holidayDate')}><input type="date" value={newDate} onChange={(e) => setNewDate(e.target.value)} /></Field>
              <Field label={t('sla.holidayName')}><input value={newName} onChange={(e) => setNewName(e.target.value)} maxLength={200} /></Field>
              <button type="button" disabled={!newDate || !newName.trim() || holidays.some((h) => h.date === newDate)}
                onClick={() => { setHolidays([...holidays, { date: newDate, name: newName.trim() }].sort((a, b) => a.date.localeCompare(b.date))); setNewDate(''); setNewName(''); }}>
                {t('work.add')}
              </button>
            </div>
            {error?.field('holidays') ? <small className="error" role="alert">{error.field('holidays')}</small> : null}
          </fieldset>

          <fieldset>
            <legend>{t('sla.sites')}</legend>
            <p className="muted">{t('sla.sitesHint')}</p>
            {siteNodes.map((s) => (
              <label className="check" key={s.id}>
                <input type="checkbox" checked={sites.has(s.id)} onChange={(e) => setSites((prev) => { const next = new Set(prev); if (e.target.checked) next.add(s.id); else next.delete(s.id); return next; })} />
                <bdi>{nameOf(s)}</bdi>
              </label>
            ))}
            {error?.field('siteIds') ? <small className="error" role="alert">{error.field('siteIds')}</small> : null}
          </fieldset>

          <label className="check"><input type="checkbox" checked={isDefault} onChange={(e) => setIsDefault(e.target.checked)} /> {t('sla.makeDefault')}</label>
          <ErrorBanner error={error?.status === 400 ? null : save.error} />
          <div className="actions">
            <button type="button" onClick={onClose}>{t('common.cancel')}</button>
            <button type="submit" className="primary" disabled={save.isPending}>{t('common.save')}</button>
          </div>
        </form>
      </div>
    </div>
  );
}

// ---------------- policies ----------------

function PoliciesPart() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const client = useQueryClient();
  const policies = useSlaPolicies();
  const priorityLabel = useEnumLabel('workPriority');
  const [editing, setEditing] = useState<SlaPolicy | 'new' | null>(null);

  const archive = useMutation({
    mutationFn: (p: SlaPolicy) => api.post(`/work-orders/sla/policies/${p.id}/${p.isArchived ? 'restore' : 'archive'}`, { rowVersion: p.rowVersion }),
    onSuccess: () => client.invalidateQueries({ queryKey: ['sla-policies'] }),
  });

  return (
    <>
      <div className="toolbar">
        <h2>{t('sla.policies')}</h2>
        <button type="button" onClick={() => setEditing('new')}>{t('sla.newPolicy')}</button>
      </div>
      <p className="muted">{t('sla.policiesHint')}</p>
      <QueryState query={policies}>
        {policies.data && policies.data.length === 0 ? <p className="muted">{t('sla.noPolicies')}</p> : null}
        {policies.data?.map((p) => (
          <div className="card" key={p.id}>
            <strong><bdi>{p.name}</bdi></strong> {p.isDefault ? <Badge tone="ok">{t('sla.default')}</Badge> : <span className="muted">· <bdi>{p.categoryName}</bdi></span>} {p.isArchived ? <Badge tone="neutral">{t('common.archived')}</Badge> : null}
            <div className="table-wrap">
              <table>
                <caption className="visually-hidden">{p.name}</caption>
                <thead><tr><th scope="col">{t('work.priority')}</th><th scope="col">{t('sla.response')}</th><th scope="col">{t('sla.resolution')}</th></tr></thead>
                <tbody>
                  {p.targets.map((x) => (
                    <tr key={x.priority}><th scope="row">{priorityLabel(x.priority)}</th><td>{fmtMinutes(t, x.responseMinutes)}</td><td>{fmtMinutes(t, x.resolutionMinutes)}</td></tr>
                  ))}
                </tbody>
              </table>
            </div>
            <button type="button" onClick={() => setEditing(p)}>{t('common.edit')}</button>{' '}
            <button type="button" disabled={archive.isPending} onClick={() => archive.mutate(p)}>{p.isArchived ? t('common.restore') : t('common.archive')}</button>
          </div>
        ))}
        <ErrorBanner error={archive.error} />
      </QueryState>
      {editing ? <PolicyDialog policy={editing === 'new' ? null : editing} onClose={() => setEditing(null)} /> : null}
    </>
  );
}

function fmtMinutes(t: (k: string, o?: Record<string, unknown>) => string, minutes: number): string {
  if (minutes % 1440 === 0) return t('sla.days', { count: minutes / 1440 });
  if (minutes % 60 === 0) return t('sla.hoursShort', { count: minutes / 60 });
  return t('sla.minutesShort', { count: minutes });
}

function PolicyDialog({ policy, onClose }: { policy: SlaPolicy | null; onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { name: nameOf } = usePrefs();
  const client = useQueryClient();
  const categories = useWorkCategories();
  const priorityLabel = useEnumLabel('workPriority');
  const [name, setName] = useState(policy?.name ?? '');
  const [isDefault, setIsDefault] = useState(policy ? policy.isDefault : true);
  const [categoryId, setCategoryId] = useState(policy?.categoryId ?? '');
  const [targets, setTargets] = useState<SlaTarget[]>(
    PRIORITIES.map((p) => policy?.targets.find((x) => x.priority === p) ?? { priority: p, responseMinutes: 240, resolutionMinutes: 1440 }),
  );

  const save = useMutation({
    mutationFn: () => {
      const body = { name: name.trim(), isDefault, categoryId: isDefault ? null : categoryId || null, targets, rowVersion: policy?.rowVersion };
      return policy ? api.put(`/work-orders/sla/policies/${policy.id}`, body) : api.post('/work-orders/sla/policies', body);
    },
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: ['sla-policies'] });
      onClose();
    },
  });
  const error = save.error instanceof ApiError ? save.error : null;
  const setTarget = (p: WorkPriority, patch: Partial<SlaTarget>) => setTargets((prev) => prev.map((x) => (x.priority === p ? { ...x, ...patch } : x)));

  return (
    <div className="overlay" onKeyDown={(e) => e.key === 'Escape' && onClose()}>
      <div className="dialog wide" role="dialog" aria-modal="true" aria-label={policy ? t('sla.editPolicy') : t('sla.newPolicy')}>
        <h2>{policy ? t('sla.editPolicy') : t('sla.newPolicy')}</h2>
        <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }}>
          <Field label={t('assets.name')} error={error?.field('name')} required>
            <input value={name} onChange={(e) => setName(e.target.value)} maxLength={200} autoFocus />
          </Field>
          <label className="check"><input type="checkbox" checked={isDefault} onChange={(e) => setIsDefault(e.target.checked)} /> {t('sla.appliesToAll')}</label>
          {!isDefault ? (
            <Field label={t('work.category')} error={error?.field('categoryId')} required>
              <select value={categoryId} onChange={(e) => setCategoryId(e.target.value)}>
                <option value="">{t('common.choose')}</option>
                {(categories.data ?? []).map((c) => <option key={c.id} value={c.id}>{nameOf(c)}</option>)}
              </select>
            </Field>
          ) : error?.field('categoryId') ? <small className="error" role="alert">{error.field('categoryId')}</small> : null}
          <fieldset>
            <legend>{t('sla.targets')}</legend>
            <p className="muted">{t('sla.targetsHint')}</p>
            {targets.map((x) => (
              <div className="sla-row" key={x.priority}>
                <strong>{priorityLabel(x.priority)}</strong>
                <Field label={t('sla.responseMinutes')}>
                  <input type="number" min={1} max={525600} value={x.responseMinutes} onChange={(e) => setTarget(x.priority, { responseMinutes: Number(e.target.value) })} />
                </Field>
                <Field label={t('sla.resolutionMinutes')}>
                  <input type="number" min={1} max={525600} value={x.resolutionMinutes} onChange={(e) => setTarget(x.priority, { resolutionMinutes: Number(e.target.value) })} />
                </Field>
              </div>
            ))}
            {error?.field('targets') ? <small className="error" role="alert">{error.field('targets')}</small> : null}
          </fieldset>
          <p className="banner">{t('sla.appliesLater')}</p>
          <ErrorBanner error={error?.status === 400 ? null : save.error} />
          <div className="actions">
            <button type="button" onClick={onClose}>{t('common.cancel')}</button>
            <button type="submit" className="primary" disabled={save.isPending}>{t('common.save')}</button>
          </div>
        </form>
      </div>
    </div>
  );
}
