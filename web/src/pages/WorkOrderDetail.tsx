import { useState } from 'react';
import { Link, useParams, useSearchParams } from 'react-router-dom';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import { useAssignees, usePermissions, useWorkOrder } from '../api/hooks';
import type { ClosureCode, WorkOrder, WorkOrderAction, WorkPriority } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Badge, ErrorBanner, Field, QueryState, ReasonDialog, useEnumLabel } from '../components/ui';
import { usePrefs } from '../lib/prefs';
import { PRIORITIES, priorityTone, slaTone, workStatusTone } from './WorkOrders';
import { ChecklistTab, CostsTab, FilesTab, SignaturesTab } from './WorkOrderEvidence';
import { PartsTab } from './WorkParts';
import { VendorRows } from './WorkVendor';
import { SummaryPanel } from '../components/AiAssist';

const CLOSURE_CODES: ClosureCode[] = ['Fixed', 'NoFaultFound', 'TemporaryFix', 'PartsRequired', 'Duplicate', 'NotRequired'];

/** Verb endpoint for each action the server can allow. Edited is a form, not a verb. */
const VERBS: Partial<Record<WorkOrderAction, string>> = {
  Approved: 'approve', Rejected: 'reject', Assigned: 'assign', Started: 'start', Held: 'hold', Resumed: 'resume',
  Completed: 'complete', Verified: 'verify', Closed: 'close', Reopened: 'reopen', Cancelled: 'cancel',
};
const ORDER: WorkOrderAction[] = ['Approved', 'Rejected', 'Assigned', 'Started', 'Held', 'Resumed', 'Completed', 'Verified', 'Closed', 'Reopened', 'Cancelled', 'Edited'];
const NEEDS_REASON: WorkOrderAction[] = ['Rejected', 'Held', 'Reopened', 'Cancelled'];

