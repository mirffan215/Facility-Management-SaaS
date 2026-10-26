import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import { useErpItems, useErpSettings, useErpSummary, usePermissions } from '../api/hooks';
import type { ErpSettings } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Badge, ErrorBanner, Field, Loading, Pager, QueryState, useEnumLabel } from '../components/ui';
import { usePrefs } from '../lib/prefs';

/** The connection to the accounting system: whether it is on, what has been sent, what is stuck, and whether the two systems still agree. */
export function IntegrationsPage() {
  const { t } = useTranslation();
  return (
    <section>
      <h1>{t('integration.title')}</h1>
      <p className="muted">{t('integration.intro')}</p>
      <ConnectionSection />
      <ActivitySection />
    </section>
  );
}

function useRefresh() {
  const client = useQueryClient();
  return () => client.invalidateQueries({ queryKey: ['integration'] });
}

function ConnectionSection() {
  const settings = useErpSettings();
  if (settings.isLoading || !settings.data) return <Loading />;
  return <ConnectionForm settings={settings.data} />;
}

function ConnectionForm({ settings }: { settings: ErpSettings }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { can } = usePermissions();
  const refresh = useRefresh();
  const [enabled, setEnabled] = useState(settings.enabled);
  const [adapter, setAdapter] = useState(settings.adapter || settings.availableAdapters[0] || '');
  const [company, setCompany] = useState(settings.companyId);
  const manage = can('integration.manage');
  const save = useMutation({
    mutationFn: () => api.put('/integration/erp/settings', { enabled, adapter, companyId: company.trim(), rowVersion: settings.rowVersion }),
    onSuccess: () => refresh(),
  });
  const error = save.error instanceof ApiError ? save.error : null;
  return (
    <fieldset>
      <legend>{t('integration.connection')}</legend>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }}>
        <label className="check"><input type="checkbox" checked={enabled} disabled={!manage} onChange={(e) => setEnabled(e.target.checked)} /> {t('integration.sendToErp')}</label>
        <div className="row">
          <Field label={t('integration.adapter')} hint={t('integration.adapterHint')} error={error?.field('adapter')}>
            <select value={adapter} disabled={!manage} onChange={(e) => setAdapter(e.target.value)}>
              {settings.availableAdapters.length === 0 ? <option value="">{t('integration.noAdapters')}</option> : null}
              {settings.availableAdapters.map((a) => <option key={a} value={a}>{a}</option>)}
            </select>
          </Field>
          <Field label={t('integration.company')} hint={t('integration.companyHint')} error={error?.field('companyId')}><input value={company} readOnly={!manage} onChange={(e) => setCompany(e.target.value)} maxLength={50} /></Field>
        </div>
        <ErrorBanner error={error?.status === 400 ? null : save.error} />
        {manage ? <div className="actions"><button type="submit" className="primary" disabled={save.isPending}>{t('common.save')}</button></div> : <p className="muted">{t('integration.readOnly')}</p>}
      </form>
    </fieldset>
  );
}

