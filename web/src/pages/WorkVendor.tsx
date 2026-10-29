import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import { useAssignmentSuggestions, useContracts, usePermissions, useVendorAssignment, useVendors } from '../api/hooks';
import type { WorkOrder } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Dialog } from '../components/InventoryBits';
import { Badge, ErrorBanner, Field } from '../components/ui';
import { usePrefs } from '../lib/prefs';

const OPEN = ['Approved', 'Assigned', 'InProgress', 'OnHold'];

/** The "done by" row of a work order: the vendor and the contract terms when the work was given outside, and the way to give it or take it back. */
export function VendorRows({ order }: { order: WorkOrder }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { can } = usePermissions();
  const { name } = usePrefs();
  const client = useQueryClient();
  const readable = can('workorders.read');
  const assignment = useVendorAssignment(readable ? order.id : undefined);
  const [open, setOpen] = useState(false);
  const clear = useMutation({
    mutationFn: () => api.post(`/work-orders/${order.id}/vendor/clear`, {}),
    onSuccess: () => client.invalidateQueries(),
  });
  const a = assignment.data;
  const canAssign = can('workorders.assign') && OPEN.includes(order.status);
  if (!a && !canAssign) return null;
  return (
    <>
      <dt>{t('vendors.doneBy')}</dt>
      <dd>
        {a ? (
          <>
            <bdi>{name({ name: a.vendorName, nameAr: a.vendorNameAr })}</bdi>
            {a.contractNumber ? <div className="muted">{t('vendors.underContract', { number: a.contractNumber })} · {a.contractInForce ? t('vendors.inForce') : <Badge tone="bad">{t('vendors.notInForce')}</Badge>}</div> : <div className="muted">{t('vendors.noContract')}</div>}
            {a.covered === false ? <div><Badge tone="warn">{t('vendors.notCovered')}</Badge></div> : null}
            {a.responseMinutes !== null && a.resolutionMinutes !== null ? <div className="muted">{t('vendors.contractTargets', { response: a.responseMinutes, resolution: a.resolutionMinutes })}</div> : null}
          </>
        ) : <span className="muted">—</span>}
        {canAssign ? (
          <div className="actions">
            <button type="button" onClick={() => setOpen(true)}>{a ? t('vendors.changeVendor') : t('vendors.giveToVendor')}</button>
            {a ? <button type="button" disabled={clear.isPending} onClick={() => clear.mutate()}>{t('vendors.takeBack')}</button> : null}
          </div>
        ) : null}
        <ErrorBanner error={clear.error} />
      </dd>
      {open ? <AssignVendorDialog order={order} onClose={() => setOpen(false)} /> : null}
    </>
  );
}

function AssignVendorDialog({ order, onClose }: { order: WorkOrder; onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { name, date } = usePrefs();
  const client = useQueryClient();
  const vendors = useVendors({ status: 'Active', pageSize: 100 }, true);
  const [vendorId, setVendorId] = useState('');
  const [contractId, setContractId] = useState('');
  const contracts = useContracts({ vendorId: vendorId || undefined, pageSize: 100 }, !!vendorId);
  const suggestions = useAssignmentSuggestions(order.id, true);
  const usable = contracts.data?.items.filter((c) => c.status === 'Active' || c.status === 'Expiring') ?? [];
  const save = useMutation({
    mutationFn: () => api.post(`/work-orders/${order.id}/vendor`, { vendorId: vendorId || null, contractId: contractId || null }),
    onSuccess: async () => { await client.invalidateQueries(); onClose(); },
  });
  const error = save.error instanceof ApiError ? save.error : null;
  return (
    <Dialog title={t('vendors.giveToVendor')} onClose={onClose}>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }}>
        {suggestions.data && suggestions.data.vendors.length > 0 ? (
          <div className="ai-box" role="group" aria-label={t('suggest.vendorsTitle')}>
            <strong>{t('suggest.vendorsTitle')}</strong> <span className="muted">{t('suggest.fromContracts')}</span>
            <ul className="plain">
              {suggestions.data.vendors.map((v) => (
                <li key={v.vendorId}>
                  <bdi>{v.name}</bdi>: {t('suggest.underContract', { number: v.contractNumber })}{v.resolutionMinutes != null ? ` · ${t('suggest.resolvesIn', { hours: Math.round(v.resolutionMinutes / 60) })}` : ''}{v.pastJobs > 0 ? ` · ${t('suggest.pastJobs', { count: v.pastJobs })}` : ''}{' '}
                  <button type="button" aria-label={t('suggest.chooseX', { name: v.name })} onClick={() => { setVendorId(v.vendorId); setContractId(v.contractId); }}>{t('suggest.choose')}</button>
                </li>
              ))}
            </ul>
          </div>
        ) : null}
        <Field label={t('vendors.vendor')} error={error?.field('vendorId')} required>
          <select value={vendorId} onChange={(e) => { setVendorId(e.target.value); setContractId(''); }} autoFocus>
            <option value="">{t('common.choose')}</option>
            {vendors.data?.items.map((v) => <option key={v.id} value={v.id}>{v.code} · {name(v)}</option>)}
          </select>
        </Field>
        <Field label={t('vendors.contract')} hint={t('vendors.contractOptional')} error={error?.field('contractId')}>
          <select value={contractId} onChange={(e) => setContractId(e.target.value)} disabled={!vendorId}>
            <option value="">{t('vendors.noContract')}</option>
            {usable.map((c) => <option key={c.id} value={c.id}>{c.number} · {c.title} ({t('vendors.until', { date: date(c.endOn) })})</option>)}
          </select>
        </Field>
        <ErrorBanner error={error?.status === 400 ? null : save.error} />
        <div className="actions">
          <button type="button" onClick={onClose}>{t('common.cancel')}</button>
          <button type="submit" className="primary" disabled={save.isPending}>{t('common.confirm')}</button>
        </div>
      </form>
    </Dialog>
  );
}
