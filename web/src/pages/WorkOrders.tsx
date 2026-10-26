import { Link, useSearchParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { useWorkOrders } from '../api/hooks';
import type { SlaState, WorkOrderStatus, WorkPriority } from '../api/types';
import { Badge, Can, Field, Pager, QueryState, useEnumLabel } from '../components/ui';
import { usePrefs } from '../lib/prefs';

export const PRIORITIES: WorkPriority[] = ['Low', 'Medium', 'High', 'Urgent'];
export const STATUSES: WorkOrderStatus[] = ['Requested', 'Approved', 'Assigned', 'InProgress', 'OnHold', 'Completed', 'Verified', 'Closed', 'Cancelled'];

export function workStatusTone(status: WorkOrderStatus): 'ok' | 'warn' | 'bad' | 'neutral' {
  switch (status) {
    case 'Closed':
    case 'Verified':
    case 'Completed':
      return 'ok';
    case 'OnHold':
    case 'Requested':
      return 'warn';
    case 'Cancelled':
      return 'bad';
    default:
      return 'neutral';
  }
}

export function slaTone(state: SlaState): 'ok' | 'warn' | 'bad' | 'neutral' {
  return state === 'Breached' ? 'bad' : state === 'AtRisk' ? 'warn' : state === 'Met' || state === 'OnTrack' ? 'ok' : 'neutral';
}

export function priorityTone(priority: WorkPriority): 'ok' | 'warn' | 'bad' | 'neutral' {
  return priority === 'Urgent' ? 'bad' : priority === 'High' ? 'warn' : 'neutral';
}

export function WorkOrdersPage() {
  const { t } = useTranslation();
  const { date } = usePrefs();
  const statusLabel = useEnumLabel('workStatus');
  const priorityLabel = useEnumLabel('workPriority');
  const slaLabel = useEnumLabel('slaState');
  const [params, setParams] = useSearchParams();

  const filters = {
    q: params.get('q') ?? '',
    view: params.get('view') ?? 'open',
    priority: params.get('priority') ?? '',
    status: params.get('status') ?? '',
    page: Number(params.get('page') ?? '1') || 1,
  };

  const set = (patch: Record<string, string | number | undefined>) => {
    const next = new URLSearchParams(params);
    for (const [k, v] of Object.entries(patch)) {
      if (v === undefined || v === '' || (k === 'page' && v === 1) || (k === 'view' && v === 'open')) next.delete(k);
      else next.set(k, String(v));
    }
    if (!('page' in patch)) next.delete('page');
    setParams(next);
  };

  const query = useWorkOrders({
    q: filters.q || undefined,
    openOnly: filters.view === 'open' ? true : undefined,
    mine: filters.view === 'mine' ? true : undefined,
    assignedToMe: filters.view === 'assigned' ? true : undefined,
    overdue: filters.view === 'overdue' ? true : undefined,
    slaBreached: filters.view === 'sla' ? true : undefined,
    priority: filters.priority || undefined,
    status: filters.status || undefined,
    page: filters.page,
    pageSize: 25,
  });

  return (
    <section>
      <div className="toolbar">
        <h1>{t('nav.workOrders')}</h1>
        <Can permission="workorders.request">
          <Link to="/work-orders/new" className="button primary">
            {t('work.new')}
          </Link>
        </Can>
      </div>

      <div className="tabs" role="tablist" aria-label={t('work.views')}>
        {['open', 'mine', 'assigned', 'overdue', 'sla', 'all'].map((v) => (
          <button key={v} role="tab" type="button" aria-selected={filters.view === v} className={filters.view === v ? 'active' : ''} onClick={() => set({ view: v })}>
            {t(`work.view.${v}`)}
          </button>
        ))}
      </div>

      <div className="filters">
        <Field label={t('common.search')}>
          <input type="search" value={filters.q} onChange={(e) => set({ q: e.target.value })} placeholder={t('work.searchHint')} />
        </Field>
        <Field label={t('work.status')}>
          <select value={filters.status} onChange={(e) => set({ status: e.target.value })}>
            <option value="">{t('common.any')}</option>
            {STATUSES.map((s) => (
              <option key={s} value={s}>
                {statusLabel(s)}
              </option>
            ))}
          </select>
        </Field>
        <Field label={t('work.priority')}>
          <select value={filters.priority} onChange={(e) => set({ priority: e.target.value })}>
            <option value="">{t('common.any')}</option>
            {PRIORITIES.map((p) => (
              <option key={p} value={p}>
                {priorityLabel(p)}
              </option>
            ))}
          </select>
        </Field>
      </div>

      <QueryState query={query}>
        {query.data && query.data.items.length === 0 ? (
          <p className="muted">{t('work.empty')}</p>
        ) : (
          <div className="table-wrap">
            <table>
              <caption className="visually-hidden">{t('nav.workOrders')}</caption>
              <thead>
                <tr>
                  <th scope="col">{t('work.number')}</th>
                  <th scope="col">{t('work.titleField')}</th>
                  <th scope="col">{t('work.priority')}</th>
                  <th scope="col">{t('work.status')}</th>
                  <th scope="col">{t('work.where')}</th>
                  <th scope="col">{t('work.assignedTo')}</th>
                  <th scope="col">{t('work.due')}</th>
                  <th scope="col">{t('work.sla')}</th>
                </tr>
              </thead>
              <tbody>
                {query.data?.items.map((w) => (
                  <tr key={w.id}>
                    <td>
                      <Link to={`/work-orders/${w.id}`}>
                        <bdi>{w.displayNumber}</bdi>
                      </Link>
                    </td>
                    <td>
                      <bdi>{w.title}</bdi>
                    </td>
                    <td>
                      <Badge tone={priorityTone(w.priority)}>{priorityLabel(w.priority)}</Badge>
                    </td>
                    <td>
                      <Badge tone={workStatusTone(w.status)}>{statusLabel(w.status)}</Badge>
                    </td>
                    <td>
                      <bdi>{w.assetName ?? w.locationName ?? ''}</bdi>
                    </td>
                    <td>
                      <bdi>{w.assignedToName ?? '—'}</bdi>
                    </td>
                    <td>
                      {w.dueAt ? date(w.dueAt, true) : '—'} {w.isOverdue ? <Badge tone="bad">{t('work.overdue')}</Badge> : null}
                    </td>
                    <td>{w.sla.state === 'None' ? '—' : <Badge tone={slaTone(w.sla.state)}>{slaLabel(w.sla.state)}</Badge>}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {query.data ? <Pager page={filters.page} pageSize={25} total={query.data.total} onChange={(page) => set({ page })} /> : null}
      </QueryState>
    </section>
  );
}
