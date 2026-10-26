import { useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import { useOrder, useOrders, usePermissions, usePurchasingSettings, useRequest, useRequests, useVendors } from '../api/hooks';
import type { OrderStatus, PurchaseRequest, RequestStatus } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Dialog, ItemPicker, StoreSelect } from '../components/InventoryBits';
import { Badge, ErrorBanner, Field, Loading, Pager, QueryState, ReasonDialog, useEnumLabel } from '../components/ui';
import { usePrefs } from '../lib/prefs';

const asError = (e: unknown) => (e instanceof ApiError ? e : null);

export function PurchasingPage() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const [params, setParams] = useSearchParams();
  const tabs = ['requests', ...(can('purchasing.read') ? ['orders'] : []), ...(can('purchasing.manage') ? ['settings'] : [])];
  const tab = tabs.find((x) => x === params.get('tab')) ?? 'requests';
  return (
    <section>
      <h1>{t('purchasing.title')}</h1>
      <p className="muted">{t('purchasing.intro')}</p>
      <div className="tabs" role="tablist" aria-label={t('purchasing.sections')}>
        {tabs.map((x) => (
          <button key={x} role="tab" type="button" aria-selected={tab === x} className={tab === x ? 'active' : ''} onClick={() => setParams({ tab: x })}>
            {t(`purchasing.tab.${x}`)}
          </button>
        ))}
      </div>
      <div role="tabpanel">{tab === 'requests' ? <RequestsTab initialOpen={params.get('request')} /> : tab === 'orders' ? <OrdersTab /> : <SettingsTab />}</div>
    </section>
  );
}

function useRefresh() {
  const client = useQueryClient();
  return () => client.invalidateQueries({ queryKey: ['purchasing'] });
}

const requestTone = (s: RequestStatus): 'ok' | 'warn' | 'bad' | 'neutral' => (s === 'Approved' || s === 'Ordered' ? 'ok' : s === 'Submitted' ? 'warn' : s === 'Rejected' ? 'bad' : 'neutral');
const orderTone = (s: OrderStatus): 'ok' | 'warn' | 'bad' | 'neutral' => (s === 'Received' ? 'ok' : s === 'Cancelled' ? 'neutral' : s === 'PartiallyReceived' ? 'warn' : 'neutral');

// ---------------- requests ----------------

function RequestsTab({ initialOpen }: { initialOpen: string | null }) {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const { date, num } = usePrefs();
  const statusLabel = useEnumLabel('requestStatus');
  const [status, setStatus] = useState('');
  const [waiting, setWaiting] = useState(false);
  const [page, setPage] = useState(1);
  const [open, setOpen] = useState<string | 'new' | null>(initialOpen);
  const requests = useRequests({ status: status || undefined, awaitingMe: waiting || undefined, page, pageSize: 25 });
  return (
    <>
      <div className="filters">
        <Field label={t('assets.status')}>
          <select value={status} onChange={(e) => { setStatus(e.target.value); setPage(1); }}>
            <option value="">{t('common.all')}</option>
            {(['Draft', 'Submitted', 'Approved', 'Rejected', 'Cancelled', 'Ordered'] as const).map((s) => <option key={s} value={s}>{statusLabel(s)}</option>)}
          </select>
        </Field>
        <label className="check"><input type="checkbox" checked={waiting} onChange={(e) => { setWaiting(e.target.checked); setPage(1); }} /> {t('purchasing.waitingForMe')}</label>
        {can('purchasing.request') ? <button type="button" onClick={() => setOpen('new')}>{t('purchasing.newRequest')}</button> : null}
      </div>
      <QueryState query={requests}>
        {requests.data && requests.data.items.length === 0 ? <p className="muted">{waiting ? t('purchasing.nothingWaiting') : t('purchasing.noRequests')}</p> : null}
        {requests.data && requests.data.items.length > 0 ? (
          <div className="table-wrap">
            <table>
              <caption className="visually-hidden">{t('purchasing.tab.requests')}</caption>
              <thead>
                <tr>
                  <th scope="col">{t('purchasing.number')}</th>
                  <th scope="col">{t('purchasing.requestTitle')}</th>
                  <th scope="col">{t('assets.status')}</th>
                  <th scope="col">{t('purchasing.total')}</th>
                  <th scope="col">{t('purchasing.level')}</th>
                  <th scope="col">{t('purchasing.requestedBy')}</th>
                  <th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th>
                </tr>
              </thead>
              <tbody>
                {requests.data.items.map((r) => (
                  <tr key={r.id}>
                    <td><bdi>{r.display}</bdi></td>
                    <td><bdi>{r.title}</bdi></td>
                    <td><Badge tone={requestTone(r.status)}>{statusLabel(r.status)}</Badge>{r.orderDisplay ? <div className="muted"><bdi>{r.orderDisplay}</bdi></div> : null}</td>
                    <td>{num(r.total)}</td>
                    <td>{r.requiredLevel > 0 ? r.requiredLevel : '—'}</td>
                    <td><bdi>{r.requestedByName ?? '—'}</bdi><div className="muted">{date(r.requestedAt)}</div></td>
                    <td className="actions"><button type="button" onClick={() => setOpen(r.id)}>{r.canApprove ? t('purchasing.review') : t('inventory.openTake')}</button></td>
                  </tr>
                ))}
              </tbody>
            </table>
            <Pager page={requests.data.pageNumber} pageSize={requests.data.pageSize} total={requests.data.total} onChange={setPage} />
          </div>
        ) : null}
      </QueryState>
      {open ? <RequestDialog id={open === 'new' ? null : open} onClose={() => setOpen(null)} /> : null}
    </>
  );
}

