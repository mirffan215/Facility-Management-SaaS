import { useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import { useLease, useLeaseSchedule, useLeaseTotals, useLeases, usePermissions } from '../api/hooks';
import type { DeadlineKind, Lease, LeaseDeadline, LeaseKind, LeaseState, RentFrequency } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Dialog } from '../components/InventoryBits';
import { LocationSelect } from '../components/LocationSelect';
import { Badge, ErrorBanner, Field, Pager, QueryState, ReasonDialog, useEnumLabel } from '../components/ui';
import { formatNumber } from '../lib/format';
import { usePrefs } from '../lib/prefs';

const STATES: LeaseState[] = ['Draft', 'Active', 'Expired', 'Terminated'];
const KINDS: LeaseKind[] = ['Occupied', 'LetOut'];
const FREQUENCIES: RentFrequency[] = ['Monthly', 'Quarterly', 'HalfYearly', 'Annual'];
const asError = (e: unknown) => (e instanceof ApiError ? e : null);

function stateTone(s: LeaseState): 'ok' | 'warn' | 'bad' | 'neutral' {
  return s === 'Active' ? 'ok' : s === 'Expired' ? 'bad' : 'neutral';
}

function deadlineTone(d: LeaseDeadline): 'warn' | 'bad' | 'neutral' {
  return d.daysAway <= 30 ? 'bad' : d.daysAway <= 90 ? 'warn' : 'neutral';
}

function useRefresh() {
  const client = useQueryClient();
  return () => client.invalidateQueries({ queryKey: ['leases'] });
}

function useMoney() {
  const { i18n } = useTranslation();
  return (v: number | null | undefined, c?: string) => (v == null ? '—' : `${formatNumber(v, i18n.language, 2)} ${c ?? ''}`.trim());
}

export function LeasesPage() {
  const [params] = useSearchParams();
  const id = params.get('lease');
  return id ? <LeaseView id={id} /> : <LeaseList />;
}

function DeadlineBadge({ d }: { d: LeaseDeadline }) {
  const { t } = useTranslation();
  const { date } = usePrefs();
  const label = useEnumLabel('deadlineKind');
  return <Badge tone={deadlineTone(d)}>{label(d.kind as DeadlineKind)} {date(d.on)} · {t('leases.daysAway', { count: d.daysAway })}</Badge>;
}

// ---------------------------------------------------------------- list

