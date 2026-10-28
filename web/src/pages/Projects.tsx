import { useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import { usePermissions, useProject, useProjectCosts, useProjects, useUsers } from '../api/hooks';
import type { BudgetCategory, LinkKind, ProjectBudgetLine, ProjectDetail, ProjectMilestone, ProjectPhase, ProjectStatus, ProjectSummary, ProjectTask, WorkItemStatus } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Dialog } from '../components/InventoryBits';
import { LocationSelect } from '../components/LocationSelect';
import { Badge, ErrorBanner, Field, Pager, QueryState, ReasonDialog, useEnumLabel } from '../components/ui';
import { formatNumber } from '../lib/format';
import { usePrefs } from '../lib/prefs';

const STATUSES: ProjectStatus[] = ['Planning', 'Active', 'OnHold', 'Completed', 'Cancelled'];
const CATEGORIES: BudgetCategory[] = ['Design', 'Construction', 'Equipment', 'Furniture', 'Professional', 'Contingency', 'Other'];
const TASK_STATUSES: WorkItemStatus[] = ['ToDo', 'InProgress', 'Blocked', 'Done'];
const SECTIONS = ['plan', 'budget', 'links'] as const;
const asError = (e: unknown) => (e instanceof ApiError ? e : null);

function statusTone(s: ProjectStatus): 'ok' | 'warn' | 'bad' | 'neutral' {
  return s === 'Active' || s === 'Completed' ? 'ok' : s === 'OnHold' ? 'warn' : s === 'Cancelled' ? 'bad' : 'neutral';
}

function useRefresh() {
  const client = useQueryClient();
  return () => client.invalidateQueries({ queryKey: ['projects'] });
}

function useMoney() {
  const { i18n } = useTranslation();
  return (v: number | null | undefined, c?: string) => (v == null ? '—' : `${formatNumber(v, i18n.language, 2)} ${c ?? ''}`.trim());
}

export function ProjectsPage() {
  const [params] = useSearchParams();
  const id = params.get('project');
  return id ? <ProjectView id={id} /> : <ProjectList />;
}

// ---------------------------------------------------------------- list