interface LineDraft { key: number; stock: boolean; itemId: string; itemLabel: string; storeId: string; description: string; unit: string; quantity: string; price: string }
let lineKey = 0;
const blankLine = (): LineDraft => ({ key: ++lineKey, stock: true, itemId: '', itemLabel: '', storeId: '', description: '', unit: 'pcs', quantity: '1', price: '' });

function RequestDialog({ id, onClose }: { id: string | null; onClose: () => void }) {
  const query = useRequest(id ?? undefined);
  const { t } = useTranslation();
  if (id && (query.isLoading || !query.data)) return <Dialog title={t('purchasing.request')} onClose={onClose}><Loading /></Dialog>;
  const request = query.data ?? null;
  return request && request.status !== 'Draft' ? <RequestView request={request} onClose={onClose} /> : <RequestEditor request={request} onClose={onClose} />;
}

function RequestEditor({ request, onClose }: { request: PurchaseRequest | null; onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { num } = usePrefs();
  const refresh = useRefresh();
  const [title, setTitle] = useState(request?.title ?? '');
  const [justification, setJustification] = useState(request?.justification ?? '');
  const [lines, setLines] = useState<LineDraft[]>(
    request?.lines?.map((l) => ({ key: ++lineKey, stock: l.itemId !== null, itemId: l.itemId ?? '', itemLabel: l.itemCode ?? '', storeId: l.storeId ?? '', description: l.description, unit: l.unit, quantity: String(l.quantity), price: String(l.estimatedUnitPrice) })) ?? [blankLine()],
  );
  const set = (key: number, patch: Partial<LineDraft>) => setLines(lines.map((l) => (l.key === key ? { ...l, ...patch } : l)));
  const total = lines.reduce((sum, l) => sum + (Number(l.quantity) || 0) * (Number(l.price) || 0), 0);
  const body = () => ({
    title: title.trim(), justification: justification.trim() || null, rowVersion: request?.rowVersion,
    lines: lines.map((l) => ({
      itemId: l.stock ? l.itemId || null : null, storeId: l.stock ? l.storeId || null : null, description: l.description.trim() || null, unit: l.unit.trim() || null,
      quantity: l.quantity === '' ? null : Number(l.quantity), estimatedUnitPrice: l.price === '' ? null : Number(l.price),
    })),
  });
  const save = useMutation({
    mutationFn: async (submit: boolean) => {
      const saved = request ? await api.put<PurchaseRequest>(`/purchasing/requests/${request.id}`, body()) : await api.post<PurchaseRequest>('/purchasing/requests', body());
      if (submit) await api.post(`/purchasing/requests/${saved.id}/submit`, {});
      return saved;
    },
    onSuccess: async () => { await refresh(); onClose(); },
  });
  const error = asError(save.error);
  return (
    <Dialog title={request ? t('purchasing.editRequest', { number: request.display }) : t('purchasing.newRequest')} onClose={onClose} wide>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate(false); }}>
        <Field label={t('purchasing.requestTitle')} error={error?.field('title')} required><input value={title} onChange={(e) => setTitle(e.target.value)} maxLength={200} autoFocus /></Field>
        <Field label={t('purchasing.justification')} error={error?.field('justification')}><textarea value={justification} onChange={(e) => setJustification(e.target.value)} rows={2} maxLength={2000} /></Field>
        {lines.map((l, n) => (
          <fieldset key={l.key}>
            <legend>{t('purchasing.lineNumber', { number: n + 1 })}</legend>
            <label className="check"><input type="checkbox" checked={l.stock} onChange={(e) => set(l.key, { stock: e.target.checked })} /> {t('purchasing.stockItem')}</label>
            {l.stock ? (
              <div className="row">
                <ItemPicker value={l.itemId ? { id: l.itemId, label: l.itemLabel } : null} onChange={(i) => set(l.key, i ? { itemId: i.id, itemLabel: `${i.code} ${i.name}`, unit: i.unit } : { itemId: '', itemLabel: '' })} />
                <StoreSelect value={l.storeId} onChange={(v) => set(l.key, { storeId: v })} label={t('purchasing.deliverTo')} />
              </div>
            ) : (
              <div className="row">
                <Field label={t('purchasing.description')}><input value={l.description} onChange={(e) => set(l.key, { description: e.target.value })} maxLength={300} /></Field>
                <Field label={t('inventory.unit')}><input value={l.unit} onChange={(e) => set(l.key, { unit: e.target.value })} maxLength={20} /></Field>
              </div>
            )}
            <div className="row">
              <Field label={t('inventory.quantity')}><input type="number" step="any" min="0" value={l.quantity} onChange={(e) => set(l.key, { quantity: e.target.value })} /></Field>
              <Field label={t('purchasing.estimatedPrice')}><input type="number" step="any" min="0" value={l.price} onChange={(e) => set(l.key, { price: e.target.value })} /></Field>
            </div>
            <button type="button" disabled={lines.length === 1} onClick={() => setLines(lines.filter((x) => x.key !== l.key))}>{t('common.delete')}</button>
          </fieldset>
        ))}
        {error?.field('lines') ? <p role="alert" className="error">{error.fieldErrors.lines?.join(' ')}</p> : null}
        <p><button type="button" onClick={() => setLines([...lines, blankLine()])}>{t('purchasing.addLine')}</button> <strong>{t('purchasing.total')}: {num(total)}</strong></p>
        <ErrorBanner error={error?.status === 400 ? null : save.error} />
        <div className="actions">
          <button type="button" onClick={onClose}>{t('common.cancel')}</button>
          <button type="submit" disabled={save.isPending}>{t('purchasing.saveDraft')}</button>
          <button type="button" className="primary" disabled={save.isPending} onClick={() => save.mutate(true)}>{t('purchasing.submit')}</button>
        </div>
      </form>
    </Dialog>
  );
}

