import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import { useChecklistTemplates } from '../api/hooks';
import type { ChecklistTemplate } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Badge, ErrorBanner, Field, QueryState } from '../components/ui';
import { usePrefs } from '../lib/prefs';

export function ChecklistsSection() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { name } = usePrefs();
  const client = useQueryClient();
  const templates = useChecklistTemplates(true, true);
  const [editing, setEditing] = useState<ChecklistTemplate | 'new' | null>(null);
  const archive = useMutation({
    mutationFn: (c: ChecklistTemplate) => api.post(`/work-orders/checklists/${c.id}/${c.isArchived ? 'restore' : 'archive'}`, { rowVersion: c.rowVersion }),
    onSuccess: () => client.invalidateQueries({ queryKey: ['checklist-templates'] }),
  });

  return (
    <>
      <div className="toolbar">
        <h2>{t('evidence.templates')}</h2>
        <button type="button" onClick={() => setEditing('new')}>{t('evidence.newTemplate')}</button>
      </div>
      <p className="muted">{t('evidence.templatesHint')}</p>
      <QueryState query={templates}>
        {templates.data && templates.data.length === 0 ? <p className="muted">{t('evidence.noTemplates')}</p> : null}
        <ul className="plain">
          {templates.data?.map((c) => (
            <li key={c.id}>
              <strong><bdi>{name(c)}</bdi></strong> <span className="muted">· {t('evidence.lines', { count: c.items.length })}</span> {c.isArchived ? <Badge tone="neutral">{t('common.archived')}</Badge> : null}{' '}
              <button type="button" onClick={() => setEditing(c)}>{t('common.edit')}</button>{' '}
              <button type="button" disabled={archive.isPending} onClick={() => archive.mutate(c)}>{c.isArchived ? t('common.restore') : t('common.archive')}</button>
            </li>
          ))}
        </ul>
        <ErrorBanner error={archive.error} />
      </QueryState>
      {editing ? <TemplateDialog template={editing === 'new' ? null : editing} onClose={() => setEditing(null)} /> : null}
    </>
  );
}

function TemplateDialog({ template, onClose }: { template: ChecklistTemplate | null; onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const client = useQueryClient();
  const [name, setName] = useState(template?.name ?? '');
  const [nameAr, setNameAr] = useState(template?.nameAr ?? '');
  const [items, setItems] = useState(template?.items ?? [{ text: '', required: true }]);

  const save = useMutation({
    mutationFn: () => {
      const body = { name: name.trim(), nameAr: nameAr.trim() || null, items: items.map((i) => ({ text: i.text.trim(), required: i.required })), rowVersion: template?.rowVersion };
      return template ? api.put(`/work-orders/checklists/${template.id}`, body) : api.post('/work-orders/checklists', body);
    },
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: ['checklist-templates'] });
      onClose();
    },
  });
  const error = save.error instanceof ApiError ? save.error : null;
  const setItem = (n: number, patch: Partial<{ text: string; required: boolean }>) => setItems((prev) => prev.map((x, i) => (i === n ? { ...x, ...patch } : x)));
  const move = (n: number, by: number) => setItems((prev) => { const next = [...prev]; const [it] = next.splice(n, 1); next.splice(n + by, 0, it); return next; });

  return (
    <div className="overlay" onKeyDown={(e) => e.key === 'Escape' && onClose()}>
      <div className="dialog wide" role="dialog" aria-modal="true" aria-label={template ? t('evidence.editTemplate') : t('evidence.newTemplate')}>
        <h2>{template ? t('evidence.editTemplate') : t('evidence.newTemplate')}</h2>
        <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }}>
          <div className="row">
            <Field label={t('assets.name')} error={error?.field('name')} required><input value={name} onChange={(e) => setName(e.target.value)} maxLength={200} autoFocus /></Field>
            <Field label={t('assets.nameAr')} error={error?.field('nameAr')}><input value={nameAr} onChange={(e) => setNameAr(e.target.value)} maxLength={200} lang="ar" dir="rtl" /></Field>
          </div>
          <fieldset>
            <legend>{t('evidence.templateLines')}</legend>
            {items.map((item, n) => (
              <div className="row" key={n}>
                <Field label={t('evidence.lineText', { number: n + 1 })}><input value={item.text} onChange={(e) => setItem(n, { text: e.target.value })} maxLength={300} /></Field>
                <label className="check"><input type="checkbox" checked={item.required} onChange={(e) => setItem(n, { required: e.target.checked })} /> {t('evidence.required')}</label>
                <button type="button" disabled={n === 0} onClick={() => move(n, -1)} aria-label={t('evidence.moveUp', { number: n + 1 })}>↑</button>
                <button type="button" disabled={n === items.length - 1} onClick={() => move(n, 1)} aria-label={t('evidence.moveDown', { number: n + 1 })}>↓</button>
                <button type="button" disabled={items.length === 1} onClick={() => setItems(items.filter((_, i) => i !== n))}>{t('common.delete')}</button>
              </div>
            ))}
            {error?.field('items') ? <small className="error" role="alert">{error.field('items')}</small> : null}
            <button type="button" onClick={() => setItems([...items, { text: '', required: false }])}>{t('evidence.addLine')}</button>
          </fieldset>
          <ErrorBanner error={error?.status === 400 ? null : save.error} />
          <div className="actions">
            <button type="button" onClick={onClose}>{t('common.cancel')}</button>
            <button type="submit" className="primary" disabled={save.isPending}>{t('common.save')}</button>
          </div>
        </form>
      </div>
    </div>
  );
}