function ActivitySection() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { can } = usePermissions();
  const { date, num } = usePrefs();
  const refresh = useRefresh();
  const statusLabel = useEnumLabel('exportStatus');
  const entityLabel = useEnumLabel('erpEntity');
  const reconcileLabel = useEnumLabel('reconcileState');
  const summary = useErpSummary();
  const [status, setStatus] = useState('');
  const [problems, setProblems] = useState(false);
  const [page, setPage] = useState(1);
  const items = useErpItems({ status: status || undefined, problemsOnly: problems || undefined, page, pageSize: 25 });
  const retry = useMutation({ mutationFn: (id: string) => api.post(`/integration/erp/items/${id}/retry`, {}), onSuccess: () => refresh() });
  const reconcile = useMutation({
    mutationFn: () => api.post<{ checked: number; matched: number; mismatched: number; missing: number }>('/integration/erp/reconcile', {}),
    onSuccess: () => refresh(),
  });
  const s = summary.data;
  const tone = (st: string): 'ok' | 'warn' | 'bad' | 'neutral' => (st === 'Succeeded' ? 'ok' : st === 'DeadLetter' ? 'bad' : st === 'Pending' || st === 'Sending' ? 'warn' : 'neutral');
  return (
    <>
      <h2>{t('integration.activity')}</h2>
      {s ? (
        <dl className="tiles">
          <div><dt>{t('integration.waiting')}</dt><dd>{num(s.pending + s.sending)}</dd></div>
          <div><dt>{t('integration.needsAttention')}</dt><dd>{num(s.deadLetter)}</dd></div>
          <div><dt>{t('integration.disagreements')}</dt><dd>{num(s.mismatched)}</dd></div>
          <div><dt>{t('integration.sent')}</dt><dd>{num(s.succeeded)}</dd></div>
        </dl>
      ) : null}
      <div className="filters">
        <Field label={t('assets.status')}>
          <select value={status} onChange={(e) => { setStatus(e.target.value); setPage(1); }}>
            <option value="">{t('common.all')}</option>
            {(['Pending', 'Sending', 'Succeeded', 'DeadLetter', 'Superseded'] as const).map((x) => <option key={x} value={x}>{statusLabel(x)}</option>)}
          </select>
        </Field>
        <label className="check"><input type="checkbox" checked={problems} onChange={(e) => { setProblems(e.target.checked); setPage(1); }} /> {t('integration.problemsOnly')}</label>
        {can('integration.manage') ? <button type="button" disabled={reconcile.isPending} onClick={() => reconcile.mutate()}>{t('integration.reconcile')}</button> : null}
      </div>
      {reconcile.data ? <p role="status">{t('integration.reconciled', { checked: reconcile.data.checked, matched: reconcile.data.matched, mismatched: reconcile.data.mismatched, missing: reconcile.data.missing })}</p> : null}
      <ErrorBanner error={reconcile.error ?? retry.error} />
      <QueryState query={items}>
        {items.data && items.data.items.length === 0 ? <p className="muted">{problems ? t('integration.noProblems') : t('integration.nothingSent')}</p> : null}
        {items.data && items.data.items.length > 0 ? (
          <div className="table-wrap">
            <table>
              <caption className="visually-hidden">{t('integration.activity')}</caption>
              <thead>
                <tr>
                  <th scope="col">{t('integration.what')}</th>
                  <th scope="col">{t('assets.status')}</th>
                  <th scope="col">{t('integration.erpReference')}</th>
                  <th scope="col">{t('integration.check')}</th>
                  <th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th>
                </tr>
              </thead>
              <tbody>
                {items.data.items.map((i) => (
                  <tr key={i.id}>
                    <td>{entityLabel(i.entity)}: <bdi>{i.label ?? i.entityId}</bdi><div className="muted">{date(i.createdAt, true)}</div></td>
                    <td>
                      <Badge tone={tone(i.status)}>{statusLabel(i.status)}</Badge>
                      {i.attempts > 0 && i.status !== 'Succeeded' ? <div className="muted">{t('integration.attempts', { count: i.attempts })}</div> : null}
                      {i.status === 'Pending' && i.attempts > 0 ? <div className="muted">{t('integration.nextTry', { when: date(i.nextAttemptAt, true) })}</div> : null}
                      {i.lastError ? <div className="error"><bdi>{i.lastError}</bdi></div> : null}
                    </td>
                    <td>{i.erpReference ? <bdi>{i.erpReference}</bdi> : '—'}</td>
                    <td>{i.reconcile === 'NotChecked' ? '—' : <Badge tone={i.reconcile === 'Matched' ? 'ok' : 'bad'}>{reconcileLabel(i.reconcile)}</Badge>}{i.reconcileDetail ? <div className="muted"><bdi>{i.reconcileDetail}</bdi></div> : null}</td>
                    <td className="actions">
                      {can('integration.manage') && (i.status === 'DeadLetter' || (i.status === 'Pending' && i.attempts > 0)) ? <button type="button" disabled={retry.isPending} onClick={() => retry.mutate(i.id)}>{t('integration.retry')}</button> : null}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            <Pager page={items.data.pageNumber} pageSize={items.data.pageSize} total={items.data.total} onChange={setPage} />
          </div>
        ) : null}
      </QueryState>
    </>
  );
}