function RequestView({ request, onClose }: { request: PurchaseRequest; onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { can } = usePermissions();
  const { date, num } = usePrefs();
  const refresh = useRefresh();
  const statusLabel = useEnumLabel('requestStatus');
  const [asking, setAsking] = useState<'reject' | 'cancel' | 'approve' | null>(null);
  const [ordering, setOrdering] = useState(false);
  const act = useMutation({
    mutationFn: ({ action, note }: { action: string; note?: string }) => api.post(`/purchasing/requests/${request.id}/${action}`, { note: note ?? null }),
    onSuccess: async () => { setAsking(null); await refresh(); },
  });
  const mayCancel = ['Submitted', 'Approved'].includes(request.status);
  const mayDecide = request.status === 'Submitted' && (request.canApprove || can('purchasing.approve1') || can('purchasing.approve2') || can('purchasing.approve3'));
  return (
    <Dialog title={`${request.display} · ${request.title}`} onClose={onClose} wide>
      <p>
        <Badge tone={requestTone(request.status)}>{statusLabel(request.status)}</Badge>{' '}
        <span className="muted">{t('purchasing.byOn', { name: request.requestedByName ?? '—', date: date(request.requestedAt) })}</span>
        {request.requiredLevel > 0 ? <span className="muted"> · {t('purchasing.needsLevel', { level: request.requiredLevel })}</span> : null}
      </p>
      {request.justification ? <p className="preline"><bdi>{request.justification}</bdi></p> : null}
      {request.decidedAt ? <p className="muted">{t('purchasing.decidedBy', { name: request.decidedByName ?? '—', date: date(request.decidedAt, true) })}{request.decisionNote ? <> — <bdi>{request.decisionNote}</bdi></> : null}</p> : null}
      <div className="table-wrap">
        <table>
          <caption className="visually-hidden">{t('purchasing.lines')}</caption>
          <thead>
            <tr>
              <th scope="col">{t('purchasing.description')}</th>
              <th scope="col">{t('inventory.quantity')}</th>
              <th scope="col">{t('purchasing.estimatedPrice')}</th>
              <th scope="col">{t('purchasing.total')}</th>
            </tr>
          </thead>
          <tbody>
            {request.lines?.map((l) => (
              <tr key={l.id}>
                <td><bdi>{l.description}</bdi>{l.itemCode ? <div className="muted"><bdi>{l.itemCode}</bdi> → <bdi>{l.storeName}</bdi></div> : null}</td>
                <td>{num(l.quantity)} {l.unit}</td>
                <td>{num(l.estimatedUnitPrice)}</td>
                <td>{num(l.total)}</td>
              </tr>
            ))}
          </tbody>
          <tfoot><tr><th scope="row" colSpan={3}>{t('purchasing.total')}</th><td><strong>{num(request.total)}</strong></td></tr></tfoot>
        </table>
      </div>
      <ErrorBanner error={act.error} />
      <div className="actions">
        <button type="button" onClick={onClose}>{t('common.back')}</button>
        {mayDecide ? <button type="button" className="primary" disabled={act.isPending} onClick={() => setAsking('approve')}>{t('purchasing.approve')}</button> : null}
        {mayDecide ? <button type="button" onClick={() => setAsking('reject')}>{t('purchasing.reject')}</button> : null}
        {request.status === 'Approved' && can('purchasing.order') ? <button type="button" className="primary" onClick={() => setOrdering(true)}>{t('purchasing.makeOrder')}</button> : null}
        {mayCancel ? <button type="button" onClick={() => setAsking('cancel')}>{t('purchasing.cancelRequest')}</button> : null}
      </div>
      {asking ? (
        <ReasonDialog
          title={t(`purchasing.${asking === 'cancel' ? 'cancelRequest' : asking}`)} label={asking === 'approve' ? t('common.reasonOptional') : t('common.reason')} optional={asking === 'approve'} confirmLabel={t('common.confirm')}
          busy={act.isPending} error={act.error} onCancel={() => setAsking(null)} onConfirm={(note) => act.mutate({ action: asking, note })}
        />
      ) : null}
      {ordering ? <OrderDialog request={request} onClose={() => setOrdering(false)} onDone={() => { setOrdering(false); onClose(); }} /> : null}
    </Dialog>
  );
}

function OrderDialog({ request, onClose, onDone }: { request: PurchaseRequest; onClose: () => void; onDone: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { name, num } = usePrefs();
  const refresh = useRefresh();
  const vendors = useVendors({ status: 'Active', pageSize: 100 });
  const [vendorId, setVendorId] = useState(request.preferredVendorId ?? '');
  const [expectedOn, setExpectedOn] = useState('');
  const [notes, setNotes] = useState('');
  const [prices, setPrices] = useState<Record<string, string>>(Object.fromEntries((request.lines ?? []).map((l) => [l.id, String(l.estimatedUnitPrice)])));
  const total = (request.lines ?? []).reduce((sum, l) => sum + l.quantity * (Number(prices[l.id]) || 0), 0);
  const save = useMutation({
    mutationFn: () => api.post(`/purchasing/requests/${request.id}/order`, {
      vendorId: vendorId || null, expectedOn: expectedOn || null, notes: notes.trim() || null,
      prices: (request.lines ?? []).map((l) => ({ lineId: l.id, unitPrice: Number(prices[l.id]) })),
    }),
    onSuccess: async () => { await refresh(); onDone(); },
  });
  const error = asError(save.error);
  return (
    <Dialog title={t('purchasing.makeOrder')} onClose={onClose} wide>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }}>
        <div className="row">
          <Field label={t('vendors.vendor')} error={error?.field('vendorId')} required>
            <select value={vendorId} onChange={(e) => setVendorId(e.target.value)} autoFocus>
              <option value="">{t('common.choose')}</option>
              {vendors.data?.items.map((v) => <option key={v.id} value={v.id}>{v.code} · {name(v)}</option>)}
            </select>
          </Field>
          <Field label={t('purchasing.expectedOn')} error={error?.field('expectedOn')}><input type="date" value={expectedOn} onChange={(e) => setExpectedOn(e.target.value)} /></Field>
        </div>
        <fieldset>
          <legend>{t('purchasing.agreedPrices')}</legend>
          <p className="muted">{t('purchasing.agreedPricesHint')}</p>
          {request.lines?.map((l) => (
            <Field key={l.id} label={`${l.description} (${num(l.quantity)} ${l.unit})`}><input type="number" step="any" min="0" value={prices[l.id]} onChange={(e) => setPrices({ ...prices, [l.id]: e.target.value })} /></Field>
          ))}
          <p><strong>{t('purchasing.total')}: {num(total)}</strong> <span className="muted">· {t('purchasing.approvedTotal', { total: num(request.total) })}</span></p>
          {error?.field('prices') ? <p role="alert" className="error">{error.fieldErrors.prices?.join(' ')}</p> : null}
        </fieldset>
        <Field label={t('vendors.notes')} error={error?.field('notes')}><textarea value={notes} onChange={(e) => setNotes(e.target.value)} rows={2} maxLength={2000} /></Field>
        <ErrorBanner error={error?.status === 400 ? null : save.error} />
        <div className="actions">
          <button type="button" onClick={onClose}>{t('common.cancel')}</button>
          <button type="submit" className="primary" disabled={save.isPending}>{t('purchasing.issueOrder')}</button>
        </div>
      </form>
    </Dialog>
  );
}

