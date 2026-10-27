import { useMemo, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import { useDeskVisits, useMyVisits, useOnSite, usePermissions, useSites, useUsers, useVisitorSettings, useWatchlist } from '../api/hooks';
import type { CreatedVisit, Visit, VisitKind, VisitStatus } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Dialog } from '../components/InventoryBits';
import { Badge, ErrorBanner, Field, QueryState, ReasonDialog, useEnumLabel } from '../components/ui';
import { usePrefs } from '../lib/prefs';

const KINDS: VisitKind[] = ['Guest', 'Contractor', 'Delivery', 'Interview'];
const asError = (e: unknown) => (e instanceof ApiError ? e : null);

export function VisitorsPage() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const [params, setParams] = useSearchParams();
  const tabs = useMemo(() => ['mine', ...(can('visitors.reception') ? ['desk'] : []), ...(can('visitors.security') ? ['security'] : [])], [can]);
  const tab = tabs.find((x) => x === params.get('tab')) ?? 'mine';
  return (
    <section>
      <h1>{t('visitors.title')}</h1>
      <p className="muted">{t('visitors.intro')}</p>
      <div className="tabs" role="tablist" aria-label={t('visitors.sections')}>
        {tabs.map((x) => (
          <button key={x} role="tab" type="button" aria-selected={tab === x} className={tab === x ? 'active' : ''} onClick={() => setParams({ tab: x })}>
            {t(`visitors.tab.${x}`)}
          </button>
        ))}
      </div>
      <div role="tabpanel">{tab === 'mine' ? <MineTab /> : tab === 'desk' ? <DeskTab /> : <SecurityTab />}</div>
    </section>
  );
}

function useRefresh() {
  const client = useQueryClient();
  return () => client.invalidateQueries({ queryKey: ['visitors'] });
}

function statusTone(s: VisitStatus): 'ok' | 'warn' | 'bad' | 'neutral' {
  return s === 'CheckedIn' ? 'ok' : s === 'Expected' ? 'warn' : s === 'NoShow' ? 'bad' : 'neutral';
}

/** A pass as an image the browser shows and prints. The picture comes from our own server. */
const svgSrc = (svg: string) => `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`;

// ---------------- hosts ----------------

