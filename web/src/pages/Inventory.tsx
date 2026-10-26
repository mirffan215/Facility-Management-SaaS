import { useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import { useItems, useMovements, usePermissions, useStock, useStockTake, useStockTakes, useStores } from '../api/hooks';
import type { Item, Movement, StockLevel, StockTake, Store } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Dialog, ItemPicker, StoreSelect } from '../components/InventoryBits';
import { Badge, ErrorBanner, Field, Loading, Pager, QueryState, ReasonDialog, useEnumLabel } from '../components/ui';
import { usePrefs } from '../lib/prefs';

const TABS = ['stock', 'items', 'stores', 'takes'] as const;

export function InventoryPage() {
  const { t } = useTranslation();
  const [params, setParams] = useSearchParams();
  const tab = TABS.find((x) => x === params.get('tab')) ?? 'stock';
  return (
    <section>
      <h1>{t('inventory.title')}</h1>
      <p className="muted">{t('inventory.intro')}</p>
      <div className="tabs" role="tablist" aria-label={t('inventory.sections')}>
        {TABS.map((x) => (
          <button key={x} role="tab" type="button" aria-selected={tab === x} className={tab === x ? 'active' : ''} onClick={() => setParams({ tab: x })}>
            {t(`inventory.tab.${x}`)}
          </button>
        ))}
      </div>
      <div role="tabpanel">{tab === 'stock' ? <StockTab /> : tab === 'items' ? <ItemsTab /> : tab === 'stores' ? <StoresTab /> : <TakesTab />}</div>
    </section>
  );
}

function useRefresh() {
  const client = useQueryClient();
  return () => client.invalidateQueries({ queryKey: ['inventory'] });
}

const asError = (e: unknown) => (e instanceof ApiError ? e : null);

// ---------------- stock ----------------

type StockDialog = { kind: 'receive' | 'transfer' | 'adjust'; level?: StockLevel } | { kind: 'reorder'; level: StockLevel } | { kind: 'history'; level: StockLevel };