// ---------------- orders ----------------

function OrdersTab() {
  const { t } = useTranslation();
  const { date, num } = usePrefs();
  const statusLabel = useEnumLabel('orderStatus');
  const [status, setStatus] = useState('');
  const [page, setPage] = useState(1);
  const [open, setOpen] = useState<string | null>(null);
  const orders = useOrders({ status: status || undefined, page, pageSize: 25 });
  return (
    <>
      <div className="filters">
        <Field label={t('assets.status')}>
          <select value={status} onChange={(e) => { setStatus(e.target.value); setPage(1); }}>
            <option value="">{t('common.all')}</option>
            {(['Issued', 'Acknowledged', 'PartiallyReceived', 'Received', 'Cancelled'] as const).map((s) => <option key={s} value={s}>{statusLabel(s)}</option>)}
          </select>
        </Field>
      </div>
      <QueryState query={orders}>
        {orders.data && orders.data.items.length === 0 ? <p className="muted">{t('purchasing.noOrders')}</p> : null}
        {orders.data && orders.data.items.length > 0 ? (
          <div className="table-wrap">
            <table>
              <caption className="visually-hidden">{t('purchasing.tab.orders')}</caption>
              <thead>
                <tr>
                  <th scope="col">{t('purchasing.number')}</th>
                  <th scope="col">{t('vendors.vendor')}</th>
                  <th scope="col">{t('assets.status')}</th>
                  <th scope="col">{t('purchasing.total')}</th>
                  <th scope="col">{t('purchasing.expectedOn')}</th>
                  <th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th>
                </tr>
              </thead>
              <tbody>
                {orders.data.items.map((o) => (
                  <tr key={o.id}>
                    <td><bdi>{o.display}</bdi><div className="muted"><bdi>{o.requestDisplay}</bdi></div></td>
                    <td><bdi>{o.vendorName}</bdi></td>
                    <td><Badge tone={orderTone(o.status)}>{statusLabel(o.status)}</Badge></td>
                    <td>{num(o.total)} {o.currencyCode}</td>
                    <td>{o.expectedOn ? date(o.expectedOn) : '—'}</td>
                    <td className="actions"><button type="button" onClick={() => setOpen(o.id)}>{t('inventory.openTake')}</button></td>
                  </tr>
                ))}
              </tbody>
            </table>
            <Pager page={orders.data.pageNumber} pageSize={orders.data.pageSize} total={orders.data.total} onChange={setPage} />
          </div>
        ) : null}
      </QueryState>
      {open ? <OrderView id={open} onClose={() => setOpen(null)} /> : null}
    </>
  );
}