export function WorkOrderDetailPage() {
  const { id } = useParams();
  const { t } = useTranslation();
  const { date } = usePrefs();
  const query = useWorkOrder(id);
  const statusLabel = useEnumLabel('workStatus');
  const priorityLabel = useEnumLabel('workPriority');
  const typeLabel = useEnumLabel('workType');
  const closureLabel = useEnumLabel('closureCode');
  const slaLabel = useEnumLabel('slaState');
  const [dialog, setDialog] = useState<WorkOrderAction | null>(null);
  const [params, setParams] = useSearchParams();
  const { can } = usePermissions();
  const tabs = ['overview', 'checklist', ...(can('workorders.read') ? ['costs'] : []), ...(can('inventory.read') ? ['parts'] : []), 'files', 'signatures'];
  const tab = tabs.find((x) => x === params.get('tab')) ?? 'overview';

  const detail = query.data;
  const o = detail?.order;

  return (
    <section>
      <p>
        <Link to="/work-orders">← {t('nav.workOrders')}</Link>
      </p>
      <QueryState query={query}>
        {detail && o ? (
          <>
            <header className="toolbar">
              <div>
                <h1>
                  <bdi>{o.displayNumber}</bdi> · <bdi>{o.title}</bdi>
                </h1>
                <p>
                  <Badge tone={workStatusTone(o.status)}>{statusLabel(o.status)}</Badge> <Badge tone={priorityTone(o.priority)}>{priorityLabel(o.priority)}</Badge>{' '}
                  {o.type !== 'Corrective' ? <Badge tone="neutral">{typeLabel(o.type)}</Badge> : null} {o.isOverdue ? <Badge tone="bad">{t('work.overdue')}</Badge> : null}
                </p>
              </div>
              <div className="actions" role="group" aria-label={t('work.actions')}>
                {ORDER.filter((a) => o.allowed.includes(a)).map((a) => (
                  <button key={a} type="button" className={a === 'Approved' || a === 'Completed' || a === 'Started' ? 'primary' : undefined} onClick={() => setDialog(a)}>
                    {t(`workAction.${a}`)}
                  </button>
                ))}
              </div>
            </header>

            <SummaryPanel workOrderId={o.id} />

            {o.sla.state !== 'None' ? (
              <div className="banner" role="group" aria-label={t('work.sla')}>
                <strong>{t('work.sla')}:</strong> <Badge tone={slaTone(o.sla.state)}>{slaLabel(o.sla.state)}</Badge>{' '}
                <span>
                  {t('work.respondBy')}: {o.sla.respondedAt ? `${date(o.sla.respondedAt, true)} (${o.sla.responseBreached ? t('work.late') : t('work.onTime')})` : o.sla.responseDueAt ? date(o.sla.responseDueAt, true) : '—'}
                </span>{' · '}
                <span>
                  {t('work.resolveBy')}: {o.sla.paused ? t('work.clockPaused') : o.sla.resolutionDueAt ? date(o.sla.resolutionDueAt, true) : '—'}
                </span>
              </div>
            ) : null}
            {o.statusReason && (o.status === 'OnHold' || o.status === 'Cancelled') ? <p className="banner">{t('work.reason', { reason: o.statusReason })}</p> : null}

            <div className="tabs" role="tablist" aria-label={t('evidence.sections')}>
              {tabs.map((x) => (
                <button key={x} role="tab" type="button" aria-selected={tab === x} className={tab === x ? 'active' : ''} onClick={() => setParams({ tab: x }, { replace: true })}>
                  {t(`evidence.tabs.${x}`)}
                </button>
              ))}
            </div>
            <div role="tabpanel">
              {tab === 'overview' ? (
                <>
            <dl className="details">
              <dt>{t('work.asset')}</dt>
              <dd>
                <bdi>{o.assetName ?? '—'}</bdi>
              </dd>
              <dt>{t('work.where')}</dt>
              <dd>
                <bdi>{o.locationName ?? '—'}</bdi>
              </dd>
              <dt>{t('work.category')}</dt>
              <dd>
                <bdi>{o.categoryName ?? '—'}</bdi>
              </dd>
              <dt>{t('work.requestedBy')}</dt>
              <dd>
                <bdi>{o.requestedByName ?? '—'}</bdi> · {date(o.requestedAt, true)}
              </dd>
              <dt>{t('work.assignedTo')}</dt>
              <dd>
                <bdi>{o.assignedToName ?? '—'}</bdi>
              </dd>
              <VendorRows order={o} />
              <dt>{t('work.due')}</dt>
              <dd>{o.dueAt ? date(o.dueAt, true) : '—'}</dd>
              {o.completedAt ? (
                <>
                  <dt>{t('work.completedAt')}</dt>
                  <dd>{date(o.completedAt, true)}</dd>
                  <dt>{t('work.closure')}</dt>
                  <dd>
                    {closureLabel(o.closureCode)}
                    {o.closureNotes ? <> — <bdi>{o.closureNotes}</bdi></> : null}
                  </dd>
                </>
              ) : null}
            </dl>
            {o.description ? <p className="preline"><bdi>{o.description}</bdi></p> : null}

            <h2>{t('work.history')}</h2>
            <ol className="timeline" aria-label={t('work.history')}>
              {detail.events.map((e) => (
                <li key={e.id}>
                  <strong>{t(`workAction.${e.action}Past`)}</strong>
                  {e.subjectName ? <> → <bdi>{e.subjectName}</bdi></> : null}
                  {' '}<span className="muted">· <bdi>{e.actorName ?? '—'}</bdi> · {date(e.at, true)}</span>
                  {e.note ? <div><bdi>{e.note}</bdi></div> : null}
                </li>
              ))}
            </ol>

            <h2>{t('work.comments')}</h2>
            <Comments order={o} comments={detail.comments} />

                </>
              ) : null}
              {tab === 'checklist' ? <ChecklistTab order={o} /> : null}
              {tab === 'costs' ? <CostsTab order={o} /> : null}
              {tab === 'parts' ? <PartsTab order={o} /> : null}
              {tab === 'files' ? <FilesTab order={o} /> : null}
              {tab === 'signatures' ? <SignaturesTab order={o} /> : null}
            </div>

            {dialog ? <ActionDialog action={dialog} order={o} onClose={() => setDialog(null)} /> : null}
          </>
        ) : null}
      </QueryState>
    </section>
  );
}