function StockTab() {
  const { t } = useTranslation();
  const { num } = usePrefs();
  const { can } = usePermissions();
  const [params] = useSearchParams();
  const [storeId, setStoreId] = useState('');
  const [q, setQ] = useState('');
  const [lowOnly, setLowOnly] = useState(params.get('low') === '1');
  const [page, setPage] = useState(1);
  const [dialog, setDialog] = useState<StockDialog | null>(null);
  const stock = useStock({ storeId: storeId || undefined, q: q.trim() || undefined, lowOnly, page, pageSize: 25 });

  return (
    <>
      <div className="filters">
        <StoreSelect value={storeId} onChange={(v) => { setStoreId(v); setPage(1); }} allowAll />
        <Field label={t('inventory.search')}><input value={q} onChange={(e) => { setQ(e.target.value); setPage(1); }} /></Field>
        <label className="check"><input type="checkbox" checked={lowOnly} onChange={(e) => { setLowOnly(e.target.checked); setPage(1); }} /> {t('inventory.lowOnly')}</label>
      </div>
      <div className="toolbar">
        {can('inventory.manage') ? <button type="button" onClick={() => setDialog({ kind: 'receive' })}>{t('inventory.receive')}</button> : null}
        {can('inventory.manage') ? <button type="button" onClick={() => setDialog({ kind: 'transfer' })}>{t('inventory.transfer')}</button> : null}
        {can('inventory.approve') ? <button type="button" onClick={() => setDialog({ kind: 'adjust' })}>{t('inventory.adjust')}</button> : null}
      </div>
      <QueryState query={stock}>
        {stock.data && stock.data.items.length === 0 ? <p className="muted">{lowOnly ? t('inventory.noLow') : t('inventory.noStock')}</p> : null}
        {stock.data && stock.data.items.length > 0 ? (
          <div className="table-wrap">
            <table>
              <caption className="visually-hidden">{t('inventory.tab.stock')}</caption>
              <thead>
                <tr>
                  <th scope="col">{t('inventory.item')}</th>
                  <th scope="col">{t('inventory.store')}</th>
                  <th scope="col">{t('inventory.onHand')}</th>
                  <th scope="col">{t('inventory.reserved')}</th>
                  <th scope="col">{t('inventory.available')}</th>
                  <th scope="col">{t('inventory.avgCost')}</th>
                  <th scope="col">{t('inventory.value')}</th>
                  <th scope="col">{t('inventory.reorder')}</th>
                  <th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th>
                </tr>
              </thead>
              <tbody>
                {stock.data.items.map((l) => (
                  <tr key={`${l.storeId}-${l.itemId}`}>
                    <td><bdi>{l.itemCode}</bdi> <bdi>{l.itemName}</bdi></td>
                    <td><bdi>{l.storeName}</bdi></td>
                    <td>{num(l.onHand)} {l.unit}</td>
                    <td>{num(l.reserved)}</td>
                    <td>{l.isLow ? <Badge tone="bad">{num(l.available)} · {t('inventory.low')}</Badge> : num(l.available)}</td>
                    <td>{num(l.averageCost)}</td>
                    <td>{num(l.value)}</td>
                    <td>
                      {l.minQty === null ? <span className="muted">{t('common.notSet')}</span> : <>{num(l.minQty)}{l.maxQty !== null ? ` – ${num(l.maxQty)}` : ''}</>}
                      {l.isLow && l.suggestedOrder > 0 ? <div className="muted">{t('inventory.suggested', { quantity: num(l.suggestedOrder) })}</div> : null}
                    </td>
                    <td className="actions">
                      <button type="button" onClick={() => setDialog({ kind: 'history', level: l })}>{t('inventory.history')}</button>
                      {can('inventory.manage') ? <button type="button" onClick={() => setDialog({ kind: 'reorder', level: l })}>{t('inventory.setReorder')}</button> : null}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            <Pager page={stock.data.pageNumber} pageSize={stock.data.pageSize} total={stock.data.total} onChange={setPage} />
          </div>
        ) : null}
      </QueryState>
      {dialog?.kind === 'receive' ? <ReceiveDialog level={dialog.level} onClose={() => setDialog(null)} /> : null}
      {dialog?.kind === 'transfer' ? <TransferDialog level={dialog.level} onClose={() => setDialog(null)} /> : null}
      {dialog?.kind === 'adjust' ? <AdjustDialog level={dialog.level} onClose={() => setDialog(null)} /> : null}
      {dialog?.kind === 'reorder' ? <ReorderDialog level={dialog.level} onClose={() => setDialog(null)} /> : null}
      {dialog?.kind === 'history' ? <HistoryDialog level={dialog.level} onClose={() => setDialog(null)} /> : null}
    </>
  );
}

function useStockAction<T>(path: string, method: 'post' | 'put', onDone: () => void) {
  const { api } = useAuth();
  const refresh = useRefresh();
  return useMutation({
    mutationFn: (body: T) => (method === 'post' ? api.post(path, body) : api.put(path, body)),
    onSuccess: async () => { await refresh(); onDone(); },
  });
}

function Actions({ onClose, busy }: { onClose: () => void; busy: boolean }) {
  const { t } = useTranslation();
  return (
    <div className="actions">
      <button type="button" onClick={onClose}>{t('common.cancel')}</button>
      <button type="submit" className="primary" disabled={busy}>{t('common.save')}</button>
    </div>
  );
}

function ReceiveDialog({ level, onClose }: { level?: StockLevel; onClose: () => void }) {
  const { t } = useTranslation();
  const [storeId, setStoreId] = useState(level?.storeId ?? '');
  const [item, setItem] = useState<{ id: string; label: string } | null>(level ? { id: level.itemId, label: `${level.itemCode} ${level.itemName}` } : null);
  const [quantity, setQuantity] = useState('');
  const [unitCost, setUnitCost] = useState(level ? String(level.averageCost) : '');
  const [reference, setReference] = useState('');
  const [note, setNote] = useState('');
  const save = useStockAction<object>('/inventory/stock/receive', 'post', onClose);
  const error = asError(save.error);
  return (
    <Dialog title={t('inventory.receive')} onClose={onClose}>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate({ storeId: storeId || null, itemId: item?.id ?? null, quantity: quantity ? Number(quantity) : null, unitCost: unitCost ? Number(unitCost) : null, reference: reference.trim() || null, note: note.trim() || null }); }}>
        <StoreSelect value={storeId} onChange={setStoreId} error={error?.field('storeId')} />
        <ItemPicker value={item} onChange={(i) => setItem(i ? { id: i.id, label: `${i.code} ${i.name}` } : null)} error={error?.field('itemId')} />
        <div className="row">
          <Field label={t('inventory.quantity')} error={error?.field('quantity')} required><input type="number" step="any" min="0" value={quantity} onChange={(e) => setQuantity(e.target.value)} /></Field>
          <Field label={t('inventory.unitCost')} hint={t('inventory.unitCostHint')} error={error?.field('unitCost')} required><input type="number" step="any" min="0" value={unitCost} onChange={(e) => setUnitCost(e.target.value)} /></Field>
        </div>
        <div className="row">
          <Field label={t('inventory.reference')} hint={t('inventory.referenceHint')} error={error?.field('reference')}><input value={reference} onChange={(e) => setReference(e.target.value)} maxLength={100} /></Field>
          <Field label={t('inventory.note')} error={error?.field('note')}><input value={note} onChange={(e) => setNote(e.target.value)} maxLength={500} /></Field>
        </div>
        <ErrorBanner error={error?.status === 400 ? null : save.error} />
        <Actions onClose={onClose} busy={save.isPending} />
      </form>
    </Dialog>
  );
}

function TransferDialog({ level, onClose }: { level?: StockLevel; onClose: () => void }) {
  const { t } = useTranslation();
  const [fromStoreId, setFrom] = useState(level?.storeId ?? '');
  const [toStoreId, setTo] = useState('');
  const [item, setItem] = useState<{ id: string; label: string } | null>(level ? { id: level.itemId, label: `${level.itemCode} ${level.itemName}` } : null);
  const [quantity, setQuantity] = useState('');
  const [note, setNote] = useState('');
  const save = useStockAction<object>('/inventory/stock/transfer', 'post', onClose);
  const error = asError(save.error);
  return (
    <Dialog title={t('inventory.transfer')} onClose={onClose}>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate({ fromStoreId: fromStoreId || null, toStoreId: toStoreId || null, itemId: item?.id ?? null, quantity: quantity ? Number(quantity) : null, note: note.trim() || null }); }}>
        <div className="row">
          <StoreSelect value={fromStoreId} onChange={setFrom} label={t('inventory.fromStore')} error={error?.field('fromStoreId')} />
          <StoreSelect value={toStoreId} onChange={setTo} label={t('inventory.toStore')} error={error?.field('toStoreId')} exclude={fromStoreId} />
        </div>
        <ItemPicker value={item} onChange={(i) => setItem(i ? { id: i.id, label: `${i.code} ${i.name}` } : null)} error={error?.field('itemId')} />
        <div className="row">
          <Field label={t('inventory.quantity')} error={error?.field('quantity')} required><input type="number" step="any" min="0" value={quantity} onChange={(e) => setQuantity(e.target.value)} /></Field>
          <Field label={t('inventory.note')} error={error?.field('note')}><input value={note} onChange={(e) => setNote(e.target.value)} maxLength={500} /></Field>
        </div>
        <ErrorBanner error={error?.status === 400 ? null : save.error} />
        <Actions onClose={onClose} busy={save.isPending} />
      </form>
    </Dialog>
  );
}

