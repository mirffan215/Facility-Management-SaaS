import { useMemo, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import { useAssetChoices, useAssetClasses, useContract, useContractDocuments, useContracts, useLocationTree, usePermissions, useVendorContacts, useVendors } from '../api/hooks';
import type { Contract, ContractDocumentKind, ContractSla, ContractType, CoverageKind, Vendor, VendorStatus } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Dialog } from '../components/InventoryBits';
import { Badge, ErrorBanner, Field, Loading, Pager, QueryState, ReasonDialog, useEnumLabel } from '../components/ui';
import { downloadBlob } from '../lib/csv';
import { formatBytes } from '../lib/format';
import { usePrefs } from '../lib/prefs';

const TABS = ['vendors', 'contracts'] as const;
const asError = (e: unknown) => (e instanceof ApiError ? e : null);

export function VendorsPage() {
  const { t } = useTranslation();
  const [params, setParams] = useSearchParams();
  const tab = TABS.find((x) => x === params.get('tab')) ?? 'vendors';
  return (
    <section>
      <h1>{t('vendors.title')}</h1>
      <p className="muted">{t('vendors.intro')}</p>
      <div className="tabs" role="tablist" aria-label={t('vendors.sections')}>
        {TABS.map((x) => (
          <button key={x} role="tab" type="button" aria-selected={tab === x} className={tab === x ? 'active' : ''} onClick={() => setParams({ tab: x })}>
            {t(`vendors.tab.${x}`)}
          </button>
        ))}
      </div>
      <div role="tabpanel">{tab === 'vendors' ? <VendorsTab /> : <ContractsTab initialOpen={params.get('contract')} />}</div>
    </section>
  );
}

function useRefresh() {
  const client = useQueryClient();
  return () => client.invalidateQueries({ queryKey: ['vendors'] });
}

function statusTone(s: ContractStatus): 'ok' | 'warn' | 'bad' | 'neutral' {
  return s === 'Active' ? 'ok' : s === 'Expiring' ? 'warn' : s === 'Expired' ? 'bad' : 'neutral';
}
type ContractStatus = Contract['status'];

// ---------------- vendors ----------------

