import { useMemo, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import { useAssetChoices, useAssetClasses, useAssignees, useChecklistTemplates, usePermissions, usePmForecast, usePmSchedules, usePmTemplates } from '../api/hooks';
import type { ForecastItem, PmIntervalUnit, PmSchedule, PmTargetKind, PmTemplate, PmTimeBasis, WorkPriority } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Badge, ErrorBanner, Field, QueryState, useEnumLabel } from '../components/ui';
import { usePrefs } from '../lib/prefs';

const TABS = ['forecast', 'schedules', 'templates'] as const;
const PRIORITIES: WorkPriority[] = ['Low', 'Medium', 'High', 'Urgent'];
const UNITS: PmIntervalUnit[] = ['Day', 'Week', 'Month', 'Year'];

export function PmPage() {
  const { t } = useTranslation();
  const [params, setParams] = useSearchParams();
  const tab = TABS.find((x) => x === params.get('tab')) ?? 'forecast';
  return (
    <section>
      <h1>{t('pm.title')}</h1>
      <p className="muted">{t('pm.intro')}</p>
      <div className="tabs" role="tablist" aria-label={t('pm.sections')}>
        {TABS.map((x) => (
          <button key={x} role="tab" type="button" aria-selected={tab === x} className={tab === x ? 'active' : ''} onClick={() => setParams({ tab: x })}>
            {t(`pm.tab.${x}`)}
          </button>
        ))}
      </div>
      <div role="tabpanel">{tab === 'forecast' ? <ForecastTab /> : tab === 'schedules' ? <SchedulesTab /> : <TemplatesTab />}</div>
    </section>
  );
}

// ---------------- forecast ----------------

const isoDay = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;