function AdjustDialog({ level, onClose }: { level?: StockLevel; onClose: () => void }) {
  const { t } = useTranslation();
  const [storeId, setStoreId] = useState(level?.storeId ?? '');
  const [item, setItem] = useState<{ id: string; label: string } | null>(level ? { id: level.itemId, label: `${level.itemCode} ${level.itemName}` } : null);
  const [delta, setDelta] = useState('');
  const [reason, setReason] = useState('');
  const save = useStockAction<object>('/inventory/stock/adjust', 'post', onClose);
  const error = asError(save.error);
  return (
    <Dialog title={t('inventory.adjust')} onClose={onClose}>
      <p className="muted">{t('inventory.adjustHint')}</p>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate({ storeId: storeId || null, itemId: item?.id ?? null, delta: delta ? Number(delta) : null, reason: reason.trim() || null }); }}>
        <StoreSelect value={storeId} onChange={setStoreId} error={error?.field('storeId')} />
        <ItemPicker value={item} onChange={(i) => setItem(i ? { id: i.id, label: `${i.code} ${i.name}` } : null)} error={error?.field('itemId')} />
        <div className="row">
          <Field label={t('inventory.delta')} hint={t('inventory.deltaHint')} error={error?.field('delta')} required><input type="number" step="any" value={delta} onChange={(e) => setDelta(e.target.value)} /></Field>
          <Field label={t('common.reason')} error={error?.field('reason')} required><input value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} /></Field>
        </div>
        <ErrorBanner error={error?.status === 400 ? null : save.error} />
        <Actions onClose={onClose} busy={save.isPending} />
      </form>
    </Dialog>
  );
}

