import { useMemo, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import {
  useAlarmEvents, useAlarmRules, useConsumption, useEnergyDevices, useEnergyMeters, useEnergyReadings, usePermissions, useTariffs,
} from '../api/hooks';
import type { AlarmRule, CreatedEnergyDevice, EnergyKind, EnergyMeter, ImportResult, Tariff } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Dialog } from '../components/InventoryBits';
import { LocationSelect } from '../components/LocationSelect';
import { Badge, ErrorBanner, Field, Pager, QueryState, ReasonDialog, useEnumLabel } from '../components/ui';
import { formatNumber } from '../lib/format';
import { usePrefs } from '../lib/prefs';

const KINDS: EnergyKind[] = ['Electricity', 'Water', 'Gas', 'Cooling', 'Heat', 'Other'];
const asError = (e: unknown) => (e instanceof ApiError ? e : null);
const day = (offset: number) => new Date(Date.now() + offset * 86_400_000).toISOString().slice(0, 10);

function useRefresh() {
  const client = useQueryClient();
  return () => client.invalidateQueries({ queryKey: ['energy'] });
}

export function EnergyPage() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const [params, setParams] = useSearchParams();
  const tabs = useMemo(() => ['overview', 'meters', 'tariffs', 'alarms', ...(can('energy.manage') ? ['devices'] : [])], [can]);
  const tab = tabs.find((x) => x === params.get('tab')) ?? 'overview';
  return (
    <section>
      <h1>{t('energy.title')}</h1>
      <p className="muted">{t('energy.intro')}</p>
      <div className="tabs" role="tablist" aria-label={t('energy.sections')}>
        {tabs.map((x) => (
          <button key={x} role="tab" type="button" aria-selected={tab === x} className={tab === x ? 'active' : ''} onClick={() => setParams({ tab: x })}>
            {t(`energy.tab.${x}`)}
          </button>
        ))}
      </div>
      <div role="tabpanel">{tab === 'overview' ? <OverviewTab /> : tab === 'meters' ? <MetersTab /> : tab === 'tariffs' ? <TariffsTab /> : tab === 'alarms' ? <AlarmsTab /> : <DevicesTab />}</div>
    </section>
  );
}

// ---------------------------------------------------------------- overview

