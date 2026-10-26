import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useItems, useStores } from '../api/hooks';
import type { Item } from '../api/types';
import { usePrefs } from '../lib/prefs';
import { Field } from './ui';

/** A modal form. Escape closes it; the first field takes focus through its own autoFocus. */
export function Dialog({ title, onClose, wide, children }: { title: string; onClose: () => void; wide?: boolean; children: ReactNode }) {
  return (
    <div className="overlay" onKeyDown={(e) => e.key === 'Escape' && onClose()}>
      <div className={wide ? 'dialog wide' : 'dialog'} role="dialog" aria-modal="true" aria-label={title}>
        <h2>{title}</h2>
        {children}
      </div>
    </div>
  );
}

/** Finds an item by what is typed (code, name or barcode) and lets the person pick from the matches. */
export function ItemPicker({ value, onChange, label, error, autoFocus }: { value: { id: string; label: string } | null; onChange: (item: Item | null) => void; label?: string; error?: string; autoFocus?: boolean }) {
  const { t } = useTranslation();
  const { name } = usePrefs();
  const [search, setSearch] = useState('');
  const matches = useItems({ q: search.trim() || undefined, pageSize: 20 }, search.trim().length >= 2);
  return (
    <>
      <Field label={label ?? t('inventory.findItem')} hint={value ? t('inventory.selected', { name: value.label }) : t('inventory.findItemHint')} error={error}>
        <input value={search} onChange={(e) => setSearch(e.target.value)} autoFocus={autoFocus} />
      </Field>
      {matches.data && matches.data.items.length > 0 ? (
        <Field label={t('inventory.matchingItems')}>
          <select value={value?.id ?? ''} onChange={(e) => onChange(matches.data?.items.find((i) => i.id === e.target.value) ?? null)}>
            <option value="">{t('common.choose')}</option>
            {matches.data.items.map((i) => <option key={i.id} value={i.id}>{i.code} · {name(i)} ({i.unit})</option>)}
          </select>
        </Field>
      ) : null}
    </>
  );
}

export function StoreSelect({ value, onChange, label, error, allowAll, exclude }: { value: string; onChange: (id: string) => void; label?: string; error?: string; allowAll?: boolean; exclude?: string }) {
  const { t } = useTranslation();
  const { name } = usePrefs();
  const stores = useStores(false);
  return (
    <Field label={label ?? t('inventory.store')} error={error}>
      <select value={value} onChange={(e) => onChange(e.target.value)}>
        <option value="">{allowAll ? t('common.all') : t('common.choose')}</option>
        {stores.data?.filter((s) => s.id !== exclude).map((s) => <option key={s.id} value={s.id}>{s.code} · {name(s)}</option>)}
      </select>
    </Field>
  );
}
