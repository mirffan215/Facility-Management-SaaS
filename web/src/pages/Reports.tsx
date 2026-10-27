import { useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import { useDashboard, useFeedDatasets, useFeedTokens, usePermissions, useSites } from '../api/hooks';
import type { CreatedFeedToken, Dashboard, Kpi, ReportSeries } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Badge, ErrorBanner, Field, QueryState } from '../components/ui';
import { downloadBlob } from '../lib/csv';
import { barWidths, formatKpi, isWeekly, keyId, pointLabelGroup } from '../lib/reportFormat';
import { usePrefs } from '../lib/prefs';

const iso = (d: Date) => d.toISOString().slice(0, 10);

/** The KPI pack: every figure the person may see, for a period and optionally one site, with a chart or two per area and a spreadsheet export. */
export function ReportsPage() {
  const { t } = useTranslation();
  const { can } = usePermissions();
  const [params, setParams] = useSearchParams();
  const tab = can('reports.manage') && params.get('tab') === 'feed' ? 'feed' : 'kpi';
  return (
    <section>
      <h1>{t('reports.title')}</h1>
      {can('reports.manage') ? (
        <div className="tabs" role="tablist" aria-label={t('reports.sections')}>
          {(['kpi', 'feed'] as const).map((x) => (
            <button key={x} role="tab" type="button" aria-selected={tab === x} className={tab === x ? 'active' : ''} onClick={() => setParams(x === 'kpi' ? {} : { tab: x })}>
              {t(`reports.tab.${x}`)}
            </button>
          ))}
        </div>
      ) : null}
      <div role={can('reports.manage') ? 'tabpanel' : undefined}>{tab === 'kpi' ? <KpiPack /> : <FeedTab />}</div>
    </section>
  );
}

function KpiPack() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const sites = useSites();
  const today = iso(new Date());
  const [from, setFrom] = useState(() => iso(new Date(Date.now() - 29 * 86400000)));
  const [to, setTo] = useState(today);
  const [siteId, setSiteId] = useState('');
  const params = { from, to, siteId: siteId || undefined };
  const dashboard = useDashboard(params);
  const exportCsv = useMutation({
    mutationFn: async () => {
      const { blob, fileName } = await api.blob(`/reports/export.csv?from=${from}&to=${to}${siteId ? `&siteId=${siteId}` : ''}`);
      downloadBlob(fileName ?? 'kpi.csv', blob);
    },
  });
  return (
    <>
      <p className="muted">{t('reports.intro')}</p>
      <div className="filters">
        <Field label={t('reports.from')}><input type="date" value={from} max={to} onChange={(e) => setFrom(e.target.value)} /></Field>
        <Field label={t('reports.to')}><input type="date" value={to} min={from} onChange={(e) => setTo(e.target.value)} /></Field>
        <Field label={t('reports.site')}>
          <select value={siteId} onChange={(e) => setSiteId(e.target.value)}>
            <option value="">{t('reports.allSites')}</option>
            {sites.data.map((s) => <option key={s.id} value={s.id}>{s.code} · {s.name}</option>)}
          </select>
        </Field>
        <button type="button" disabled={exportCsv.isPending || !dashboard.data} onClick={() => exportCsv.mutate()}>{t('reports.export')}</button>
      </div>
      <ErrorBanner error={exportCsv.error} />
      <QueryState query={dashboard}>{dashboard.data ? <Pack data={dashboard.data} /> : null}</QueryState>
    </>
  );
}

function Pack({ data }: { data: Dashboard }) {
  const { t } = useTranslation();
  const { date } = usePrefs();
  return (
    <>
      <p className="muted">{t('reports.period', { from: date(data.from), to: date(data.to) })}{data.siteName ? ` · ${data.siteName}` : ''}</p>
      {data.groups.length === 0 ? <p className="muted">{t('reports.nothing')}</p> : null}
      {data.groups.map((g) => (
        <section key={g.group} aria-labelledby={`g-${g.group}`}>
          <h2 id={`g-${g.group}`}>{t(`reports.group.${g.group}`)}</h2>
          {g.failed ? <p className="banner error" role="alert">{t('reports.failed')}</p> : null}
          {g.group !== 'work' && data.siteName && ['inventory', 'purchasing', 'vendors'].includes(g.group) ? <p className="muted">{t('reports.notBySite')}</p> : null}
          <dl className="tiles">
            {g.kpis.map((k) => <Tile key={k.key} kpi={k} />)}
          </dl>
          <div className="charts">
            {g.series.map((s) => <Chart key={s.key} series={s} />)}
          </div>
        </section>
      ))}
    </>
  );
}

function Tile({ kpi }: { kpi: Kpi }) {
  const { t } = useTranslation();
  const { lang } = usePrefs();
  return (
    <div>
      <dt>{t(`reports.kpi.${keyId(kpi.key)}`)}</dt>
      <dd>{formatKpi(kpi.value, kpi.unit, lang)}</dd>
    </div>
  );
}

function Chart({ series }: { series: ReportSeries }) {
  const { t, i18n } = useTranslation();
  const { lang, date } = usePrefs();
  const group = pointLabelGroup(series.key);
  const label = (l: string) => (isWeekly(series.key) ? date(l) : group ? t(`${group}.${l}`, { defaultValue: l }) : l);
  const widths = barWidths(series.points.map((p) => p.value));
  const title = t(`reports.series.${keyId(series.key)}`);
  return (
    <figure className="chart">
      <figcaption>{title}</figcaption>
      {series.points.length === 0 ? <p className="muted">{t('reports.noData')}</p> : (
        <ul className="bars" aria-label={title}>
          {series.points.map((p, i) => (
            <li key={p.label} dir={i18n.dir()}>
              <span className="bar-label"><bdi>{label(p.label)}</bdi></span>
              <span className="bar-track" aria-hidden="true"><span className="bar-fill" style={{ inlineSize: `${widths[i]}%` }} /></span>
              <span className="bar-value">{formatKpi(p.value, series.unit, lang)}</span>
            </li>
          ))}
        </ul>
      )}
    </figure>
  );
}