function ReorderDialog({ level, onClose }: { level: StockLevel; onClose: () => void }) {
  const { t } = useTranslation();
  const [min, setMin] = useState(level.minQty?.toString() ?? '');
  const [max, setMax] = useState(level.maxQty?.toString() ?? '');
  const save = useStockAction<object>('/inventory/stock/levels', 'put', onClose);
  const error = asError(save.error);
  return (
    <Dialog title={t('inventory.setReorderFor', { item: `${level.itemCode} · ${level.storeName}` })} onClose={onClose}>
      <p className="muted">{t('inventory.reorderHint')}</p>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate({ storeId: level.storeId, itemId: level.itemId, minQty: min === '' ? null : Number(min), maxQty: max === '' ? null : Number(max), rowVersion: level.rowVersion }); }}>
        <div className="row">
          <Field label={t('inventory.minQty')} error={error?.field('minQty')}><input type="number" step="any" min="0" value={min} onChange={(e) => setMin(e.target.value)} autoFocus /></Field>
          <Field label={t('inventory.maxQty')} error={error?.field('maxQty')}><input type="number" step="any" min="0" value={max} onChange={(e) => setMax(e.target.value)} /></Field>
        </div>
        <ErrorBanner error={error?.status === 400 ? null : save.error} />
        <Actions onClose={onClose} busy={save.isPending} />
      </form>
    </Dialog>
  );
}

function HistoryDialog({ level, onClose }: { level: StockLevel; onClose: () => void }) {
  const { t } = useTranslation();
  const { date, num } = usePrefs();
  const typeLabel = useEnumLabel('movementType');
  const [page, setPage] = useState(1);
  const moves = useMovements({ storeId: level.storeId, itemId: level.itemId, page, pageSize: 10 });
  return (
    <Dialog title={t('inventory.historyFor', { item: `${level.itemCode} · ${level.storeName}` })} onClose={onClose} wide>
      <QueryState query={moves}>
        {moves.data ? <MovementTable moves={moves.data.items} typeLabel={typeLabel} date={date} num={num} /> : null}
        {moves.data ? <Pager page={moves.data.pageNumber} pageSize={moves.data.pageSize} total={moves.data.total} onChange={setPage} /> : null}
      </QueryState>
      <div className="actions"><button type="button" className="primary" onClick={onClose}>{t('common.back')}</button></div>
    </Dialog>
  );
}

