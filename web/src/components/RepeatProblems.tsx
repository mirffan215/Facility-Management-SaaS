import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { usePermissions, useRepeatProblems } from '../api/hooks';
import { formatNumber } from '../lib/format';
import { usePrefs } from '../lib/prefs';

/** Assets with three or more repair requests in 90 days, counted from the work orders. Plain counting, no AI, and it says why each asset is listed. */
export function RepeatProblems() {
  const { t, i18n } = useTranslation();
  const { can } = usePermissions();
  const { date } = usePrefs();
  const allowed = can('workorders.manage') || can('workorders.assign');
  const list = useRepeatProblems(allowed);
  if (!allowed || !list.data || list.data.length === 0) return null;
  return (
    <details className="ai-box">
      <summary>{t('work.repeat.title', { count: list.data.length })}</summary>
      <p className="muted">{t('work.repeat.help')}</p>
      <ul className="plain">
        {list.data.map((r) => (
          <li key={r.assetId}>
            <Link to={`/work-orders?assetId=${r.assetId}`}><bdi>{r.assetCode ?? ''} {r.assetName ?? ''}</bdi></Link>:{' '}
            {t('work.repeat.line', { count: r.count, emergencies: r.emergencies, last: date(r.lastAt) })}
            {r.averageDaysBetween != null ? ` ${t('work.repeat.gap', { days: formatNumber(r.averageDaysBetween, i18n.language, 1) })}` : ''}
          </li>
        ))}
      </ul>
    </details>
  );
}