// ---------------- the data feed for outside tools ----------------

function FeedTab() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { date } = usePrefs();
  const client = useQueryClient();
  const tokens = useFeedTokens();
  const datasets = useFeedDatasets();
  const [name, setName] = useState('');
  const [days, setDays] = useState('365');
  const [chosen, setChosen] = useState<string[]>([]);
  const [created, setCreated] = useState<CreatedFeedToken | null>(null);
  const refresh = () => client.invalidateQueries({ queryKey: ['reports', 'feed'] });
  const create = useMutation({
    mutationFn: () => api.post<CreatedFeedToken>('/reports/feed/tokens', { name, expiresInDays: Number(days), datasets: chosen }),
    onSuccess: async (c) => {
      setCreated(c);
      setName('');
      await refresh();
    },
  });
  const revoke = useMutation({ mutationFn: (id: string) => api.post(`/reports/feed/tokens/${id}/revoke`), onSuccess: () => refresh() });
  const err = create.error instanceof ApiError ? create.error : null;
  const feedUrl = `${window.location.origin}/api/v1/odata`;
  return (
    <>
      <p className="muted">{t('reports.feedIntro')}</p>
      <h2>{t('reports.feedHow')}</h2>
      <ol>
        <li>{t('reports.feedStep1')}</li>
        <li>{t('reports.feedStep2')}</li>
        <li>{t('reports.feedStep3')}</li>
      </ol>
      <Field label={t('reports.feedAddress')}><input readOnly value={feedUrl} dir="ltr" onFocus={(e) => e.currentTarget.select()} /></Field>

      <h2>{t('reports.feedNew')}</h2>
      <form onSubmit={(e) => { e.preventDefault(); create.mutate(); }}>
        <Field label={t('reports.feedName')} hint={t('reports.feedNameHint')} error={err?.field('name')}><input value={name} onChange={(e) => setName(e.target.value)} /></Field>
        <Field label={t('reports.feedDays')} error={err?.field('expiresInDays')}><input type="number" min={1} max={1095} value={days} onChange={(e) => setDays(e.target.value)} /></Field>
        <fieldset>
          <legend>{t('reports.feedDatasets')}</legend>
          <p className="muted">{t('reports.feedDatasetsHint')}</p>
          {(datasets.data ?? []).map((d) => (
            <label key={d.name} className="inline">
              <input type="checkbox" checked={chosen.includes(d.name)} onChange={(e) => setChosen(e.target.checked ? [...chosen, d.name] : chosen.filter((x) => x !== d.name))} /> {d.name}
            </label>
          ))}
          {err?.field('datasets') ? <p className="field-error" role="alert">{err.field('datasets')}</p> : null}
        </fieldset>
        <ErrorBanner error={err && (err.field('name') || err.field('expiresInDays') || err.field('datasets')) ? null : create.error} />
        <button type="submit" className="primary" disabled={create.isPending}>{t('reports.feedMake')}</button>
      </form>
      {created ? (
        <div className="banner" role="status">
          <strong>{t('reports.feedSecretTitle', { name: created.token.name })}</strong>
          <p>{t('reports.feedSecretHelp')}</p>
          <input readOnly value={created.secret} aria-label={t('reports.feedSecret')} dir="ltr" onFocus={(e) => e.currentTarget.select()} />
        </div>
      ) : null}

      <h2>{t('reports.feedTokens')}</h2>
      <ErrorBanner error={revoke.error} />
      <QueryState query={tokens}>
        {tokens.data && tokens.data.length === 0 ? <p className="muted">{t('reports.feedNone')}</p> : null}
        {tokens.data && tokens.data.length > 0 ? (
          <div className="table-wrap">
            <table>
              <caption className="visually-hidden">{t('reports.feedTokens')}</caption>
              <thead>
                <tr>
                  <th scope="col">{t('reports.feedName')}</th>
                  <th scope="col">{t('reports.feedStarts')}</th>
                  <th scope="col">{t('reports.feedExpires')}</th>
                  <th scope="col">{t('reports.feedLastUsed')}</th>
                  <th scope="col">{t('assets.status')}</th>
                  <th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th>
                </tr>
              </thead>
              <tbody>
                {tokens.data.map((k) => (
                  <tr key={k.id}>
                    <td><bdi>{k.name}</bdi><div className="muted"><bdi>{k.scope === '*' ? t('reports.feedAll') : k.scope}</bdi></div></td>
                    <td><bdi>{k.prefix}…</bdi></td>
                    <td>{date(k.expiresAt)}</td>
                    <td>{k.lastUsedAt ? date(k.lastUsedAt, true) : '—'}</td>
                    <td><Badge tone={k.state === 'Active' ? 'ok' : 'neutral'}>{t(`reports.feedState.${k.state}`)}</Badge></td>
                    <td className="actions">{k.state === 'Active' ? <button type="button" disabled={revoke.isPending} onClick={() => revoke.mutate(k.id)}>{t('reports.feedRevoke')}</button> : null}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : null}
      </QueryState>
    </>
  );
}