function VendorsTab() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { can } = usePermissions();
  const { name, num } = usePrefs();
  const refresh = useRefresh();
  const statusLabel = useEnumLabel('vendorStatus');
  const [q, setQ] = useState('');
  const [status, setStatus] = useState('');
  const [page, setPage] = useState(1);
  const [editing, setEditing] = useState<Vendor | 'new' | null>(null);
  const [contactsOf, setContactsOf] = useState<Vendor | null>(null);
  const [statusOf, setStatusOf] = useState<Vendor | null>(null);
  const vendors = useVendors({ q: q.trim() || undefined, status: status || undefined, includeArchived: true, page, pageSize: 25 });
  const archive = useMutation({
    mutationFn: (v: Vendor) => api.post(`/vendors/${v.id}/${v.isArchived ? 'restore' : 'archive'}`, { rowVersion: v.rowVersion }),
    onSuccess: () => refresh(),
  });
  return (
    <>
      <div className="filters">
        <Field label={t('vendors.search')}><input value={q} onChange={(e) => { setQ(e.target.value); setPage(1); }} /></Field>
        <Field label={t('assets.status')}>
          <select value={status} onChange={(e) => { setStatus(e.target.value); setPage(1); }}>
            <option value="">{t('common.all')}</option>
            {(['Active', 'OnHold', 'Blocked'] as const).map((s) => <option key={s} value={s}>{statusLabel(s)}</option>)}
          </select>
        </Field>
        {can('vendors.manage') ? <button type="button" onClick={() => setEditing('new')}>{t('vendors.newVendor')}</button> : null}
      </div>
      <ErrorBanner error={archive.error} />
      <QueryState query={vendors}>
        {vendors.data && vendors.data.items.length === 0 ? <p className="muted">{t('vendors.noVendors')}</p> : null}
        {vendors.data && vendors.data.items.length > 0 ? (
          <div className="table-wrap">
            <table>
              <caption className="visually-hidden">{t('vendors.tab.vendors')}</caption>
              <thead>
                <tr>
                  <th scope="col">{t('inventory.code')}</th>
                  <th scope="col">{t('assets.name')}</th>
                  <th scope="col">{t('assets.status')}</th>
                  <th scope="col">{t('vendors.activeContracts')}</th>
                  <th scope="col">{t('vendors.contact')}</th>
                  <th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th>
                </tr>
              </thead>
              <tbody>
                {vendors.data.items.map((v) => (
                  <tr key={v.id}>
                    <td><bdi>{v.code}</bdi></td>
                    <td><bdi>{name(v)}</bdi> {v.isArchived ? <Badge tone="neutral">{t('common.archived')}</Badge> : null}{v.taxNumber ? <div className="muted">{t('vendors.vat')} <bdi>{v.taxNumber}</bdi></div> : null}</td>
                    <td><Badge tone={v.status === 'Active' ? 'ok' : v.status === 'OnHold' ? 'warn' : 'bad'}>{statusLabel(v.status)}</Badge>{v.statusReason ? <div className="muted"><bdi>{v.statusReason}</bdi></div> : null}</td>
                    <td>{num(v.activeContracts)}</td>
                    <td>{v.email ? <bdi>{v.email}</bdi> : '—'}{v.phone ? <div className="muted"><bdi>{v.phone}</bdi></div> : null}</td>
                    <td className="actions">
                      <button type="button" onClick={() => setContactsOf(v)}>{t('vendors.contacts')}</button>
                      {can('vendors.manage') ? (
                        <>
                          <button type="button" onClick={() => setEditing(v)}>{t('common.edit')}</button>
                          <button type="button" onClick={() => setStatusOf(v)}>{t('vendors.changeStatus')}</button>
                          <button type="button" disabled={archive.isPending} onClick={() => archive.mutate(v)}>{v.isArchived ? t('common.restore') : t('common.archive')}</button>
                        </>
                      ) : null}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            <Pager page={vendors.data.pageNumber} pageSize={vendors.data.pageSize} total={vendors.data.total} onChange={setPage} />
          </div>
        ) : null}
      </QueryState>
      {editing ? <VendorDialog vendor={editing === 'new' ? null : editing} onClose={() => setEditing(null)} /> : null}
      {contactsOf ? <ContactsDialog vendor={contactsOf} onClose={() => setContactsOf(null)} /> : null}
      {statusOf ? <VendorStatusDialog vendor={statusOf} onClose={() => setStatusOf(null)} /> : null}
    </>
  );
}

function useSave<T extends object>(path: string, method: 'post' | 'put', onClose: () => void) {
  const { api } = useAuth();
  const refresh = useRefresh();
  return useMutation({
    mutationFn: (body: T) => (method === 'post' ? api.post(path, body) : api.put(path, body)),
    onSuccess: async () => { await refresh(); onClose(); },
  });
}

function Buttons({ onClose, busy }: { onClose: () => void; busy: boolean }) {
  const { t } = useTranslation();
  return (
    <div className="actions">
      <button type="button" onClick={onClose}>{t('common.cancel')}</button>
      <button type="submit" className="primary" disabled={busy}>{t('common.save')}</button>
    </div>
  );
}

function VendorDialog({ vendor, onClose }: { vendor: Vendor | null; onClose: () => void }) {
  const { t } = useTranslation();
  const [f, setF] = useState({
    code: vendor?.code ?? '', name: vendor?.name ?? '', nameAr: vendor?.nameAr ?? '', taxNumber: vendor?.taxNumber ?? '', registrationNumber: vendor?.registrationNumber ?? '',
    email: vendor?.email ?? '', phone: vendor?.phone ?? '', address: vendor?.address ?? '', country: vendor?.country ?? 'SA', paymentTerms: vendor?.paymentTerms ?? '', notes: vendor?.notes ?? '',
  });
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF({ ...f, [k]: e.target.value });
  const save = useSave<object>(vendor ? `/vendors/${vendor.id}` : '/vendors', vendor ? 'put' : 'post', onClose);
  const error = asError(save.error);
  const body = () => Object.fromEntries(Object.entries(f).map(([k, v]) => [k, v.trim() || null]).concat([['rowVersion', vendor?.rowVersion ?? null]]));
  return (
    <Dialog title={vendor ? t('vendors.editVendor') : t('vendors.newVendor')} onClose={onClose} wide>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate(body()); }}>
        <div className="row">
          <Field label={t('inventory.code')} hint={t('inventory.codeHint')} error={error?.field('code')} required><input value={f.code} onChange={set('code')} maxLength={50} autoFocus /></Field>
          <Field label={t('vendors.country')} hint={t('vendors.countryHint')} error={error?.field('country')}><input value={f.country} onChange={set('country')} maxLength={2} /></Field>
        </div>
        <div className="row">
          <Field label={t('assets.name')} error={error?.field('name')} required><input value={f.name} onChange={set('name')} maxLength={200} /></Field>
          <Field label={t('assets.nameAr')} error={error?.field('nameAr')}><input value={f.nameAr} onChange={set('nameAr')} maxLength={200} lang="ar" dir="rtl" /></Field>
        </div>
        <div className="row">
          <Field label={t('vendors.taxNumber')} error={error?.field('taxNumber')}><input value={f.taxNumber} onChange={set('taxNumber')} maxLength={30} /></Field>
          <Field label={t('vendors.registrationNumber')} error={error?.field('registrationNumber')}><input value={f.registrationNumber} onChange={set('registrationNumber')} maxLength={30} /></Field>
        </div>
        <div className="row">
          <Field label={t('vendors.email')} error={error?.field('email')}><input inputMode="email" value={f.email} onChange={set('email')} maxLength={320} /></Field>
          <Field label={t('vendors.phone')} error={error?.field('phone')}><input value={f.phone} onChange={set('phone')} maxLength={40} /></Field>
        </div>
        <Field label={t('vendors.address')} error={error?.field('address')}><input value={f.address} onChange={set('address')} maxLength={500} /></Field>
        <Field label={t('vendors.paymentTerms')} error={error?.field('paymentTerms')}><input value={f.paymentTerms} onChange={set('paymentTerms')} maxLength={200} /></Field>
        <Field label={t('vendors.notes')} error={error?.field('notes')}><textarea value={f.notes} onChange={set('notes')} rows={3} maxLength={2000} /></Field>
        <ErrorBanner error={error?.status === 400 ? null : save.error} />
        <Buttons onClose={onClose} busy={save.isPending} />
      </form>
    </Dialog>
  );
}