function OrderView({ id, onClose }: { id: string; onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { can } = usePermissions();
  const { date, num } = usePrefs();
  const refresh = useRefresh();
  const statusLabel = useEnumLabel('orderStatus');
  const query = useOrder(id);
  const [received, setReceived] = useState<Record<string, string>>({});
  const [note, setNote] = useState('');
  const [cancelling, setCancelling] = useState(false);
  const receive = useMutation({
    mutationFn: () => api.post(`/purchasing/orders/${id}/receive`, {
      deliveryNote: note.trim() || null,
      lines: Object.entries(received).filter(([, v]) => v !== '' && Number(v) > 0).map(([lineId, v]) => ({ lineId, quantity: Number(v) })),
    }),
    onSuccess: async () => { setReceived({}); setNote(''); await refresh(); },
  });
  const act = useMutation({
    mutationFn: ({ action, reason }: { action: string; reason?: string }) => api.post(`/purchasing/orders/${id}/${action}`, { reason: reason ?? null, rowVersion: query.data?.rowVersion }),
    onSuccess: async () => { setCancelling(false); await refresh(); },
  });
  if (query.isLoading || !query.data) return <Dialog title={t('purchasing.order')} onClose={onClose}><Loading /></Dialog>;
  const o = query.data;
  const open = o.status === 'Issued' || o.status === 'Acknowledged' || o.status === 'PartiallyReceived';
  const error = asError(receive.error);
  return (
    <Dialog title={`${o.display} · ${o.vendorName}`} onClose={onClose} wide>
      <p>
        <Badge tone={orderTone(o.status)}>{statusLabel(o.status)}</Badge>{' '}
        <span className="muted">{t('purchasing.byOn', { name: o.issuedByName ?? '—', date: date(o.issuedAt) })}{o.expectedOn ? ` · ${t('purchasing.expectedBy', { date: date(o.expectedOn) })}` : ''}</span>
      </p>
      {o.notes ? <p className="preline"><bdi>{o.notes}</bdi></p> : null}
      {o.cancelReason ? <p className="banner"><bdi>{o.cancelReason}</bdi></p> : null}
      <div className="table-wrap">
        <table>
          <caption className="visually-hidden">{t('purchasing.lines')}</caption>
          <thead>
            <tr>
              <th scope="col">{t('purchasing.description')}</th>
              <th scope="col">{t('purchasing.ordered')}</th>
              <th scope="col">{t('purchasing.received')}</th>
              <th scope="col">{t('purchasing.unitPrice')}</th>
              <th scope="col">{t('purchasing.total')}</th>
              {open && can('purchasing.receive') ? <th scope="col">{t('purchasing.receiveNow')}</th> : null}
            </tr>
          </thead>
          <tbody>
            {o.lines?.map((l) => (
              <tr key={l.id}>
                <td><bdi>{l.description}</bdi>{l.itemCode ? <div className="muted"><bdi>{l.itemCode}</bdi> → <bdi>{l.storeName}</bdi></div> : null}</td>
                <td>{num(l.quantity)} {l.unit}</td>
                <td>{num(l.receivedQty)}</td>
                <td>{num(l.unitPrice)}</td>
                <td>{num(l.total)}</td>
                {open && can('purchasing.receive') ? (
                  <td>{l.outstanding > 0 ? <input type="number" step="any" min="0" max={l.outstanding} aria-label={t('purchasing.receiveFor', { item: l.description })} value={received[l.id] ?? ''} onChange={(e) => setReceived({ ...received, [l.id]: e.target.value })} /> : '—'}</td>
                ) : null}
              </tr>
            ))}
          </tbody>
          <tfoot><tr><th scope="row" colSpan={4}>{t('purchasing.total')}</th><td colSpan={2}><strong>{num(o.total)} {o.currencyCode}</strong></td></tr></tfoot>
        </table>
      </div>
      {open && can('purchasing.receive') ? (
        <div className="row">
          <Field label={t('purchasing.deliveryNote')} error={error?.field('deliveryNote')}><input value={note} onChange={(e) => setNote(e.target.value)} maxLength={100} /></Field>
          <button type="button" className="primary" disabled={receive.isPending || Object.values(received).every((v) => v === '' || Number(v) <= 0)} onClick={() => receive.mutate()}>{t('purchasing.recordReceipt')}</button>
        </div>
      ) : null}
      {error ? <p role="alert" className="error">{Object.values(error.fieldErrors).flat().join(' ')}</p> : null}
      <ErrorBanner error={error?.status === 400 ? null : receive.error ?? act.error} />
      {o.receipts && o.receipts.length > 0 ? (
        <>
          <h3>{t('purchasing.receipts')}</h3>
          <ul className="plain">
            {o.receipts.map((r) => <li key={r.id}>{date(r.receivedAt, true)} · {num(r.quantity)} · <bdi>{o.lines?.find((l) => l.id === r.lineId)?.description}</bdi>{r.deliveryNote ? ` · ${r.deliveryNote}` : ''}</li>)}
          </ul>
        </>
      ) : null}
      <div className="actions">
        <button type="button" onClick={onClose}>{t('common.back')}</button>
        {o.status === 'Issued' && can('purchasing.order') ? <button type="button" disabled={act.isPending} onClick={() => act.mutate({ action: 'acknowledge' })}>{t('purchasing.acknowledge')}</button> : null}
        {(o.status === 'Issued' || o.status === 'Acknowledged') && can('purchasing.order') ? <button type="button" onClick={() => setCancelling(true)}>{t('purchasing.cancelOrder')}</button> : null}
      </div>
      {cancelling ? <ReasonDialog title={t('purchasing.cancelOrder')} label={t('common.reason')} confirmLabel={t('common.confirm')} busy={act.isPending} error={act.error} onCancel={() => setCancelling(false)} onConfirm={(reason) => act.mutate({ action: 'cancel', reason })} /> : null}
    </Dialog>
  );
}

// ---------------- settings ----------------

function SettingsTab() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const refresh = useRefresh();
  const settings = usePurchasingSettings();
  if (settings.isLoading || !settings.data) return <Loading />;
  return <SettingsForm initial={settings.data} onSave={(body) => api.put('/purchasing/settings', body).then(() => refresh())} title={t('purchasing.limits')} />;
}