function MovementTable({ moves, typeLabel, date, num }: { moves: Movement[]; typeLabel: (v: string) => string; date: (v: string, withTime?: boolean) => string; num: (v: number) => string }) {
  const { t } = useTranslation();
  return (
    <div className="table-wrap">
      <table>
        <caption className="visually-hidden">{t('inventory.movements')}</caption>
        <thead>
          <tr>
            <th scope="col">{t('inventory.when')}</th>
            <th scope="col">{t('inventory.movement')}</th>
            <th scope="col">{t('inventory.quantity')}</th>
            <th scope="col">{t('inventory.unitCost')}</th>
            <th scope="col">{t('inventory.balance')}</th>
            <th scope="col">{t('inventory.by')}</th>
            <th scope="col">{t('inventory.why')}</th>
          </tr>
        </thead>
        <tbody>
          {moves.map((m) => (
            <tr key={m.id}>
              <td>{date(m.at, true)}</td>
              <td>{typeLabel(m.type)}</td>
              <td>{m.quantity > 0 ? '+' : ''}{num(m.quantity)}</td>
              <td>{num(m.unitCost)}</td>
              <td>{num(m.balanceAfter)}</td>
              <td><bdi>{m.byName ?? '—'}</bdi></td>
              <td>{m.reason ?? m.reference ?? ''}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

// ---------------- items ----------------

function ItemsTab() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { can } = usePermissions();
  const { name, num } = usePrefs();
  const refresh = useRefresh();
  const [q, setQ] = useState('');
  const [page, setPage] = useState(1);
  const [editing, setEditing] = useState<Item | 'new' | null>(null);
  const items = useItems({ q: q.trim() || undefined, includeArchived: true, page, pageSize: 25 });
  const archive = useMutation({
    mutationFn: (i: Item) => api.post(`/inventory/items/${i.id}/${i.isArchived ? 'restore' : 'archive'}`, { rowVersion: i.rowVersion }),
    onSuccess: () => refresh(),
  });
  return (
    <>
      <div className="toolbar">
        <Field label={t('inventory.search')}><input value={q} onChange={(e) => { setQ(e.target.value); setPage(1); }} /></Field>
        {can('inventory.manage') ? <button type="button" onClick={() => setEditing('new')}>{t('inventory.newItem')}</button> : null}
      </div>
      <ErrorBanner error={archive.error} />
      <QueryState query={items}>
        {items.data && items.data.items.length === 0 ? <p className="muted">{t('inventory.noItems')}</p> : null}
        {items.data && items.data.items.length > 0 ? (
          <div className="table-wrap">
            <table>
              <caption className="visually-hidden">{t('inventory.tab.items')}</caption>
              <thead>
                <tr>
                  <th scope="col">{t('inventory.code')}</th>
                  <th scope="col">{t('assets.name')}</th>
                  <th scope="col">{t('inventory.unit')}</th>
                  <th scope="col">{t('inventory.onHand')}</th>
                  <th scope="col">{t('inventory.available')}</th>
                  <th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th>
                </tr>
              </thead>
              <tbody>
                {items.data.items.map((i) => (
                  <tr key={i.id}>
                    <td><bdi>{i.code}</bdi></td>
                    <td><bdi>{name(i)}</bdi> {i.isArchived ? <Badge tone="neutral">{t('common.archived')}</Badge> : null}{i.barcode ? <div className="muted"><bdi>{i.barcode}</bdi></div> : null}</td>
                    <td>{i.unit}</td>
                    <td>{num(i.onHand)}</td>
                    <td>{num(i.available)}</td>
                    <td className="actions">
                      {can('inventory.manage') ? (
                        <>
                          <button type="button" onClick={() => setEditing(i)}>{t('common.edit')}</button>
                          <button type="button" disabled={archive.isPending} onClick={() => archive.mutate(i)}>{i.isArchived ? t('common.restore') : t('common.archive')}</button>
                        </>
                      ) : null}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            <Pager page={items.data.pageNumber} pageSize={items.data.pageSize} total={items.data.total} onChange={setPage} />
          </div>
        ) : null}
      </QueryState>
      {editing ? <ItemDialog item={editing === 'new' ? null : editing} onClose={() => setEditing(null)} /> : null}
    </>
  );
}

function ItemDialog({ item, onClose }: { item: Item | null; onClose: () => void }) {
  const { t } = useTranslation();
  const [code, setCode] = useState(item?.code ?? '');
  const [nameEn, setNameEn] = useState(item?.name ?? '');
  const [nameAr, setNameAr] = useState(item?.nameAr ?? '');
  const [description, setDescription] = useState(item?.description ?? '');
  const [unit, setUnit] = useState(item?.unit ?? 'pcs');
  const [barcode, setBarcode] = useState(item?.barcode ?? '');
  const save = useStockAction<object>(item ? `/inventory/items/${item.id}` : '/inventory/items', item ? 'put' : 'post', onClose);
  const error = asError(save.error);
  return (
    <Dialog title={item ? t('inventory.editItem') : t('inventory.newItem')} onClose={onClose} wide>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate({ code: code.trim(), name: nameEn.trim(), nameAr: nameAr.trim() || null, description: description.trim() || null, unit: unit.trim() || null, barcode: barcode.trim() || null, rowVersion: item?.rowVersion }); }}>
        <div className="row">
          <Field label={t('inventory.code')} hint={t('inventory.codeHint')} error={error?.field('code')} required><input value={code} onChange={(e) => setCode(e.target.value)} maxLength={50} autoFocus /></Field>
          <Field label={t('inventory.unit')} hint={t('inventory.unitHint')} error={error?.field('unit')}><input value={unit} onChange={(e) => setUnit(e.target.value)} maxLength={20} /></Field>
        </div>
        <div className="row">
          <Field label={t('assets.name')} error={error?.field('name')} required><input value={nameEn} onChange={(e) => setNameEn(e.target.value)} maxLength={200} /></Field>
          <Field label={t('assets.nameAr')} error={error?.field('nameAr')}><input value={nameAr} onChange={(e) => setNameAr(e.target.value)} maxLength={200} lang="ar" dir="rtl" /></Field>
        </div>
        <Field label={t('inventory.description')} error={error?.field('description')}><textarea value={description} onChange={(e) => setDescription(e.target.value)} rows={3} maxLength={2000} /></Field>
        <Field label={t('inventory.barcode')} error={error?.field('barcode')}><input value={barcode} onChange={(e) => setBarcode(e.target.value)} maxLength={100} /></Field>
        <ErrorBanner error={error?.status === 400 ? null : save.error} />
        <Actions onClose={onClose} busy={save.isPending} />
      </form>
    </Dialog>
  );
}

// ---------------- stores ----------------

function StoresTab() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { can } = usePermissions();
  const { name } = usePrefs();
  const refresh = useRefresh();
  const stores = useStores(true);
  const [editing, setEditing] = useState<Store | 'new' | null>(null);
  const archive = useMutation({
    mutationFn: (s: Store) => api.post(`/inventory/stores/${s.id}/${s.isArchived ? 'restore' : 'archive'}`, { rowVersion: s.rowVersion }),
    onSuccess: () => refresh(),
  });
  return (
    <>
      <div className="toolbar">
        <h2>{t('inventory.tab.stores')}</h2>
        {can('inventory.manage') ? <button type="button" onClick={() => setEditing('new')}>{t('inventory.newStore')}</button> : null}
      </div>
      <ErrorBanner error={archive.error} />
      <QueryState query={stores}>
        {stores.data && stores.data.length === 0 ? <p className="muted">{t('inventory.noStores')}</p> : null}
        <ul className="plain">
          {stores.data?.map((s) => (
            <li key={s.id}>
              <strong><bdi>{s.code}</bdi></strong> <bdi>{name(s)}</bdi> {s.isArchived ? <Badge tone="neutral">{t('common.archived')}</Badge> : null}{' '}
              {can('inventory.manage') ? (
                <>
                  <button type="button" onClick={() => setEditing(s)}>{t('common.edit')}</button>{' '}
                  <button type="button" disabled={archive.isPending} onClick={() => archive.mutate(s)}>{s.isArchived ? t('common.restore') : t('common.archive')}</button>
                </>
              ) : null}
            </li>
          ))}
        </ul>
      </QueryState>
      {editing ? <StoreDialog store={editing === 'new' ? null : editing} onClose={() => setEditing(null)} /> : null}
    </>
  );
}

function StoreDialog({ store, onClose }: { store: Store | null; onClose: () => void }) {
  const { t } = useTranslation();
  const [code, setCode] = useState(store?.code ?? '');
  const [nameEn, setNameEn] = useState(store?.name ?? '');
  const [nameAr, setNameAr] = useState(store?.nameAr ?? '');
  const save = useStockAction<object>(store ? `/inventory/stores/${store.id}` : '/inventory/stores', store ? 'put' : 'post', onClose);
  const error = asError(save.error);
  return (
    <Dialog title={store ? t('inventory.editStore') : t('inventory.newStore')} onClose={onClose}>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate({ code: code.trim(), name: nameEn.trim(), nameAr: nameAr.trim() || null, siteId: store?.siteId ?? null, rowVersion: store?.rowVersion }); }}>
        <Field label={t('inventory.code')} hint={t('inventory.codeHint')} error={error?.field('code')} required><input value={code} onChange={(e) => setCode(e.target.value)} maxLength={50} autoFocus /></Field>
        <div className="row">
          <Field label={t('assets.name')} error={error?.field('name')} required><input value={nameEn} onChange={(e) => setNameEn(e.target.value)} maxLength={200} /></Field>
          <Field label={t('assets.nameAr')} error={error?.field('nameAr')}><input value={nameAr} onChange={(e) => setNameAr(e.target.value)} maxLength={200} lang="ar" dir="rtl" /></Field>
        </div>
        <ErrorBanner error={error?.status === 400 ? null : save.error} />
        <Actions onClose={onClose} busy={save.isPending} />
      </form>
    </Dialog>
  );
}

// ---------------- stock takes ----------------

function TakesTab() {
  const { t } = useTranslation();
  const { date, num } = usePrefs();
  const { can } = usePermissions();
  const takes = useStockTakes();
  const statusLabel = useEnumLabel('takeStatus');
  const [open, setOpen] = useState<string | null>(null);
  const [starting, setStarting] = useState(false);
  return (
    <>
      <div className="toolbar">
        <h2>{t('inventory.tab.takes')}</h2>
        {can('inventory.manage') ? <button type="button" onClick={() => setStarting(true)}>{t('inventory.startTake')}</button> : null}
      </div>
      <p className="muted">{t('inventory.takesHint')}</p>
      <QueryState query={takes}>
        {takes.data && takes.data.length === 0 ? <p className="muted">{t('inventory.noTakes')}</p> : null}
        {takes.data && takes.data.length > 0 ? (
          <div className="table-wrap">
            <table>
              <caption className="visually-hidden">{t('inventory.tab.takes')}</caption>
              <thead>
                <tr>
                  <th scope="col">{t('inventory.store')}</th>
                  <th scope="col">{t('assets.status')}</th>
                  <th scope="col">{t('inventory.started')}</th>
                  <th scope="col">{t('inventory.counted')}</th>
                  <th scope="col">{t('inventory.differences')}</th>
                  <th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th>
                </tr>
              </thead>
              <tbody>
                {takes.data.map((x) => (
                  <tr key={x.id}>
                    <td><bdi>{x.storeName}</bdi></td>
                    <td><Badge tone={x.status === 'Approved' ? 'ok' : x.status === 'Cancelled' ? 'neutral' : 'warn'}>{statusLabel(x.status)}</Badge></td>
                    <td>{date(x.startedAt, true)}<div className="muted"><bdi>{x.startedByName}</bdi></div></td>
                    <td>{num(x.counted)} / {num(x.lines)}</td>
                    <td>{num(x.differences)}</td>
                    <td className="actions"><button type="button" onClick={() => setOpen(x.id)}>{t('inventory.openTake')}</button></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : null}
      </QueryState>
      {starting ? <StartTakeDialog onClose={() => setStarting(false)} onStarted={(id) => { setStarting(false); setOpen(id); }} /> : null}
      {open ? <TakeDialog id={open} onClose={() => setOpen(null)} /> : null}
    </>
  );
}

function StartTakeDialog({ onClose, onStarted }: { onClose: () => void; onStarted: (id: string) => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const refresh = useRefresh();
  const [storeId, setStoreId] = useState('');
  const [note, setNote] = useState('');
  const save = useMutation({
    mutationFn: () => api.post<StockTake>('/inventory/stock-takes', { storeId: storeId || null, note: note.trim() || null }),
    onSuccess: async (take) => { await refresh(); onStarted(take.id); },
  });
  const error = asError(save.error);
  return (
    <Dialog title={t('inventory.startTake')} onClose={onClose}>
      <p className="muted">{t('inventory.startTakeHint')}</p>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }}>
        <StoreSelect value={storeId} onChange={setStoreId} error={error?.field('storeId')} />
        <Field label={t('inventory.note')} error={error?.field('note')}><input value={note} onChange={(e) => setNote(e.target.value)} maxLength={500} /></Field>
        <ErrorBanner error={error?.status === 400 ? null : save.error} />
        <Actions onClose={onClose} busy={save.isPending} />
      </form>
    </Dialog>
  );
}

function TakeDialog({ id, onClose }: { id: string; onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { num, date } = usePrefs();
  const { can } = usePermissions();
  const refresh = useRefresh();
  const take = useStockTake(id);
  const statusLabel = useEnumLabel('takeStatus');
  const [entered, setEntered] = useState<Record<string, string>>({});
  const [extra, setExtra] = useState<{ id: string; label: string } | null>(null);
  const [extraQty, setExtraQty] = useState('');
  const [asking, setAsking] = useState<'cancel' | 'back' | null>(null);

  const done = async () => { await refresh(); };
  const saveCounts = useMutation({
    mutationFn: (counts: { itemId: string; countedQty: number | null }[]) => api.put(`/inventory/stock-takes/${id}/counts`, { counts }),
    onSuccess: async () => { setEntered({}); setExtra(null); setExtraQty(''); await done(); },
  });
  const act = useMutation({
    mutationFn: ({ action, body }: { action: string; body?: object }) => api.post(`/inventory/stock-takes/${id}/${action}`, body ?? {}),
    onSuccess: async () => { setAsking(null); await done(); },
  });

  if (take.isLoading || !take.data) return <Dialog title={t('inventory.openTake')} onClose={onClose}><Loading /></Dialog>;
  const x = take.data;
  const counting = x.status === 'Counting';
  const pending = Object.entries(entered).map(([itemId, v]) => ({ itemId, countedQty: v === '' ? null : Number(v) }));
  const withExtra = extra && extraQty !== '' ? [...pending, { itemId: extra.id, countedQty: Number(extraQty) }] : pending;
  const error = asError(saveCounts.error) ?? asError(act.error);

  return (
    <Dialog title={t('inventory.takeOf', { store: x.storeName })} onClose={onClose} wide>
      <p>
        <Badge tone={x.status === 'Approved' ? 'ok' : x.status === 'Cancelled' ? 'neutral' : 'warn'}>{statusLabel(x.status)}</Badge>{' '}
        <span className="muted">{t('inventory.takeSummary', { counted: num(x.counted), lines: num(x.lines), differences: num(x.differences), value: num(x.netVarianceValue) })}</span>
      </p>
      {x.note ? <p className="preline">{x.note}</p> : null}
      <p className="muted">
        {t('inventory.startedBy', { name: x.startedByName ?? '—', when: date(x.startedAt, true) })}
        {x.submittedAt ? ` · ${t('inventory.submittedBy', { name: x.submittedByName ?? '—', when: date(x.submittedAt, true) })}` : ''}
        {x.decidedAt ? ` · ${t('inventory.decidedBy', { name: x.decidedByName ?? '—', when: date(x.decidedAt, true) })}` : ''}
      </p>
      <div className="table-wrap">
        <table>
          <caption className="visually-hidden">{t('inventory.countSheet')}</caption>
          <thead>
            <tr>
              <th scope="col">{t('inventory.item')}</th>
              <th scope="col">{t('inventory.systemQty')}</th>
              <th scope="col">{t('inventory.countedQty')}</th>
              <th scope="col">{t('inventory.variance')}</th>
            </tr>
          </thead>
          <tbody>
            {x.detail?.map((l) => (
              <tr key={l.itemId}>
                <td><bdi>{l.itemCode}</bdi> <bdi>{l.itemName}</bdi></td>
                <td>{num(l.systemQty)} {l.unit}</td>
                <td>
                  {counting && can('inventory.manage') ? (
                    <input
                      type="number" step="any" min="0" aria-label={t('inventory.countFor', { item: l.itemCode })}
                      value={entered[l.itemId] ?? (l.countedQty === null ? '' : String(l.countedQty))}
                      onChange={(e) => setEntered({ ...entered, [l.itemId]: e.target.value })}
                    />
                  ) : l.countedQty === null ? '—' : num(l.countedQty)}
                </td>
                <td>{l.variance === null || l.variance === 0 ? '' : <Badge tone={l.variance < 0 ? 'bad' : 'warn'}>{l.variance > 0 ? '+' : ''}{num(l.variance)}</Badge>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {counting && can('inventory.manage') ? (
        <fieldset>
          <legend>{t('inventory.addFound')}</legend>
          <ItemPicker value={extra} onChange={(i) => setExtra(i ? { id: i.id, label: `${i.code} ${i.name}` } : null)} />
          <Field label={t('inventory.countedQty')}><input type="number" step="any" min="0" value={extraQty} onChange={(e) => setExtraQty(e.target.value)} /></Field>
        </fieldset>
      ) : null}
      <ErrorBanner error={error?.status === 400 ? null : saveCounts.error ?? act.error} />
      {error?.status === 400 ? <p role="alert" className="error">{Object.values(error.fieldErrors).flat().join(' ')}</p> : null}
      <div className="actions">
        <button type="button" onClick={onClose}>{t('common.back')}</button>
        {counting && can('inventory.manage') ? (
          <>
            <button type="button" disabled={saveCounts.isPending || withExtra.length === 0} onClick={() => saveCounts.mutate(withExtra)}>{t('inventory.saveCounts')}</button>
            <button type="button" disabled={act.isPending || withExtra.length > 0} onClick={() => act.mutate({ action: 'submit' })}>{t('inventory.submitCount')}</button>
            <button type="button" onClick={() => setAsking('cancel')}>{t('inventory.cancelTake')}</button>
          </>
        ) : null}
        {x.status === 'Submitted' ? (
          <>
            {can('inventory.approve') ? <button type="button" className="primary" disabled={act.isPending} onClick={() => act.mutate({ action: 'approve' })}>{t('inventory.approveTake')}</button> : null}
            {can('inventory.approve') ? <button type="button" onClick={() => setAsking('back')}>{t('inventory.sendBack')}</button> : null}
            {can('inventory.manage') ? <button type="button" onClick={() => setAsking('cancel')}>{t('inventory.cancelTake')}</button> : null}
          </>
        ) : null}
      </div>
      {asking ? (
        <ReasonDialog
          title={asking === 'cancel' ? t('inventory.cancelTake') : t('inventory.sendBack')} label={t('common.reason')} confirmLabel={t('common.confirm')} busy={act.isPending} error={act.error}
          onCancel={() => setAsking(null)} onConfirm={(reason) => act.mutate({ action: asking === 'cancel' ? 'cancel' : 'send-back', body: { reason } })}
        />
      ) : null}
    </Dialog>
  );
}
