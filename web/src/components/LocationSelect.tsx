import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useLocationTree } from '../api/hooks';
import { buildTree, flatten } from '../lib/locations';
import { usePrefs } from '../lib/prefs';

/** A drop-down of every place, indented by depth. */
export function LocationSelect({ value, onChange, allowNone = true, ...rest }: { value: string; onChange: (id: string) => void; allowNone?: boolean; id?: string; 'aria-invalid'?: boolean; 'aria-describedby'?: string; 'aria-required'?: boolean; 'aria-label'?: string }) {
  const { t } = useTranslation();
  const { name } = usePrefs();
  const tree = useLocationTree();
  const items = useMemo(() => flatten(buildTree(tree.data ?? [])), [tree.data]);
  return (
    <select value={value} onChange={(e) => onChange(e.target.value)} {...rest}>
      <option value="">{allowNone ? t('common.notSet') : t('common.choose')}</option>
      {items.map((l) => (
        <option key={l.id} value={l.id}>
          {' '.repeat(l.depth)}
          {l.code} · {name(l)}
        </option>
      ))}
    </select>
  );
}