function VendorStatusDialog({ vendor, onClose }: { vendor: Vendor; onClose: () => void }) {
  const { t } = useTranslation();
  const statusLabel = useEnumLabel('vendorStatus');
  const [status, setStatus] = useState<VendorStatus>(vendor.status);
  const [reason, setReason] = useState(vendor.statusReason ?? '');
  const save = useSave<object>(`/vendors/${vendor.id}/status`, 'post', onClose);
  const error = asError(save.error);
  return (
    <Dialog title={t('vendors.changeStatusOf', { name: vendor.name })} onClose={onClose}>
      <p className="muted">{t('vendors.statusHint')}</p>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate({ status, reason: status === 'Active' ? null : reason.trim() || null, rowVersion: vendor.rowVersion }); }}>
        <Field label={t('assets.status')} error={error?.field('status')}>
          <select value={status} onChange={(e) => setStatus(e.target.value as VendorStatus)}>
            {(['Active', 'OnHold', 'Blocked'] as const).map((s) => <option key={s} value={s}>{statusLabel(s)}</option>)}
          </select>
        </Field>
        {status !== 'Active' ? <Field label={t('common.reason')} error={error?.field('reason')} required><input value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} /></Field> : null}
        <ErrorBanner error={error?.status === 400 ? null : save.error} />
        <Buttons onClose={onClose} busy={save.isPending} />
      </form>
    </Dialog>
  );
}