function ForecastTab() {
  const { t } = useTranslation();
  const { date, month: monthName, name } = usePrefs();
  const { can } = usePermissions();
  const { api } = useAuth();
  const client = useQueryClient();
  const classes = useAssetClasses();
  const [days, setDays] = useState(90);
  const [classId, setClassId] = useState('');
  const from = useMemo(() => isoDay(new Date()), []);
  const to = useMemo(() => { const d = new Date(); d.setDate(d.getDate() + days); return isoDay(d); }, [days]);
  const forecast = usePmForecast({ from, to, assetClassId: classId || undefined });
  const kindLabel = useEnumLabel('pmKind');
  const statusLabel = useEnumLabel('workStatus');
  const run = useMutation({
    mutationFn: () => api.post<{ workOrdersCreated: number }>('/pm/run'),
    onSuccess: () => client.invalidateQueries({ queryKey: ['pm'] }),
  });

  const byMonth = useMemo(() => {
    const groups = new Map<string, ForecastItem[]>();
    for (const item of forecast.data?.items ?? []) groups.set(item.date.slice(0, 7), [...(groups.get(item.date.slice(0, 7)) ?? []), item]);
    return [...groups.entries()];
  }, [forecast.data]);

  return (
    <>
      <div className="filters">
        <Field label={t('pm.horizon')}>
          <select value={days} onChange={(e) => setDays(Number(e.target.value))}>
            {[30, 90, 180, 365].map((d) => <option key={d} value={d}>{t('pm.nextDays', { count: d })}</option>)}
          </select>
        </Field>
        <Field label={t('pm.assetClass')}>
          <select value={classId} onChange={(e) => setClassId(e.target.value)}>
            <option value="">{t('common.all')}</option>
            {classes.data?.filter((c) => !c.isArchived).map((c) => <option key={c.id} value={c.id}>{name(c)}</option>)}
          </select>
        </Field>
        {can('pm.manage') ? <button type="button" disabled={run.isPending} onClick={() => run.mutate()}>{t('pm.generateNow')}</button> : null}
      </div>
      <ErrorBanner error={run.error} />
      {run.data ? <p role="status">{t('pm.generated', { count: run.data.workOrdersCreated })}</p> : null}
      <QueryState query={forecast}>
        {forecast.data && forecast.data.items.length === 0 ? <p className="muted">{t('pm.noForecast')}</p> : null}
        {forecast.data?.truncated ? <p role="status" className="muted">{t('pm.truncated')}</p> : null}
        {byMonth.map(([month, items]) => (
          <div key={month}>
            <h2>{monthName(month)}</h2>
            <div className="table-wrap">
              <table>
                <caption className="visually-hidden">{t('pm.forecastFor', { month })}</caption>
                <thead>
                  <tr>
                    <th scope="col">{t('pm.due')}</th>
                    <th scope="col">{t('pm.schedule')}</th>
                    <th scope="col">{t('pm.asset')}</th>
                    <th scope="col">{t('assets.status')}</th>
                  </tr>
                </thead>
                <tbody>
                  {items.map((i, n) => (
                    <tr key={`${i.scheduleId}-${i.assetId}-${i.date}-${n}`}>
                      <td>{date(i.date)}</td>
                      <td>{i.scheduleName}{i.templateName ? <div className="muted"><bdi>{i.templateName}</bdi></div> : null}</td>
                      <td><Link to={`/assets/${i.assetId}`}><bdi>{i.assetCode}</bdi> <bdi>{i.assetName}</bdi></Link></td>
                      <td>
                        {i.workOrderId ? <Link to={`/work-orders/${i.workOrderId}`}><bdi>{i.workOrderNumber}</bdi></Link> : null}{' '}
                        <Badge tone={i.overdue ? 'bad' : i.kind === 'Open' ? 'ok' : 'neutral'}>
                          {i.overdue ? t('pm.overdue') : i.kind === 'Open' ? statusLabel(i.status) : kindLabel(i.kind)}
                        </Badge>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        ))}
      </QueryState>
    </>
  );
}

// ---------------- templates ----------------

function TemplatesTab() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { name } = usePrefs();
  const { can } = usePermissions();
  const client = useQueryClient();
  const templates = usePmTemplates(true);
  const prio = useEnumLabel('workPriority');
  const [editing, setEditing] = useState<PmTemplate | 'new' | null>(null);
  const archive = useMutation({
    mutationFn: (x: PmTemplate) => api.post(`/pm/templates/${x.id}/${x.isArchived ? 'restore' : 'archive'}`, { rowVersion: x.rowVersion }),
    onSuccess: () => client.invalidateQueries({ queryKey: ['pm'] }),
  });
  return (
    <>
      <div className="toolbar">
        <h2>{t('pm.tab.templates')}</h2>
        {can('pm.manage') ? <button type="button" onClick={() => setEditing('new')}>{t('pm.newTemplate')}</button> : null}
      </div>
      <p className="muted">{t('pm.templatesHint')}</p>
      <QueryState query={templates}>
        {templates.data && templates.data.length === 0 ? <p className="muted">{t('pm.noTemplates')}</p> : null}
        <ul className="plain">
          {templates.data?.map((x) => (
            <li key={x.id}>
              <strong><bdi>{name(x)}</bdi></strong> <span className="muted">· {prio(x.priority)}{x.checklistName ? ` · ${x.checklistName}` : ''}</span> {x.isArchived ? <Badge tone="neutral">{t('common.archived')}</Badge> : null}{' '}
              {can('pm.manage') ? (
                <>
                  <button type="button" onClick={() => setEditing(x)}>{t('common.edit')}</button>{' '}
                  <button type="button" disabled={archive.isPending} onClick={() => archive.mutate(x)}>{x.isArchived ? t('common.restore') : t('common.archive')}</button>
                </>
              ) : null}
            </li>
          ))}
        </ul>
        <ErrorBanner error={archive.error} />
      </QueryState>
      {editing ? <TemplateDialog template={editing === 'new' ? null : editing} onClose={() => setEditing(null)} /> : null}
    </>
  );
}

function TemplateDialog({ template, onClose }: { template: PmTemplate | null; onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const client = useQueryClient();
  const checklists = useChecklistTemplates(true, false);
  const prio = useEnumLabel('workPriority');
  const [nameEn, setNameEn] = useState(template?.name ?? '');
  const [nameAr, setNameAr] = useState(template?.nameAr ?? '');
  const [instructions, setInstructions] = useState(template?.instructions ?? '');
  const [priority, setPriority] = useState<WorkPriority>(template?.priority ?? 'Medium');
  const [minutes, setMinutes] = useState(template?.estimatedMinutes?.toString() ?? '');
  const [checklistId, setChecklistId] = useState(template?.checklistTemplateId ?? '');

  const save = useMutation({
    mutationFn: () => {
      const body = {
        name: nameEn.trim(), nameAr: nameAr.trim() || null, instructions: instructions.trim() || null, priority,
        estimatedMinutes: minutes ? Number(minutes) : null, checklistTemplateId: checklistId || null, rowVersion: template?.rowVersion,
      };
      return template ? api.put(`/pm/templates/${template.id}`, body) : api.post('/pm/templates', body);
    },
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: ['pm'] });
      onClose();
    },
  });
  const error = save.error instanceof ApiError ? save.error : null;

  return (
    <div className="overlay" onKeyDown={(e) => e.key === 'Escape' && onClose()}>
      <div className="dialog wide" role="dialog" aria-modal="true" aria-label={template ? t('pm.editTemplate') : t('pm.newTemplate')}>
        <h2>{template ? t('pm.editTemplate') : t('pm.newTemplate')}</h2>
        <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }}>
          <div className="row">
            <Field label={t('assets.name')} error={error?.field('name')} required><input value={nameEn} onChange={(e) => setNameEn(e.target.value)} maxLength={200} autoFocus /></Field>
            <Field label={t('assets.nameAr')} error={error?.field('nameAr')}><input value={nameAr} onChange={(e) => setNameAr(e.target.value)} maxLength={200} lang="ar" dir="rtl" /></Field>
          </div>
          <Field label={t('pm.instructions')} error={error?.field('instructions')}><textarea value={instructions} onChange={(e) => setInstructions(e.target.value)} rows={4} maxLength={4000} /></Field>
          <div className="row">
            <Field label={t('work.priority')}>
              <select value={priority} onChange={(e) => setPriority(e.target.value as WorkPriority)}>{PRIORITIES.map((p) => <option key={p} value={p}>{prio(p)}</option>)}</select>
            </Field>
            <Field label={t('pm.estimatedMinutes')} error={error?.field('estimatedMinutes')}><input type="number" min={1} value={minutes} onChange={(e) => setMinutes(e.target.value)} /></Field>
            <Field label={t('pm.checklist')} error={error?.field('checklistTemplateId')}>
              <select value={checklistId} onChange={(e) => setChecklistId(e.target.value)}>
                <option value="">{t('common.none')}</option>
                {checklists.data?.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
              </select>
            </Field>
          </div>
          <ErrorBanner error={error?.status === 400 ? null : save.error} />
          <div className="actions">
            <button type="button" onClick={onClose}>{t('common.cancel')}</button>
            <button type="submit" className="primary" disabled={save.isPending}>{t('common.save')}</button>
          </div>
        </form>
      </div>
    </div>
  );
}

