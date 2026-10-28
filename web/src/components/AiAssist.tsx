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
  const answer = (s: AiTriageSuggestion, accepted: boolean) => {
    if (s.usageId) void api.post('/ai/feedback', { usageId: s.usageId, accepted }).catch(() => undefined); // best effort: a lost answer must never get in the way
  };
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
          <button type="button" onClick={() => { answer(suggestion, true); onApply({ categoryId: suggestion.categoryId, priority: suggestion.priority }); setSuggestion(null); }}>{t('ai.use')}</button>{' '}
          <button type="button" onClick={() => { answer(suggestion, false); setSuggestion(null); }}>{t('ai.dismiss')}</button>
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
  const [summary, setSummary] = useState<AiSummary | null>(null);
  const [answered, setAnswered] = useState(false);
  const ask = useMutation({
    mutationFn: () => api.post<AiSummary>(`/ai/work-orders/${workOrderId}/summary`, {}),
    onSuccess: (s) => { setSummary(s); setAnswered(false); },
  });
  const answer = (accepted: boolean) => {
    setAnswered(true);
    if (summary?.usageId) void api.post('/ai/feedback', { usageId: summary.usageId, accepted }).catch(() => undefined);
  };
  if (!can('ai.use') || !status.data?.summary) return null;

  return (
    <div className="ai-box" role="group" aria-label={t('ai.summaryTitle')}>
      <button type="button" disabled={ask.isPending} onClick={() => ask.mutate()}>{t('ai.summarise')}</button>
      <ErrorBanner error={ask.error} />
      {summary ? (
        <>
          <p role="status"><bdi>{summary.text}</bdi></p>
          <small className="muted">{t('ai.checkIt')}</small>{' '}
          {answered ? <small>{t('ai.thanks')}</small> : (
            <>
              <button type="button" onClick={() => answer(true)}>{t('ai.helpful')}</button>{' '}
              <button type="button" onClick={() => answer(false)}>{t('ai.notHelpful')}</button>
            </>
          )}
        </>
      ) : null}
    </div>
  );
}