function ContactsDialog({ vendor, onClose }: { vendor: Vendor; onClose: () => void }) {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const existing = useVendorContacts(vendor.id);
  const [rows, setRows] = useState<{ name: string; role: string; email: string; phone: string; isPrimary: boolean }[] | null>(null);
  const shown = rows ?? existing.data?.map((c) => ({ name: c.name, role: c.role ?? '', email: c.email ?? '', phone: c.phone ?? '', isPrimary: c.isPrimary })) ?? [];
  const edit = can('vendors.manage');
  const save = useSave<object>(`/vendors/${vendor.id}/contacts`, 'put', onClose);
  const error = asError(save.error);
  const update = (i: number, patch: Partial<(typeof shown)[number]>) => setRows(shown.map((r, n) => (n === i ? { ...r, ...patch } : patch.isPrimary ? { ...r, isPrimary: false } : r)));
  return (
    <Dialog title={t('vendors.contactsOf', { name: vendor.name })} onClose={onClose} wide>
      {existing.isLoading ? <Loading /> : (
        <form onSubmit={(e) => { e.preventDefault(); save.mutate({ contacts: shown.map((r) => ({ name: r.name.trim(), role: r.role.trim() || null, email: r.email.trim() || null, phone: r.phone.trim() || null, isPrimary: r.isPrimary })) }); }}>
          {shown.length === 0 ? <p className="muted">{t('vendors.noContacts')}</p> : null}
          {shown.map((r, i) => (
            <fieldset key={i}>
              <legend>{t('vendors.contactNumber', { number: i + 1 })}</legend>
              <div className="row">
                <Field label={t('assets.name')}><input value={r.name} onChange={(e) => update(i, { name: e.target.value })} readOnly={!edit} maxLength={200} /></Field>
                <Field label={t('vendors.role')}><input value={r.role} onChange={(e) => update(i, { role: e.target.value })} readOnly={!edit} maxLength={100} /></Field>
              </div>
              <div className="row">
                <Field label={t('vendors.email')}><input inputMode="email" value={r.email} onChange={(e) => update(i, { email: e.target.value })} readOnly={!edit} /></Field>
                <Field label={t('vendors.phone')}><input value={r.phone} onChange={(e) => update(i, { phone: e.target.value })} readOnly={!edit} maxLength={40} /></Field>
              </div>
              {edit ? (
                <>
                  <label className="check"><input type="checkbox" checked={r.isPrimary} onChange={(e) => update(i, { isPrimary: e.target.checked })} /> {t('vendors.mainContact')}</label>{' '}
                  <button type="button" onClick={() => setRows(shown.filter((_, n) => n !== i))}>{t('common.delete')}</button>
                </>
              ) : r.isPrimary ? <Badge tone="ok">{t('vendors.mainContact')}</Badge> : null}
            </fieldset>
          ))}
          {error ? <p role="alert" className="error">{Object.values(error.fieldErrors).flat().join(' ')}</p> : null}
          <ErrorBanner error={error?.status === 400 ? null : save.error} />
          <div className="actions">
            <button type="button" onClick={onClose}>{edit ? t('common.cancel') : t('common.back')}</button>
            {edit ? <button type="button" onClick={() => setRows([...shown, { name: '', role: '', email: '', phone: '', isPrimary: shown.length === 0 }])}>{t('vendors.addContact')}</button> : null}
            {edit ? <button type="submit" className="primary" disabled={save.isPending}>{t('common.save')}</button> : null}
          </div>
        </form>
      )}
    </Dialog>
  );
}

// ---------------- contracts ----------------

function ContractsTab({ initialOpen }: { initialOpen: string | null }) {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const { date, num } = usePrefs();
  const statusLabel = useEnumLabel('contractStatus');
  const typeLabel = useEnumLabel('contractType');
  const vendors = useVendors({ pageSize: 100, includeArchived: false });
  const [vendorId, setVendorId] = useState('');
  const [status, setStatus] = useState('');
  const [q, setQ] = useState('');
  const [page, setPage] = useState(1);
  const [open, setOpen] = useState<string | 'new' | null>(initialOpen);
  const contracts = useContracts({ vendorId: vendorId || undefined, status: status || undefined, q: q.trim() || undefined, page, pageSize: 25 });
  return (
    <>
      <div className="filters">
        <Field label={t('vendors.vendor')}>
          <select value={vendorId} onChange={(e) => { setVendorId(e.target.value); setPage(1); }}>
            <option value="">{t('common.all')}</option>
            {vendors.data?.items.map((v) => <option key={v.id} value={v.id}>{v.code} · {v.name}</option>)}
          </select>
        </Field>
        <Field label={t('assets.status')}>
          <select value={status} onChange={(e) => { setStatus(e.target.value); setPage(1); }}>
            <option value="">{t('common.all')}</option>
            {(['Upcoming', 'Active', 'Expiring', 'Expired', 'Terminated'] as const).map((s) => <option key={s} value={s}>{statusLabel(s)}</option>)}
          </select>
        </Field>
        <Field label={t('vendors.search')}><input value={q} onChange={(e) => { setQ(e.target.value); setPage(1); }} /></Field>
        {can('vendors.manage') ? <button type="button" onClick={() => setOpen('new')}>{t('vendors.newContract')}</button> : null}
      </div>
      <QueryState query={contracts}>
        {contracts.data && contracts.data.items.length === 0 ? <p className="muted">{t('vendors.noContracts')}</p> : null}
        {contracts.data && contracts.data.items.length > 0 ? (
          <div className="table-wrap">
            <table>
              <caption className="visually-hidden">{t('vendors.tab.contracts')}</caption>
              <thead>
                <tr>
                  <th scope="col">{t('vendors.number')}</th>
                  <th scope="col">{t('vendors.contractTitle')}</th>
                  <th scope="col">{t('vendors.vendor')}</th>
                  <th scope="col">{t('vendors.period')}</th>
                  <th scope="col">{t('assets.status')}</th>
                  <th scope="col">{t('vendors.value')}</th>
                  <th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th>
                </tr>
              </thead>
              <tbody>
                {contracts.data.items.map((c) => (
                  <tr key={c.id}>
                    <td><bdi>{c.number}</bdi></td>
                    <td><bdi>{c.title}</bdi><div className="muted">{typeLabel(c.type)}</div></td>
                    <td><bdi>{c.vendorName}</bdi></td>
                    <td>{date(c.startOn)} – {date(c.endOn)}</td>
                    <td>
                      <Badge tone={statusTone(c.status)}>{statusLabel(c.status)}</Badge>
                      {c.status === 'Active' || c.status === 'Expiring' ? <div className="muted">{t('vendors.daysLeft', { count: c.daysLeft })}</div> : null}
                    </td>
                    <td>{c.value === null ? '—' : `${num(c.value)} ${c.currencyCode}`}</td>
                    <td className="actions"><button type="button" onClick={() => setOpen(c.id)}>{t('inventory.openTake')}</button></td>
                  </tr>
                ))}
              </tbody>
            </table>
            <Pager page={contracts.data.pageNumber} pageSize={contracts.data.pageSize} total={contracts.data.total} onChange={setPage} />
          </div>
        ) : null}
      </QueryState>
      {open ? <ContractDialog id={open === 'new' ? null : open} defaultVendor={vendorId} onClose={() => setOpen(null)} /> : null}
    </>
  );
}

