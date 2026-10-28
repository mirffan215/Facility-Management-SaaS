import { useEffect, useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import { useAiSettings, useAiUsage } from '../api/hooks';
import type { AiSettings } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Badge, ErrorBanner, Field, QueryState, useEnumLabel } from '../components/ui';
import { formatNumber } from '../lib/format';

/** Whether AI suggestions are on, which ones, the daily limit, and what has been used. Off until an administrator turns it on. */
export function AiSettingsPage() {
  const { t, i18n } = useTranslation();
  const { api } = useAuth();
  const client = useQueryClient();
  const featureLabel = useEnumLabel('aiFeature');
  const settings = useAiSettings();
  const usage = useAiUsage();
  const [enabled, setEnabled] = useState(false);
  const [triage, setTriage] = useState(true);
  const [summary, setSummary] = useState(true);
  const [limit, setLimit] = useState('200');
  const s = settings.data;

  useEffect(() => {
    if (!s) return;
    setEnabled(s.enabled);
    setTriage(s.triageEnabled);
    setSummary(s.summaryEnabled);
    setLimit(s.dailyLimit.toString());
  }, [s]);

  const save = useMutation({
    mutationFn: () => api.put<AiSettings>('/ai/settings', { enabled, triageEnabled: triage, summaryEnabled: summary, dailyLimit: limit.trim() === '' ? null : Number(limit), rowVersion: s?.rowVersion ?? null }),
    onSuccess: async () => { await client.invalidateQueries({ queryKey: ['ai'] }); },
  });
  const errors = save.error instanceof ApiError ? save.error.fieldErrors : {};

  return (
    <section className="narrow">
      <h1>{t('ai.title')}</h1>
      <p className="muted">{t('ai.intro')}</p>
      <QueryState query={settings}>
        {s ? (
          <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }} noValidate>
            <p role="status">
              {s.available ? <Badge tone={s.enabled ? 'ok' : 'neutral'}>{s.enabled ? t('ai.on') : t('ai.off')}</Badge> : <Badge tone="warn">{t('ai.notSetUp')}</Badge>}
            </p>
            {!s.available ? <p className="muted">{t('ai.notSetUpHelp')}</p> : null}
            <fieldset disabled={!s.available}>
              <legend>{t('ai.switches')}</legend>
              <label className="check"><input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} /> {t('ai.enable')}</label>
              <label className="check"><input type="checkbox" checked={triage} onChange={(e) => setTriage(e.target.checked)} /> {t('ai.enableTriage')}</label>
              <label className="check"><input type="checkbox" checked={summary} onChange={(e) => setSummary(e.target.checked)} /> {t('ai.enableSummary')}</label>
              <Field label={t('ai.dailyLimit')} hint={t('ai.dailyLimitHint')} error={errors.dailyLimit?.[0]}>
                <input type="number" min={0} max={5000} value={limit} onChange={(e) => setLimit(e.target.value)} />
              </Field>
            </fieldset>
            <ErrorBanner error={Object.keys(errors).length ? null : save.error} />
            {save.isSuccess ? <p role="status" className="muted">{t('ai.saved')}</p> : null}
            <div className="actions"><button type="submit" className="primary" disabled={save.isPending || !s.available}>{t('common.save')}</button></div>
          </form>
        ) : null}
      </QueryState>

      <h2>{t('ai.whatIsSent')}</h2>
      <p>{t('ai.whatIsSentText')}</p>

      <h2>{t('ai.usage')}</h2>
      <QueryState query={usage}>
        {usage.data ? (
          <>
            <p>{t('ai.usedToday', { used: usage.data.usedToday, limit: usage.data.dailyLimit })}</p>
            {usage.data.last30Days.length === 0 ? <p className="muted">{t('ai.noUsage')}</p> : (
              <div className="table-wrap" tabIndex={0} role="region" aria-label={t('ai.usage')}>
                <table>
                  <caption className="visually-hidden">{t('ai.usage')}</caption>
                  <thead>
                    <tr>
                      <th scope="col">{t('ai.feature')}</th>
                      <th scope="col">{t('ai.ok')}</th>
                      <th scope="col">{t('ai.failed')}</th>
                      <th scope="col">{t('ai.tokens')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {usage.data.last30Days.map((l) => (
                      <tr key={l.feature}>
                        <th scope="row">{featureLabel(l.feature)}</th>
                        <td>{l.ok}</td>
                        <td>{l.failed}</td>
                        <td>{formatNumber(l.inputTokens + l.outputTokens, i18n.language, 0)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </>
        ) : null}
      </QueryState>
    </section>
  );
}
