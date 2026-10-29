import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { usePermissions, usePredictiveHints } from '../api/hooks';
import { formatNumber } from '../lib/format';
import { usePrefs } from '../lib/prefs';

/** Early warnings from two plain rules (repairs coming closer together, a service meter running faster). Each line shows the numbers behind it. */
export function PredictiveHints() {
  const { t, i18n } = useTranslation();
  const { can } = usePermissions();
  const { date } = usePrefs();
  const allowed = can('workorders.manage') || can('workorders.assign');
  const list = usePredictiveHints(allowed);
  if (!allowed || !list.data || list.data.length === 0) return null;
  const n = (v: number | null) => formatNumber(v ?? 0, i18n.language, 1);
  return (
    <details className="ai-box">
      <summary>{t('work.predict.title', { count: list.data.length })}</summary>
      <p className="muted">{t('work.predict.help')}</p>
      <ul className="plain">
        {list.data.map((h) => (
          <li key={`${h.kind}-${h.assetId}-${h.meterName ?? ''}`}>
            <Link to={`/work-orders?assetId=${h.assetId}`}><bdi>{h.assetCode ?? ''} {h.assetName ?? ''}</bdi></Link>:{' '}
            {h.kind === 'FailuresSpeedingUp'
              ? t('work.predict.failures', { events: h.events, recent: n(h.recentGapDays), earlier: n(h.earlierGapDays), last: h.lastAt ? date(h.lastAt) : '', next: h.expectedBy ? date(h.expectedBy) : '' })
              : <>
                {t('work.predict.pace', { meter: h.meterName, unit: h.unit, recent: n(h.recentPerDay), prior: n(h.priorPerDay), ratio: n(h.ratio) })}{' '}
                {h.daysToService == null ? null : h.daysToService === 0 ? t('work.predict.serviceDue') : t('work.predict.service', { days: h.daysToService })}
              </>}
          </li>
        ))}
      </ul>
    </details>
  );
}