function MineTab() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { date } = usePrefs();
  const refresh = useRefresh();
  const statusLabel = useEnumLabel('visitStatus');
  const kindLabel = useEnumLabel('visitKind');
  const mine = useMyVisits();
  const [creating, setCreating] = useState(false);
  const [pass, setPass] = useState<CreatedVisit | null>(null);
  const [cancelling, setCancelling] = useState<Visit | null>(null);
  const newPass = useMutation({ mutationFn: (v: Visit) => api.post<CreatedVisit>(`/visits/${v.id}/pass`), onSuccess: (p) => setPass(p) });
  const cancel = useMutation({
    mutationFn: (v: { visit: Visit; reason: string }) => api.post(`/visits/${v.visit.id}/cancel`, { reason: v.reason, rowVersion: v.visit.rowVersion }),
    onSuccess: async () => {
      await refresh();
      setCancelling(null);
    },
  });
  return (
    <>
      <div className="filters"><button type="button" onClick={() => setCreating(true)}>{t('visitors.expectVisitor')}</button></div>
      <ErrorBanner error={newPass.error} />
      <QueryState query={mine}>
        {mine.data && mine.data.length === 0 ? <p className="muted">{t('visitors.noneMine')}</p> : null}
        {mine.data && mine.data.length > 0 ? (
          <div className="table-wrap">
            <table>
              <caption className="visually-hidden">{t('visitors.tab.mine')}</caption>
              <thead>
                <tr>
                  <th scope="col">{t('visitors.visitor')}</th>
                  <th scope="col">{t('visitors.site')}</th>
                  <th scope="col">{t('visitors.expected')}</th>
                  <th scope="col">{t('assets.status')}</th>
                  <th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th>
                </tr>
              </thead>
              <tbody>
                {mine.data.map((v) => (
                  <tr key={v.id}>
                    <td><bdi>{v.visitorName}</bdi>{v.visitorCompany ? <div className="muted"><bdi>{v.visitorCompany}</bdi></div> : null}<div className="muted">{kindLabel(v.kind)}</div></td>
                    <td><bdi>{v.siteName}</bdi></td>
                    <td>{date(v.expectedAt, true)}</td>
                    <td><Badge tone={statusTone(v.status)}>{statusLabel(v.status)}</Badge>{v.cancelReason ? <div className="muted"><bdi>{v.cancelReason}</bdi></div> : null}</td>
                    <td className="actions">
                      {v.status === 'Expected' ? (
                        <>
                          <button type="button" disabled={newPass.isPending} onClick={() => newPass.mutate(v)}>{t('visitors.newPass')}</button>
                          <button type="button" onClick={() => setCancelling(v)}>{t('visitors.cancelVisit')}</button>
                        </>
                      ) : null}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : null}
      </QueryState>
      {creating ? <ExpectDialog onClose={() => setCreating(false)} onCreated={(c) => { setCreating(false); setPass(c); }} /> : null}
      {pass ? <PassDialog created={pass} onClose={() => setPass(null)} /> : null}
      {cancelling ? (
        <ReasonDialog
          title={t('visitors.cancelTitle', { name: cancelling.visitorName })}
          label={t('visitors.cancelReason')}
          confirmLabel={t('visitors.cancelVisit')}
          optional
          busy={cancel.isPending}
          error={cancel.error}
          onConfirm={(reason) => cancel.mutate({ visit: cancelling, reason })}
          onCancel={() => setCancelling(null)}
        />
      ) : null}
    </>
  );
}

function ExpectDialog({ onClose, onCreated }: { onClose: () => void; onCreated: (c: CreatedVisit) => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { can } = usePermissions();
  const refresh = useRefresh();
  const kindLabel = useEnumLabel('visitKind');
  const sites = useSites();
  const users = useUsers();
  const now = new Date();
  now.setMinutes(0, 0, 0);
  const local = (d: Date) => new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
  const [f, setF] = useState({ siteId: '', kind: 'Guest' as VisitKind, name: '', company: '', email: '', phone: '', purpose: '', at: local(new Date(now.getTime() + 3600000)), until: '', hostId: '' });
  const set = <K extends keyof typeof f>(k: K, v: (typeof f)[K]) => setF((x) => ({ ...x, [k]: v }));
  const save = useMutation({
    mutationFn: () =>
      api.post<CreatedVisit>('/visits', {
        siteId: f.siteId || undefined, kind: f.kind, visitorName: f.name, visitorCompany: f.company, visitorEmail: f.email, visitorPhone: f.phone, purpose: f.purpose,
        expectedAt: f.at ? new Date(f.at).toISOString() : undefined, expectedUntil: f.until ? new Date(f.until).toISOString() : undefined, hostId: f.hostId || undefined,
      }),
    onSuccess: async (c) => {
      await refresh();
      onCreated(c);
    },
  });
  const err = asError(save.error);
  const fields = ['siteId', 'kind', 'visitorName', 'visitorCompany', 'visitorEmail', 'visitorPhone', 'purpose', 'expectedAt', 'expectedUntil', 'hostId'];
  return (
    <Dialog title={t('visitors.expectVisitor')} onClose={onClose} wide>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }}>
        <Field label={t('visitors.visitorName')} required error={err?.field('visitorName')}><input value={f.name} onChange={(e) => set('name', e.target.value)} /></Field>
        <Field label={t('visitors.company')} error={err?.field('visitorCompany')}><input value={f.company} onChange={(e) => set('company', e.target.value)} /></Field>
        <Field label={t('visitors.email')} hint={t('visitors.emailHint')} error={err?.field('visitorEmail')}><input inputMode="email" value={f.email} onChange={(e) => set('email', e.target.value)} /></Field>
        <Field label={t('visitors.phone')} error={err?.field('visitorPhone')}><input inputMode="tel" value={f.phone} onChange={(e) => set('phone', e.target.value)} /></Field>
        <Field label={t('visitors.kind')} error={err?.field('kind')}>
          <select value={f.kind} onChange={(e) => set('kind', e.target.value as VisitKind)}>{KINDS.map((k) => <option key={k} value={k}>{kindLabel(k)}</option>)}</select>
        </Field>
        <Field label={t('visitors.site')} required error={err?.field('siteId')}>
          <select value={f.siteId} onChange={(e) => set('siteId', e.target.value)}>
            <option value="">{t('common.choose')}</option>
            {sites.data.map((s) => <option key={s.id} value={s.id}>{s.code} · {s.name}</option>)}
          </select>
        </Field>
        <Field label={t('visitors.expectedAt')} required error={err?.field('expectedAt')}><input type="datetime-local" value={f.at} onChange={(e) => set('at', e.target.value)} /></Field>
        <Field label={t('visitors.until')} hint={t('visitors.untilHint')} error={err?.field('expectedUntil')}><input type="datetime-local" value={f.until} onChange={(e) => set('until', e.target.value)} /></Field>
        <Field label={t('visitors.purpose')} error={err?.field('purpose')}><input value={f.purpose} onChange={(e) => set('purpose', e.target.value)} /></Field>
        {can('visitors.reception') && users.data ? (
          <Field label={t('visitors.onBehalf')} hint={t('visitors.onBehalfHint')} error={err?.field('hostId')}>
            <select value={f.hostId} onChange={(e) => set('hostId', e.target.value)}>
              <option value="">{t('visitors.myself')}</option>
              {users.data.filter((u) => u.isActive).map((u) => <option key={u.id} value={u.id}>{u.displayName ?? u.email ?? u.id}</option>)}
            </select>
          </Field>
        ) : null}
        <ErrorBanner error={err && fields.some((x) => err.field(x)) ? null : save.error} />
        <div className="actions">
          <button type="button" onClick={onClose}>{t('common.cancel')}</button>
          <button type="submit" className="primary" disabled={save.isPending}>{t('visitors.expect')}</button>
        </div>
      </form>
    </Dialog>
  );
}

function PassDialog({ created, onClose }: { created: CreatedVisit; onClose: () => void }) {
  const { t } = useTranslation();
  return (
    <Dialog title={t('visitors.passTitle', { name: created.visit.visitorName })} onClose={onClose}>
      <p>{t('visitors.passHelp')}</p>
      <img src={svgSrc(created.qrSvg)} alt={t('visitPass.qrAlt')} width={200} height={200} />
      <Field label={t('visitors.passAddress')}>
        <input readOnly value={created.passUrl} dir="ltr" onFocus={(e) => e.currentTarget.select()} />
      </Field>
      <p className="muted">{t('visitors.passOnce')}</p>
      <div className="actions"><button type="button" className="primary" onClick={onClose}>{t('visitors.done')}</button></div>
    </Dialog>
  );
}

// ---------------- front desk ----------------

function DeskTab() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { can } = usePermissions();
  const { date } = usePrefs();
  const refresh = useRefresh();
  const statusLabel = useEnumLabel('visitStatus');
  const [view, setView] = useState<'today' | 'onsite'>('today');
  const [day, setDay] = useState(() => new Date().toISOString().slice(0, 10));
  const [q, setQ] = useState('');
  const [scan, setScan] = useState('');
  const [checkingIn, setCheckingIn] = useState<Visit | null>(null);
  const [badge, setBadge] = useState<Visit | null>(null);
  const [lookupError, setLookupError] = useState<string | null>(null);
  const expected = useDeskVisits({ day, q: q.trim() || undefined }, view === 'today');
  const onSite = useOnSite(view === 'onsite');
  const list = view === 'today' ? expected : onSite;
  const lookup = useMutation({
    mutationFn: () => api.get<Visit>('/visits/lookup', { code: scan }),
    onSuccess: (v) => {
      setLookupError(null);
      setScan('');
      setCheckingIn(v);
    },
    onError: (e) => setLookupError(e instanceof ApiError ? e.title : t('visitors.lookupFailed')),
  });
  const checkOut = useMutation({ mutationFn: (v: Visit) => api.post(`/visits/${v.id}/check-out`, { rowVersion: v.rowVersion }), onSuccess: () => refresh() });
  return (
    <>
      <form className="filters" onSubmit={(e) => { e.preventDefault(); if (scan.trim()) lookup.mutate(); }}>
        <Field label={t('visitors.scan')} hint={t('visitors.scanHint')}><input value={scan} onChange={(e) => setScan(e.target.value)} autoComplete="off" dir="ltr" /></Field>
        <button type="submit" disabled={!scan.trim() || lookup.isPending}>{t('visitors.find')}</button>
      </form>
      {lookupError ? <p className="banner error" role="alert">{lookupError}</p> : null}
      <div className="filters">
        <Field label={t('visitors.show')}>
          <select value={view} onChange={(e) => setView(e.target.value as 'today' | 'onsite')}>
            <option value="today">{t('visitors.viewToday')}</option>
            <option value="onsite">{t('visitors.viewOnSite')}</option>
          </select>
        </Field>
        {view === 'today' ? (
          <>
            <Field label={t('bookings.day')}><input type="date" value={day} onChange={(e) => setDay(e.target.value)} /></Field>
            <Field label={t('visitors.search')}><input value={q} onChange={(e) => setQ(e.target.value)} /></Field>
          </>
        ) : null}
      </div>
      <ErrorBanner error={checkOut.error} />
      <QueryState query={list}>
        {list.data && list.data.length === 0 ? <p className="muted">{view === 'today' ? t('visitors.noneToday') : t('visitors.nobodyOnSite')}</p> : null}
        {list.data && list.data.length > 0 ? (
          <div className="table-wrap">
            <table>
              <caption className="visually-hidden">{t('visitors.tab.desk')}</caption>
              <thead>
                <tr>
                  <th scope="col">{t('visitors.visitor')}</th>
                  <th scope="col">{t('visitors.host')}</th>
                  <th scope="col">{t('visitors.site')}</th>
                  <th scope="col">{t('visitors.expected')}</th>
                  <th scope="col">{t('assets.status')}</th>
                  <th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th>
                </tr>
              </thead>
              <tbody>
                {list.data.map((v) => (
                  <tr key={v.id}>
                    <td><bdi>{v.visitorName}</bdi>{v.visitorCompany ? <div className="muted"><bdi>{v.visitorCompany}</bdi></div> : null}</td>
                    <td><bdi>{v.hostName}</bdi></td>
                    <td><bdi>{v.siteName}</bdi></td>
                    <td>{date(v.expectedAt, true)}{v.checkedInAt ? <div className="muted">{t('visitors.arrivedAt', { time: date(v.checkedInAt, true) })}</div> : null}</td>
                    <td><Badge tone={statusTone(v.status)}>{statusLabel(v.status)}</Badge>{v.badgeNumber ? <div className="muted">{t('visitors.badgeNo', { number: v.badgeNumber })}</div> : null}</td>
                    <td className="actions">
                      {v.canCheckInNow ? <button type="button" onClick={() => setCheckingIn(v)}>{t('visitors.checkIn')}</button> : null}
                      {v.status === 'CheckedIn' ? <button type="button" disabled={checkOut.isPending} onClick={() => checkOut.mutate(v)}>{t('visitors.checkOut')}</button> : null}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : null}
      </QueryState>
      {checkingIn ? <CheckInDialog visit={checkingIn} canOverride={can('visitors.security')} onClose={() => setCheckingIn(null)} onDone={(v) => { setCheckingIn(null); setBadge(v); }} /> : null}
      {badge ? <BadgeDialog visit={badge} onClose={() => setBadge(null)} /> : null}
    </>
  );
}

function CheckInDialog({ visit, canOverride, onClose, onDone }: { visit: Visit; canOverride: boolean; onClose: () => void; onDone: (v: Visit) => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const refresh = useRefresh();
  const settings = useVisitorSettings(true);
  const [badge, setBadge] = useState('');
  const [induction, setInduction] = useState(false);
  const [override, setOverride] = useState('');
  const [blocked, setBlocked] = useState(false);
  const needsInduction = visit.kind === 'Contractor' && settings.data?.requireInduction === true;
  const save = useMutation({
    mutationFn: () => api.post<Visit>(`/visits/${visit.id}/check-in`, { badgeNumber: badge, inductionAcknowledged: induction, overrideReason: override || undefined, rowVersion: visit.rowVersion }),
    onSuccess: async (v) => {
      await refresh();
      onDone(v);
    },
    onError: (e) => setBlocked(e instanceof ApiError && e.isForbidden),
  });
  const err = asError(save.error);
  return (
    <Dialog title={t('visitors.checkInTitle', { name: visit.visitorName })} onClose={onClose}>
      <p><bdi>{visit.visitorName}</bdi>{visit.visitorCompany ? <> · <bdi>{visit.visitorCompany}</bdi></> : null} → <bdi>{visit.hostName}</bdi>, <bdi>{visit.siteName}</bdi></p>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }}>
        <Field label={t('visitors.badge')} hint={t('visitors.badgeHint')} error={err?.field('badgeNumber')}><input value={badge} onChange={(e) => setBadge(e.target.value)} /></Field>
        {needsInduction ? (
          <div className="panel">
            <h3>{t('visitors.induction')}</h3>
            <p><bdi>{settings.data?.inductionText}</bdi></p>
            <label className="inline"><input type="checkbox" checked={induction} onChange={(e) => setInduction(e.target.checked)} /> {t('visitors.inductionAck')}</label>
            {err?.field('inductionAcknowledged') ? <p className="field-error" role="alert">{err.field('inductionAcknowledged')}</p> : null}
          </div>
        ) : null}
        {blocked && canOverride ? (
          <Field label={t('visitors.overrideReason')} hint={t('visitors.overrideHint')} error={err?.field('overrideReason')}><textarea rows={2} value={override} onChange={(e) => setOverride(e.target.value)} /></Field>
        ) : null}
        {blocked ? <p className="banner error" role="alert">{t('visitors.blocked')}</p> : <ErrorBanner error={err?.field('badgeNumber') || err?.field('inductionAcknowledged') ? null : save.error} />}
        <div className="actions">
          <button type="button" onClick={onClose}>{t('common.cancel')}</button>
          <button type="submit" className="primary" disabled={save.isPending}>{blocked && canOverride ? t('visitors.letIn') : t('visitors.checkIn')}</button>
        </div>
      </form>
    </Dialog>
  );
}

function BadgeDialog({ visit, onClose }: { visit: Visit; onClose: () => void }) {
  const { t } = useTranslation();
  const { date } = usePrefs();
  return (
    <Dialog title={t('visitors.badgeTitle')} onClose={onClose}>
      <div className="badge-print">
        <p className="badge-kind">{t('visitors.visitorBadge')}</p>
        <p className="badge-name"><bdi>{visit.visitorName}</bdi></p>
        {visit.visitorCompany ? <p><bdi>{visit.visitorCompany}</bdi></p> : null}
        <p>{t('visitors.visiting', { host: visit.hostName })}</p>
        <p><bdi>{visit.siteName}</bdi> · {date(visit.checkedInAt ?? visit.expectedAt, true)}</p>
        {visit.badgeNumber ? <p>{t('visitors.badgeNo', { number: visit.badgeNumber })}</p> : null}
      </div>
      <div className="actions">
        <button type="button" onClick={() => window.print()}>{t('visitors.print')}</button>
        <button type="button" className="primary" onClick={onClose}>{t('visitors.done')}</button>
      </div>
    </Dialog>
  );
}

// ---------------- security ----------------

function SecurityTab() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { date } = usePrefs();
  const refresh = useRefresh();
  const watchlist = useWatchlist(true);
  const settings = useVisitorSettings(true);
  const [name, setName] = useState('');
  const [reason, setReason] = useState('');
  const [removing, setRemoving] = useState<{ id: string; name: string } | null>(null);
  const add = useMutation({
    mutationFn: () => api.post('/visits/watchlist', { name, reason }),
    onSuccess: async () => {
      setName('');
      setReason('');
      await refresh();
    },
  });
  const remove = useMutation({
    mutationFn: (v: { id: string; reason: string }) => api.post(`/visits/watchlist/${v.id}/remove`, { reason: v.reason }),
    onSuccess: async () => {
      await refresh();
      setRemoving(null);
    },
  });
  const addErr = asError(add.error);
  return (
    <>
      <h2>{t('visitors.watchlist')}</h2>
      <p className="muted">{t('visitors.watchlistHelp')}</p>
      <form className="filters" onSubmit={(e) => { e.preventDefault(); add.mutate(); }}>
        <Field label={t('visitors.visitorName')} error={addErr?.field('name')}><input value={name} onChange={(e) => setName(e.target.value)} /></Field>
        <Field label={t('visitors.watchReason')} error={addErr?.field('reason')}><input value={reason} onChange={(e) => setReason(e.target.value)} /></Field>
        <button type="submit" disabled={add.isPending}>{t('visitors.addToWatchlist')}</button>
      </form>
      <ErrorBanner error={addErr?.field('name') || addErr?.field('reason') ? null : add.error} />
      <QueryState query={watchlist}>
        {watchlist.data && watchlist.data.length === 0 ? <p className="muted">{t('visitors.watchlistEmpty')}</p> : null}
        {watchlist.data && watchlist.data.length > 0 ? (
          <div className="table-wrap">
            <table>
              <caption className="visually-hidden">{t('visitors.watchlist')}</caption>
              <thead>
                <tr>
                  <th scope="col">{t('visitors.visitorName')}</th>
                  <th scope="col">{t('visitors.watchReason')}</th>
                  <th scope="col">{t('visitors.added')}</th>
                  <th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th>
                </tr>
              </thead>
              <tbody>
                {watchlist.data.map((w) => (
                  <tr key={w.id}>
                    <td><bdi>{w.name}</bdi></td>
                    <td><bdi>{w.reason}</bdi></td>
                    <td>{date(w.addedAt)}</td>
                    <td className="actions"><button type="button" onClick={() => setRemoving({ id: w.id, name: w.name })}>{t('visitors.removeFromWatchlist')}</button></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : null}
      </QueryState>
      {removing ? (
        <ReasonDialog
          title={t('visitors.removeTitle', { name: removing.name })}
          label={t('visitors.removeReason')}
          confirmLabel={t('visitors.removeFromWatchlist')}
          busy={remove.isPending}
          error={remove.error}
          onConfirm={(r) => remove.mutate({ id: removing.id, reason: r })}
          onCancel={() => setRemoving(null)}
        />
      ) : null}
      <h2>{t('visitors.rules')}</h2>
      <QueryState query={settings}>{settings.data ? <SettingsForm key={settings.data.rowVersion ?? 'new'} initial={settings.data} /> : null}</QueryState>
    </>
  );
}

function SettingsForm({ initial }: { initial: NonNullable<ReturnType<typeof useVisitorSettings>['data']> }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const refresh = useRefresh();
  const [require, setRequire] = useState(initial.requireInduction);
  const [text, setText] = useState(initial.inductionText ?? '');
  const [days, setDays] = useState(String(initial.retentionDays));
  const save = useMutation({
    mutationFn: () => api.put('/visits/settings', { requireInduction: require, inductionText: text, retentionDays: Number(days), rowVersion: initial.rowVersion ?? undefined }),
    onSuccess: () => refresh(),
  });
  const err = asError(save.error);
  return (
    <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }}>
      <label className="inline"><input type="checkbox" checked={require} onChange={(e) => setRequire(e.target.checked)} /> {t('visitors.requireInduction')}</label>
      <Field label={t('visitors.inductionText')} error={err?.field('inductionText')}><textarea rows={4} value={text} onChange={(e) => setText(e.target.value)} /></Field>
      <Field label={t('visitors.retention')} hint={t('visitors.retentionHint')} error={err?.field('retentionDays')}><input type="number" min={30} max={3650} value={days} onChange={(e) => setDays(e.target.value)} /></Field>
      <ErrorBanner error={err?.field('inductionText') || err?.field('retentionDays') ? null : save.error} />
      <button type="submit" disabled={save.isPending}>{t('common.save')}</button>
    </form>
  );
}