function Comments({ order, comments }: { order: WorkOrder; comments: { id: string; authorName: string | null; at: string; body: string; isInternal: boolean }[] }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { date } = usePrefs();
  const { can } = usePermissions();
  const client = useQueryClient();
  const [body, setBody] = useState('');
  const [internal, setInternal] = useState(false);

  const add = useMutation({
    mutationFn: () => api.post(`/work-orders/${order.id}/comments`, { body: body.trim(), isInternal: internal }),
    onSuccess: async () => {
      setBody('');
      setInternal(false);
      await client.invalidateQueries({ queryKey: ['work-orders', order.id] });
    },
  });

  return (
    <>
      {comments.length === 0 ? <p className="muted">{t('work.noComments')}</p> : null}
      <ul className="comments">
        {comments.map((c) => (
          <li key={c.id}>
            <span className="muted">
              <bdi>{c.authorName ?? '—'}</bdi> · {date(c.at, true)} {c.isInternal ? <Badge tone="warn">{t('work.internal')}</Badge> : null}
            </span>
            <div className="preline"><bdi>{c.body}</bdi></div>
          </li>
        ))}
      </ul>
      <form
        onSubmit={(e) => {
          e.preventDefault();
          add.mutate();
        }}
      >
        <Field label={t('work.addComment')}>
          <textarea value={body} onChange={(e) => setBody(e.target.value)} rows={3} maxLength={2000} />
        </Field>
        {can('workorders.read') ? (
          <label className="check">
            <input type="checkbox" checked={internal} onChange={(e) => setInternal(e.target.checked)} /> {t('work.internalOnly')}
          </label>
        ) : null}
        <ErrorBanner error={add.error} />
        <button type="submit" className="primary" disabled={add.isPending || body.trim() === ''}>
          {t('work.post')}
        </button>
      </form>
    </>
  );
}

function ActionDialog({ action, order, onClose }: { action: WorkOrderAction; order: WorkOrder; onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const client = useQueryClient();
  const [reasonFor] = useState(NEEDS_REASON.includes(action));

  const run = useMutation({
    mutationFn: (body: unknown) => api.post(`/work-orders/${order.id}/${VERBS[action]}`, body ?? {}),
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: ['work-orders'] });
      onClose();
    },
  });

  if (action === 'Edited') return <EditDialog order={order} onClose={onClose} />;
  if (reasonFor) {
    return (
      <ReasonDialog
        title={t(`workAction.${action}`)}
        label={t('common.reason')}
        confirmLabel={t(`workAction.${action}`)}
        busy={run.isPending}
        error={run.error}
        onConfirm={(reason) => run.mutate({ reason })}
        onCancel={onClose}
      />
    );
  }
  if (action === 'Assigned') return <AssignDialog order={order} busy={run.isPending} error={run.error} onConfirm={(assigneeId) => run.mutate({ assigneeId })} onClose={onClose} />;
  if (action === 'Completed') return <CompleteDialog busy={run.isPending} error={run.error} onConfirm={(v) => run.mutate(v)} onClose={onClose} />;

  return (
    <div className="overlay" onKeyDown={(e) => e.key === 'Escape' && onClose()}>
      <div className="dialog" role="dialog" aria-modal="true" aria-label={t(`workAction.${action}`)}>
        <h2>{t(`workAction.${action}`)}</h2>
        <p>{t(`workAction.${action}Help`, { defaultValue: '' })}</p>
        <ErrorBanner error={run.error} />
        <div className="actions">
          <button type="button" onClick={onClose}>
            {t('common.cancel')}
          </button>
          <button type="button" className="primary" disabled={run.isPending} onClick={() => run.mutate({})} autoFocus>
            {t('common.confirm')}
          </button>
        </div>
      </div>
    </div>
  );
}

