import { useEffect, useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import { useHintSettings } from '../api/hooks';
import type { HintSettings } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { ErrorBanner, Field, QueryState } from './ui';

type Draft = Record<'historyDays' | 'minFailures' | 'shrinkPercent' | 'paceRisePercent', string>;

const toDraft = (s: HintSettings): Draft => ({ historyDays: String(s.historyDays), minFailures: String(s.minFailures), shrinkPercent: String(s.shrinkPercent), paceRisePercent: String(s.paceRisePercent) });

/** How eagerly the suggested people and the early warnings speak. The defaults are what the rules were written with. */
export function HintSettingsForm() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const client = useQueryClient();
  const query = useHintSettings(true);
  const [draft, setDraft] = useState<Draft | null>(null);
  useEffect(() => { if (query.data) setDraft(toDraft(query.data)); }, [query.data]);
  const save = useMutation({
    mutationFn: () => api.put('/work-orders/hint-settings', {
      historyDays: Number(draft?.historyDays), minFailures: Number(draft?.minFailures), shrinkPercent: Number(draft?.shrinkPercent), paceRisePercent: Number(draft?.paceRisePercent),
      rowVersion: query.data?.rowVersion ?? null,
    }),
    onSuccess: () => client.invalidateQueries({ queryKey: ['work-orders'] }),
  });
  const error = save.error instanceof ApiError ? save.error : null;
  const field = (key: keyof Draft, label: string, hint: string, min: number, max: number) => (
    <Field label={label} hint={hint} error={error?.field(key)}>
      <input type="number" min={min} max={max} value={draft?.[key] ?? ''} onChange={(e) => setDraft({ ...(draft as Draft), [key]: e.target.value })} />
    </Field>
  );
  return (
    <>
      <h2>{t('hints.title')}</h2>
      <p className="muted">{t('hints.help')}</p>
      <QueryState query={query}>
        {draft ? (
          <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }}>
            <div className="row">
              {field('historyDays', t('hints.historyDays'), t('hints.historyDaysHint'), 30, 730)}
              {field('minFailures', t('hints.minFailures'), t('hints.minFailuresHint'), 3, 12)}
            </div>
            <div className="row">
              {field('shrinkPercent', t('hints.shrinkPercent'), t('hints.shrinkPercentHint'), 20, 90)}
              {field('paceRisePercent', t('hints.paceRisePercent'), t('hints.paceRisePercentHint'), 110, 500)}
            </div>
            <ErrorBanner error={error?.status === 400 ? null : save.error} />
            <button type="submit" className="primary" disabled={save.isPending}>{t('common.save')}</button>
            {save.isSuccess ? <span role="status"> {t('ai.saved')}</span> : null}
          </form>
        ) : null}
      </QueryState>
    </>
  );
}
