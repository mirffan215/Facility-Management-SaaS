import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { useNotifications } from '../api/hooks';
import type { NotificationItem } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Badge, ErrorBanner, Pager, QueryState } from '../components/ui';
import { usePrefs } from '../lib/prefs';

/** The text of a notice, in the reader's language, from its type and data. Unknown types still say something useful. */
export function noticeText(t: (key: string, options?: Record<string, unknown>) => string, n: NotificationItem): string {
  return t(`notify.type.${n.type}`, { number: n.data.number ?? '', title: n.data.title ?? '', defaultValue: t('notify.generic', { number: n.data.number ?? '' }) });
}

export function NotificationsPage() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { date } = usePrefs();
  const client = useQueryClient();
  const navigate = useNavigate();
  const [unreadOnly, setUnreadOnly] = useState(false);
  const [page, setPage] = useState(1);
  const query = useNotifications(unreadOnly, page);

  const refresh = () => client.invalidateQueries({ queryKey: ['notifications'] });
  const markRead = useMutation({ mutationFn: (id: string) => api.post(`/notifications/${id}/read`), onSuccess: refresh });
  const markAll = useMutation({ mutationFn: () => api.post('/notifications/read-all'), onSuccess: refresh });

  const open = async (n: NotificationItem) => {
    if (!n.isRead) await markRead.mutateAsync(n.id);
    if (n.linkPath) navigate(n.linkPath);
  };

  return (
    <section>
      <div className="toolbar">
        <h1>{t('notify.title')}</h1>
        <div className="actions">
          <label className="check">
            <input type="checkbox" checked={unreadOnly} onChange={(e) => { setUnreadOnly(e.target.checked); setPage(1); }} /> {t('notify.unreadOnly')}
          </label>
          <button type="button" disabled={markAll.isPending} onClick={() => markAll.mutate()}>
            {t('notify.markAll')}
          </button>
        </div>
      </div>
      <ErrorBanner error={markRead.error ?? markAll.error} />
      <QueryState query={query}>
        {query.data && query.data.items.length === 0 ? <p className="muted">{t('notify.empty')}</p> : null}
        <ul className="notices" aria-label={t('notify.title')}>
          {query.data?.items.map((n) => (
            <li key={n.id} className={n.isRead ? 'read' : 'unread'}>
              <button type="button" onClick={() => void open(n)}>
                <span>
                  {!n.isRead ? <Badge tone="warn">{t('notify.new')}</Badge> : null} <bdi>{noticeText(t, n)}</bdi>
                </span>
                {n.data.title ? <span className="muted"><bdi>{n.data.title}</bdi></span> : null}
                {n.data.reason ? <span className="muted">{t('work.reason', { reason: n.data.reason })}</span> : null}
                <span className="muted">{date(n.createdAt, true)}</span>
              </button>
            </li>
          ))}
        </ul>
        {query.data ? <Pager page={page} pageSize={25} total={query.data.total} onChange={setPage} /> : null}
      </QueryState>
    </section>
  );
}
