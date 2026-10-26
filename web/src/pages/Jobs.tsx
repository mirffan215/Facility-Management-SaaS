import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useAuth } from '../auth/AuthContext';
import { Badge, useEnumLabel } from '../components/ui';
import { applyOps } from '../offline/model';
import { useOffline } from '../offline/OfflineProvider';
import { usePrefs } from '../lib/prefs';
import { priorityTone, workStatusTone } from './WorkOrders';

const PRIORITY_RANK: Record<string, number> = { Urgent: 0, High: 1, Medium: 2, Low: 3 };

/** The technician's day: the jobs assigned to them, kept on the phone, usable without a connection. */
export function JobsPage() {
  const { t } = useTranslation();
  const { displayName } = useAuth();
  const { date } = usePrefs();
  const { snapshot, ops, state, online, ready, syncNow, retry, discard } = useOffline();
  const statusLabel = useEnumLabel('workStatus');
  const priorityLabel = useEnumLabel('workPriority');

  const jobs = (snapshot?.jobs ?? [])
    .map((j) => applyOps(j, ops, displayName ?? ''))
    .sort((a, b) => (PRIORITY_RANK[a.order.priority] ?? 9) - (PRIORITY_RANK[b.order.priority] ?? 9) || a.order.number - b.order.number);
  const failed = ops.filter((o) => o.state === 'failed');
  const numberOf = (id: string) => snapshot?.jobs.find((j) => j.order.id === id)?.order.displayNumber ?? '';

  return (
    <section>
      <div className="toolbar">
        <h1>{t('jobs.title')}</h1>
        <div className="actions">
          {snapshot ? <span className="muted">{t('jobs.updated', { when: date(new Date(snapshot.fetchedAt).toISOString(), true) })}</span> : null}
          <button type="button" disabled={!online || state.syncing} onClick={() => void syncNow()}>{t('jobs.refresh')}</button>
        </div>
      </div>

      {failed.length > 0 ? (
        <div className="banner error" role="alert">
          <strong>{t('jobs.issuesTitle')}</strong>
          <ul className="plain">
            {failed.map((o) => (
              <li key={o.id}>
                <bdi>{numberOf(o.workOrderId)}</bdi> · {t(`jobs.op.${o.kind}`)} — <bdi>{o.error}</bdi>{' '}
                <button type="button" onClick={() => void retry(o.id)}>{t('jobs.retry')}</button>{' '}
                <button type="button" onClick={() => void discard(o.id)}>{t('jobs.discard')}</button>
              </li>
            ))}
          </ul>
        </div>
      ) : null}

      {!ready ? <p className="muted">{t('common.loading')}</p> : null}
      {ready && !snapshot ? <p className="banner">{online ? t('jobs.notDownloaded') : t('jobs.neverDownloaded')}</p> : null}
      {snapshot && jobs.length === 0 ? <p className="muted">{t('jobs.none')}</p> : null}

      <ul className="jobs" aria-label={t('jobs.title')}>
        {jobs.map((j) => (
          <li key={j.order.id}>
            <Link to={`/jobs/${j.order.id}`}>
              <span className="job-head">
                <strong><bdi>{j.order.displayNumber}</bdi> · <bdi>{j.order.title}</bdi></strong>
              </span>
              <span>
                <Badge tone={priorityTone(j.order.priority)}>{priorityLabel(j.order.priority)}</Badge> <Badge tone={workStatusTone(j.order.status)}>{statusLabel(j.order.status)}</Badge>{' '}
                {j.opIds.size > 0 ? <Badge tone="warn">{t('jobs.waiting')}</Badge> : null}
              </span>
              <span className="muted"><bdi>{j.order.assetName ?? j.order.locationName ?? ''}</bdi></span>
              {j.order.dueAt ? <span className="muted">{t('work.due')}: {date(j.order.dueAt, true)}</span> : null}
            </Link>
          </li>
        ))}
      </ul>
    </section>
  );
}
