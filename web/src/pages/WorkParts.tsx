import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import { usePermissions, useWorkParts } from '../api/hooks';
import type { WorkOrder, WorkPart } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Dialog, ItemPicker, StoreSelect } from '../components/InventoryBits';
import { Badge, ErrorBanner, Field, QueryState } from '../components/ui';
import { usePrefs } from '../lib/prefs';

type Action = 'reserve' | 'issue' | 'return' | 'release';

/** Parts on a work order: held back by planners, taken from stock by technicians, put back when not used. Cost comes from the stock records. */
export function PartsTab({ order }: { order: WorkOrder }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { num } = usePrefs();
  const { can } = usePermissions();
  const client = useQueryClient();
  const parts = useWorkParts(order.id, true);
  const [acting, setActing] = useState<{ action: Action; line: WorkPart } | null>(null);
  const [item, setItem] = useState<{ id: string; label: string } | null>(null);
  const [storeId, setStoreId] = useState('');
  const [quantity, setQuantity] = useState('');

  const refresh = async () => {
    await client.invalidateQueries({ queryKey: ['inventory'] });
    await client.invalidateQueries({ queryKey: ['work-evidence', order.id] });
  };
  const call = useMutation({
    mutationFn: ({ action, body }: { action: Action; body: object }) => api.post(`/work-orders/${order.id}/parts/${action}`, body),
    onSuccess: async () => { setItem(null); setQuantity(''); await refresh(); },
  });
  const error = call.error instanceof ApiError ? call.error : null;
  const accepts = parts.data?.acceptsParts ?? false;
  const body = () => ({ itemId: item?.id ?? null, storeId: storeId || null, quantity: quantity ? Number(quantity) : null });

  return (
    <>
      <p className="muted">{t('parts.intro')}</p>
      <QueryState query={parts}>
        {parts.data && !accepts ? <p className="banner">{t('parts.closed')}</p> : null}
        {parts.data && parts.data.lines.length === 0 ? <p className="muted">{t('parts.none')}</p> : null}
        {parts.data && parts.data.lines.length > 0 ? (
          <div className="table-wrap">
            <table>
              <caption className="visually-hidden">{t('evidence.tabs.parts')}</caption>
              <thead>
                <tr>
                  <th scope="col">{t('inventory.item')}</th>
                  <th scope="col">{t('inventory.store')}</th>
                  <th scope="col">{t('parts.reserved')}</th>
                  <th scope="col">{t('parts.issued')}</th>
                  <th scope="col">{t('inventory.unitCost')}</th>
                  <th scope="col">{t('parts.cost')}</th>
                  <th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th>
                </tr>
              </thead>
              <tbody>
                {parts.data.lines.map((l) => (
                  <tr key={`${l.storeId}-${l.itemId}`}>
                    <td><bdi>{l.itemCode}</bdi> <bdi>{l.itemName}</bdi></td>
                    <td><bdi>{l.storeName}</bdi></td>
                    <td>{l.reserved > 0 ? <Badge tone="warn">{num(l.reserved)} {l.unit}</Badge> : '—'}</td>
                    <td>{num(l.issued)} {l.unit}</td>
                    <td>{num(l.unitCost)}</td>
                    <td>{num(l.cost)}</td>
                    <td className="actions">
                      {accepts && can('inventory.issue') ? <button type="button" onClick={() => setActing({ action: 'issue', line: l })}>{t('parts.issue')}</button> : null}
                      {accepts && can('inventory.issue') && l.issued > 0 ? <button type="button" onClick={() => setActing({ action: 'return', line: l })}>{t('parts.return')}</button> : null}
                      {accepts && can('inventory.manage') && l.reserved > 0 ? <button type="button" onClick={() => setActing({ action: 'release', line: l })}>{t('parts.release')}</button> : null}
                    </td>
                  </tr>
                ))}
              </tbody>
              <tfoot>
                <tr><th scope="row" colSpan={5}>{t('parts.total')}</th><td colSpan={2}><strong>{num(parts.data.totalCost)}</strong></td></tr>
              </tfoot>
            </table>
          </div>
        ) : null}
      </QueryState>

      {accepts && (can('inventory.manage') || can('inventory.issue')) ? (
        <fieldset>
          <legend>{t('parts.add')}</legend>
          <form onSubmit={(e) => e.preventDefault()}>
            <ItemPicker value={item} onChange={(i) => setItem(i ? { id: i.id, label: `${i.code} ${i.name}` } : null)} error={error?.field('itemId')} />
            <div className="row">
              <StoreSelect value={storeId} onChange={setStoreId} label={t('parts.storeOptional')} error={error?.field('storeId')} allowAll />
              <Field label={t('inventory.quantity')} error={error?.field('quantity')} required><input type="number" step="any" min="0" value={quantity} onChange={(e) => setQuantity(e.target.value)} /></Field>
            </div>
            <ErrorBanner error={error?.status === 400 ? null : call.error} />
            <div className="actions">
              {can('inventory.manage') ? <button type="button" disabled={call.isPending} onClick={() => call.mutate({ action: 'reserve', body: body() })}>{t('parts.reserve')}</button> : null}
              {can('inventory.issue') ? <button type="button" className="primary" disabled={call.isPending} onClick={() => call.mutate({ action: 'issue', body: body() })}>{t('parts.issueNow')}</button> : null}
            </div>
          </form>
        </fieldset>
      ) : null}
      {acting ? <LineDialog order={order} acting={acting} onClose={() => setActing(null)} onDone={refresh} /> : null}
    </>
  );
}

function LineDialog({ order, acting, onClose, onDone }: { order: WorkOrder; acting: { action: Action; line: WorkPart }; onClose: () => void; onDone: () => Promise<void> }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { line, action } = acting;
  const max = action === 'return' ? line.issued : action === 'release' ? line.reserved : line.reserved > 0 ? line.reserved : Math.max(0, line.storeAvailable);
  const [quantity, setQuantity] = useState(String(max));
  const [reason, setReason] = useState('');
  const save = useMutation({
    mutationFn: () => api.post(`/work-orders/${order.id}/parts/${action}`, { itemId: line.itemId, storeId: line.storeId, quantity: Number(quantity), reason: reason.trim() || null }),
    onSuccess: async () => { await onDone(); onClose(); },
  });
  const error = save.error instanceof ApiError ? save.error : null;
  return (
    <Dialog title={`${t(`parts.${action}`)}: ${line.itemCode}`} onClose={onClose}>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }}>
        {action !== 'release' ? (
          <Field label={t('inventory.quantity')} hint={`${t('parts.upTo')} ${max} ${line.unit}`} error={error?.field('quantity')} required>
            <input type="number" step="any" min="0" value={quantity} onChange={(e) => setQuantity(e.target.value)} autoFocus />
          </Field>
        ) : <p>{t('parts.releaseWarning', { quantity: line.reserved, unit: line.unit })}</p>}
        {action === 'return' ? <Field label={t('common.reasonOptional')} error={error?.field('reason')}><input value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} /></Field> : null}
        <ErrorBanner error={error?.status === 400 ? null : save.error} />
        <div className="actions">
          <button type="button" onClick={onClose}>{t('common.cancel')}</button>
          <button type="submit" className="primary" disabled={save.isPending}>{t('common.confirm')}</button>
        </div>
      </form>
    </Dialog>
  );
}