function LeaseList() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const { date } = usePrefs();
  const [, setParams] = useSearchParams();
  const stateLabel = useEnumLabel('leaseState');
  const kindLabel = useEnumLabel('leaseKind');
  const money = useMoney();
  const totals = useLeaseTotals();
  const [state, setState] = useState('');
  const [kind, setKind] = useState('');
  const [q, setQ] = useState('');
  const [page, setPage] = useState(1);
  const [creating, setCreating] = useState(false);
  const list = useLeases({ state: state || undefined, kind: kind || undefined, q: q.trim() || undefined, page, pageSize: 25 });
  const change = (fn: () => void) => { fn(); setPage(1); };

  return (
    <section>
      <h1>{t('leases.title')}</h1>
      <p className="muted">{t('leases.intro')}</p>
      <QueryState query={totals}>
        {totals.data ? (
          <ul className="cards plain" aria-label={t('leases.summary')}>
            <li className="card"><span className="value">{totals.data.active}</span><span className="muted">{t('leases.active')}</span></li>
            <li className="card"><span className="value">{totals.data.expiringSoon}</span><span className="muted">{t('leases.expiringSoon')}</span></li>
            <li className="card"><span className="value">{totals.data.deadlinesSoon}</span><span className="muted">{t('leases.deadlinesSoon')}</span></li>
            <li className="card"><span className="value">{totals.data.expired}</span><span className="muted">{t('leases.expired')}</span></li>
            {totals.data.rent.map((r) => (
              <li className="card" key={`${r.kind}-${r.currencyCode}`}><span className="value">{money(r.nextTwelveMonths, r.currencyCode)}</span><span className="muted">{t(r.kind === 'Occupied' ? 'leases.rentPayable' : 'leases.rentReceivable')}</span></li>
            ))}
          </ul>
        ) : null}
      </QueryState>
      <div className="toolbar">
        <Field label={t('leases.state')}>
          <select value={state} onChange={(e) => change(() => setState(e.target.value))}>
            <option value="">{t('common.all')}</option>
            {STATES.map((s) => <option key={s} value={s}>{stateLabel(s)}</option>)}
          </select>
        </Field>
        <Field label={t('leases.kind')}>
          <select value={kind} onChange={(e) => change(() => setKind(e.target.value))}>
            <option value="">{t('common.all')}</option>
            {KINDS.map((k) => <option key={k} value={k}>{kindLabel(k)}</option>)}
          </select>
        </Field>
        <Field label={t('common.search')}><input value={q} onChange={(e) => change(() => setQ(e.target.value))} /></Field>
        {can('leases.manage') ? <button type="button" className="primary" onClick={() => setCreating(true)}>{t('leases.new')}</button> : null}
      </div>
      <QueryState query={list}>
        {list.data && list.data.items.length === 0 ? <p className="muted">{t('leases.none')}</p> : null}
        {list.data && list.data.items.length > 0 ? (
          <>
            <div className="table-wrap" tabIndex={0} role="region" aria-label={t('leases.title')}>
              <table>
                <caption className="visually-hidden">{t('leases.title')}</caption>
                <thead>
                  <tr>
                    <th scope="col">{t('leases.lease')}</th>
                    <th scope="col">{t('leases.kind')}</th>
                    <th scope="col">{t('leases.counterparty')}</th>
                    <th scope="col">{t('leases.term')}</th>
                    <th scope="col">{t('leases.rent')}</th>
                    <th scope="col">{t('leases.state')}</th>
                    <th scope="col">{t('leases.next')}</th>
                  </tr>
                </thead>
                <tbody>
                  {list.data.items.map((l) => (
                    <tr key={l.id}>
                      <th scope="row"><button type="button" className="link" onClick={() => setParams({ lease: l.id })}><bdi>{l.reference}</bdi> <bdi>{l.title}</bdi></button></th>
                      <td>{kindLabel(l.kind)}</td>
                      <td><bdi>{l.counterparty}</bdi></td>
                      <td>{date(l.startOn)} → {date(l.endOn)}</td>
                      <td>{money(l.baseRent, l.currencyCode)} / {t(`leases.per.${l.frequency}`)}</td>
                      <td><Badge tone={stateTone(l.state)}>{stateLabel(l.state)}</Badge></td>
                      <td>{l.nextDeadline ? <DeadlineBadge d={l.nextDeadline} /> : '—'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <Pager page={page} pageSize={25} total={list.data.total} onChange={setPage} />
          </>
        ) : null}
      </QueryState>
      {creating ? <LeaseForm lease={null} onClose={() => setCreating(false)} onSaved={(id) => { setCreating(false); setParams({ lease: id }); }} /> : null}
    </section>
  );
}

function LeaseForm({ lease, onClose, onSaved }: { lease: Lease | null; onClose: () => void; onSaved?: (id: string) => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const refresh = useRefresh();
  const kindLabel = useEnumLabel('leaseKind');
  const freqLabel = useEnumLabel('rentFrequency');
  const locked = lease?.termsLocked ?? false;
  const [reference, setReference] = useState(lease?.reference ?? '');
  const [title, setTitle] = useState(lease?.title ?? '');
  const [kind, setKind] = useState<LeaseKind>(lease?.kind ?? 'Occupied');
  const [counterparty, setCounterparty] = useState(lease?.counterparty ?? '');
  const [locationId, setLocationId] = useState(lease?.locationId ?? '');
  const [area, setArea] = useState(lease?.areaSquareMetres?.toString() ?? '');
  const [startOn, setStartOn] = useState(lease?.startOn ?? '');
  const [endOn, setEndOn] = useState(lease?.endOn ?? '');
  const [noticeDays, setNoticeDays] = useState((lease?.noticeDays ?? 0).toString());
  const [breakOn, setBreakOn] = useState(lease?.breakOn ?? '');
  const [breakNotice, setBreakNotice] = useState((lease?.breakNoticeDays ?? 0).toString());
  const [renewal, setRenewal] = useState(lease?.renewalMonths?.toString() ?? '');
  const [currency, setCurrency] = useState(lease?.currencyCode ?? 'SAR');
  const [rent, setRent] = useState(lease?.baseRent.toString() ?? '');
  const [frequency, setFrequency] = useState<RentFrequency>(lease?.frequency ?? 'Monthly');
  const [escalation, setEscalation] = useState((lease?.escalationPercent ?? 0).toString());
  const [every, setEvery] = useState((lease?.escalationEveryMonths ?? 12).toString());
  const [free, setFree] = useState((lease?.rentFreeMonths ?? 0).toString());
  const [deposit, setDeposit] = useState(lease?.deposit?.toString() ?? '');
  const [lead, setLead] = useState((lease?.alertLeadDays ?? 90).toString());
  const [notes, setNotes] = useState(lease?.notes ?? '');
  const num = (v: string) => (v.trim() === '' ? null : Number(v));

  const save = useMutation({
    mutationFn: () => {
      const body = {
        reference, title, kind, counterparty, locationId: locationId || null, areaSquareMetres: num(area), startOn: startOn || null, endOn: endOn || null, noticeDays: Number(noticeDays || 0),
        breakOn: breakOn || null, breakNoticeDays: Number(breakNotice || 0), renewalMonths: num(renewal), currencyCode: currency, baseRent: num(rent), frequency, escalationPercent: Number(escalation || 0),
        escalationEveryMonths: Number(every || 12), rentFreeMonths: Number(free || 0), deposit: num(deposit), alertLeadDays: Number(lead || 90), notes: notes || null, rowVersion: lease?.rowVersion ?? null,
      };
      return lease ? api.put<Lease>(`/leases/${lease.id}`, body) : api.post<Lease>('/leases', body);
    },
    onSuccess: async (saved) => { await refresh(); (onSaved ?? onClose)(saved.id); },
  });
  const errors = asError(save.error)?.fieldErrors ?? {};

  return (
    <Dialog title={lease ? t('leases.edit') : t('leases.new')} onClose={onClose} wide>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }} noValidate>
        <div className="row">
          <Field label={t('leases.reference')} error={errors.reference?.[0]} required><input value={reference} onChange={(e) => setReference(e.target.value)} maxLength={30} autoFocus /></Field>
          <Field label={t('leases.titleField')} error={errors.title?.[0]} required><input value={title} onChange={(e) => setTitle(e.target.value)} maxLength={200} /></Field>
          <Field label={t('leases.kind')} error={errors.kind?.[0]}>
            <select value={kind} disabled={locked} onChange={(e) => setKind(e.target.value as LeaseKind)}>{KINDS.map((k) => <option key={k} value={k}>{kindLabel(k)}</option>)}</select>
          </Field>
        </div>
        <div className="row">
          <Field label={t('leases.counterparty')} hint={t('leases.counterpartyHint')} error={errors.counterparty?.[0]} required><input value={counterparty} onChange={(e) => setCounterparty(e.target.value)} maxLength={200} /></Field>
          <Field label={t('leases.place')} error={errors.locationId?.[0]}><LocationSelect value={locationId} onChange={setLocationId} /></Field>
          <Field label={t('leases.area')} error={errors.areaSquareMetres?.[0]}><input type="number" min={0} step="any" value={area} onChange={(e) => setArea(e.target.value)} /></Field>
        </div>
        <div className="row">
          <Field label={t('leases.startOn')} error={errors.startOn?.[0]} required><input type="date" value={startOn} disabled={locked} onChange={(e) => setStartOn(e.target.value)} /></Field>
          <Field label={t('leases.endOn')} error={errors.endOn?.[0]} required><input type="date" value={endOn} disabled={locked} onChange={(e) => setEndOn(e.target.value)} /></Field>
          <Field label={t('leases.noticeDays')} hint={t('leases.noticeHint')} error={errors.noticeDays?.[0]}><input type="number" min={0} max={730} value={noticeDays} onChange={(e) => setNoticeDays(e.target.value)} /></Field>
          <Field label={t('leases.alertLeadDays')} hint={t('leases.alertHint')} error={errors.alertLeadDays?.[0]}><input type="number" min={0} max={365} value={lead} onChange={(e) => setLead(e.target.value)} /></Field>
        </div>
        <div className="row">
          <Field label={t('leases.breakOn')} error={errors.breakOn?.[0]}><input type="date" value={breakOn} onChange={(e) => setBreakOn(e.target.value)} /></Field>
          <Field label={t('leases.breakNoticeDays')} error={errors.breakNoticeDays?.[0]}><input type="number" min={0} max={365} value={breakNotice} onChange={(e) => setBreakNotice(e.target.value)} /></Field>
          <Field label={t('leases.renewalMonths')} hint={t('leases.renewalHint')} error={errors.renewalMonths?.[0]}><input type="number" min={1} max={120} value={renewal} onChange={(e) => setRenewal(e.target.value)} /></Field>
        </div>
        <fieldset>
          <legend>{t('leases.rentTerms')}</legend>
          {locked ? <p className="muted">{t('leases.locked')}</p> : null}
          <div className="row">
            <Field label={t('leases.baseRent')} hint={t('leases.baseRentHint')} error={errors.baseRent?.[0]} required><input type="number" min={0} step="any" value={rent} disabled={locked} onChange={(e) => setRent(e.target.value)} /></Field>
            <Field label={t('leases.currency')} error={errors.currencyCode?.[0]}><input value={currency} disabled={locked} onChange={(e) => setCurrency(e.target.value.toUpperCase())} maxLength={3} /></Field>
            <Field label={t('leases.frequency')} error={errors.frequency?.[0]}>
              <select value={frequency} disabled={locked} onChange={(e) => setFrequency(e.target.value as RentFrequency)}>{FREQUENCIES.map((f) => <option key={f} value={f}>{freqLabel(f)}</option>)}</select>
            </Field>
            <Field label={t('leases.deposit')} error={errors.deposit?.[0]}><input type="number" min={0} step="any" value={deposit} disabled={locked} onChange={(e) => setDeposit(e.target.value)} /></Field>
          </div>
          <div className="row">
            <Field label={t('leases.escalationPercent')} error={errors.escalationPercent?.[0]}><input type="number" min={0} max={100} step="any" value={escalation} disabled={locked} onChange={(e) => setEscalation(e.target.value)} /></Field>
            <Field label={t('leases.escalationEveryMonths')} error={errors.escalationEveryMonths?.[0]}><input type="number" min={1} max={120} value={every} disabled={locked} onChange={(e) => setEvery(e.target.value)} /></Field>
            <Field label={t('leases.rentFreeMonths')} error={errors.rentFreeMonths?.[0]}><input type="number" min={0} value={free} disabled={locked} onChange={(e) => setFree(e.target.value)} /></Field>
          </div>
        </fieldset>
        <Field label={t('leases.notes')} error={errors.notes?.[0]}><textarea value={notes} onChange={(e) => setNotes(e.target.value)} rows={2} maxLength={2000} /></Field>
        <ErrorBanner error={Object.keys(errors).length ? null : save.error} />
        <div className="actions">
          <button type="button" onClick={onClose}>{t('common.cancel')}</button>
          <button type="submit" className="primary" disabled={save.isPending}>{t('common.save')}</button>
        </div>
      </form>
    </Dialog>
  );
}

// ---------------------------------------------------------------- one lease

function LeaseView({ id }: { id: string }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { can } = usePermissions();
  const { date } = usePrefs();
  const [, setParams] = useSearchParams();
  const refresh = useRefresh();
  const stateLabel = useEnumLabel('leaseState');
  const kindLabel = useEnumLabel('leaseKind');
  const freqLabel = useEnumLabel('rentFrequency');
  const money = useMoney();
  const detail = useLease(id);
  const schedule = useLeaseSchedule(id);
  const [editing, setEditing] = useState(false);
  const [extending, setExtending] = useState(false);
  const [ending, setEnding] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const l = detail.data;
  const canManage = can('leases.manage');

  const activate = useMutation({
    mutationFn: () => api.post(`/leases/${id}/activate`, { rowVersion: l?.rowVersion }),
    onSuccess: () => refresh(),
  });
  const remove = useMutation({
    mutationFn: (reason: string) => api.post(`/leases/${id}/delete`, { reason }),
    onSuccess: async () => { setDeleting(false); await refresh(); setParams({}); },
  });

  return (
    <section>
      <p><button type="button" className="link" onClick={() => setParams({})}>← {t('leases.back')}</button></p>
      <QueryState query={detail}>
        {l ? (
          <>
            <h1><bdi>{l.reference}</bdi> <bdi>{l.title}</bdi></h1>
            <p>
              <Badge tone={stateTone(l.state)}>{stateLabel(l.state)}</Badge> {kindLabel(l.kind)} · <bdi>{l.counterparty}</bdi>
              {l.locationName ? <> · <bdi>{l.locationName}</bdi></> : null}
            </p>
            {l.deadlines.length > 0 ? (
              <ul className="plain" aria-label={t('leases.deadlines')}>
                {l.deadlines.map((d) => <li key={d.kind}><DeadlineBadge d={d} /></li>)}
              </ul>
            ) : null}
            {l.state === 'Expired' ? <p role="status"><Badge tone="bad">{t('leases.expiredNotice')}</Badge></p> : null}
            <dl className="facts">
              <dt>{t('leases.term')}</dt><dd>{date(l.startOn)} → {date(l.endOn)}</dd>
              <dt>{t('leases.noticeDays')}</dt><dd>{l.noticeDays > 0 && l.noticeDeadline ? t('leases.noticeBy', { days: l.noticeDays, date: date(l.noticeDeadline) }) : '—'}</dd>
              <dt>{t('leases.breakOn')}</dt><dd>{l.breakOn ? t('leases.breakOnBy', { date: date(l.breakOn), days: l.breakNoticeDays }) : '—'}</dd>
              <dt>{t('leases.renewalMonths')}</dt><dd>{l.renewalMonths ? t('leases.months', { count: l.renewalMonths }) : '—'}</dd>
              <dt>{t('leases.baseRent')}</dt><dd>{money(l.baseRent, l.currencyCode)} / {freqLabel(l.frequency)}</dd>
              <dt>{t('leases.escalation')}</dt><dd>{l.escalationPercent > 0 ? t('leases.escalationText', { percent: l.escalationPercent, months: l.escalationEveryMonths }) : '—'}</dd>
              <dt>{t('leases.rentFreeMonths')}</dt><dd>{l.rentFreeMonths}</dd>
              <dt>{t('leases.deposit')}</dt><dd>{money(l.deposit, l.currencyCode)}</dd>
              <dt>{t('leases.area')}</dt><dd>{l.areaSquareMetres != null ? `${formatNumber(l.areaSquareMetres, 'en', 2)} m²` : '—'}</dd>
              <dt>{t('leases.totalRent')}</dt><dd>{money(l.totalRent, l.currencyCode)}</dd>
              {l.terminatedOn ? <><dt>{t('leases.terminatedOn')}</dt><dd>{date(l.terminatedOn)}{l.terminationReason ? <> · <bdi>{l.terminationReason}</bdi></> : null}</dd></> : null}
            </dl>
            {l.notes ? <p><bdi>{l.notes}</bdi></p> : null}
            {canManage && l.state !== 'Terminated' ? (
              <div className="actions">
                <button type="button" onClick={() => setEditing(true)}>{t('common.edit')}</button>
                {l.state === 'Draft' ? <button type="button" className="primary" disabled={activate.isPending} onClick={() => activate.mutate()}>{t('leases.activate')}</button> : null}
                {l.state === 'Draft' ? <button type="button" onClick={() => setDeleting(true)}>{t('common.delete')}</button> : null}
                {l.state === 'Active' || l.state === 'Expired' ? <button type="button" onClick={() => setExtending(true)}>{t('leases.extend')}</button> : null}
                {l.state === 'Active' || l.state === 'Expired' ? <button type="button" onClick={() => setEnding(true)}>{t('leases.terminate')}</button> : null}
              </div>
            ) : null}
            <ErrorBanner error={activate.error} />

            <h2>{t('leases.schedule')}</h2>
            <QueryState query={schedule}>
              {schedule.data ? (
                <div className="table-wrap" tabIndex={0} role="region" aria-label={t('leases.schedule')}>
                  <table>
                    <caption className="visually-hidden">{t('leases.schedule')}</caption>
                    <thead>
                      <tr>
                        <th scope="col">#</th>
                        <th scope="col">{t('leases.dueOn')}</th>
                        <th scope="col">{t('leases.period')}</th>
                        <th scope="col">{t('leases.amount')}</th>
                        <th scope="col">{t('leases.notes')}</th>
                      </tr>
                    </thead>
                    <tbody>
                      {schedule.data.installments.map((i) => (
                        <tr key={i.number}>
                          <th scope="row">{i.number}</th>
                          <td>{date(i.dueOn)}</td>
                          <td>{date(i.dueOn)} → {date(i.periodEnd)}</td>
                          <td>{money(i.amount, schedule.data!.currencyCode)}</td>
                          <td>{i.isRentFree ? <Badge tone="ok">{t('leases.rentFree')}</Badge> : null} {i.isPartial ? <Badge tone="neutral">{t('leases.partial')}</Badge> : null} {i.escalationStep > 0 ? t('leases.stepN', { step: i.escalationStep }) : ''}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              ) : null}
            </QueryState>
            <p className="muted">{t('leases.scheduleNote')}</p>

            {editing ? <LeaseForm lease={l} onClose={() => setEditing(false)} /> : null}
            {extending ? <ExtendDialog lease={l} onClose={() => setExtending(false)} /> : null}
            {ending ? <TerminateDialog lease={l} onClose={() => setEnding(false)} /> : null}
            {deleting ? (
              <ReasonDialog title={t('leases.deleteTitle')} label={t('leases.reason')} confirmLabel={t('common.confirm')} busy={remove.isPending} error={remove.error} onConfirm={(r) => remove.mutate(r)} onCancel={() => setDeleting(false)} />
            ) : null}
          </>
        ) : null}
      </QueryState>
    </section>
  );
}

function ExtendDialog({ lease, onClose }: { lease: Lease; onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const refresh = useRefresh();
  const [newEnd, setNewEnd] = useState('');
  const [reason, setReason] = useState('');
  const save = useMutation({
    mutationFn: () => api.post(`/leases/${lease.id}/extend`, { newEndOn: newEnd || null, reason, rowVersion: lease.rowVersion }),
    onSuccess: async () => { await refresh(); onClose(); },
  });
  const errors = asError(save.error)?.fieldErrors ?? {};
  return (
    <Dialog title={t('leases.extend')} onClose={onClose}>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }} noValidate>
        <Field label={t('leases.newEndOn')} error={errors.newEndOn?.[0]} required><input type="date" value={newEnd} onChange={(e) => setNewEnd(e.target.value)} autoFocus /></Field>
        <Field label={t('leases.reason')} error={errors.reason?.[0]} required><textarea value={reason} onChange={(e) => setReason(e.target.value)} rows={2} maxLength={500} /></Field>
        <ErrorBanner error={Object.keys(errors).length ? null : save.error} />
        <div className="actions">
          <button type="button" onClick={onClose}>{t('common.cancel')}</button>
          <button type="submit" className="primary" disabled={save.isPending}>{t('leases.extend')}</button>
        </div>
      </form>
    </Dialog>
  );
}

function TerminateDialog({ lease, onClose }: { lease: Lease; onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const refresh = useRefresh();
  const [on, setOn] = useState('');
  const [reason, setReason] = useState('');
  const save = useMutation({
    mutationFn: () => api.post(`/leases/${lease.id}/terminate`, { on: on || null, reason, rowVersion: lease.rowVersion }),
    onSuccess: async () => { await refresh(); onClose(); },
  });
  const errors = asError(save.error)?.fieldErrors ?? {};
  return (
    <Dialog title={t('leases.terminate')} onClose={onClose}>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }} noValidate>
        <Field label={t('leases.endedOn')} hint={t('leases.endedOnHint')} error={errors.on?.[0]}><input type="date" value={on} onChange={(e) => setOn(e.target.value)} autoFocus /></Field>
        <Field label={t('leases.reason')} error={errors.reason?.[0]} required><textarea value={reason} onChange={(e) => setReason(e.target.value)} rows={2} maxLength={500} /></Field>
        <ErrorBanner error={Object.keys(errors).length ? null : save.error} />
        <div className="actions">
          <button type="button" onClick={onClose}>{t('common.cancel')}</button>
          <button type="submit" className="primary" disabled={save.isPending}>{t('leases.terminate')}</button>
        </div>
      </form>
    </Dialog>
  );
}