const PRIORITIES = [1, 2, 3, 4];

function ContractDialog({ id, defaultVendor, onClose }: { id: string | null; defaultVendor: string; onClose: () => void }) {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const query = useContract(id ?? undefined);
  const manage = can('vendors.manage');
  if (id && (query.isLoading || !query.data)) return <Dialog title={t('vendors.contract')} onClose={onClose}><Loading /></Dialog>;
  return <ContractEditor contract={query.data ?? null} defaultVendor={defaultVendor} manage={manage} onClose={onClose} />;
}

function ContractEditor({ contract, defaultVendor, manage, onClose }: { contract: Contract | null; defaultVendor: string; manage: boolean; onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { date } = usePrefs();
  const refresh = useRefresh();
  const statusLabel = useEnumLabel('contractStatus');
  const typeLabel = useEnumLabel('contractType');
  const vendors = useVendors({ pageSize: 100, includeArchived: false });
  const [f, setF] = useState({
    vendorId: contract?.vendorId ?? defaultVendor, number: contract?.number ?? '', title: contract?.title ?? '', type: (contract?.type ?? 'Amc') as ContractType,
    startOn: contract?.startOn ?? new Date().toISOString().slice(0, 10), endOn: contract?.endOn ?? '', value: contract?.value?.toString() ?? '', currencyCode: contract?.currencyCode ?? 'SAR',
    renewalNoticeDays: String(contract?.renewalNoticeDays ?? 60), notes: contract?.notes ?? '',
  });
  const [terminating, setTerminating] = useState(false);
  const editable = manage && contract?.status !== 'Terminated';
  const save = useMutation({
    mutationFn: () => {
      const body = {
        vendorId: f.vendorId || null, number: f.number.trim(), title: f.title.trim(), type: f.type, startOn: f.startOn || null, endOn: f.endOn || null, value: f.value === '' ? null : Number(f.value),
        currencyCode: f.currencyCode.trim() || null, renewalNoticeDays: f.renewalNoticeDays === '' ? null : Number(f.renewalNoticeDays), notes: f.notes.trim() || null, rowVersion: contract?.rowVersion,
      };
      return contract ? api.put<Contract>(`/contracts/${contract.id}`, body) : api.post<Contract>('/contracts', body);
    },
    onSuccess: async (saved) => { await refresh(); if (!contract) onClose(); else void saved; },
  });
  const terminate = useMutation({
    mutationFn: (reason: string) => api.post(`/contracts/${contract!.id}/terminate`, { reason, rowVersion: contract!.rowVersion }),
    onSuccess: async () => { setTerminating(false); await refresh(); },
  });
  const error = asError(save.error);
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF({ ...f, [k]: e.target.value });

  return (
    <Dialog title={contract ? t('vendors.contractNumber', { number: contract.number }) : t('vendors.newContract')} onClose={onClose} wide>
      {contract ? (
        <p>
          <Badge tone={statusTone(contract.status)}>{statusLabel(contract.status)}</Badge>{' '}
          {contract.status === 'Active' || contract.status === 'Expiring' ? <span className="muted">{t('vendors.daysLeft', { count: contract.daysLeft })}</span> : null}
          {contract.terminatedAt ? <span className="muted"> · {date(contract.terminatedAt, true)} · <bdi>{contract.terminationReason}</bdi></span> : null}
        </p>
      ) : null}
      <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }}>
        <fieldset disabled={!editable}>
          <div className="row">
            <Field label={t('vendors.vendor')} error={error?.field('vendorId')} required>
              <select value={f.vendorId} onChange={set('vendorId')} disabled={!!contract}>
                <option value="">{t('common.choose')}</option>
                {vendors.data?.items.map((v) => <option key={v.id} value={v.id}>{v.code} · {v.name}</option>)}
                {contract && !vendors.data?.items.some((v) => v.id === contract.vendorId) ? <option value={contract.vendorId}>{contract.vendorName}</option> : null}
              </select>
            </Field>
            <Field label={t('vendors.number')} error={error?.field('number')} required><input value={f.number} onChange={set('number')} maxLength={50} /></Field>
          </div>
          <div className="row">
            <Field label={t('vendors.contractTitle')} error={error?.field('title')} required><input value={f.title} onChange={set('title')} maxLength={200} /></Field>
            <Field label={t('vendors.type')} error={error?.field('type')}>
              <select value={f.type} onChange={set('type')}>{(['Amc', 'Sla', 'Service', 'Supply', 'Other'] as const).map((x) => <option key={x} value={x}>{typeLabel(x)}</option>)}</select>
            </Field>
          </div>
          <div className="row">
            <Field label={t('vendors.startOn')} error={error?.field('startOn')} required><input type="date" value={f.startOn} onChange={set('startOn')} /></Field>
            <Field label={t('vendors.endOn')} error={error?.field('endOn')} required><input type="date" value={f.endOn} onChange={set('endOn')} /></Field>
            <Field label={t('vendors.renewalNotice')} hint={t('vendors.renewalNoticeHint')} error={error?.field('renewalNoticeDays')}><input type="number" min={0} max={365} value={f.renewalNoticeDays} onChange={set('renewalNoticeDays')} /></Field>
          </div>
          <div className="row">
            <Field label={t('vendors.value')} error={error?.field('value')}><input type="number" step="0.01" min="0" value={f.value} onChange={set('value')} /></Field>
            <Field label={t('vendors.currency')} error={error?.field('currencyCode')}><input value={f.currencyCode} onChange={set('currencyCode')} maxLength={3} /></Field>
          </div>
          <Field label={t('vendors.notes')} error={error?.field('notes')}><textarea value={f.notes} onChange={set('notes')} rows={2} maxLength={2000} /></Field>
        </fieldset>
        <ErrorBanner error={error?.status === 400 ? null : save.error} />
        <div className="actions">
          <button type="button" onClick={onClose}>{contract ? t('common.back') : t('common.cancel')}</button>
          {editable ? <button type="submit" className="primary" disabled={save.isPending}>{t('common.save')}</button> : null}
          {editable && contract ? <button type="button" onClick={() => setTerminating(true)}>{t('vendors.terminate')}</button> : null}
        </div>
      </form>
      {contract ? (
        <>
          <CoverageSection contract={contract} editable={editable} />
          <SlaSection contract={contract} editable={editable} />
          <DocumentsSection contract={contract} editable={editable} />
        </>
      ) : <p className="muted">{t('vendors.saveFirst')}</p>}
      {terminating ? (
        <ReasonDialog title={t('vendors.terminate')} label={t('common.reason')} confirmLabel={t('common.confirm')} busy={terminate.isPending} error={terminate.error}
          onCancel={() => setTerminating(false)} onConfirm={(reason) => terminate.mutate(reason)} />
      ) : null}
    </Dialog>
  );
}