// ---------------- schedules ----------------

function SchedulesTab() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { date } = usePrefs();
  const { can } = usePermissions();
  const client = useQueryClient();
  const schedules = usePmSchedules(true);
  const basisLabel = useEnumLabel('pmBasis');
  const unitLabel = useEnumLabel('pmUnit');
  const [editing, setEditing] = useState<PmSchedule | 'new' | null>(null);
  const archive = useMutation({
    mutationFn: (x: PmSchedule) => api.post(`/pm/schedules/${x.id}/${x.isArchived ? 'restore' : 'archive'}`, { rowVersion: x.rowVersion }),
    onSuccess: () => client.invalidateQueries({ queryKey: ['pm'] }),
  });

  const every = (s: PmSchedule) => {
    const parts: string[] = [];
    if (s.timeBasis !== 'None') parts.push(`${t('pm.every', { count: s.interval, unit: unitLabel(s.intervalUnit).toLowerCase() })} · ${basisLabel(s.timeBasis)}`);
    if (s.meterInterval) parts.push(t('pm.everyMeter', { interval: s.meterInterval, meter: s.meterName }));
    return parts.join(' + ');
  };

  return (
    <>
      <div className="toolbar">
        <h2>{t('pm.tab.schedules')}</h2>
        {can('pm.manage') ? <button type="button" onClick={() => setEditing('new')}>{t('pm.newSchedule')}</button> : null}
      </div>
      <QueryState query={schedules}>
        {schedules.data && schedules.data.length === 0 ? <p className="muted">{t('pm.noSchedules')}</p> : null}
        {schedules.data && schedules.data.length > 0 ? (
          <div className="table-wrap">
            <table>
              <caption className="visually-hidden">{t('pm.tab.schedules')}</caption>
              <thead>
                <tr>
                  <th scope="col">{t('pm.schedule')}</th>
                  <th scope="col">{t('pm.appliesTo')}</th>
                  <th scope="col">{t('pm.repeats')}</th>
                  <th scope="col">{t('pm.starts')}</th>
                  <th scope="col">{t('pm.openCount')}</th>
                  <th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th>
                </tr>
              </thead>
              <tbody>
                {schedules.data.map((s) => (
                  <tr key={s.id}>
                    <td>
                      <strong><bdi>{s.name}</bdi></strong> {s.isArchived ? <Badge tone="neutral">{t('common.archived')}</Badge> : null}
                      <div className="muted"><bdi>{s.templateName}</bdi></div>
                    </td>
                    <td><bdi>{s.targetKind === 'Asset' ? s.assetName : s.assetClassName}</bdi>{s.targetKind === 'AssetClass' ? <div className="muted">{t('pm.assetsCount', { count: s.assets })}</div> : null}</td>
                    <td>{every(s)}</td>
                    <td>{date(s.startOn)}</td>
                    <td>{s.open}</td>
                    <td className="actions">
                      {can('pm.manage') ? (
                        <>
                          <button type="button" onClick={() => setEditing(s)}>{t('common.edit')}</button>
                          <button type="button" disabled={archive.isPending} onClick={() => archive.mutate(s)}>{s.isArchived ? t('common.restore') : t('common.archive')}</button>
                        </>
                      ) : null}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : null}
        <ErrorBanner error={archive.error} />
      </QueryState>
      {editing ? <ScheduleDialog schedule={editing === 'new' ? null : editing} onClose={() => setEditing(null)} /> : null}
    </>
  );
}

function ScheduleDialog({ schedule, onClose }: { schedule: PmSchedule | null; onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { name } = usePrefs();
  const client = useQueryClient();
  const templates = usePmTemplates(false);
  const classes = useAssetClasses();
  const people = useAssignees(true);
  const basisLabel = useEnumLabel('pmBasis');
  const unitLabel = useEnumLabel('pmUnit');
  const kindLabel = useEnumLabel('pmTarget');
  const [nameEn, setNameEn] = useState(schedule?.name ?? '');
  const [templateId, setTemplateId] = useState(schedule?.templateId ?? '');
  const [targetKind, setTargetKind] = useState<PmTargetKind>(schedule?.targetKind ?? 'Asset');
  const [assetId, setAssetId] = useState(schedule?.assetId ?? '');
  const [assetLabel, setAssetLabel] = useState(schedule?.assetName ?? '');
  const [search, setSearch] = useState('');
  const [classId, setClassId] = useState(schedule?.assetClassId ?? '');
  const [basis, setBasis] = useState<PmTimeBasis>(schedule?.timeBasis ?? 'Fixed');
  const [unit, setUnit] = useState<PmIntervalUnit>(schedule?.intervalUnit ?? 'Month');
  const [interval, setInterval] = useState(String(schedule?.interval ?? 3));
  const [startOn, setStartOn] = useState(schedule?.startOn ?? new Date().toISOString().slice(0, 10));
  const [leadDays, setLeadDays] = useState(String(schedule?.leadDays ?? 7));
  const [meterName, setMeterName] = useState(schedule?.meterName ?? '');
  const [meterInterval, setMeterInterval] = useState(schedule?.meterInterval?.toString() ?? '');
  const [assignee, setAssignee] = useState(schedule?.assignToUserId ?? '');
  const choices = useAssetChoices(search, '');

  const save = useMutation({
    mutationFn: () => {
      const body = {
        name: nameEn.trim(), templateId: templateId || null, targetKind,
        assetId: targetKind === 'Asset' ? assetId || null : null, assetClassId: targetKind === 'AssetClass' ? classId || null : null,
        timeBasis: basis, intervalUnit: unit, interval: basis === 'None' ? 1 : Number(interval) || 0, startOn, leadDays: Number(leadDays) || 0,
        meterName: meterInterval ? meterName.trim() || null : null, meterInterval: meterInterval ? Number(meterInterval) : null,
        assignToUserId: assignee || null, rowVersion: schedule?.rowVersion,
      };
      return schedule ? api.put(`/pm/schedules/${schedule.id}`, body) : api.post('/pm/schedules', body);
    },
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: ['pm'] });
      onClose();
    },
  });
  const error = save.error instanceof ApiError ? save.error : null;

  return (
    <div className="overlay" onKeyDown={(e) => e.key === 'Escape' && onClose()}>
      <div className="dialog wide" role="dialog" aria-modal="true" aria-label={schedule ? t('pm.editSchedule') : t('pm.newSchedule')}>
        <h2>{schedule ? t('pm.editSchedule') : t('pm.newSchedule')}</h2>
        <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }}>
          <div className="row">
            <Field label={t('assets.name')} error={error?.field('name')} required><input value={nameEn} onChange={(e) => setNameEn(e.target.value)} maxLength={200} autoFocus /></Field>
            <Field label={t('pm.template')} error={error?.field('templateId')} required>
              <select value={templateId} onChange={(e) => setTemplateId(e.target.value)}>
                <option value="">{t('common.choose')}</option>
                {templates.data?.map((x) => <option key={x.id} value={x.id}>{name(x)}</option>)}
              </select>
            </Field>
          </div>

          <fieldset>
            <legend>{t('pm.appliesTo')}</legend>
            <div className="row">
              <Field label={t('pm.target')}>
                <select value={targetKind} onChange={(e) => setTargetKind(e.target.value as PmTargetKind)}>
                  {(['Asset', 'AssetClass'] as const).map((k) => <option key={k} value={k}>{kindLabel(k)}</option>)}
                </select>
              </Field>
              {targetKind === 'Asset' ? (
                <>
                  <Field label={t('pm.findAsset')} hint={assetId ? t('pm.selectedAsset', { name: assetLabel }) : t('pm.findAssetHint')} error={error?.field('assetId')}>
                    <input value={search} onChange={(e) => setSearch(e.target.value)} />
                  </Field>
                  {choices.data && choices.data.length > 0 ? (
                    <Field label={t('pm.chooseAsset')}>
                      <select value={assetId} onChange={(e) => { setAssetId(e.target.value); setAssetLabel(choices.data?.find((c) => c.id === e.target.value)?.name ?? ''); }}>
                        <option value="">{t('common.choose')}</option>
                        {choices.data.map((c) => <option key={c.id} value={c.id}>{c.code} · {c.name}</option>)}
                      </select>
                    </Field>
                  ) : null}
                </>
              ) : (
                <Field label={t('pm.assetClass')} error={error?.field('assetClassId')}>
                  <select value={classId} onChange={(e) => setClassId(e.target.value)}>
                    <option value="">{t('common.choose')}</option>
                    {classes.data?.filter((c) => !c.isArchived).map((c) => <option key={c.id} value={c.id}>{name(c)}</option>)}
                  </select>
                </Field>
              )}
            </div>
          </fieldset>

          <fieldset>
            <legend>{t('pm.repeats')}</legend>
            <div className="row">
              <Field label={t('pm.basis')} hint={t(`pm.basisHint.${basis}`)}>
                <select value={basis} onChange={(e) => setBasis(e.target.value as PmTimeBasis)}>
                  {(['Fixed', 'Floating', 'None'] as const).map((b) => <option key={b} value={b}>{basisLabel(b)}</option>)}
                </select>
              </Field>
              {basis !== 'None' ? (
                <>
                  <Field label={t('pm.interval')} error={error?.field('interval')}><input type="number" min={1} value={interval} onChange={(e) => setInterval(e.target.value)} /></Field>
                  <Field label={t('pm.unit')}>
                    <select value={unit} onChange={(e) => setUnit(e.target.value as PmIntervalUnit)}>{UNITS.map((u) => <option key={u} value={u}>{unitLabel(u)}</option>)}</select>
                  </Field>
                </>
              ) : null}
            </div>
            <div className="row">
              <Field label={basis === 'None' ? t('pm.firstDue') : t('pm.starts')} error={error?.field('startOn')}><input type="date" value={startOn} onChange={(e) => setStartOn(e.target.value)} /></Field>
              <Field label={t('pm.leadDays')} hint={t('pm.leadHint')} error={error?.field('leadDays')}><input type="number" min={0} max={365} value={leadDays} onChange={(e) => setLeadDays(e.target.value)} /></Field>
            </div>
          </fieldset>

          <fieldset>
            <legend>{t('pm.meterTrigger')}</legend>
            <p className="muted">{t('pm.meterHint')}</p>
            <div className="row">
              <Field label={t('pm.meterName')} error={error?.field('meterName')}><input value={meterName} onChange={(e) => setMeterName(e.target.value)} maxLength={100} /></Field>
              <Field label={t('pm.meterInterval')} error={error?.field('meterInterval')}><input type="number" min={0} step="any" value={meterInterval} onChange={(e) => setMeterInterval(e.target.value)} /></Field>
            </div>
          </fieldset>

          <Field label={t('pm.assignTo')} hint={t('pm.assignHint')}>
            <select value={assignee} onChange={(e) => setAssignee(e.target.value)}>
              <option value="">{t('pm.noAssignee')}</option>
              {people.data?.map((p) => <option key={p.id} value={p.id}>{p.displayName ?? p.email}</option>)}
            </select>
          </Field>
          <ErrorBanner error={error?.status === 400 ? null : save.error} />
          <div className="actions">
            <button type="button" onClick={onClose}>{t('common.cancel')}</button>
            <button type="submit" className="primary" disabled={save.isPending}>{t('common.save')}</button>
          </div>
        </form>
      </div>
    </div>
  );
}
