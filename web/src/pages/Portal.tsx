import { useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import type { PortalContract, PortalMe, PortalOrder, PortalWork } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Badge, ErrorBanner, Field, QueryState, useEnumLabel } from '../components/ui';
import { downloadBlob } from '../lib/csv';
import { formatBytes } from '../lib/format';
import { usePrefs } from '../lib/prefs';

const TABS = ['orders', 'work', 'contracts'] as const;

/** The whole of what a vendor's own people see: their orders, the jobs given to them and their contracts. */
export function PortalPage() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { name } = usePrefs();
  const [params, setParams] = useSearchParams();
  const tab = TABS.find((x) => x === params.get('tab')) ?? 'orders';
  const me = useQuery({ queryKey: ['portal', 'me'], queryFn: () => api.get<PortalMe>('/portal/me') });
  return (
    <section>
      <h1>{t('portal.title')}</h1>
      <QueryState query={me}>
        {me.data ? <p className="muted">{t('portal.intro', { vendor: name({ name: me.data.vendorName, nameAr: me.data.vendorNameAr }) })}</p> : null}
      </QueryState>
      <div className="tabs" role="tablist" aria-label={t('portal.sections')}>
        {TABS.map((x) => (
          <button key={x} role="tab" type="button" aria-selected={tab === x} className={tab === x ? 'active' : ''} onClick={() => setParams({ tab: x })}>
            {t(`portal.tab.${x}`)}
          </button>
        ))}
      </div>
      <div role="tabpanel">{tab === 'orders' ? <Orders /> : tab === 'work' ? <Work /> : <Contracts />}</div>
    </section>
  );
}

// ---------------- orders ----------------