function CoverageSection({ contract, editable }: { contract: Contract; editable: boolean }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { name } = usePrefs();
  const refresh = useRefresh();
  const kindLabel = useEnumLabel('coverageKind');
  const tree = useLocationTree();
  const classes = useAssetClasses();
  const [kind, setKind] = useState<CoverageKind>('Site');
  const [target, setTarget] = useState('');
  const [search, setSearch] = useState('');
  const assets = useAssetChoices(search, '');
  const sites = useMemo(() => (tree.data ?? []).filter((n) => n.type === 'Site'), [tree.data]);
  const save = useMutation({
    mutationFn: (coverage: { kind: CoverageKind; targetId: string }[]) => api.put(`/contracts/${contract.id}/coverage`, { coverage }),
    onSuccess: async () => { setTarget(''); await refresh(); },
  });
  const current = contract.coverage.map((c) => ({ kind: c.kind, targetId: c.targetId }));
  return (
    <fieldset>
      <legend>{t('vendors.coverage')}</legend>
      <p className="muted">{contract.coverage.length === 0 ? t('vendors.coverageAll') : t('vendors.coverageHint')}</p>
      <ul className="plain">
        {contract.coverage.map((c) => (
          <li key={`${c.kind}-${c.targetId}`}>
            <Badge tone="neutral">{kindLabel(c.kind)}</Badge> <bdi>{c.name ?? c.targetId}</bdi>{' '}
            {editable ? <button type="button" disabled={save.isPending} onClick={() => save.mutate(current.filter((x) => !(x.kind === c.kind && x.targetId === c.targetId)))}>{t('common.delete')}</button> : null}
          </li>
        ))}
      </ul>
      {editable ? (
        <div className="row">
          <Field label={t('vendors.coverageKind')}>
            <select value={kind} onChange={(e) => { setKind(e.target.value as CoverageKind); setTarget(''); }}>{(['Site', 'AssetClass', 'Asset'] as const).map((k) => <option key={k} value={k}>{kindLabel(k)}</option>)}</select>
          </Field>
          {kind === 'Asset' ? <Field label={t('pm.findAsset')}><input value={search} onChange={(e) => setSearch(e.target.value)} /></Field> : null}
          <Field label={t('vendors.coverageTarget')}>
            <select value={target} onChange={(e) => setTarget(e.target.value)}>
              <option value="">{t('common.choose')}</option>
              {kind === 'Site' ? sites.map((s) => <option key={s.id} value={s.id}>{s.code} · {name(s)}</option>) : null}
              {kind === 'AssetClass' ? classes.data?.filter((c) => !c.isArchived).map((c) => <option key={c.id} value={c.id}>{c.code} · {name(c)}</option>) : null}
              {kind === 'Asset' ? assets.data?.map((a) => <option key={a.id} value={a.id}>{a.code} · {a.name}</option>) : null}
            </select>
          </Field>
          <button type="button" disabled={!target || save.isPending || current.some((x) => x.kind === kind && x.targetId === target)} onClick={() => save.mutate([...current, { kind, targetId: target }])}>{t('vendors.addCoverage')}</button>
        </div>
      ) : null}
      <ErrorBanner error={save.error} />
    </fieldset>
  );
}

