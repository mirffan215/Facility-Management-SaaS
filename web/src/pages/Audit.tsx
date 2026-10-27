import { useState } from 'react';
import { useMutation } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { useAudit, useAuditActions } from '../api/hooks';
import { useAuth } from '../auth/AuthContext';
import { ErrorBanner, Field, Pager, QueryState } from '../components/ui';
import { downloadBlob } from '../lib/csv';
import { usePrefs } from '../lib/prefs';

const PAGE_SIZE = 25;
const day = (offset: number) => new Date(Date.now() + offset * 86_400_000).toISOString().slice(0, 10);

/** The audit trail: search and export only. Nothing on this page can change what it shows. */
export function AuditPage() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { date } = usePrefs();
  const [from, setFrom] = useState(day(-7));
  const [to, setTo] = useState(day(0));
  const [action, setAction] = useState('');
  const [entityType, setEntityType] = useState('');
  const [entityId, setEntityId] = useState('');
  const [page, setPage] = useState(1);

  const filters = { from: from || undefined, to: to || undefined, action: action || undefined, entityType: entityType.trim() || undefined, entityId: entityId.trim() || undefined };
  const entries = useAudit({ ...filters, page, pageSize: PAGE_SIZE });
  const actions = useAuditActions({ from: filters.from, to: filters.to });
  const change = (fn: () => void) => { fn(); setPage(1); };

  const exportCsv = useMutation({
    mutationFn: async () => {
      const query = Object.entries(filters).filter(([, v]) => v).map(([k, v]) => `${k}=${encodeURIComponent(v as string)}`).join('&');
      const { blob, fileName } = await api.blob(`/audit/export.csv?${query}`);
      downloadBlob(fileName ?? 'audit.csv', blob);
    },
  });

  return (
    <section>
      <h1>{t('audit.title')}</h1>
      <p className="muted">{t('audit.intro')}</p>
      <form className="inline-form" onSubmit={(e) => e.preventDefault()} aria-label={t('audit.filters')}>
        <div className="row">
          <Field label={t('reports.from')}><input type="date" value={from} onChange={(e) => change(() => setFrom(e.target.value))} /></Field>
          <Field label={t('reports.to')}><input type="date" value={to} onChange={(e) => change(() => setTo(e.target.value))} /></Field>
          <Field label={t('audit.action')}>
            <select value={action} onChange={(e) => change(() => setAction(e.target.value))}>
              <option value="">{t('common.all')}</option>
              {(actions.data ?? []).map((a) => <option key={a} value={a}>{a}</option>)}
            </select>
          </Field>
          <Field label={t('audit.entityType')}><input value={entityType} onChange={(e) => change(() => setEntityType(e.target.value))} maxLength={100} /></Field>
          <Field label={t('audit.entityId')}><input value={entityId} onChange={(e) => change(() => setEntityId(e.target.value))} maxLength={100} /></Field>
        </div>
        <button type="button" disabled={exportCsv.isPending} onClick={() => exportCsv.mutate()}>{t('audit.export')}</button>
      </form>
      <ErrorBanner error={exportCsv.error} />
      <QueryState query={entries}>
        {entries.data ? (
          entries.data.items.length === 0 ? <p className="muted">{t('common.noResults')}</p> : (
            <>
              <p className="muted" role="status">{t('common.results', { count: entries.data.total })}</p>
              <div className="table-wrap" tabIndex={0} role="region" aria-label={t('audit.title')}>
                <table>
                  <caption className="visually-hidden">{t('audit.title')}</caption>
                  <thead>
                    <tr>
                      <th scope="col">{t('audit.when')}</th>
                      <th scope="col">{t('audit.who')}</th>
                      <th scope="col">{t('audit.action')}</th>
                      <th scope="col">{t('audit.entity')}</th>
                      <th scope="col">{t('audit.summary')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {entries.data.items.map((e) => (
                      <tr key={e.id}>
                        <td>{date(e.occurredAt, true)}</td>
                        <td><bdi>{e.actorName ?? t('audit.system')}</bdi></td>
                        <td><code>{e.action}</code></td>
                        <td><bdi>{e.entityType}{e.entityId ? ` · ${e.entityId}` : ''}</bdi></td>
                        <td><bdi>{e.summary}</bdi></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              <Pager page={page} pageSize={PAGE_SIZE} total={entries.data.total} onChange={setPage} />
            </>
          )
        ) : null}
      </QueryState>
    </section>
  );
}