function Orders() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { date, num } = usePrefs();
  const client = useQueryClient();
  const statusLabel = useEnumLabel('orderStatus');
  const [openId, setOpenId] = useState<string | null>(null);
  const list = useQuery({ queryKey: ['portal', 'orders'], queryFn: () => api.get<PortalOrder[]>('/portal/orders') });
  const detail = useQuery({ queryKey: ['portal', 'orders', openId], queryFn: () => api.get<PortalOrder>(`/portal/orders/${openId}`), enabled: !!openId });
  const acknowledge = useMutation({
    mutationFn: (id: string) => api.post(`/portal/orders/${id}/acknowledge`),
    onSuccess: () => client.invalidateQueries({ queryKey: ['portal', 'orders'] }),
  });
  return (
    <>
      <ErrorBanner error={acknowledge.error} />
      <QueryState query={list}>
        {list.data && list.data.length === 0 ? <p className="muted">{t('portal.noOrders')}</p> : null}
        {list.data && list.data.length > 0 ? (
          <div className="table-wrap">
            <table>
              <caption className="visually-hidden">{t('portal.tab.orders')}</caption>
              <thead>
                <tr>
                  <th scope="col">{t('purchasing.order')}</th>
                  <th scope="col">{t('assets.status')}</th>
                  <th scope="col">{t('purchasing.expectedOn')}</th>
                  <th scope="col">{t('purchasing.total')}</th>
                  <th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th>
                </tr>
              </thead>
              <tbody>
                {list.data.map((o) => (
                  <tr key={o.id}>
                    <td><bdi>{o.display}</bdi><div className="muted">{date(o.issuedAt)}</div></td>
                    <td><Badge tone={o.status === 'Cancelled' ? 'bad' : o.status === 'Received' ? 'ok' : 'neutral'}>{statusLabel(o.status)}</Badge></td>
                    <td>{o.expectedOn ? date(o.expectedOn) : '—'}</td>
                    <td><bdi>{num(o.total)} {o.currencyCode}</bdi></td>
                    <td className="actions">
                      <button type="button" onClick={() => setOpenId(openId === o.id ? null : o.id)}>{openId === o.id ? t('common.collapse') : t('common.expand')}</button>
                      {o.status === 'Issued' ? <button type="button" disabled={acknowledge.isPending} onClick={() => acknowledge.mutate(o.id)}>{t('portal.acknowledge')}</button> : null}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : null}
      </QueryState>
      {openId && detail.data ? (
        <div className="panel">
          <h2>{detail.data.display}</h2>
          {detail.data.notes ? <p><bdi>{detail.data.notes}</bdi></p> : null}
          {detail.data.cancelReason ? <p className="banner error"><bdi>{detail.data.cancelReason}</bdi></p> : null}
          <div className="table-wrap">
            <table>
              <caption className="visually-hidden">{t('purchasing.lines')}</caption>
              <thead>
                <tr>
                  <th scope="col">{t('purchasing.description')}</th>
                  <th scope="col">{t('portal.quantity')}</th>
                  <th scope="col">{t('purchasing.unitPrice')}</th>
                  <th scope="col">{t('purchasing.received')}</th>
                </tr>
              </thead>
              <tbody>
                {(detail.data.lines ?? []).map((l) => (
                  <tr key={l.position}>
                    <td><bdi>{l.description}</bdi></td>
                    <td>{num(l.quantity)} <bdi>{l.unit}</bdi></td>
                    <td>{num(l.unitPrice)}</td>
                    <td>{num(l.receivedQty)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      ) : null}
    </>
  );
}

// ---------------- work ----------------

function Work() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { date } = usePrefs();
  const client = useQueryClient();
  const statusLabel = useEnumLabel('workStatus');
  const priorityLabel = useEnumLabel('workPriority');
  const [openId, setOpenId] = useState<string | null>(null);
  const [body, setBody] = useState('');
  const list = useQuery({ queryKey: ['portal', 'work'], queryFn: () => api.get<PortalWork[]>('/portal/work-orders') });
  const detail = useQuery({ queryKey: ['portal', 'work', openId], queryFn: () => api.get<PortalWork>(`/portal/work-orders/${openId}`), enabled: !!openId });
  const comment = useMutation({
    mutationFn: () => api.post(`/portal/work-orders/${openId}/comments`, { body }),
    onSuccess: async () => {
      setBody('');
      await client.invalidateQueries({ queryKey: ['portal', 'work', openId] });
    },
  });
  const fieldError = comment.error instanceof ApiError ? comment.error.field('body') : undefined;
  return (
    <>
      <QueryState query={list}>
        {list.data && list.data.length === 0 ? <p className="muted">{t('portal.noWork')}</p> : null}
        {list.data && list.data.length > 0 ? (
          <div className="table-wrap">
            <table>
              <caption className="visually-hidden">{t('portal.tab.work')}</caption>
              <thead>
                <tr>
                  <th scope="col">{t('work.number')}</th>
                  <th scope="col">{t('work.titleField')}</th>
                  <th scope="col">{t('work.priority')}</th>
                  <th scope="col">{t('work.status')}</th>
                  <th scope="col">{t('work.due')}</th>
                  <th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th>
                </tr>
              </thead>
              <tbody>
                {list.data.map((w) => (
                  <tr key={w.id}>
                    <td><bdi>{w.display}</bdi></td>
                    <td><bdi>{w.title}</bdi>{w.locationName ? <div className="muted"><bdi>{w.locationName}</bdi></div> : null}</td>
                    <td>{priorityLabel(w.priority)}</td>
                    <td>{statusLabel(w.status)}</td>
                    <td>{w.dueAt ? date(w.dueAt, true) : '—'}</td>
                    <td className="actions"><button type="button" onClick={() => setOpenId(openId === w.id ? null : w.id)}>{openId === w.id ? t('common.collapse') : t('common.expand')}</button></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : null}
      </QueryState>
      {openId && detail.data ? (
        <div className="panel">
          <h2><bdi>{detail.data.display}</bdi> <bdi>{detail.data.title}</bdi></h2>
          {detail.data.description ? <p><bdi>{detail.data.description}</bdi></p> : null}
          {detail.data.assetName ? <p>{t('portal.asset')}: <bdi>{detail.data.assetName}</bdi></p> : null}
          {detail.data.contractNumber ? (
            <p>
              {t('portal.underContract', { number: detail.data.contractNumber })}
              {detail.data.responseMinutes !== null && detail.data.resolutionMinutes !== null
                ? ` ${t('vendors.contractTargets', { response: detail.data.responseMinutes, resolution: detail.data.resolutionMinutes })}`
                : ''}
            </p>
          ) : null}
          <h3>{t('portal.messages')}</h3>
          {(detail.data.comments ?? []).length === 0 ? <p className="muted">{t('portal.noMessages')}</p> : (
            <ul className="plain">
              {(detail.data.comments ?? []).map((c) => (
                <li key={c.id}><strong><bdi>{c.mine ? t('portal.you') : (c.authorName ?? '—')}</bdi></strong> <span className="muted">{date(c.at, true)}</span><div><bdi>{c.body}</bdi></div></li>
              ))}
            </ul>
          )}
          <ErrorBanner error={fieldError ? null : comment.error} />
          <Field label={t('portal.writeMessage')} error={fieldError}>
            <textarea value={body} onChange={(e) => setBody(e.target.value)} rows={3} />
          </Field>
          <button type="button" disabled={comment.isPending || body.trim() === ''} onClick={() => comment.mutate()}>{t('portal.send')}</button>
        </div>
      ) : null}
    </>
  );
}

// ---------------- contracts ----------------

function Contracts() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { date, num, lang } = usePrefs();
  const typeLabel = useEnumLabel('contractType');
  const statusLabel = useEnumLabel('contractStatus');
  const [openId, setOpenId] = useState<string | null>(null);
  const list = useQuery({ queryKey: ['portal', 'contracts'], queryFn: () => api.get<PortalContract[]>('/portal/contracts') });
  const docs = useQuery({
    queryKey: ['portal', 'contracts', openId, 'documents'],
    queryFn: () => api.get<{ id: string; fileName: string; sizeBytes: number; description: string | null }[]>(`/portal/contracts/${openId}/documents`),
    enabled: !!openId,
  });
  const download = useMutation({
    mutationFn: async (id: string) => {
      const { blob, fileName } = await api.blob(`/portal/contract-documents/${id}/download`);
      downloadBlob(fileName ?? 'document', blob);
    },
  });
  return (
    <>
      <ErrorBanner error={download.error} />
      <QueryState query={list}>
        {list.data && list.data.length === 0 ? <p className="muted">{t('portal.noContracts')}</p> : null}
        {list.data && list.data.length > 0 ? (
          <div className="table-wrap">
            <table>
              <caption className="visually-hidden">{t('portal.tab.contracts')}</caption>
              <thead>
                <tr>
                  <th scope="col">{t('vendors.contract')}</th>
                  <th scope="col">{t('assets.status')}</th>
                  <th scope="col">{t('vendors.period')}</th>
                  <th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th>
                </tr>
              </thead>
              <tbody>
                {list.data.map((c) => (
                  <tr key={c.id}>
                    <td><bdi>{c.number}</bdi> <bdi>{c.title}</bdi><div className="muted">{typeLabel(c.type)}</div></td>
                    <td><Badge tone={c.status === 'Active' ? 'ok' : c.status === 'Expiring' ? 'warn' : c.status === 'Expired' ? 'bad' : 'neutral'}>{statusLabel(c.status)}</Badge></td>
                    <td>{date(c.startOn)} – {date(c.endOn)}{c.value !== null ? <div className="muted"><bdi>{num(c.value)} {c.currencyCode}</bdi></div> : null}</td>
                    <td className="actions"><button type="button" onClick={() => setOpenId(openId === c.id ? null : c.id)}>{openId === c.id ? t('common.collapse') : t('portal.files', { count: c.documents })}</button></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : null}
      </QueryState>
      {openId && docs.data ? (
        <ul className="plain">
          {docs.data.map((d) => (
            <li key={d.id}>
              <button type="button" onClick={() => download.mutate(d.id)}><bdi>{d.fileName}</bdi></button> <span className="muted">{formatBytes(d.sizeBytes, lang)}</span>
            </li>
          ))}
        </ul>
      ) : null}
    </>
  );
}