function ProjectList() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const { date } = usePrefs();
  const [, setParams] = useSearchParams();
  const statusLabel = useEnumLabel('projectStatus');
  const money = useMoney();
  const [status, setStatus] = useState('');
  const [q, setQ] = useState('');
  const [page, setPage] = useState(1);
  const [creating, setCreating] = useState(false);
  const list = useProjects({ status: status || undefined, q: q.trim() || undefined, page, pageSize: 25 });
  const change = (fn: () => void) => { fn(); setPage(1); };

  return (
    <section>
      <h1>{t('projects.title')}</h1>
      <p className="muted">{t('projects.intro')}</p>
      <div className="toolbar">
        <Field label={t('projects.status')}>
          <select value={status} onChange={(e) => change(() => setStatus(e.target.value))}>
            <option value="">{t('common.all')}</option>
            {STATUSES.map((s) => <option key={s} value={s}>{statusLabel(s)}</option>)}
          </select>
        </Field>
        <Field label={t('common.search')}><input value={q} onChange={(e) => change(() => setQ(e.target.value))} /></Field>
        {can('projects.manage') ? <button type="button" className="primary" onClick={() => setCreating(true)}>{t('projects.new')}</button> : null}
      </div>
      <QueryState query={list}>
        {list.data && list.data.items.length === 0 ? <p className="muted">{t('projects.none')}</p> : null}
        {list.data && list.data.items.length > 0 ? (
          <>
            <div className="table-wrap" tabIndex={0} role="region" aria-label={t('projects.title')}>
              <table>
                <caption className="visually-hidden">{t('projects.title')}</caption>
                <thead>
                  <tr>
                    <th scope="col">{t('projects.project')}</th>
                    <th scope="col">{t('projects.status')}</th>
                    <th scope="col">{t('projects.manager')}</th>
                    <th scope="col">{t('projects.dates')}</th>
                    <th scope="col">{t('projects.progress')}</th>
                    <th scope="col">{t('projects.budget')}</th>
                  </tr>
                </thead>
                <tbody>
                  {list.data.items.map((p: ProjectSummary) => (
                    <tr key={p.id}>
                      <th scope="row">
                        <button type="button" className="link" onClick={() => setParams({ project: p.id })}><bdi>{p.code}</bdi> <bdi>{p.name}</bdi></button>
                      </th>
                      <td><Badge tone={statusTone(p.status)}>{statusLabel(p.status)}</Badge></td>
                      <td><bdi>{p.managerName ?? '—'}</bdi></td>
                      <td>
                        {date(p.startOn)} → {date(p.endOn)} {p.endOverdue ? <Badge tone="bad">{t('projects.late')}</Badge> : null}
                      </td>
                      <td>{p.percentDone}% ({p.tasksDone}/{p.tasksTotal}){p.milestonesLate > 0 ? <> <Badge tone="bad">{t('projects.lateMilestones', { count: p.milestonesLate })}</Badge></> : null}</td>
                      <td>{money(p.budget, p.currencyCode)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <Pager page={page} pageSize={25} total={list.data.total} onChange={setPage} />
          </>
        ) : null}
      </QueryState>
      {creating ? <ProjectForm project={null} onClose={() => setCreating(false)} onSaved={(id) => { setCreating(false); setParams({ project: id }); }} /> : null}
    </section>
  );
}

function ProjectForm({ project, onClose, onSaved }: { project: ProjectDetail | null; onClose: () => void; onSaved?: (id: string) => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { can } = usePermissions();
  const refresh = useRefresh();
  const users = useUsers();
  const [code, setCode] = useState(project?.code ?? '');
  const [name, setName] = useState(project?.name ?? '');
  const [description, setDescription] = useState(project?.description ?? '');
  const [managerId, setManagerId] = useState(project?.managerId ?? '');
  const [locationId, setLocationId] = useState(project?.locationId ?? '');
  const [startOn, setStartOn] = useState(project?.startOn ?? '');
  const [endOn, setEndOn] = useState(project?.endOn ?? '');
  const [currency, setCurrency] = useState(project?.currencyCode ?? 'SAR');
  const save = useMutation({
    mutationFn: () => {
      const body = { code, name, description: description || null, managerId: managerId || null, locationId: locationId || null, startOn: startOn || null, endOn: endOn || null, currencyCode: currency, rowVersion: project?.rowVersion ?? null };
      return project ? api.put<ProjectDetail>(`/projects/${project.id}`, body) : api.post<ProjectDetail>('/projects', body);
    },
    onSuccess: async (saved) => { await refresh(); (onSaved ?? onClose)(saved.id); },
  });
  const errors = asError(save.error)?.fieldErrors ?? {};

  return (
    <Dialog title={project ? t('projects.edit') : t('projects.new')} onClose={onClose} wide>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }} noValidate>
        <div className="row">
          <Field label={t('projects.code')} error={errors.code?.[0]} required><input value={code} onChange={(e) => setCode(e.target.value)} maxLength={30} autoFocus /></Field>
          <Field label={t('projects.name')} error={errors.name?.[0]} required><input value={name} onChange={(e) => setName(e.target.value)} maxLength={200} /></Field>
        </div>
        <div className="row">
          <Field label={t('projects.startOn')} error={errors.startOn?.[0]} required><input type="date" value={startOn} onChange={(e) => setStartOn(e.target.value)} /></Field>
          <Field label={t('projects.endOn')} error={errors.endOn?.[0]} required><input type="date" value={endOn} onChange={(e) => setEndOn(e.target.value)} /></Field>
          <Field label={t('projects.currency')} hint={t('projects.currencyHint')} error={errors.currencyCode?.[0]}><input value={currency} onChange={(e) => setCurrency(e.target.value.toUpperCase())} maxLength={3} /></Field>
        </div>
        <div className="row">
          <Field label={t('projects.place')} error={errors.locationId?.[0]}><LocationSelect value={locationId} onChange={setLocationId} /></Field>
          {can('users.read') ? (
            <Field label={t('projects.manager')} error={errors.managerId?.[0]}>
              <select value={managerId} onChange={(e) => setManagerId(e.target.value)}>
                <option value="">{t('common.notSet')}</option>
                {(users.data ?? []).filter((u) => u.isActive).map((u) => <option key={u.id} value={u.id}>{u.displayName ?? u.email}</option>)}
              </select>
            </Field>
          ) : null}
        </div>
        <Field label={t('projects.description')} error={errors.description?.[0]}><textarea value={description} onChange={(e) => setDescription(e.target.value)} rows={3} maxLength={2000} /></Field>
        <ErrorBanner error={Object.keys(errors).length ? null : save.error} />
        <div className="actions">
          <button type="button" onClick={onClose}>{t('common.cancel')}</button>
          <button type="submit" className="primary" disabled={save.isPending}>{t('common.save')}</button>
        </div>
      </form>
    </Dialog>
  );
}

// ---------------------------------------------------------------- one project

function ProjectView({ id }: { id: string }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { can } = usePermissions();
  const { date } = usePrefs();
  const [params, setParams] = useSearchParams();
  const refresh = useRefresh();
  const statusLabel = useEnumLabel('projectStatus');
  const detail = useProject(id);
  const [editing, setEditing] = useState(false);
  const [changing, setChanging] = useState<ProjectStatus | null>(null);
  const p = detail.data;
  const section = SECTIONS.find((s) => s === params.get('section')) ?? 'plan';
  const canManage = can('projects.manage');

  const change = useMutation({
    mutationFn: ({ status, reason }: { status: ProjectStatus; reason: string }) => api.post(`/projects/${id}/status`, { status, reason: reason || null, rowVersion: p?.rowVersion }),
    onSuccess: async () => { setChanging(null); await refresh(); },
  });

  return (
    <section>
      <p><button type="button" className="link" onClick={() => setParams({})}>← {t('projects.back')}</button></p>
      <QueryState query={detail}>
        {p ? (
          <>
            <h1><bdi>{p.code}</bdi> <bdi>{p.name}</bdi></h1>
            <p>
              <Badge tone={statusTone(p.status)}>{statusLabel(p.status)}</Badge> {date(p.startOn)} → {date(p.endOn)}
              {p.locationName ? <> · <bdi>{p.locationName}</bdi></> : null}
              {p.managerName ? <> · <bdi>{p.managerName}</bdi></> : null}
              {' · '}{t('projects.percentDone', { percent: p.percentDone })}
            </p>
            {p.description ? <p><bdi>{p.description}</bdi></p> : null}
            {p.closedOn ? <p className="muted">{t('projects.closedOn', { date: date(p.closedOn) })}{p.closeReason ? <> · <bdi>{p.closeReason}</bdi></> : null}</p> : null}
            {canManage ? (
              <div className="actions">
                <button type="button" onClick={() => setEditing(true)}>{t('common.edit')}</button>
                {p.nextStatuses.map((s) => (
                  <button key={s} type="button" onClick={() => setChanging(s)}>{t('projects.moveTo', { status: statusLabel(s) })}</button>
                ))}
              </div>
            ) : null}
            <div className="tabs" role="tablist" aria-label={t('projects.sections')}>
              {SECTIONS.map((s) => (
                <button key={s} role="tab" type="button" aria-selected={section === s} className={section === s ? 'active' : ''} onClick={() => setParams({ project: id, section: s })}>{t(`projects.section.${s}`)}</button>
              ))}
            </div>
            <div role="tabpanel">
              {section === 'plan' ? <PlanSection p={p} canManage={canManage} /> : section === 'budget' ? <BudgetSection p={p} canManage={canManage} /> : <LinksSection p={p} canManage={canManage} />}
            </div>
            {editing ? <ProjectForm project={p} onClose={() => setEditing(false)} /> : null}
            {changing ? (
              <ReasonDialog
                title={t('projects.moveTo', { status: statusLabel(changing) })}
                label={t('projects.reason')}
                confirmLabel={t('common.confirm')}
                optional={changing !== 'Cancelled' && changing !== 'OnHold'}
                busy={change.isPending}
                error={change.error}
                onConfirm={(reason) => change.mutate({ status: changing, reason })}
                onCancel={() => setChanging(null)}
              />
            ) : null}
          </>
        ) : null}
      </QueryState>
    </section>
  );
}

type Removal = { path: string; title: string };

function useRemoval() {
  const { api } = useAuth();
  const refresh = useRefresh();
  const [target, setTarget] = useState<Removal | null>(null);
  const remove = useMutation({
    mutationFn: (reason: string) => api.post(target!.path, { reason }),
    onSuccess: async () => { setTarget(null); await refresh(); },
  });
  return { target, setTarget, remove };
}

function RemovalDialog({ removal }: { removal: ReturnType<typeof useRemoval> }) {
  const { t } = useTranslation();
  const { target, setTarget, remove } = removal;
  return target ? (
    <ReasonDialog title={target.title} label={t('projects.reason')} confirmLabel={t('common.confirm')} busy={remove.isPending} error={remove.error} onConfirm={(r) => remove.mutate(r)} onCancel={() => setTarget(null)} />
  ) : null;
}

function PlanSection({ p, canManage }: { p: ProjectDetail; canManage: boolean }) {
  const { t } = useTranslation();
  const { date } = usePrefs();
  const taskLabel = useEnumLabel('workItemStatus');
  const removal = useRemoval();
  const [phase, setPhase] = useState<ProjectPhase | 'new' | null>(null);
  const [task, setTask] = useState<ProjectTask | 'new' | null>(null);
  const [milestone, setMilestone] = useState<ProjectMilestone | 'new' | null>(null);
  const open = p.status !== 'Completed' && p.status !== 'Cancelled';
  const phaseName = (id: string | null) => p.phases.find((x) => x.id === id)?.name ?? '';

  return (
    <>
      <h2>{t('projects.phases')}</h2>
      {p.phases.length === 0 ? <p className="muted">{t('projects.noPhases')}</p> : (
        <ul className="plain">
          {p.phases.map((x) => (
            <li key={x.id}>
              <bdi>{x.name}</bdi>
              {canManage && open ? (
                <>
                  {' '}<button type="button" className="link" aria-label={t('projects.editX', { name: x.name })} onClick={() => setPhase(x)}>{t('common.edit')}</button>
                  {' '}<button type="button" className="link" aria-label={t('projects.deleteX', { name: x.name })} onClick={() => removal.setTarget({ path: `/projects/phases/${x.id}/delete`, title: t('projects.deleteX', { name: x.name }) })}>{t('common.delete')}</button>
                </>
              ) : null}
            </li>
          ))}
        </ul>
      )}
      {canManage && open ? <div className="actions"><button type="button" onClick={() => setPhase('new')}>{t('projects.newPhase')}</button></div> : null}

      <h2>{t('projects.tasks')}</h2>
      {p.tasks.length === 0 ? <p className="muted">{t('projects.noTasks')}</p> : (
        <div className="table-wrap" tabIndex={0} role="region" aria-label={t('projects.tasks')}>
          <table>
            <caption className="visually-hidden">{t('projects.tasks')}</caption>
            <thead>
              <tr>
                <th scope="col">{t('projects.task')}</th>
                <th scope="col">{t('projects.phase')}</th>
                <th scope="col">{t('projects.assignee')}</th>
                <th scope="col">{t('projects.dueOn')}</th>
                <th scope="col">{t('projects.status')}</th>
                <th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th>
              </tr>
            </thead>
            <tbody>
              {p.tasks.map((x) => (
                <tr key={x.id}>
                  <th scope="row"><bdi>{x.title}</bdi></th>
                  <td><bdi>{phaseName(x.phaseId)}</bdi></td>
                  <td><bdi>{x.assigneeName ?? '—'}</bdi></td>
                  <td>{x.dueOn ? date(x.dueOn) : '—'} {x.isOverdue ? <Badge tone="bad">{t('projects.late')}</Badge> : null}</td>
                  <td><Badge tone={x.status === 'Done' ? 'ok' : x.status === 'Blocked' ? 'bad' : 'neutral'}>{taskLabel(x.status)}</Badge></td>
                  <td>
                    {canManage && open ? (
                      <>
                        <button type="button" className="link" aria-label={t('projects.editX', { name: x.title })} onClick={() => setTask(x)}>{t('common.edit')}</button>
                        {' '}<button type="button" className="link" aria-label={t('projects.deleteX', { name: x.title })} onClick={() => removal.setTarget({ path: `/projects/tasks/${x.id}/delete`, title: t('projects.deleteX', { name: x.title }) })}>{t('common.delete')}</button>
                      </>
                    ) : null}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {canManage && open ? <div className="actions"><button type="button" onClick={() => setTask('new')}>{t('projects.newTask')}</button></div> : null}

      <h2>{t('projects.milestones')}</h2>
      {p.milestones.length === 0 ? <p className="muted">{t('projects.noMilestones')}</p> : (
        <ul className="plain">
          {p.milestones.map((m) => (
            <li key={m.id}>
              <bdi>{m.name}</bdi> · {date(m.dueOn)}{' '}
              {m.achievedOn ? <Badge tone="ok">{t('projects.achievedOn', { date: date(m.achievedOn) })}</Badge> : m.isLate ? <Badge tone="bad">{t('projects.late')}</Badge> : null}
              {canManage && open ? (
                <>
                  {!m.achievedOn ? <> <AchieveButton milestone={m} /></> : null}
                  {' '}<button type="button" className="link" aria-label={t('projects.editX', { name: m.name })} onClick={() => setMilestone(m)}>{t('common.edit')}</button>
                  {' '}<button type="button" className="link" aria-label={t('projects.deleteX', { name: m.name })} onClick={() => removal.setTarget({ path: `/projects/milestones/${m.id}/delete`, title: t('projects.deleteX', { name: m.name }) })}>{t('common.delete')}</button>
                </>
              ) : null}
            </li>
          ))}
        </ul>
      )}
      {canManage && open ? <div className="actions"><button type="button" onClick={() => setMilestone('new')}>{t('projects.newMilestone')}</button></div> : null}

      {phase ? <PhaseForm projectId={p.id} phase={phase === 'new' ? null : phase} onClose={() => setPhase(null)} /> : null}
      {task ? <TaskForm project={p} task={task === 'new' ? null : task} onClose={() => setTask(null)} /> : null}
      {milestone ? <MilestoneForm projectId={p.id} milestone={milestone === 'new' ? null : milestone} onClose={() => setMilestone(null)} /> : null}
      <RemovalDialog removal={removal} />
    </>
  );
}

function AchieveButton({ milestone }: { milestone: ProjectMilestone }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const refresh = useRefresh();
  const achieve = useMutation({ mutationFn: () => api.post(`/projects/milestones/${milestone.id}/achieve`, { rowVersion: milestone.rowVersion }), onSuccess: () => refresh() });
  return (
    <>
      <button type="button" className="link" aria-label={t('projects.achieveX', { name: milestone.name })} disabled={achieve.isPending} onClick={() => achieve.mutate()}>{t('projects.achieve')}</button>
      <ErrorBanner error={achieve.error} />
    </>
  );
}

function PhaseForm({ projectId, phase, onClose }: { projectId: string; phase: ProjectPhase | null; onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const refresh = useRefresh();
  const [name, setName] = useState(phase?.name ?? '');
  const [order, setOrder] = useState((phase?.sortOrder ?? 0).toString());
  const save = useMutation({
    mutationFn: () => {
      const body = { name, sortOrder: Number(order || 0) };
      return phase ? api.put(`/projects/phases/${phase.id}`, body) : api.post(`/projects/${projectId}/phases`, body);
    },
    onSuccess: async () => { await refresh(); onClose(); },
  });
  const errors = asError(save.error)?.fieldErrors ?? {};
  return (
    <Dialog title={phase ? t('projects.editPhase') : t('projects.newPhase')} onClose={onClose}>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }} noValidate>
        <Field label={t('projects.phaseName')} error={errors.name?.[0]} required><input value={name} onChange={(e) => setName(e.target.value)} maxLength={100} autoFocus /></Field>
        <Field label={t('projects.sortOrder')} error={errors.sortOrder?.[0]}><input type="number" value={order} onChange={(e) => setOrder(e.target.value)} /></Field>
        <ErrorBanner error={Object.keys(errors).length ? null : save.error} />
        <div className="actions">
          <button type="button" onClick={onClose}>{t('common.cancel')}</button>
          <button type="submit" className="primary" disabled={save.isPending}>{t('common.save')}</button>
        </div>
      </form>
    </Dialog>
  );
}

function TaskForm({ project, task, onClose }: { project: ProjectDetail; task: ProjectTask | null; onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { can } = usePermissions();
  const refresh = useRefresh();
  const users = useUsers();
  const taskLabel = useEnumLabel('workItemStatus');
  const [title, setTitle] = useState(task?.title ?? '');
  const [phaseId, setPhaseId] = useState(task?.phaseId ?? '');
  const [assigneeId, setAssigneeId] = useState(task?.assigneeId ?? '');
  const [dueOn, setDueOn] = useState(task?.dueOn ?? '');
  const [status, setStatus] = useState<WorkItemStatus>(task?.status ?? 'ToDo');
  const [notes, setNotes] = useState(task?.notes ?? '');
  const save = useMutation({
    mutationFn: () => {
      const body = { phaseId: phaseId || null, title, assigneeId: assigneeId || null, dueOn: dueOn || null, notes: notes || null, status, rowVersion: task?.rowVersion ?? null };
      return task ? api.put(`/projects/tasks/${task.id}`, body) : api.post(`/projects/${project.id}/tasks`, body);
    },
    onSuccess: async () => { await refresh(); onClose(); },
  });
  const errors = asError(save.error)?.fieldErrors ?? {};
  return (
    <Dialog title={task ? t('projects.editTask') : t('projects.newTask')} onClose={onClose} wide>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }} noValidate>
        <Field label={t('projects.task')} error={errors.title?.[0]} required><input value={title} onChange={(e) => setTitle(e.target.value)} maxLength={200} autoFocus /></Field>
        <div className="row">
          <Field label={t('projects.phase')} error={errors.phaseId?.[0]}>
            <select value={phaseId} onChange={(e) => setPhaseId(e.target.value)}>
              <option value="">{t('common.notSet')}</option>
              {project.phases.map((x) => <option key={x.id} value={x.id}>{x.name}</option>)}
            </select>
          </Field>
          {can('users.read') ? (
            <Field label={t('projects.assignee')} error={errors.assigneeId?.[0]}>
              <select value={assigneeId} onChange={(e) => setAssigneeId(e.target.value)}>
                <option value="">{t('common.notSet')}</option>
                {(users.data ?? []).filter((u) => u.isActive).map((u) => <option key={u.id} value={u.id}>{u.displayName ?? u.email}</option>)}
              </select>
            </Field>
          ) : null}
          <Field label={t('projects.dueOn')} error={errors.dueOn?.[0]}><input type="date" value={dueOn} onChange={(e) => setDueOn(e.target.value)} /></Field>
          <Field label={t('projects.status')} error={errors.status?.[0]}>
            <select value={status} onChange={(e) => setStatus(e.target.value as WorkItemStatus)}>{TASK_STATUSES.map((s) => <option key={s} value={s}>{taskLabel(s)}</option>)}</select>
          </Field>
        </div>
        <Field label={t('projects.notes')} error={errors.notes?.[0]}><textarea value={notes} onChange={(e) => setNotes(e.target.value)} rows={2} maxLength={1000} /></Field>
        <ErrorBanner error={Object.keys(errors).length ? null : save.error} />
        <div className="actions">
          <button type="button" onClick={onClose}>{t('common.cancel')}</button>
          <button type="submit" className="primary" disabled={save.isPending}>{t('common.save')}</button>
        </div>
      </form>
    </Dialog>
  );
}

function MilestoneForm({ projectId, milestone, onClose }: { projectId: string; milestone: ProjectMilestone | null; onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const refresh = useRefresh();
  const [name, setName] = useState(milestone?.name ?? '');
  const [dueOn, setDueOn] = useState(milestone?.dueOn ?? '');
  const save = useMutation({
    mutationFn: () => {
      const body = { name, dueOn: dueOn || null, rowVersion: milestone?.rowVersion ?? null };
      return milestone ? api.put(`/projects/milestones/${milestone.id}`, body) : api.post(`/projects/${projectId}/milestones`, body);
    },
    onSuccess: async () => { await refresh(); onClose(); },
  });
  const errors = asError(save.error)?.fieldErrors ?? {};
  return (
    <Dialog title={milestone ? t('projects.editMilestone') : t('projects.newMilestone')} onClose={onClose}>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }} noValidate>
        <Field label={t('projects.milestoneName')} error={errors.name?.[0]} required><input value={name} onChange={(e) => setName(e.target.value)} maxLength={200} autoFocus /></Field>
        <Field label={t('projects.dueOn')} error={errors.dueOn?.[0]} required><input type="date" value={dueOn} onChange={(e) => setDueOn(e.target.value)} /></Field>
        <ErrorBanner error={Object.keys(errors).length ? null : save.error} />
        <div className="actions">
          <button type="button" onClick={onClose}>{t('common.cancel')}</button>
          <button type="submit" className="primary" disabled={save.isPending}>{t('common.save')}</button>
        </div>
      </form>
    </Dialog>
  );
}

// ---------------------------------------------------------------- budget and costs

function BudgetSection({ p, canManage }: { p: ProjectDetail; canManage: boolean }) {
  const { t } = useTranslation();
  const categoryLabel = useEnumLabel('budgetCategory');
  const money = useMoney();
  const costs = useProjectCosts(p.id);
  const removal = useRemoval();
  const [line, setLine] = useState<ProjectBudgetLine | 'new' | null>(null);
  const open = p.status !== 'Completed' && p.status !== 'Cancelled';

  return (
    <>
      <h2>{t('projects.budgetLines')}</h2>
      {p.budget.length === 0 ? <p className="muted">{t('projects.noBudget')}</p> : (
        <div className="table-wrap" tabIndex={0} role="region" aria-label={t('projects.budgetLines')}>
          <table>
            <caption className="visually-hidden">{t('projects.budgetLines')}</caption>
            <thead>
              <tr>
                <th scope="col">{t('projects.category')}</th>
                <th scope="col">{t('projects.description')}</th>
                <th scope="col">{t('projects.amount')}</th>
                <th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th>
              </tr>
            </thead>
            <tbody>
              {p.budget.map((b) => (
                <tr key={b.id}>
                  <th scope="row">{categoryLabel(b.category)}</th>
                  <td><bdi>{b.description}</bdi></td>
                  <td>{money(b.amount, p.currencyCode)}</td>
                  <td>
                    {canManage && open ? (
                      <>
                        <button type="button" className="link" aria-label={t('projects.editX', { name: b.description })} onClick={() => setLine(b)}>{t('common.edit')}</button>
                        {' '}<button type="button" className="link" aria-label={t('projects.deleteX', { name: b.description })} onClick={() => removal.setTarget({ path: `/projects/budget/${b.id}/delete`, title: t('projects.deleteX', { name: b.description }) })}>{t('common.delete')}</button>
                      </>
                    ) : null}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {canManage && open ? <div className="actions"><button type="button" onClick={() => setLine('new')}>{t('projects.newBudgetLine')}</button></div> : null}

      <h2>{t('projects.costs')}</h2>
      <QueryState query={costs}>
        {costs.data ? (
          <>
            <ul className="cards plain" aria-label={t('projects.costs')}>
              <li className="card"><span className="value">{money(costs.data.budget, costs.data.currencyCode)}</span><span className="muted">{t('projects.budget')}</span></li>
              <li className="card"><span className="value">{money(costs.data.spent, costs.data.currencyCode)}</span><span className="muted">{t('projects.spent')}</span></li>
              <li className="card"><span className="value">{money(costs.data.committed, costs.data.currencyCode)}</span><span className="muted">{t('projects.committed')}</span></li>
              <li className="card"><span className="value">{money(costs.data.remaining, costs.data.currencyCode)}</span><span className="muted">{t('projects.remaining')}</span></li>
            </ul>
            {costs.data.overBudget ? <p role="status"><Badge tone="bad">{t('projects.overBudget')}</Badge></p> : null}
            <div className="table-wrap" tabIndex={0} role="region" aria-label={t('projects.byCategory')}>
              <table>
                <caption className="visually-hidden">{t('projects.byCategory')}</caption>
                <thead>
                  <tr>
                    <th scope="col">{t('projects.category')}</th>
                    <th scope="col">{t('projects.budget')}</th>
                    <th scope="col">{t('projects.spent')}</th>
                    <th scope="col">{t('projects.committed')}</th>
                    <th scope="col">{t('projects.remaining')}</th>
                  </tr>
                </thead>
                <tbody>
                  {costs.data.byCategory.map((r) => (
                    <tr key={r.category}>
                      <th scope="row">{categoryLabel(r.category)}</th>
                      <td>{money(r.budget)}</td>
                      <td>{money(r.spent)}</td>
                      <td>{money(r.committed)}</td>
                      <td>{money(r.remaining)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </>
        ) : null}
      </QueryState>
      {line ? <BudgetForm project={p} line={line === 'new' ? null : line} onClose={() => setLine(null)} /> : null}
      <RemovalDialog removal={removal} />
    </>
  );
}

function BudgetForm({ project, line, onClose }: { project: ProjectDetail; line: ProjectBudgetLine | null; onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const refresh = useRefresh();
  const categoryLabel = useEnumLabel('budgetCategory');
  const [category, setCategory] = useState<BudgetCategory>(line?.category ?? 'Construction');
  const [description, setDescription] = useState(line?.description ?? '');
  const [amount, setAmount] = useState(line?.amount.toString() ?? '');
  const save = useMutation({
    mutationFn: () => {
      const body = { category, description, amount: amount.trim() ? Number(amount) : null, rowVersion: line?.rowVersion ?? null };
      return line ? api.put(`/projects/budget/${line.id}`, body) : api.post(`/projects/${project.id}/budget`, body);
    },
    onSuccess: async () => { await refresh(); onClose(); },
  });
  const errors = asError(save.error)?.fieldErrors ?? {};
  return (
    <Dialog title={line ? t('projects.editBudgetLine') : t('projects.newBudgetLine')} onClose={onClose}>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }} noValidate>
        <Field label={t('projects.category')} error={errors.category?.[0]}>
          <select value={category} onChange={(e) => setCategory(e.target.value as BudgetCategory)}>{CATEGORIES.map((c) => <option key={c} value={c}>{categoryLabel(c)}</option>)}</select>
        </Field>
        <Field label={t('projects.description')} error={errors.description?.[0]} required><input value={description} onChange={(e) => setDescription(e.target.value)} maxLength={200} autoFocus /></Field>
        <Field label={t('projects.amountIn', { currency: project.currencyCode })} error={errors.amount?.[0]} required><input type="number" min={0} step="any" value={amount} onChange={(e) => setAmount(e.target.value)} /></Field>
        <ErrorBanner error={Object.keys(errors).length ? null : save.error} />
        <div className="actions">
          <button type="button" onClick={onClose}>{t('common.cancel')}</button>
          <button type="submit" className="primary" disabled={save.isPending}>{t('common.save')}</button>
        </div>
      </form>
    </Dialog>
  );
}

// ---------------------------------------------------------------- linked work orders and purchase orders

function LinksSection({ p, canManage }: { p: ProjectDetail; canManage: boolean }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const refresh = useRefresh();
  const categoryLabel = useEnumLabel('budgetCategory');
  const kindLabel = useEnumLabel('linkKind');
  const money = useMoney();
  const costs = useProjectCosts(p.id);
  const removal = useRemoval();
  const open = p.status !== 'Completed' && p.status !== 'Cancelled';
  const [kind, setKind] = useState<LinkKind>('WorkOrder');
  const [number, setNumber] = useState('');
  const [category, setCategory] = useState<BudgetCategory>('Construction');
  const link = useMutation({
    mutationFn: () => api.post(`/projects/${p.id}/links`, { kind, number, category }),
    onSuccess: async () => { setNumber(''); await refresh(); },
  });
  const errors = asError(link.error)?.fieldErrors ?? {};
  const cost = (linkId: string) => costs.data?.items.find((i) => i.linkId === linkId);

  return (
    <>
      <p className="muted">{t('projects.linksIntro')}</p>
      {p.links.length === 0 ? <p className="muted">{t('projects.noLinks')}</p> : (
        <div className="table-wrap" tabIndex={0} role="region" aria-label={t('projects.section.links')}>
          <table>
            <caption className="visually-hidden">{t('projects.section.links')}</caption>
            <thead>
              <tr>
                <th scope="col">{t('projects.number')}</th>
                <th scope="col">{t('projects.kind')}</th>
                <th scope="col">{t('projects.category')}</th>
                <th scope="col">{t('projects.spent')}</th>
                <th scope="col">{t('projects.committed')}</th>
                <th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th>
              </tr>
            </thead>
            <tbody>
              {p.links.map((l) => {
                const c = cost(l.id);
                return (
                  <tr key={l.id}>
                    <th scope="row"><bdi>{l.refNumber}</bdi>{c ? <> <bdi className="muted">{c.title}</bdi></> : null}</th>
                    <td>{kindLabel(l.kind)}</td>
                    <td>{categoryLabel(l.category)}</td>
                    <td>{c ? (c.excluded ? <span title={c.excludedBecause ?? undefined}>{t('projects.excluded')}</span> : money(c.spent, p.currencyCode)) : '…'}</td>
                    <td>{c && !c.excluded ? money(c.committed, p.currencyCode) : ''}</td>
                    <td>
                      {canManage && open ? (
                        <button type="button" className="link" aria-label={t('projects.unlinkX', { number: l.refNumber })} onClick={() => removal.setTarget({ path: `/projects/links/${l.id}/unlink`, title: t('projects.unlinkX', { number: l.refNumber }) })}>{t('projects.unlink')}</button>
                      ) : null}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
      {canManage && open ? (
        <form onSubmit={(e) => { e.preventDefault(); link.mutate(); }} noValidate>
          <h2>{t('projects.addLink')}</h2>
          <div className="row">
            <Field label={t('projects.kind')} error={errors.kind?.[0]}>
              <select value={kind} onChange={(e) => setKind(e.target.value as LinkKind)}>
                <option value="WorkOrder">{kindLabel('WorkOrder')}</option>
                <option value="PurchaseOrder">{kindLabel('PurchaseOrder')}</option>
              </select>
            </Field>
            <Field label={t('projects.number')} hint={t('projects.numberHint')} error={errors.number?.[0]}><input value={number} onChange={(e) => setNumber(e.target.value)} maxLength={30} /></Field>
            <Field label={t('projects.chargedTo')} error={errors.category?.[0]}>
              <select value={category} onChange={(e) => setCategory(e.target.value as BudgetCategory)}>{CATEGORIES.map((c) => <option key={c} value={c}>{categoryLabel(c)}</option>)}</select>
            </Field>
          </div>
          <ErrorBanner error={Object.keys(errors).length ? null : link.error} />
          <div className="actions"><button type="submit" className="primary" disabled={link.isPending}>{t('projects.link')}</button></div>
        </form>
      ) : null}
      <RemovalDialog removal={removal} />
    </>
  );
}