function SlaSection({ contract, editable }: { contract: Contract; editable: boolean }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const refresh = useRefresh();
  const prio = useEnumLabel('workPriority');
  const initial = (p: number, k: keyof ContractSla) => contract.sla.find((s) => s.priority === p)?.[k]?.toString() ?? '';
  const [rows, setRows] = useState<Record<number, { response: string; resolution: string }>>(
    Object.fromEntries(PRIORITIES.map((p) => [p, { response: initial(p, 'responseMinutes'), resolution: initial(p, 'resolutionMinutes') }])),
  );
  const save = useMutation({
    mutationFn: () => api.put(`/contracts/${contract.id}/sla`, {
      targets: PRIORITIES.filter((p) => rows[p].response !== '' || rows[p].resolution !== '').map((p) => ({ priority: p, responseMinutes: Number(rows[p].response) || null, resolutionMinutes: Number(rows[p].resolution) || null })),
    }),
    onSuccess: () => refresh(),
  });
  const error = asError(save.error);
  const names = ['Low', 'Medium', 'High', 'Urgent'];
  return (
    <fieldset>
      <legend>{t('vendors.sla')}</legend>
      <p className="muted">{t('vendors.slaHint')}</p>
      {PRIORITIES.map((p) => (
        <div className="sla-row" key={p}>
          <strong>{prio(names[p - 1])}</strong>
          <Field label={`${t('sla.response')} (${prio(names[p - 1])})`}><input type="number" min={1} value={rows[p].response} readOnly={!editable} onChange={(e) => setRows({ ...rows, [p]: { ...rows[p], response: e.target.value } })} /></Field>
          <Field label={`${t('sla.resolution')} (${prio(names[p - 1])})`}><input type="number" min={1} value={rows[p].resolution} readOnly={!editable} onChange={(e) => setRows({ ...rows, [p]: { ...rows[p], resolution: e.target.value } })} /></Field>
        </div>
      ))}
      {error ? <p role="alert" className="error">{Object.values(error.fieldErrors).flat().join(' ')}</p> : null}
      <ErrorBanner error={error?.status === 400 ? null : save.error} />
      {editable ? <button type="button" disabled={save.isPending} onClick={() => save.mutate()}>{t('vendors.saveSla')}</button> : null}
    </fieldset>
  );
}