function SettingsForm({ initial, onSave, title }: { initial: { level1Limit: number; level2Limit: number; overrunTolerancePercent: number; currencyCode: string; rowVersion: string | null }; onSave: (body: object) => Promise<unknown>; title: string }) {
  const { t } = useTranslation();
  const [l1, setL1] = useState(String(initial.level1Limit));
  const [l2, setL2] = useState(String(initial.level2Limit));
  const [tol, setTol] = useState(String(initial.overrunTolerancePercent));
  const [currency, setCurrency] = useState(initial.currencyCode);
  const [saved, setSaved] = useState(false);
  const save = useMutation({
    mutationFn: () => onSave({ level1Limit: Number(l1), level2Limit: Number(l2), overrunTolerancePercent: Number(tol), currencyCode: currency.trim(), rowVersion: initial.rowVersion }),
    onSuccess: () => setSaved(true),
  });
  const error = asError(save.error);
  return (
    <>
      <h2>{title}</h2>
      <p className="muted">{t('purchasing.limitsHint')}</p>
      <form onSubmit={(e) => { e.preventDefault(); setSaved(false); save.mutate(); }}>
        <div className="row">
          <Field label={t('purchasing.level1')} hint={t('purchasing.level1Hint')} error={error?.field('level1Limit')}><input type="number" step="0.01" min="0" value={l1} onChange={(e) => setL1(e.target.value)} /></Field>
          <Field label={t('purchasing.level2')} hint={t('purchasing.level2Hint')} error={error?.field('level2Limit')}><input type="number" step="0.01" min="0" value={l2} onChange={(e) => setL2(e.target.value)} /></Field>
        </div>
        <div className="row">
          <Field label={t('purchasing.tolerance')} hint={t('purchasing.toleranceHint')} error={error?.field('overrunTolerancePercent')}><input type="number" step="0.01" min="0" max="100" value={tol} onChange={(e) => setTol(e.target.value)} /></Field>
          <Field label={t('vendors.currency')} error={error?.field('currencyCode')}><input value={currency} onChange={(e) => setCurrency(e.target.value)} maxLength={3} /></Field>
        </div>
        <ErrorBanner error={error?.status === 400 ? null : save.error} />
        {saved ? <p role="status">{t('purchasing.saved')}</p> : null}
        <div className="actions"><button type="submit" className="primary" disabled={save.isPending}>{t('common.save')}</button></div>
      </form>
    </>
  );
}