function AssignDialog({ order, busy, error, onConfirm, onClose }: { order: WorkOrder; busy: boolean; error: unknown; onConfirm: (id: string) => void; onClose: () => void }) {
  const { t } = useTranslation();
  const people = useAssignees(true);
  const [who, setWho] = useState(order.assignedTo ?? '');
  return (
    <div className="overlay" onKeyDown={(e) => e.key === 'Escape' && onClose()}>
      <div className="dialog" role="dialog" aria-modal="true" aria-label={t('workAction.Assigned')}>
        <h2>{t('workAction.Assigned')}</h2>
        <form
          onSubmit={(e) => {
            e.preventDefault();
            if (who) onConfirm(who);
          }}
        >
          <Field label={t('work.assignedTo')} required>
            <select value={who} onChange={(e) => setWho(e.target.value)} autoFocus>
              <option value="">{t('common.choose')}</option>
              {(people.data ?? []).map((p) => (
                <option key={p.id} value={p.id}>
                  {p.displayName ?? p.email ?? p.id}
                </option>
              ))}
            </select>
          </Field>
          <ErrorBanner error={error ?? people.error} />
          <div className="actions">
            <button type="button" onClick={onClose}>
              {t('common.cancel')}
            </button>
            <button type="submit" className="primary" disabled={busy || !who}>
              {t('common.confirm')}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

function CompleteDialog({ busy, error, onConfirm, onClose }: { busy: boolean; error: unknown; onConfirm: (v: { code: ClosureCode; notes: string }) => void; onClose: () => void }) {
  const { t } = useTranslation();
  const label = useEnumLabel('closureCode');
  const [code, setCode] = useState<ClosureCode>('Fixed');
  const [notes, setNotes] = useState('');
  const problem = error instanceof ApiError ? error : null;
  return (
    <div className="overlay" onKeyDown={(e) => e.key === 'Escape' && onClose()}>
      <div className="dialog" role="dialog" aria-modal="true" aria-label={t('workAction.Completed')}>
        <h2>{t('workAction.Completed')}</h2>
        <form
          onSubmit={(e) => {
            e.preventDefault();
            onConfirm({ code, notes: notes.trim() });
          }}
        >
          <Field label={t('work.outcome')} required>
            <select value={code} onChange={(e) => setCode(e.target.value as ClosureCode)} autoFocus>
              {CLOSURE_CODES.map((c) => (
                <option key={c} value={c}>
                  {label(c)}
                </option>
              ))}
            </select>
          </Field>
          <Field label={t('work.whatWasDone')} required>
            <textarea value={notes} onChange={(e) => setNotes(e.target.value)} rows={4} maxLength={2000} />
          </Field>
          {problem?.field('checklist') ? <p className="banner error" role="alert">{problem.field('checklist')}</p> : null}
          <ErrorBanner error={problem?.status === 400 ? null : error} />
          <div className="actions">
            <button type="button" onClick={onClose}>
              {t('common.cancel')}
            </button>
            <button type="submit" className="primary" disabled={busy || notes.trim() === ''}>
              {t('common.confirm')}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

function EditDialog({ order, onClose }: { order: WorkOrder; onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const client = useQueryClient();
  const priorityLabel = useEnumLabel('workPriority');
  const [title, setTitle] = useState(order.title);
  const [description, setDescription] = useState(order.description ?? '');
  const [priority, setPriority] = useState<WorkPriority>(order.priority);

  const save = useMutation({
    mutationFn: () =>
      api.put(`/work-orders/${order.id}`, {
        title: title.trim(), description: description.trim() || null, priority, locationId: order.locationId, assetId: order.assetId,
        categoryId: order.categoryId, dueAt: order.dueAt, rowVersion: order.rowVersion,
      }),
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: ['work-orders'] });
      onClose();
    },
  });
  const error = save.error instanceof ApiError ? save.error : null;

  return (
    <div className="overlay" onKeyDown={(e) => e.key === 'Escape' && onClose()}>
      <div className="dialog" role="dialog" aria-modal="true" aria-label={t('workAction.Edited')}>
        <h2>{t('workAction.Edited')}</h2>
        <form
          onSubmit={(e) => {
            e.preventDefault();
            save.mutate();
          }}
        >
          <Field label={t('work.titleField')} error={error?.field('title')} required>
            <input value={title} onChange={(e) => setTitle(e.target.value)} maxLength={200} autoFocus />
          </Field>
          <Field label={t('work.description')} error={error?.field('description')}>
            <textarea value={description} onChange={(e) => setDescription(e.target.value)} rows={4} maxLength={4000} />
          </Field>
          <Field label={t('work.priority')}>
            <select value={priority} onChange={(e) => setPriority(e.target.value as WorkPriority)}>
              {PRIORITIES.map((p) => (
                <option key={p} value={p}>
                  {priorityLabel(p)}
                </option>
              ))}
            </select>
          </Field>
          <ErrorBanner error={error?.status === 400 ? null : save.error} />
          <div className="actions">
            <button type="button" onClick={onClose}>
              {t('common.cancel')}
            </button>
            <button type="submit" className="primary" disabled={save.isPending || title.trim() === ''}>
              {t('common.save')}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
