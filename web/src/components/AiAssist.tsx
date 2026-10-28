import { useState } from 'react';
import { useMutation } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { useAiStatus, usePermissions } from '../api/hooks';
import type { AiSummary, AiTriageSuggestion, WorkPriority } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { ErrorBanner, useEnumLabel } from './ui';

/** A suggestion is never applied by itself: it is shown with how sure the model was, and a person chooses to use it. */
export function TriageSuggest({ title, description, onApply }: { title: string; description: string; onApply: (s: { categoryId: string | null; priority: WorkPriority | null }) => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { can } = usePermissions();
  const status = useAiStatus();
  const priorityLabel = useEnumLabel('workPriority');
  const [suggestion, setSuggestion] = useState<AiTriageSuggestion | null>(null);
  const ask = useMutation({
    mutationFn: () => api.post<AiTriageSuggestion>('/ai/triage', { title, description: description || null }),
    onSuccess: (s) => setSuggestion(s),
  });
  if (!can('ai.use') || !status.data?.triage) return null;

  return (
    <div className="ai-box" role="group" aria-label={t('ai.triageTitle')}>
      <button type="button" disabled={ask.isPending || title.trim().length === 0} onClick={() => { setSuggestion(null); ask.mutate(); }}>{t('ai.suggest')}</button>{' '}
      <span className="muted">{t('ai.suggestHint')}</span>
      <ErrorBanner error={ask.error} />
      {suggestion ? (
        <p role="status">
          <strong>{t('ai.suggested')}:</strong>{' '}
          {suggestion.categoryName ? <span className="chip"><bdi>{suggestion.categoryName}</bdi></span> : null}{' '}
          {suggestion.priority ? <span className="chip">{priorityLabel(suggestion.priority)}</span> : null}{' '}
          {suggestion.reason ? <bdi className="muted">{suggestion.reason}</bdi> : null}{' '}
          <button type="button" onClick={() => { onApply({ categoryId: suggestion.categoryId, priority: suggestion.priority }); setSuggestion(null); }}>{t('ai.use')}</button>{' '}
          <button type="button" onClick={() => setSuggestion(null)}>{t('ai.dismiss')}</button>
          <br />
          <small className="muted">{t('ai.checkIt')}</small>
        </p>
      ) : null}
    </div>
  );
}

export function SummaryPanel({ workOrderId }: { workOrderId: string }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { can } = usePermissions();
  const status = useAiStatus();
  const [summary, setSummary] = useState<string | null>(null);
  const ask = useMutation({
    mutationFn: () => api.post<AiSummary>(`/ai/work-orders/${workOrderId}/summary`, {}),
    onSuccess: (s) => setSummary(s.text),
  });
  if (!can('ai.use') || !status.data?.summary) return null;

  return (
    <div className="ai-box" role="group" aria-label={t('ai.summaryTitle')}>
      <button type="button" disabled={ask.isPending} onClick={() => ask.mutate()}>{t('ai.summarise')}</button>
      <ErrorBanner error={ask.error} />
      {summary ? (
        <>
          <p role="status"><bdi>{summary}</bdi></p>
          <small className="muted">{t('ai.checkIt')}</small>
        </>
      ) : null}
    </div>
  );
}
