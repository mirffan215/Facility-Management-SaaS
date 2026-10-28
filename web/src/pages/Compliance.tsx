import { useMemo, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import { useComplianceSummary, useObligation, useObligations, usePermit, usePermitTypes, usePermits, usePermissions, useUsers } from '../api/hooks';
import type { WorkFromSourceResult, Completion, CompletionResult, ComplianceStatus, Obligation, ObligationCategory, Permit, PermitStatus, PermitType } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Dialog } from '../components/InventoryBits';
import { LocationSelect } from '../components/LocationSelect';
import { Badge, ErrorBanner, Field, Pager, QueryState, ReasonDialog, useEnumLabel } from '../components/ui';
import { downloadBlob } from '../lib/csv';
import { usePrefs } from '../lib/prefs';

const CATEGORIES: ObligationCategory[] = ['Fire', 'Electrical', 'Lifts', 'Pressure', 'Water', 'Environmental', 'HealthAndSafety', 'Other'];
const STATUSES: ComplianceStatus[] = ['Failed', 'Overdue', 'DueSoon', 'Scheduled', 'Compliant'];
const PERMIT_STATUSES: PermitStatus[] = ['Requested', 'Approved', 'Issued', 'Started', 'Closed', 'Rejected', 'Cancelled'];
const RESULTS: CompletionResult[] = ['Passed', 'PassedWithRemarks', 'Failed'];
const asError = (e: unknown) => (e instanceof ApiError ? e : null);
const today = () => new Date().toISOString().slice(0, 10);
const permitNo = (n: number) => `PTW-${String(n).padStart(4, '0')}`;

function statusTone(s: ComplianceStatus): 'ok' | 'warn' | 'bad' | 'neutral' {
  return s === 'Compliant' ? 'ok' : s === 'DueSoon' ? 'warn' : s === 'Overdue' || s === 'Failed' ? 'bad' : 'neutral';
}

function permitTone(s: PermitStatus): 'ok' | 'warn' | 'bad' | 'neutral' {
  return s === 'Issued' || s === 'Started' ? 'ok' : s === 'Requested' || s === 'Approved' ? 'warn' : s === 'Rejected' ? 'bad' : 'neutral';
}

export function CompliancePage() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const [params, setParams] = useSearchParams();
  const tabs = useMemo(
    () => [...(can('compliance.read') ? ['obligations'] : []), ...(can('permits.read') || can('permits.request') ? ['permits'] : []), ...(can('compliance.manage') ? ['types'] : [])],
    [can],
  );
  const tab = tabs.find((x) => x === params.get('tab')) ?? tabs[0] ?? 'obligations';
  return (
    <section>
      <h1>{t('compliance.title')}</h1>
      <p className="muted">{t('compliance.intro')}</p>
      <div className="tabs" role="tablist" aria-label={t('compliance.sections')}>
        {tabs.map((x) => (
          <button key={x} role="tab" type="button" aria-selected={tab === x} className={tab === x ? 'active' : ''} onClick={() => setParams({ tab: x })}>
            {t(`compliance.tab.${x}`)}
          </button>
        ))}
      </div>
      <div role="tabpanel">{tab === 'obligations' ? <ObligationsTab /> : tab === 'permits' ? <PermitsTab /> : <TypesTab />}</div>
    </section>
  );
}

// ---------------------------------------------------------------- obligations