function OverviewTab() {
  const { t, i18n } = useTranslation();
  const kindLabel = useEnumLabel('energyKind');
  const [from, setFrom] = useState(day(-29));
  const [to, setTo] = useState(day(0));
  const [kind, setKind] = useState('');
  const use = useConsumption({ from, to, kind: kind || undefined });
  const money = (v: number | null, c: string | null) => (v == null ? '—' : `${formatNumber(v, i18n.language, 2)} ${c ?? ''}`);
  return (
    <>
      <div className="toolbar">
        <Field label={t('reports.from')}><input type="date" value={from} onChange={(e) => setFrom(e.target.value)} /></Field>
        <Field label={t('reports.to')}><input type="date" value={to} onChange={(e) => setTo(e.target.value)} /></Field>
        <Field label={t('energy.kind')}>
          <select value={kind} onChange={(e) => setKind(e.target.value)}>
            <option value="">{t('common.all')}</option>
            {KINDS.map((k) => <option key={k} value={k}>{kindLabel(k)}</option>)}
          </select>
        </Field>
      </div>
      <QueryState query={use}>
        {use.data && use.data.meters.length === 0 ? <p className="muted">{t('energy.noMeters')}</p> : null}
        {use.data && use.data.meters.length > 0 ? (
          <>
            <ul className="cards plain" aria-label={t('energy.totals')}>
              {use.data.totals.map((x) => (
                <li key={x.kind} className="card">
                  <span className="muted">{kindLabel(x.kind)}</span>
                  <span className="value">{x.mixedUnits ? '—' : `${formatNumber(x.total, i18n.language, 1)} ${x.unit}`}</span>
                  <span className="muted">{t('energy.cost')}: {money(x.cost, x.currency)}</span>
                </li>
              ))}
            </ul>
            <div className="table-wrap" tabIndex={0} role="region" aria-label={t('energy.byMeter')}>
              <table>
                <caption className="visually-hidden">{t('energy.byMeter')}</caption>
                <thead>
                  <tr>
                    <th scope="col">{t('energy.meter')}</th>
                    <th scope="col">{t('energy.kind')}</th>
                    <th scope="col">{t('energy.use')}</th>
                    <th scope="col">{t('energy.cost')}</th>
                    <th scope="col">{t('energy.resets')}</th>
                  </tr>
                </thead>
                <tbody>
                  {use.data.meters.map((m) => (
                    <tr key={m.meterId}>
                      <th scope="row"><bdi>{m.code}</bdi> <span className="muted"><bdi>{m.name}</bdi></span></th>
                      <td>{kindLabel(m.kind)}</td>
                      <td>{formatNumber(m.total, i18n.language, 1)} {m.unit}</td>
                      <td>{money(m.cost, m.currency)}</td>
                      <td>{m.resets > 0 ? <Badge tone="warn">{m.resets}</Badge> : 0}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <p className="muted">{t('energy.overviewHint')}</p>
          </>
        ) : null}
      </QueryState>
    </>
  );
}

// ---------------------------------------------------------------- meters

function MetersTab() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const { date } = usePrefs();
  const kindLabel = useEnumLabel('energyKind');
  const [kind, setKind] = useState('');
  const [q, setQ] = useState('');
  const [page, setPage] = useState(1);
  const [editing, setEditing] = useState<EnergyMeter | 'new' | null>(null);
  const [adding, setAdding] = useState<EnergyMeter | null>(null);
  const [viewing, setViewing] = useState<EnergyMeter | null>(null);
  const [importing, setImporting] = useState(false);
  const list = useEnergyMeters({ kind: kind || undefined, q: q.trim() || undefined, page, pageSize: 25 });
  const change = (fn: () => void) => { fn(); setPage(1); };
  return (
    <>
      <div className="toolbar">
        <Field label={t('energy.kind')}>
          <select value={kind} onChange={(e) => change(() => setKind(e.target.value))}>
            <option value="">{t('common.all')}</option>
            {KINDS.map((k) => <option key={k} value={k}>{kindLabel(k)}</option>)}
          </select>
        </Field>
        <Field label={t('common.search')}><input value={q} onChange={(e) => change(() => setQ(e.target.value))} /></Field>
        {can('energy.manage') ? <button type="button" className="primary" onClick={() => setEditing('new')}>{t('energy.newMeter')}</button> : null}
        {can('energy.record') ? <button type="button" onClick={() => setImporting(true)}>{t('energy.importCsv')}</button> : null}
      </div>
      <QueryState query={list}>
        {list.data && list.data.items.length === 0 ? <p className="muted">{t('energy.noMeters')}</p> : null}
        {list.data && list.data.items.length > 0 ? (
          <>
            <div className="table-wrap" tabIndex={0} role="region" aria-label={t('energy.tab.meters')}>
              <table>
                <caption className="visually-hidden">{t('energy.tab.meters')}</caption>
                <thead>
                  <tr>
                    <th scope="col">{t('energy.meter')}</th>
                    <th scope="col">{t('energy.kind')}</th>
                    <th scope="col">{t('energy.place')}</th>
                    <th scope="col">{t('energy.latest')}</th>
                    <th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th>
                  </tr>
                </thead>
                <tbody>
                  {list.data.items.map((m) => (
                    <tr key={m.id}>
                      <th scope="row"><bdi>{m.code}</bdi> <span className="muted"><bdi>{m.name}</bdi></span> {m.isArchived ? <Badge tone="neutral">{t('common.archived')}</Badge> : null}</th>
                      <td>{kindLabel(m.kind)} · {m.unit}</td>
                      <td><bdi>{m.locationName ?? ''}</bdi></td>
                      <td>{m.latestAt ? <>{m.latestValue} {m.unit} · {date(m.latestAt, true)}</> : '—'}</td>
                      <td>
                        <button type="button" onClick={() => setViewing(m)} aria-label={t('energy.readingsOf', { code: m.code })}>{t('energy.readings')}</button>{' '}
                        {can('energy.record') && !m.isArchived ? <button type="button" onClick={() => setAdding(m)} aria-label={t('energy.addReadingTo', { code: m.code })}>{t('energy.addReading')}</button> : null}{' '}
                        {can('energy.manage') ? <button type="button" onClick={() => setEditing(m)} aria-label={t('energy.editMeter', { code: m.code })}>{t('common.edit')}</button> : null}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <Pager page={page} pageSize={25} total={list.data.total} onChange={setPage} />
          </>
        ) : null}
      </QueryState>
      {editing ? <MeterForm meter={editing === 'new' ? null : editing} onClose={() => setEditing(null)} /> : null}
      {adding ? <ReadingForm meter={adding} onClose={() => setAdding(null)} /> : null}
      {viewing ? <ReadingsDialog meter={viewing} onClose={() => setViewing(null)} /> : null}
      {importing ? <ImportDialog onClose={() => setImporting(false)} /> : null}
    </>
  );
}

function MeterForm({ meter, onClose }: { meter: EnergyMeter | null; onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const refresh = useRefresh();
  const kindLabel = useEnumLabel('energyKind');
  const [code, setCode] = useState(meter?.code ?? '');
  const [name, setName] = useState(meter?.name ?? '');
  const [kind, setKind] = useState<EnergyKind>(meter?.kind ?? 'Electricity');
  const [unit, setUnit] = useState(meter?.unit ?? 'kWh');
  const [locationId, setLocationId] = useState(meter?.locationId ?? '');
  const [cumulative, setCumulative] = useState(meter?.isCumulative ?? true);
  const [rollover, setRollover] = useState(meter?.rolloverAt?.toString() ?? '');
  const save = useMutation({
    mutationFn: () => {
      const body = { code, name, kind, unit, locationId: locationId || null, isCumulative: cumulative, rolloverAt: cumulative && rollover.trim() ? Number(rollover) : null, rowVersion: meter?.rowVersion ?? null };
      return meter ? api.put(`/energy/meters/${meter.id}`, body) : api.post('/energy/meters', body);
    },
    onSuccess: async () => { await refresh(); onClose(); },
  });
  const archive = useMutation({
    mutationFn: () => api.post(`/energy/meters/${meter!.id}/${meter!.isArchived ? 'restore' : 'archive'}`, { rowVersion: meter!.rowVersion }),
    onSuccess: async () => { await refresh(); onClose(); },
  });
  const errors = asError(save.error)?.fieldErrors ?? {};
  return (
    <Dialog title={meter ? t('energy.editMeter', { code: meter.code }) : t('energy.newMeter')} onClose={onClose} wide>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }} noValidate>
        <div className="row">
          <Field label={t('energy.code')} error={errors.code?.[0]} required><input value={code} onChange={(e) => setCode(e.target.value)} maxLength={50} autoFocus /></Field>
          <Field label={t('energy.name')} error={errors.name?.[0]} required><input value={name} onChange={(e) => setName(e.target.value)} maxLength={200} /></Field>
        </div>
        <div className="row">
          <Field label={t('energy.kind')} error={errors.kind?.[0]}>
            <select value={kind} onChange={(e) => setKind(e.target.value as EnergyKind)}>{KINDS.map((k) => <option key={k} value={k}>{kindLabel(k)}</option>)}</select>
          </Field>
          <Field label={t('energy.unit')} error={errors.unit?.[0]} required><input value={unit} onChange={(e) => setUnit(e.target.value)} maxLength={20} /></Field>
          <Field label={t('energy.place')} error={errors.locationId?.[0]}><LocationSelect value={locationId} onChange={setLocationId} /></Field>
        </div>
        <label className="check"><input type="checkbox" checked={cumulative} onChange={(e) => setCumulative(e.target.checked)} /><span>{t('energy.cumulative')} <small className="muted">{t('energy.cumulativeHint')}</small></span></label>
        {cumulative ? <Field label={t('energy.rollover')} hint={t('energy.rolloverHint')} error={errors.rolloverAt?.[0]}><input type="number" min={0} value={rollover} onChange={(e) => setRollover(e.target.value)} /></Field> : null}
        <ErrorBanner error={Object.keys(errors).length ? null : (save.error ?? archive.error)} />
        <div className="actions">
          {meter ? <button type="button" disabled={archive.isPending} onClick={() => archive.mutate()}>{meter.isArchived ? t('common.restore') : t('common.archive')}</button> : null}
          <button type="button" onClick={onClose}>{t('common.cancel')}</button>
          <button type="submit" className="primary" disabled={save.isPending}>{t('common.save')}</button>
        </div>
      </form>
    </Dialog>
  );
}

function ReadingForm({ meter, onClose }: { meter: EnergyMeter; onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const refresh = useRefresh();
  const [value, setValue] = useState('');
  const [at, setAt] = useState('');
  const [note, setNote] = useState('');
  const save = useMutation({
    mutationFn: () => api.post(`/energy/meters/${meter.id}/readings`, { value: value.trim() ? Number(value) : null, at: at ? new Date(at).toISOString() : null, note: note || null }),
    onSuccess: async () => { await refresh(); onClose(); },
  });
  const errors = asError(save.error)?.fieldErrors ?? {};
  return (
    <Dialog title={t('energy.addReadingTo', { code: meter.code })} onClose={onClose}>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }} noValidate>
        <Field label={t('energy.reading', { unit: meter.unit })} hint={meter.latestValue != null ? t('energy.lastWas', { value: meter.latestValue, unit: meter.unit }) : undefined} error={errors.value?.[0]} required>
          <input type="number" step="any" min={0} value={value} onChange={(e) => setValue(e.target.value)} autoFocus />
        </Field>
        <Field label={t('energy.readAt')} hint={t('energy.readAtHint')} error={errors.at?.[0]}><input type="datetime-local" value={at} onChange={(e) => setAt(e.target.value)} /></Field>
        <Field label={t('energy.note')} error={errors.note?.[0]}><input value={note} onChange={(e) => setNote(e.target.value)} maxLength={200} /></Field>
        <ErrorBanner error={Object.keys(errors).length ? null : save.error} />
        <div className="actions">
          <button type="button" onClick={onClose}>{t('common.cancel')}</button>
          <button type="submit" className="primary" disabled={save.isPending}>{t('common.save')}</button>
        </div>
      </form>
    </Dialog>
  );
}

function ReadingsDialog({ meter, onClose }: { meter: EnergyMeter; onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { can } = usePermissions();
  const { date } = usePrefs();
  const refresh = useRefresh();
  const [page, setPage] = useState(1);
  const [voiding, setVoiding] = useState<string | null>(null);
  const list = useEnergyReadings(meter.id, page);
  const voidIt = useMutation({
    mutationFn: (reason: string) => api.post(`/energy/readings/${voiding}/void`, { reason }),
    onSuccess: async () => { setVoiding(null); await refresh(); },
  });
  return (
    <Dialog title={t('energy.readingsOf', { code: meter.code })} onClose={onClose} wide>
      <QueryState query={list}>
        {list.data && list.data.items.length === 0 ? <p className="muted">{t('energy.noReadings')}</p> : null}
        {list.data && list.data.items.length > 0 ? (
          <>
            <div className="table-wrap" tabIndex={0} role="region" aria-label={t('energy.readings')}>
              <table>
                <caption className="visually-hidden">{t('energy.readings')}</caption>
                <thead>
                  <tr><th scope="col">{t('energy.readAt')}</th><th scope="col">{t('energy.value')}</th><th scope="col">{t('energy.source')}</th><th scope="col">{t('energy.note')}</th><th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th></tr>
                </thead>
                <tbody>
                  {list.data.items.map((r) => (
                    <tr key={r.id} className={r.isVoided ? 'muted' : undefined}>
                      <th scope="row">{date(r.at, true)}</th>
                      <td>{r.isVoided ? <s>{r.value}</s> : r.value} {meter.unit}</td>
                      <td>{t(`energy.sourceName.${r.source}`)}</td>
                      <td><bdi>{r.isVoided ? t('energy.voided', { reason: r.voidReason }) : r.note}</bdi></td>
                      <td>{can('energy.record') && !r.isVoided ? <button type="button" onClick={() => setVoiding(r.id)} aria-label={t('energy.voidReading', { value: r.value })}>{t('energy.void')}</button> : null}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <Pager page={page} pageSize={20} total={list.data.total} onChange={setPage} />
          </>
        ) : null}
      </QueryState>
      <div className="actions"><button type="button" onClick={onClose}>{t('common.back')}</button></div>
      {voiding ? <ReasonDialog title={t('energy.void')} label={t('common.reason')} confirmLabel={t('energy.void')} busy={voidIt.isPending} error={voidIt.error} onConfirm={(r) => voidIt.mutate(r)} onCancel={() => setVoiding(null)} /> : null}
    </Dialog>
  );
}

function ImportDialog({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const refresh = useRefresh();
  const [file, setFile] = useState<File | null>(null);
  const [result, setResult] = useState<ImportResult | null>(null);
  const send = useMutation({
    mutationFn: (dryRun: boolean) => {
      const form = new FormData();
      form.append('file', file!);
      form.append('dryRun', dryRun ? 'true' : 'false');
      return api.upload<ImportResult>('/energy/readings/import', form);
    },
    onSuccess: async (r) => { setResult(r); if (!r.dryRun) await refresh(); },
  });
  return (
    <Dialog title={t('energy.importCsv')} onClose={onClose} wide>
      <p className="muted">{t('energy.importHelp')}</p>
      <Field label={t('energy.csvFile')}><input type="file" accept=".csv,text/csv" onChange={(e) => { setFile(e.target.files?.[0] ?? null); setResult(null); }} /></Field>
      <ErrorBanner error={send.error} />
      {result ? (
        <>
          <p role="status">{result.dryRun ? t('energy.importPreview') : t('energy.importDone')}: {t('energy.importCounts', { accepted: result.accepted, duplicates: result.duplicates, rejected: result.rejected })}</p>
          {result.rows.some((r) => r.status === 'Rejected') ? (
            <ul className="plain">{result.rows.filter((r) => r.status === 'Rejected').slice(0, 20).map((r) => <li key={r.line}>{t('energy.line', { line: r.line })}: <bdi>{r.message}</bdi></li>)}</ul>
          ) : null}
        </>
      ) : null}
      <div className="actions">
        <button type="button" onClick={onClose}>{t('common.back')}</button>
        <button type="button" disabled={!file || send.isPending} onClick={() => send.mutate(true)}>{t('energy.check')}</button>
        <button type="button" className="primary" disabled={!file || send.isPending || !result?.dryRun || result.accepted === 0} onClick={() => send.mutate(false)}>{t('energy.importNow')}</button>
      </div>
    </Dialog>
  );
}

// ---------------------------------------------------------------- tariffs

function TariffsTab() {
  const { t, i18n } = useTranslation();
  const { can } = usePermissions();
  const { date } = usePrefs();
  const kindLabel = useEnumLabel('energyKind');
  const list = useTariffs();
  const [editing, setEditing] = useState<Tariff | 'new' | null>(null);
  return (
    <>
      <p className="muted">{t('energy.tariffsIntro')}</p>
      {can('energy.manage') ? <div className="toolbar"><button type="button" className="primary" onClick={() => setEditing('new')}>{t('energy.newTariff')}</button></div> : null}
      <QueryState query={list}>
        {list.data && list.data.length === 0 ? <p className="muted">{t('energy.noTariffs')}</p> : null}
        <ul className="plain">
          {(list.data ?? []).map((x) => (
            <li key={x.id}>
              <strong>{kindLabel(x.kind)}</strong> · {t('energy.from')} {date(x.effectiveFrom)} · {formatNumber(x.pricePerUnit, i18n.language, 4)} {x.currency}{' '}
              {can('energy.manage') ? <button type="button" onClick={() => setEditing(x)} aria-label={t('energy.editTariff', { kind: kindLabel(x.kind), date: x.effectiveFrom })}>{t('common.edit')}</button> : null}
            </li>
          ))}
        </ul>
      </QueryState>
      {editing ? <TariffForm tariff={editing === 'new' ? null : editing} onClose={() => setEditing(null)} /> : null}
    </>
  );
}

function TariffForm({ tariff, onClose }: { tariff: Tariff | null; onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const refresh = useRefresh();
  const kindLabel = useEnumLabel('energyKind');
  const [kind, setKind] = useState<EnergyKind>(tariff?.kind ?? 'Electricity');
  const [from, setFrom] = useState(tariff?.effectiveFrom ?? day(0));
  const [price, setPrice] = useState(tariff?.pricePerUnit.toString() ?? '');
  const [currency, setCurrency] = useState(tariff?.currency ?? 'SAR');
  const save = useMutation({
    mutationFn: () => {
      const body = { kind, effectiveFrom: from, pricePerUnit: price.trim() ? Number(price) : null, currency, rowVersion: tariff?.rowVersion ?? null };
      return tariff ? api.put(`/energy/tariffs/${tariff.id}`, body) : api.post('/energy/tariffs', body);
    },
    onSuccess: async () => { await refresh(); onClose(); },
  });
  const errors = asError(save.error)?.fieldErrors ?? {};
  return (
    <Dialog title={tariff ? t('common.edit') : t('energy.newTariff')} onClose={onClose}>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }} noValidate>
        <Field label={t('energy.kind')} error={errors.kind?.[0]}>
          <select value={kind} onChange={(e) => setKind(e.target.value as EnergyKind)} autoFocus>{KINDS.map((k) => <option key={k} value={k}>{kindLabel(k)}</option>)}</select>
        </Field>
        <Field label={t('energy.effectiveFrom')} error={errors.effectiveFrom?.[0]} required><input type="date" value={from} onChange={(e) => setFrom(e.target.value)} /></Field>
        <div className="row">
          <Field label={t('energy.price')} error={errors.pricePerUnit?.[0]} required><input type="number" step="any" min={0} value={price} onChange={(e) => setPrice(e.target.value)} /></Field>
          <Field label={t('energy.currency')} error={errors.currency?.[0]} required><input value={currency} onChange={(e) => setCurrency(e.target.value)} maxLength={3} /></Field>
        </div>
        <ErrorBanner error={Object.keys(errors).length ? null : save.error} />
        <div className="actions">
          <button type="button" onClick={onClose}>{t('common.cancel')}</button>
          <button type="submit" className="primary" disabled={save.isPending}>{t('common.save')}</button>
        </div>
      </form>
    </Dialog>
  );
}

// ---------------------------------------------------------------- alarms

function AlarmsTab() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const { date } = usePrefs();
  const rules = useAlarmRules();
  const events = useAlarmEvents();
  const [editing, setEditing] = useState<AlarmRule | 'new' | null>(null);
  return (
    <>
      <p className="muted">{t('energy.alarmsIntro')}</p>
      {can('energy.manage') ? <div className="toolbar"><button type="button" className="primary" onClick={() => setEditing('new')}>{t('energy.newRule')}</button></div> : null}
      <h2>{t('energy.rules')}</h2>
      <QueryState query={rules}>
        {rules.data && rules.data.length === 0 ? <p className="muted">{t('energy.noRules')}</p> : null}
        <ul className="plain">
          {(rules.data ?? []).map((r) => (
            <li key={r.id}>
              <bdi>{r.meterCode}</bdi> · {t(`energy.alarmKind.${r.kind}`, { threshold: r.threshold })}{' '}
              {r.isRaised ? <Badge tone="bad">{t('energy.raised')}</Badge> : r.isActive ? <Badge tone="ok">{t('energy.watching')}</Badge> : <Badge tone="neutral">{t('energy.off')}</Badge>}{' '}
              {can('energy.manage') ? <button type="button" onClick={() => setEditing(r)} aria-label={t('energy.editRule', { code: r.meterCode })}>{t('common.edit')}</button> : null}
            </li>
          ))}
        </ul>
      </QueryState>
      <h2>{t('energy.events')}</h2>
      <QueryState query={events}>
        {events.data && events.data.items.length === 0 ? <p className="muted">{t('energy.noEvents')}</p> : null}
        <ul className="plain">
          {(events.data?.items ?? []).map((e, i) => (
            <li key={i}>{date(e.at, true)} · <bdi>{e.meterCode}</bdi> · <Badge tone={e.kind === 'Raised' ? 'bad' : 'ok'}>{t(`energy.eventKind.${e.kind}`)}</Badge>{e.value != null ? <> · {e.value}</> : null}</li>
          ))}
        </ul>
      </QueryState>
      {editing ? <RuleForm rule={editing === 'new' ? null : editing} onClose={() => setEditing(null)} /> : null}
    </>
  );
}

function RuleForm({ rule, onClose }: { rule: AlarmRule | null; onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const refresh = useRefresh();
  const meters = useEnergyMeters({ pageSize: 100 });
  const [meterId, setMeterId] = useState(rule?.meterId ?? '');
  const [kind, setKind] = useState<AlarmRule['kind']>(rule?.kind ?? 'HighDailyUse');
  const [threshold, setThreshold] = useState(rule?.threshold.toString() ?? '');
  const [active, setActive] = useState(rule?.isActive ?? true);
  const save = useMutation({
    mutationFn: () => {
      const body = { meterId: meterId || null, kind, threshold: threshold.trim() ? Number(threshold) : null, isActive: active, rowVersion: rule?.rowVersion ?? null };
      return rule ? api.put(`/energy/alarms/rules/${rule.id}`, body) : api.post('/energy/alarms/rules', body);
    },
    onSuccess: async () => { await refresh(); onClose(); },
  });
  const errors = asError(save.error)?.fieldErrors ?? {};
  return (
    <Dialog title={rule ? t('energy.editRule', { code: rule.meterCode }) : t('energy.newRule')} onClose={onClose}>
      <form onSubmit={(e) => { e.preventDefault(); save.mutate(); }} noValidate>
        <Field label={t('energy.meter')} error={errors.meterId?.[0]} required>
          <select value={meterId} onChange={(e) => setMeterId(e.target.value)} disabled={!!rule} autoFocus>
            <option value="">{t('common.choose')}</option>
            {(meters.data?.items ?? []).map((m) => <option key={m.id} value={m.id}>{m.code} · {m.name}</option>)}
          </select>
        </Field>
        <Field label={t('energy.watchFor')} error={errors.kind?.[0]}>
          <select value={kind} onChange={(e) => setKind(e.target.value as AlarmRule['kind'])} disabled={!!rule}>
            <option value="HighDailyUse">{t('energy.alarmKindName.HighDailyUse')}</option>
            <option value="NoReading">{t('energy.alarmKindName.NoReading')}</option>
          </select>
        </Field>
        <Field label={kind === 'HighDailyUse' ? t('energy.limitPerDay') : t('energy.limitHours')} error={errors.threshold?.[0]} required><input type="number" step="any" min={0} value={threshold} onChange={(e) => setThreshold(e.target.value)} /></Field>
        <label className="check"><input type="checkbox" checked={active} onChange={(e) => setActive(e.target.checked)} /><span>{t('energy.ruleActive')}</span></label>
        <ErrorBanner error={Object.keys(errors).length ? null : save.error} />
        <div className="actions">
          <button type="button" onClick={onClose}>{t('common.cancel')}</button>
          <button type="submit" className="primary" disabled={save.isPending}>{t('common.save')}</button>
        </div>
      </form>
    </Dialog>
  );
}

// ---------------------------------------------------------------- devices

function DevicesTab() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { date } = usePrefs();
  const refresh = useRefresh();
  const devices = useEnergyDevices(true);
  const meters = useEnergyMeters({ pageSize: 100 });
  const [name, setName] = useState('');
  const [chosen, setChosen] = useState<string[]>([]);
  const [created, setCreated] = useState<CreatedEnergyDevice | null>(null);
  const make = useMutation({
    mutationFn: () => api.post<CreatedEnergyDevice>('/energy/devices', { name, meterIds: chosen }),
    onSuccess: async (r) => { setCreated(r); setName(''); setChosen([]); await refresh(); },
  });
  const revoke = useMutation({ mutationFn: (id: string) => api.post(`/energy/devices/${id}/revoke`, {}), onSuccess: refresh });
  const errors = asError(make.error)?.fieldErrors ?? {};
  return (
    <>
      <p className="muted">{t('energy.devicesIntro')}</p>
      <form className="inline-form" onSubmit={(e) => { e.preventDefault(); make.mutate(); }} noValidate aria-label={t('energy.newDevice')}>
        <Field label={t('energy.deviceName')} error={errors.name?.[0]} required><input value={name} onChange={(e) => setName(e.target.value)} maxLength={100} /></Field>
        <fieldset>
          <legend>{t('energy.deviceMeters')}</legend>
          {(meters.data?.items ?? []).map((m) => (
            <label key={m.id} className="check">
              <input type="checkbox" checked={chosen.includes(m.id)} onChange={(e) => setChosen((c) => (e.target.checked ? [...c, m.id] : c.filter((x) => x !== m.id)))} />
              <span><bdi>{m.code}</bdi> <span className="muted"><bdi>{m.name}</bdi></span></span>
            </label>
          ))}
          {errors.meterIds?.[0] ? <p role="alert" className="error">{errors.meterIds[0]}</p> : null}
        </fieldset>
        <ErrorBanner error={Object.keys(errors).length ? null : make.error} />
        <button type="submit" className="primary" disabled={make.isPending}>{t('energy.makeDevice')}</button>
      </form>
      {created ? (
        <div className="banner ok" role="status">
          <p>{t('energy.secretOnce', { name: created.device.name })}</p>
          <Field label={t('energy.secret')}><input readOnly value={created.secret} onFocus={(e) => e.currentTarget.select()} /></Field>
          <p className="muted">{t('energy.ingestHelp', { path: created.ingestPath })}</p>
        </div>
      ) : null}
      <QueryState query={devices}>
        {devices.data && devices.data.length === 0 ? <p className="muted">{t('energy.noDevices')}</p> : null}
        {devices.data && devices.data.length > 0 ? (
          <div className="table-wrap" tabIndex={0} role="region" aria-label={t('energy.tab.devices')}>
            <table>
              <caption className="visually-hidden">{t('energy.tab.devices')}</caption>
              <thead><tr><th scope="col">{t('energy.deviceName')}</th><th scope="col">{t('energy.deviceMeters')}</th><th scope="col">{t('energy.lastUsed')}</th><th scope="col">{t('compliance.status')}</th><th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th></tr></thead>
              <tbody>
                {devices.data.map((d) => (
                  <tr key={d.id}>
                    <th scope="row"><bdi>{d.name}</bdi> <span className="muted">{d.prefix}…</span></th>
                    <td>{d.meterIds.length}</td>
                    <td>{d.lastUsedAt ? date(d.lastUsedAt, true) : '—'}</td>
                    <td><Badge tone={d.state === 'Active' ? 'ok' : 'neutral'}>{t(`energy.deviceState.${d.state}`)}</Badge></td>
                    <td>{d.state === 'Active' ? <button type="button" disabled={revoke.isPending} onClick={() => revoke.mutate(d.id)} aria-label={t('energy.revokeDevice', { name: d.name })}>{t('energy.revoke')}</button> : null}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : null}
        <ErrorBanner error={revoke.error} />
      </QueryState>
    </>
  );
}