function DocumentsSection({ contract, editable }: { contract: Contract; editable: boolean }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { date, lang } = usePrefs();
  const refresh = useRefresh();
  const kindLabel = useEnumLabel('contractDocKind');
  const docs = useContractDocuments(contract.id);
  const [file, setFile] = useState<File | null>(null);
  const [kind, setKind] = useState<ContractDocumentKind>('Contract');
  const [description, setDescription] = useState('');
  const [removing, setRemoving] = useState<string | null>(null);
  const [downloadError, setDownloadError] = useState<unknown>(null);
  const upload = useMutation({
    mutationFn: () => {
      const form = new FormData();
      form.append('file', file!);
      form.append('kind', kind);
      if (description.trim()) form.append('description', description.trim());
      return api.upload(`/contracts/${contract.id}/documents`, form);
    },
    onSuccess: async () => { setFile(null); setDescription(''); await refresh(); },
  });
  const remove = useMutation({
    mutationFn: (reason: string) => api.post(`/contract-documents/${removing}/delete`, { reason }),
    onSuccess: async () => { setRemoving(null); await refresh(); },
  });
  const download = async (id: string, fallback: string) => {
    setDownloadError(null);
    try {
      const { blob, fileName } = await api.blob(`/contract-documents/${id}/download`);
      downloadBlob(fileName ?? fallback, blob);
    } catch (e) {
      setDownloadError(e);
    }
  };
  return (
    <fieldset>
      <legend>{t('vendors.documents')}</legend>
      {editable ? (
        <form className="inline-form" onSubmit={(e) => { e.preventDefault(); upload.mutate(); }} aria-label={t('documents.upload')}>
          <Field label={t('documents.file')}><input type="file" onChange={(e) => setFile(e.target.files?.[0] ?? null)} /></Field>
          <Field label={t('documents.kind')}>
            <select value={kind} onChange={(e) => setKind(e.target.value as ContractDocumentKind)}>{(['Contract', 'Amendment', 'Certificate', 'Insurance', 'Other'] as const).map((k) => <option key={k} value={k}>{kindLabel(k)}</option>)}</select>
          </Field>
          <Field label={t('inventory.description')}><input value={description} onChange={(e) => setDescription(e.target.value)} maxLength={500} /></Field>
          <ErrorBanner error={upload.error} />
          <button type="submit" disabled={!file || upload.isPending}>{t('documents.upload')}</button>
        </form>
      ) : null}
      <ErrorBanner error={downloadError} />
      <QueryState query={docs}>
        {docs.data && docs.data.length === 0 ? <p className="muted">{t('vendors.noDocuments')}</p> : null}
        <ul className="plain">
          {docs.data?.map((d) => (
            <li key={d.id}>
              <Badge tone="neutral">{kindLabel(d.kind)}</Badge> <bdi>{d.fileName}</bdi> <span className="muted">· {formatBytes(d.sizeBytes, lang)} · {date(d.uploadedAt, true)}{d.description ? ` · ${d.description}` : ''}</span>{' '}
              <button type="button" onClick={() => void download(d.id, d.fileName)}>{t('documents.download')}</button>{' '}
              {editable ? <button type="button" onClick={() => setRemoving(d.id)}>{t('common.delete')}</button> : null}
            </li>
          ))}
        </ul>
      </QueryState>
      {removing ? <ReasonDialog title={t('common.delete')} label={t('common.reason')} confirmLabel={t('common.confirm')} busy={remove.isPending} error={remove.error} onCancel={() => setRemoving(null)} onConfirm={(r) => remove.mutate(r)} /> : null}
    </fieldset>
  );
}