function ObligationsTab() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const { date } = usePrefs();
  const statusLabel = useEnumLabel('complianceStatus');
  const categoryLabel = useEnumLabel('obligationCategory');
  const summary = useComplianceSummary();
  const [params, setParams] = useSearchParams();
  const [status, setStatus] = useState('');
  const [category, setCategory] = useState('');
  const [q, setQ] = useState('');
  const [page, setPage] = useState(1);
  const [editing, setEditing] = useState<Obligation | 'new' | null>(null);
  const openId = params.get('obligation');
  const list = useObligations({ status: status || undefined, category: category || undefined, q: q.trim() || undefined, page, pageSize: 25 });
  const change = (fn: () => void) => { fn(); setPage(1); };

  return (
    <>
      <QueryState query={summary}>
        {summary.data ? (
          <ul className="cards plain" aria-label={t('compliance.summary')}>
            <li className="card"><span className="value">{summary.data.percentCompliant == null ? '—' : `${summary.data.percentCompliant}%`}</span><span className="muted">{t('compliance.percentCompliant')}</span></li>
            <li className="card"><span className="value">{summary.data.overdue}</span><span className="muted">{t('compliance.overdue')}</span></li>
            <li className="card"><span className="value">{summary.data.failed}</span><span className="muted">{t('compliance.failed')}</span></li>
            <li className="card"><span className="value">{summary.data.dueSoon}</span><span className="muted">{t('compliance.dueSoon')}</span></li>
          </ul>
        ) : null}
      </QueryState>
      <div className="toolbar">
        <Field label={t('compliance.status')}>
          <select value={status} onChange={(e) => change(() => setStatus(e.target.value))}>
            <option value="">{t('common.all')}</option>
            {STATUSES.map((s) => <option key={s} value={s}>{statusLabel(s)}</option>)}
          </select>
        </Field>
        <Field label={t('compliance.category')}>
          <select value={category} onChange={(e) => change(() => setCategory(e.target.value))}>
            <option value="">{t('common.all')}</option>
            {CATEGORIES.map((c) => <option key={c} value={c}>{categoryLabel(c)}</option>)}
          </select>
        </Field>
        <Field label={t('common.search')}><input value={q} onChange={(e) => change(() => setQ(e.target.value))} /></Field>
        {can('compliance.manage') ? <button type="button" className="primary" onClick={() => setEditing('new')}>{t('compliance.newObligation')}</button> : null}
      </div>
      <QueryState query={list}>
        {list.data && list.data.items.length === 0 ? <p className="muted">{t('compliance.noObligations')}</p> : null}
        {list.data && list.data.items.length > 0 ? (
          <>
            <div className="table-wrap" tabIndex={0} role="region" aria-label={t('compliance.tab.obligations')}>
              <table>
                <caption className="visually-hidden">{t('compliance.tab.obligations')}</caption>
                <thead>
                  <tr>
                    <th scope="col">{t('compliance.obligation')}</th>
                    <th scope="col">{t('compliance.category')}</th>
                    <th scope="col">{t('compliance.place')}</th>
                    <th scope="col">{t('compliance.dueOn')}</th>
                    <th scope="col">{t('compliance.status')}</th>
                  </tr>
                </thead>
                <tbody>
                  {list.data.items.map((o) => (
                    <tr key={o.id}>
                      <th scope="row"><button type="button" className="link" onClick={() => setParams({ tab: 'obligations', obligation: o.id })}><bdi>{o.title}</bdi></button> {o.isArchived ? <Badge tone="neutral">{t('common.archived')}</Badge> : null}</th>
                      <td>{categoryLabel(o.category)}</td>
                      <td><bdi>{o.locationName ?? o.assetName ?? ''}</bdi></td>
                      <td>{o.dueOn ? date(o.dueOn) : '—'}</td>
                      <td><Badge tone={statusTone(o.status)}>{statusLabel(o.status)}</Badge></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <Pager page={page} pageSize={25} total={list.data.total} onChange={setPage} />
          </>
        ) : null}
      </QueryState>
      {openId ? <ObligationDialog id={openId} onClose={() => setParams({ tab: 'obligations' })} onEdit={(o) => setEditing(o)} /> : null}
      {editing ? <ObligationForm obligation={editing === 'new' ? null : editing} onClose={() => setEditing(null)} /> : null}
    </>
  );
}

function useRefresh() {
  const client = useQueryClient();
  return () => client.invalidateQueries({ queryKey: ['compliance'] });
}

function ObligationForm({ obligation, onClose }: { obligation: Obligation | null; onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { can } = usePermissions();
  const refresh = useRefresh();
  const categoryLabel = useEnumLabel('obligationCategory');
  const users = useUsers();
  const [title, setTitle] = useState(obligation?.title ?? '');
  const [category, setCategory] = useState<ObligationCategory>(obligation?.category ?? 'Fire');
  const [authority, setAuthority] = useState(obligation?.authority ?? '');
  const [locationId, setLocationId] = useState(obligation?.locationId ?? '');
  const [interval, setInterval] = useState(obligation?.intervalMonths?.toString() ?? '12');
  const [lead, setLead] = useState((obligation?.leadDays ?? 30).toString());
  const [firstDue, setFirstDue] = useState(obligation?.firstDueOn ?? '');
  const [ownerId, setOwnerId] = useState(obligation?.ownerId ?? '');
  const [notes, setNotes] = useState(obligation?.notes ?? '');
  const [raiseOnFail, setRaiseOnFail] = useState(obligation?.createWorkOrderOnFail ?? false);

  const save = useMutation({
    mutationFn: () => {
      const body = {
        title, category, authority: authority || null, locationId: locationId || null, intervalMonths: interval.trim() ? Number(interval) : null, leadDays: Number(lead || 30),
        firstDueOn: firstDue || null, ownerId: ownerId || null, notes: notes || null, rowVersion: obligation?.rowVersion ?? null, createWorkOrderOnFail: raiseOnFail,
      };
      return obligation ? api.put<Obligation>(`/compliance/obligations/${obligation.id}`, body) : api.post<Obligation>('/compliance/obligations', body);
    },
    onSuccess: async () => { await refresh(); onClose(); },
  });
  const errors = asError(save.error)?.fieldErrors ?? {};

  return (
    <Dialog title={obligation ? t('compliance.editObligation') : t('compliance.newObligation')} onClose={onClose} wide>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }} noValidate>
        <div className="row">
          <Field label={t('compliance.obligation')} error={errors.title?.[0]} required><input value={title} onChange={(e) => setTitle(e.target.value)} maxLength={200} autoFocus /></Field>
          <Field label={t('compliance.category')} error={errors.category?.[0]}>
            <select value={category} onChange={(e) => setCategory(e.target.value as ObligationCategory)}>{CATEGORIES.map((c) => <option key={c} value={c}>{categoryLabel(c)}</option>)}</select>
          </Field>
          <Field label={t('compliance.authority')} error={errors.authority?.[0]}><input value={authority} onChange={(e) => setAuthority(e.target.value)} maxLength={200} /></Field>
        </div>
        <div className="row">
          <Field label={t('compliance.place')} error={errors.locationId?.[0]}><LocationSelect value={locationId} onChange={setLocationId} /></Field>
          <Field label={t('compliance.intervalMonths')} hint={t('compliance.intervalHint')} error={errors.intervalMonths?.[0]}><input type="number" min={1} max={240} value={interval} onChange={(e) => setInterval(e.target.value)} /></Field>
          <Field label={t('compliance.leadDays')} error={errors.leadDays?.[0]}><input type="number" min={0} max={365} value={lead} onChange={(e) => setLead(e.target.value)} /></Field>
          <Field label={t('compliance.firstDueOn')} hint={t('compliance.firstDueHint')} error={errors.firstDueOn?.[0]}><input type="date" value={firstDue} onChange={(e) => setFirstDue(e.target.value)} /></Field>
        </div>
        {can('users.read') ? (
          <Field label={t('compliance.owner')} hint={t('compliance.ownerHint')} error={errors.ownerId?.[0]}>
            <select value={ownerId} onChange={(e) => setOwnerId(e.target.value)}>
              <option value="">{t('common.notSet')}</option>
              {(users.data ?? []).filter((u) => u.isActive).map((u) => <option key={u.id} value={u.id}>{u.displayName ?? u.email}</option>)}
            </select>
          </Field>
        ) : null}
        <Field label={t('compliance.notes')} error={errors.notes?.[0]}><textarea value={notes} onChange={(e) => setNotes(e.target.value)} rows={2} maxLength={1000} /></Field>
        <label className="check"><input type="checkbox" checked={raiseOnFail} onChange={(e) => setRaiseOnFail(e.target.checked)} /><span>{t('compliance.raiseOnFail')}</span></label>
        {errors.createWorkOrderOnFail ? <p className="error" role="alert">{errors.createWorkOrderOnFail[0]}</p> : null}
        <ErrorBanner error={Object.keys(errors).length ? null : save.error} />
        <div className="actions">
          <button type="button" onClick={onClose}>{t('common.cancel')}</button>
          <button type="submit" className="primary" disabled={save.isPending}>{t('common.save')}</button>
        </div>
      </form>
    </Dialog>
  );
}

function ObligationDialog({ id, onClose, onEdit }: { id: string; onClose: () => void; onEdit: (o: Obligation) => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { can } = usePermissions();
  const { date } = usePrefs();
  const refresh = useRefresh();
  const statusLabel = useEnumLabel('complianceStatus');
  const categoryLabel = useEnumLabel('obligationCategory');
  const resultLabel = useEnumLabel('completionResult');
  const detail = useObligation(id);
  const [recording, setRecording] = useState(false);
  const [archiving, setArchiving] = useState(false);
  const o = detail.data?.obligation;

  const archive = useMutation({
    mutationFn: () => api.post(`/compliance/obligations/${id}/${o?.isArchived ? 'restore' : 'archive'}`, { rowVersion: o?.rowVersion }),
    onSuccess: async () => { setArchiving(false); await refresh(); },
  });

  return (
    <Dialog title={o?.title ?? t('common.loading')} onClose={onClose} wide>
      <QueryState query={detail}>
        {o ? (
          <>
            <p>
              <Badge tone={statusTone(o.status)}>{statusLabel(o.status)}</Badge> {categoryLabel(o.category)}
              {o.locationName ? <> · <bdi>{o.locationName}</bdi></> : null}
              {o.authority ? <> · <bdi>{o.authority}</bdi></> : null}
            </p>
            <dl className="facts">
              <dt>{t('compliance.dueOn')}</dt><dd>{o.dueOn ? date(o.dueOn) : '—'}</dd>
              <dt>{t('compliance.intervalMonths')}</dt><dd>{o.intervalMonths ?? t('compliance.oneOff')}</dd>
              <dt>{t('compliance.owner')}</dt><dd><bdi>{o.ownerName ?? '—'}</bdi></dd>
            </dl>
            {o.notes ? <p><bdi>{o.notes}</bdi></p> : null}
            {can('compliance.manage') ? (
              <div className="actions">
                {!o.isArchived ? <button type="button" className="primary" onClick={() => setRecording(true)}>{t('compliance.record')}</button> : null}
                <button type="button" onClick={() => onEdit(o)}>{t('common.edit')}</button>
                <button type="button" onClick={() => setArchiving(true)}>{o.isArchived ? t('common.restore') : t('common.archive')}</button>
              </div>
            ) : null}
            <h3>{t('compliance.history')}</h3>
            {detail.data!.completions.length === 0 ? <p className="muted">{t('compliance.noHistory')}</p> : (
              <ul className="plain">
                {detail.data!.completions.map((c) => (
                  <li key={c.id}>
                    <Badge tone={c.result === 'Failed' ? 'bad' : c.result === 'Passed' ? 'ok' : 'warn'}>{resultLabel(c.result)}</Badge> {date(c.performedOn)}
                    {c.validUntil ? <> → {date(c.validUntil)}</> : null}
                    {c.reference ? <> · <bdi>{c.reference}</bdi></> : null}
                    {c.providerName ? <> · <bdi>{c.providerName}</bdi></> : null}
                    {c.notes ? <div className="muted"><bdi>{c.notes}</bdi></div> : null}
                    {c.result === 'Failed' ? (c.workOrderId ? <div><Link to={`/work-orders/${c.workOrderId}`}>{t('compliance.workOrder', { number: c.workOrderNumber })}</Link></div> : can('compliance.manage') && can('workorders.read') ? <RaiseFailureWork completionId={c.id} /> : null) : null}
                    <Documents completion={c} canManage={can('compliance.manage')} />
                  </li>
                ))}
              </ul>
            )}
          </>
        ) : null}
      </QueryState>
      <div className="actions"><button type="button" onClick={onClose}>{t('common.back')}</button></div>
      {recording && o ? <RecordForm obligation={o} onClose={() => setRecording(false)} /> : null}
      {archiving && o ? (
        <ReasonDialog title={o.isArchived ? t('common.restore') : t('common.archive')} label={t('common.reasonOptional')} optional confirmLabel={o.isArchived ? t('common.restore') : t('common.archive')} busy={archive.isPending} error={archive.error}
          onConfirm={() => archive.mutate()} onCancel={() => setArchiving(false)} />
      ) : null}
    </Dialog>
  );
}

function RaiseFailureWork({ completionId }: { completionId: string }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const refresh = useRefresh();
  const raise = useMutation({ mutationFn: () => api.post<WorkFromSourceResult>(`/compliance/completions/${completionId}/work-order`, {}), onSuccess: () => refresh() });
  return (
    <div>
      <button type="button" disabled={raise.isPending} onClick={() => raise.mutate()}>{t('compliance.raiseNow')}</button>
      <ErrorBanner error={raise.error} />
    </div>
  );
}

function RecordForm({ obligation, onClose }: { obligation: Obligation; onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const refresh = useRefresh();
  const resultLabel = useEnumLabel('completionResult');
  const [performedOn, setPerformedOn] = useState(today());
  const [result, setResult] = useState<CompletionResult>('Passed');
  const [validUntil, setValidUntil] = useState('');
  const [reference, setReference] = useState('');
  const [provider, setProvider] = useState('');
  const [notes, setNotes] = useState('');
  const save = useMutation({
    mutationFn: () => api.post(`/compliance/obligations/${obligation.id}/completions`, { performedOn, result, validUntil: validUntil || null, reference: reference || null, providerName: provider || null, notes: notes || null }),
    onSuccess: async () => { await refresh(); onClose(); },
  });
  const errors = asError(save.error)?.fieldErrors ?? {};
  return (
    <Dialog title={t('compliance.record')} onClose={onClose}>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }} noValidate>
        <div className="row">
          <Field label={t('compliance.performedOn')} error={errors.performedOn?.[0]} required><input type="date" max={today()} value={performedOn} onChange={(e) => setPerformedOn(e.target.value)} autoFocus /></Field>
          <Field label={t('compliance.result')} error={errors.result?.[0]}>
            <select value={result} onChange={(e) => setResult(e.target.value as CompletionResult)}>{RESULTS.map((r) => <option key={r} value={r}>{resultLabel(r)}</option>)}</select>
          </Field>
        </div>
        {result !== 'Failed' ? (
          <Field label={t('compliance.validUntil')} hint={obligation.intervalMonths ? t('compliance.validUntilHint', { months: obligation.intervalMonths }) : t('compliance.validUntilOneOff')} error={errors.validUntil?.[0]}>
            <input type="date" value={validUntil} onChange={(e) => setValidUntil(e.target.value)} />
          </Field>
        ) : null}
        <div className="row">
          <Field label={t('compliance.reference')} error={errors.reference?.[0]}><input value={reference} onChange={(e) => setReference(e.target.value)} maxLength={100} /></Field>
          <Field label={t('compliance.provider')} error={errors.providerName?.[0]}><input value={provider} onChange={(e) => setProvider(e.target.value)} maxLength={200} /></Field>
        </div>
        <Field label={t('compliance.notes')} hint={result === 'Failed' ? t('compliance.failedNotesHint') : undefined} error={errors.notes?.[0]} required={result === 'Failed'}>
          <textarea value={notes} onChange={(e) => setNotes(e.target.value)} rows={2} maxLength={1000} />
        </Field>
        <ErrorBanner error={Object.keys(errors).length ? null : save.error} />
        <div className="actions">
          <button type="button" onClick={onClose}>{t('common.cancel')}</button>
          <button type="submit" className="primary" disabled={save.isPending}>{t('common.save')}</button>
        </div>
      </form>
    </Dialog>
  );
}

function Documents({ completion, canManage }: { completion: Completion; canManage: boolean }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const refresh = useRefresh();
  const [removing, setRemoving] = useState<string | null>(null);
  const upload = useMutation({
    mutationFn: (file: File) => {
      const form = new FormData();
      form.append('file', file);
      return api.upload(`/compliance/completions/${completion.id}/documents`, form);
    },
    onSuccess: refresh,
  });
  const download = useMutation({
    mutationFn: async (id: string) => {
      const { blob, fileName } = await api.blob(`/compliance/documents/${id}`);
      downloadBlob(fileName ?? 'document', blob);
    },
  });
  const remove = useMutation({
    mutationFn: (reason: string) => api.post(`/compliance/documents/${removing}/delete`, { reason }),
    onSuccess: async () => { setRemoving(null); await refresh(); },
  });
  return (
    <div>
      {completion.documents.map((d) => (
        <span key={d.id} className="chip">
          <button type="button" className="link" onClick={() => download.mutate(d.id)}><bdi>{d.fileName}</bdi></button>
          {canManage ? <button type="button" className="link" aria-label={t('compliance.removeFile', { name: d.fileName })} onClick={() => setRemoving(d.id)}>×</button> : null}{' '}
        </span>
      ))}
      {canManage ? (
        <label className="file">
          <span className="visually-hidden">{t('compliance.attach', { date: completion.performedOn })}</span>
          <input type="file" accept=".pdf,.png,.jpg,.jpeg,.webp,.docx,.xlsx,.txt" disabled={upload.isPending} onChange={(e) => { const f = e.target.files?.[0]; if (f) upload.mutate(f); e.target.value = ''; }} />
        </label>
      ) : null}
      <ErrorBanner error={upload.error ?? download.error} />
      {removing ? <ReasonDialog title={t('compliance.removeFileTitle')} label={t('common.reason')} confirmLabel={t('common.delete')} busy={remove.isPending} error={remove.error} onConfirm={(r) => remove.mutate(r)} onCancel={() => setRemoving(null)} /> : null}
    </div>
  );
}

// ---------------------------------------------------------------- permits

function PermitsTab() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const { date } = usePrefs();
  const statusLabel = useEnumLabel('permitStatus');
  const [params, setParams] = useSearchParams();
  const [status, setStatus] = useState('');
  const [overdue, setOverdue] = useState(false);
  const [mine, setMine] = useState(!can('permits.read'));
  const [q, setQ] = useState('');
  const [page, setPage] = useState(1);
  const [asking, setAsking] = useState(false);
  const list = usePermits({ status: status || undefined, overdue: overdue || undefined, q: q.trim() || undefined, page, pageSize: 25 }, mine);
  const change = (fn: () => void) => { fn(); setPage(1); };
  const openId = params.get('permit');

  return (
    <>
      <div className="toolbar">
        <Field label={t('compliance.status')}>
          <select value={status} onChange={(e) => change(() => setStatus(e.target.value))}>
            <option value="">{t('common.all')}</option>
            {PERMIT_STATUSES.map((s) => <option key={s} value={s}>{statusLabel(s)}</option>)}
          </select>
        </Field>
        <Field label={t('common.search')}><input value={q} onChange={(e) => change(() => setQ(e.target.value))} /></Field>
        {can('permits.read') ? (
          <>
            <label className="check"><input type="checkbox" checked={mine} onChange={(e) => change(() => setMine(e.target.checked))} /><span>{t('compliance.onlyMine')}</span></label>
            <label className="check"><input type="checkbox" checked={overdue} disabled={mine} onChange={(e) => change(() => setOverdue(e.target.checked))} /><span>{t('compliance.onlyOverdue')}</span></label>
          </>
        ) : null}
        {can('permits.request') ? <button type="button" className="primary" onClick={() => setAsking(true)}>{t('compliance.newPermit')}</button> : null}
      </div>
      <QueryState query={list}>
        {list.data && list.data.items.length === 0 ? <p className="muted">{t('compliance.noPermits')}</p> : null}
        {list.data && list.data.items.length > 0 ? (
          <>
            <div className="table-wrap" tabIndex={0} role="region" aria-label={t('compliance.tab.permits')}>
              <table>
                <caption className="visually-hidden">{t('compliance.tab.permits')}</caption>
                <thead>
                  <tr>
                    <th scope="col">{t('compliance.permitNumber')}</th>
                    <th scope="col">{t('compliance.permitType')}</th>
                    <th scope="col">{t('compliance.place')}</th>
                    <th scope="col">{t('compliance.window')}</th>
                    <th scope="col">{t('compliance.status')}</th>
                  </tr>
                </thead>
                <tbody>
                  {list.data.items.map((p) => (
                    <tr key={p.id}>
                      <th scope="row"><button type="button" className="link" onClick={() => setParams({ tab: 'permits', permit: p.id })}>{permitNo(p.number)}</button></th>
                      <td><bdi>{p.typeName}</bdi></td>
                      <td><bdi>{p.locationName}</bdi></td>
                      <td>{date(p.plannedStart, true)} → {date(p.plannedEnd, true)}</td>
                      <td><Badge tone={permitTone(p.status)}>{statusLabel(p.status)}</Badge> {p.isOverdue ? <Badge tone="bad">{t('compliance.overdue')}</Badge> : null}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <Pager page={page} pageSize={25} total={list.data.total} onChange={setPage} />
          </>
        ) : null}
      </QueryState>
      {openId ? <PermitDialog id={openId} onClose={() => setParams({ tab: 'permits' })} /> : null}
      {asking ? <PermitForm onClose={() => setAsking(false)} /> : null}
    </>
  );
}

function PermitForm({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const refresh = useRefresh();
  const types = usePermitTypes();
  const [typeId, setTypeId] = useState('');
  const [locationId, setLocationId] = useState('');
  const [description, setDescription] = useState('');
  const [contractor, setContractor] = useState('');
  const [start, setStart] = useState('');
  const [end, setEnd] = useState('');
  const type = (types.data ?? []).find((x) => x.id === typeId);
  const save = useMutation({
    mutationFn: () => api.post<Permit>('/permits', {
      typeId: typeId || null, locationId: locationId || null, description, contractorName: contractor || null,
      plannedStart: start ? new Date(start).toISOString() : null, plannedEnd: end ? new Date(end).toISOString() : null,
    }),
    onSuccess: async () => { await refresh(); onClose(); },
  });
  const errors = asError(save.error)?.fieldErrors ?? {};
  return (
    <Dialog title={t('compliance.newPermit')} onClose={onClose} wide>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }} noValidate>
        <div className="row">
          <Field label={t('compliance.permitType')} hint={type ? t('compliance.typeSummary', { hours: type.maxDurationHours }) : undefined} error={errors.typeId?.[0]} required>
            <select value={typeId} onChange={(e) => setTypeId(e.target.value)} autoFocus>
              <option value="">{t('common.choose')}</option>
              {(types.data ?? []).map((x) => <option key={x.id} value={x.id}>{x.code} · {x.name}</option>)}
            </select>
          </Field>
          <Field label={t('compliance.place')} error={errors.locationId?.[0]} required><LocationSelect value={locationId} onChange={setLocationId} allowNone={false} /></Field>
        </div>
        <Field label={t('compliance.workDescription')} error={errors.description?.[0]} required><textarea value={description} onChange={(e) => setDescription(e.target.value)} rows={3} maxLength={1000} /></Field>
        <Field label={t('compliance.contractor')} error={errors.contractorName?.[0]}><input value={contractor} onChange={(e) => setContractor(e.target.value)} maxLength={200} /></Field>
        <div className="row">
          <Field label={t('compliance.plannedStart')} error={errors.plannedStart?.[0]} required><input type="datetime-local" value={start} onChange={(e) => setStart(e.target.value)} /></Field>
          <Field label={t('compliance.plannedEnd')} error={errors.plannedEnd?.[0]} required><input type="datetime-local" value={end} onChange={(e) => setEnd(e.target.value)} /></Field>
        </div>
        <ErrorBanner error={Object.keys(errors).length ? null : save.error} />
        <div className="actions">
          <button type="button" onClick={onClose}>{t('common.cancel')}</button>
          <button type="submit" className="primary" disabled={save.isPending}>{t('compliance.requestPermit')}</button>
        </div>
      </form>
    </Dialog>
  );
}

type PermitAction = 'reject' | 'cancel' | 'issue' | 'close';

function PermitDialog({ id, onClose }: { id: string; onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { can } = usePermissions();
  const { date } = usePrefs();
  const refresh = useRefresh();
  const statusLabel = useEnumLabel('permitStatus');
  const detail = usePermit(id);
  const [action, setAction] = useState<PermitAction | null>(null);
  const p = detail.data?.permit;

  const run = useMutation({
    mutationFn: ({ name, body }: { name: string; body?: Record<string, unknown> }) => api.post<Permit>(`/permits/${id}/${name}`, { rowVersion: p?.rowVersion, ...body }),
    onSuccess: async () => { setAction(null); await refresh(); },
  });
  const simple = (name: string) => run.mutate({ name });

  return (
    <Dialog title={p ? `${permitNo(p.number)} · ${p.typeName}` : t('common.loading')} onClose={onClose} wide>
      <QueryState query={detail}>
        {p ? (
          <>
            <p><Badge tone={permitTone(p.status)}>{statusLabel(p.status)}</Badge> {p.isOverdue ? <Badge tone="bad">{t('compliance.overdue')}</Badge> : null} <bdi>{p.locationName}</bdi></p>
            <dl className="facts">
              <dt>{t('compliance.window')}</dt><dd>{date(p.plannedStart, true)} → {date(p.plannedEnd, true)}</dd>
              <dt>{t('compliance.requestedBy')}</dt><dd><bdi>{p.requestedByName}</bdi></dd>
              {p.contractorName ? (<><dt>{t('compliance.contractor')}</dt><dd><bdi>{p.contractorName}</bdi></dd></>) : null}
              {p.workOrderNumber ? (<><dt>{t('compliance.workOrder')}</dt><dd><bdi>{p.workOrderNumber}</bdi></dd></>) : null}
              {p.isolationReference ? (<><dt>{t('compliance.isolation')}</dt><dd><bdi>{p.isolationReference}</bdi></dd></>) : null}
              {p.rejectReason ? (<><dt>{t('compliance.rejectedBecause')}</dt><dd><bdi>{p.rejectReason}</bdi></dd></>) : null}
              {p.cancelReason ? (<><dt>{t('compliance.cancelledBecause')}</dt><dd><bdi>{p.cancelReason}</bdi></dd></>) : null}
            </dl>
            <p><bdi>{p.description}</bdi></p>
            {p.precautions.length > 0 ? (
              <>
                <h3>{t('compliance.precautions')}</h3>
                <ul className="plain">{p.precautions.map((x) => <li key={x.text}>{x.confirmed ? '✔' : '☐'} <bdi>{x.text}</bdi></li>)}</ul>
              </>
            ) : null}
            <div className="actions">
              {p.status === 'Requested' && can('permits.approve') ? (
                <>
                  <button type="button" className="primary" disabled={run.isPending} onClick={() => simple('approve')}>{t('compliance.approve')}</button>
                  <button type="button" onClick={() => setAction('reject')}>{t('compliance.reject')}</button>
                </>
              ) : null}
              {p.status === 'Approved' && can('permits.issue') ? <button type="button" className="primary" onClick={() => setAction('issue')}>{t('compliance.issue')}</button> : null}
              {p.status === 'Issued' && can('permits.work') ? <button type="button" className="primary" disabled={run.isPending} onClick={() => simple('start')}>{t('compliance.start')}</button> : null}
              {(p.status === 'Issued' || p.status === 'Started') && (can('permits.work') || can('permits.issue')) ? <button type="button" onClick={() => setAction('close')}>{t('compliance.close')}</button> : null}
              {(p.status === 'Requested' || p.status === 'Approved' || p.status === 'Issued') && (can('permits.request') || can('permits.approve') || can('permits.issue')) ? <button type="button" onClick={() => setAction('cancel')}>{t('compliance.cancel')}</button> : null}
            </div>
            <ErrorBanner error={run.error} />
            <h3>{t('compliance.trail')}</h3>
            <ol className="plain" aria-label={t('compliance.trail')}>
              {detail.data!.events.map((e, i) => (
                <li key={i}>{date(e.at, true)} · <strong>{t(`compliance.event.${e.action}`, { defaultValue: e.action })}</strong> · <bdi>{e.byName}</bdi>{e.note ? <> — <bdi>{e.note}</bdi></> : null}</li>
              ))}
            </ol>
            {action === 'issue' ? <IssueForm permit={p} busy={run.isPending} error={run.error} onCancel={() => setAction(null)} onIssue={(body) => run.mutate({ name: 'issue', body })} /> : null}
            {action === 'close' ? <CloseForm busy={run.isPending} error={run.error} onCancel={() => setAction(null)} onClose={(body) => run.mutate({ name: 'close', body })} /> : null}
            {action === 'reject' || action === 'cancel' ? (
              <ReasonDialog title={t(action === 'reject' ? 'compliance.reject' : 'compliance.cancel')} label={t('common.reason')} confirmLabel={t(action === 'reject' ? 'compliance.reject' : 'compliance.cancel')} busy={run.isPending} error={run.error}
                onConfirm={(reason) => run.mutate({ name: action, body: { reason } })} onCancel={() => setAction(null)} />
            ) : null}
          </>
        ) : null}
      </QueryState>
      <div className="actions"><button type="button" onClick={onClose}>{t('common.back')}</button></div>
    </Dialog>
  );
}

function IssueForm({ permit, busy, error, onCancel, onIssue }: { permit: Permit; busy: boolean; error: unknown; onCancel: () => void; onIssue: (body: Record<string, unknown>) => void }) {
  const { t } = useTranslation();
  const [confirmed, setConfirmed] = useState<string[]>([]);
  const [isolation, setIsolation] = useState('');
  const errors = asError(error)?.fieldErrors ?? {};
  return (
    <Dialog title={t('compliance.issue')} onClose={onCancel}>
      <form onSubmit={(e) => { e.preventDefault(); onIssue({ confirmedPrecautions: confirmed, isolationReference: isolation || null }); }} noValidate>
        <fieldset>
          <legend>{t('compliance.confirmPrecautions')}</legend>
          {permit.precautions.map((x) => (
            <label key={x.text} className="check">
              <input type="checkbox" checked={confirmed.includes(x.text)} onChange={(e) => setConfirmed((c) => (e.target.checked ? [...c, x.text] : c.filter((y) => y !== x.text)))} />
              <span><bdi>{x.text}</bdi></span>
            </label>
          ))}
          {errors.confirmedPrecautions?.[0] ? <p role="alert" className="error">{errors.confirmedPrecautions[0]}</p> : null}
        </fieldset>
        {permit.requiresIsolation || isolation ? (
          <Field label={t('compliance.isolation')} hint={t('compliance.isolationHint')} error={errors.isolationReference?.[0]} required={permit.requiresIsolation}><input value={isolation} onChange={(e) => setIsolation(e.target.value)} maxLength={200} /></Field>
        ) : null}
        <ErrorBanner error={Object.keys(errors).length ? null : error} />
        <div className="actions">
          <button type="button" onClick={onCancel}>{t('common.cancel')}</button>
          <button type="submit" className="primary" disabled={busy}>{t('compliance.issue')}</button>
        </div>
      </form>
    </Dialog>
  );
}

function CloseForm({ busy, error, onCancel, onClose }: { busy: boolean; error: unknown; onCancel: () => void; onClose: (body: Record<string, unknown>) => void }) {
  const { t } = useTranslation();
  const [safe, setSafe] = useState(false);
  const [note, setNote] = useState('');
  return (
    <Dialog title={t('compliance.close')} onClose={onCancel}>
      <form onSubmit={(e) => { e.preventDefault(); onClose({ areaSafe: safe, note: note || null }); }} noValidate>
        <label className="check"><input type="checkbox" checked={safe} onChange={(e) => setSafe(e.target.checked)} /><span>{t('compliance.areaSafe')}</span></label>
        <Field label={t('compliance.closureNote')}><textarea value={note} onChange={(e) => setNote(e.target.value)} rows={3} maxLength={1000} /></Field>
        <ErrorBanner error={error} />
        <div className="actions">
          <button type="button" onClick={onCancel}>{t('common.cancel')}</button>
          <button type="submit" className="primary" disabled={busy || !safe}>{t('compliance.close')}</button>
        </div>
      </form>
    </Dialog>
  );
}

// ---------------------------------------------------------------- permit types

function TypesTab() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const refresh = useRefresh();
  const types = usePermitTypes(true);
  const [editing, setEditing] = useState<PermitType | 'new' | null>(null);
  const archive = useMutation({
    mutationFn: (x: PermitType) => api.post(`/compliance/permit-types/${x.id}/${x.isArchived ? 'restore' : 'archive'}`, { rowVersion: x.rowVersion }),
    onSuccess: refresh,
  });
  return (
    <>
      <p className="muted">{t('compliance.typesIntro')}</p>
      <div className="toolbar"><button type="button" className="primary" onClick={() => setEditing('new')}>{t('compliance.newType')}</button></div>
      <QueryState query={types}>
        {types.data && types.data.length === 0 ? <p className="muted">{t('compliance.noTypes')}</p> : null}
        <ul className="plain">
          {(types.data ?? []).map((x) => (
            <li key={x.id}>
              <strong><bdi>{x.code}</bdi></strong> · <bdi>{x.name}</bdi> <span className="muted">· {t('compliance.typeSummary', { hours: x.maxDurationHours })}</span>{' '}
              {x.exclusive ? <Badge tone="warn">{t('compliance.exclusive')}</Badge> : null} {x.requiresIsolation ? <Badge tone="warn">{t('compliance.requiresIsolation')}</Badge> : null} {x.isArchived ? <Badge tone="neutral">{t('common.archived')}</Badge> : null}{' '}
              <button type="button" onClick={() => setEditing(x)}>{t('common.edit')}</button>{' '}
              <button type="button" disabled={archive.isPending} onClick={() => archive.mutate(x)}>{x.isArchived ? t('common.restore') : t('common.archive')}</button>
            </li>
          ))}
        </ul>
        <ErrorBanner error={archive.error} />
      </QueryState>
      {editing ? <TypeForm type={editing === 'new' ? null : editing} onClose={() => setEditing(null)} /> : null}
    </>
  );
}

function TypeForm({ type, onClose }: { type: PermitType | null; onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const refresh = useRefresh();
  const [code, setCode] = useState(type?.code ?? '');
  const [name, setName] = useState(type?.name ?? '');
  const [nameAr, setNameAr] = useState(type?.nameAr ?? '');
  const [hours, setHours] = useState((type?.maxDurationHours ?? 8).toString());
  const [exclusive, setExclusive] = useState(type?.exclusive ?? false);
  const [isolation, setIsolation] = useState(type?.requiresIsolation ?? false);
  const [precautions, setPrecautions] = useState((type?.precautions ?? []).join('\n'));
  const save = useMutation({
    mutationFn: () => {
      const body = { code, name, nameAr: nameAr || null, maxDurationHours: Number(hours), exclusive, requiresIsolation: isolation, precautions: precautions.split('\n').map((x) => x.trim()).filter(Boolean), rowVersion: type?.rowVersion ?? null };
      return type ? api.put(`/compliance/permit-types/${type.id}`, body) : api.post('/compliance/permit-types', body);
    },
    onSuccess: async () => { await refresh(); onClose(); },
  });
  const errors = asError(save.error)?.fieldErrors ?? {};
  return (
    <Dialog title={type ? t('compliance.editType') : t('compliance.newType')} onClose={onClose} wide>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }} noValidate>
        <div className="row">
          <Field label={t('compliance.typeCode')} error={errors.code?.[0]} required><input value={code} onChange={(e) => setCode(e.target.value)} maxLength={30} autoFocus /></Field>
          <Field label={t('compliance.typeName')} error={errors.name?.[0]} required><input value={name} onChange={(e) => setName(e.target.value)} maxLength={200} /></Field>
          <Field label={t('compliance.typeNameAr')} error={errors.nameAr?.[0]}><input value={nameAr} onChange={(e) => setNameAr(e.target.value)} maxLength={200} lang="ar" dir="rtl" /></Field>
          <Field label={t('compliance.maxHours')} error={errors.maxDurationHours?.[0]}><input type="number" min={1} max={720} value={hours} onChange={(e) => setHours(e.target.value)} /></Field>
        </div>
        <label className="check"><input type="checkbox" checked={exclusive} onChange={(e) => setExclusive(e.target.checked)} /><span>{t('compliance.exclusive')} <small className="muted">{t('compliance.exclusiveHint')}</small></span></label>
        <label className="check"><input type="checkbox" checked={isolation} onChange={(e) => setIsolation(e.target.checked)} /><span>{t('compliance.requiresIsolation')} <small className="muted">{t('compliance.isolationRequiredHint')}</small></span></label>
        <Field label={t('compliance.precautions')} hint={t('compliance.precautionsHint')} error={errors.precautions?.[0]}><textarea value={precautions} onChange={(e) => setPrecautions(e.target.value)} rows={5} /></Field>
        <ErrorBanner error={Object.keys(errors).length ? null : save.error} />
        <div className="actions">
          <button type="button" onClick={onClose}>{t('common.cancel')}</button>
          <button type="submit" className="primary" disabled={save.isPending}>{t('common.save')}</button>
        </div>
      </form>
    </Dialog>
  );
}
