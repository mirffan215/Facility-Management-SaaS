import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { usePermissions } from '../api/hooks';
import type { ImportReport } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Badge, ErrorBanner, Field } from '../components/ui';
import { ASSET_TEMPLATE, LOCATION_TEMPLATE, downloadText } from '../lib/csv';

type Target = 'locations' | 'assets';
const MAX_BYTES = 2 * 1024 * 1024;

export function ImportPage() {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { can } = usePermissions();
  const client = useQueryClient();
  const targets = (['locations', 'assets'] as Target[]).filter((x) => can(`${x}.manage`));
  const [chosen, setChosen] = useState<Target | null>(null);
  const target = chosen ?? targets[0];
  const [file, setFile] = useState<File | null>(null);
  const [report, setReport] = useState<{ value: ImportReport; target: Target; file: File } | null>(null);
  const [onlyErrors, setOnlyErrors] = useState(true);
  const [exportError, setExportError] = useState<unknown>(null);

  const run = useMutation({
    mutationFn: async (dryRun: boolean) => {
      const form = new FormData();
      form.append('file', file!);
      return { value: await api.upload<ImportReport>(`/${target}/import`, form, { dryRun }), target: target!, file: file! };
    },
    onSuccess: async (result) => {
      setReport(result);
      if (result.value.committed) await Promise.all([client.invalidateQueries({ queryKey: ['locations'] }), client.invalidateQueries({ queryKey: ['assets'] })]);
    },
  });

  if (!target) return <p>{t('errors.noAccess')}</p>;

  const checkedOk = report && !report.value.committed && report.value.dryRun && report.value.failed === 0 && report.target === target && report.file === file;
  const tooBig = file !== null && file.size > MAX_BYTES;
  const rows = report ? (onlyErrors ? report.value.rows.filter((r) => r.outcome === 'Error') : report.value.rows) : [];

  const exportCurrent = async () => {
    setExportError(null);
    try {
      downloadText(`${target}.csv`, await api.text(`/${target}/export.csv`));
    } catch (e) {
      setExportError(e);
    }
  };

  return (
    <section className="narrow">
      <h1>{t('import.title')}</h1>
      <p>{t('import.intro')}</p>
      <fieldset>
        <legend>{t('import.what')}</legend>
        {targets.map((x) => (
          <label key={x} className="check">
            <input type="radio" name="target" checked={target === x} onChange={() => { setChosen(x); setReport(null); }} /> {t(`import.target.${x}`)}
          </label>
        ))}
      </fieldset>
      <p className="actions">
        <button type="button" onClick={() => downloadText(`${target}-template.csv`, target === 'locations' ? LOCATION_TEMPLATE : ASSET_TEMPLATE)}>{t('import.template')}</button>
        <button type="button" onClick={() => void exportCurrent()}>{t('import.exportCurrent')}</button>
      </p>
      <ErrorBanner error={exportError} />
      <p className="muted">{t(`import.hint.${target}`)}</p>

      <Field label={t('import.file')} hint={t('import.fileHint')} error={tooBig ? t('errors.tooLarge') : undefined}>
        <input type="file" accept=".csv,text/csv" onChange={(e) => { setFile(e.target.files?.[0] ?? null); setReport(null); }} />
      </Field>
      <div className="actions">
        <button type="button" disabled={!file || tooBig || run.isPending} onClick={() => run.mutate(true)}>{t('import.check')}</button>
        <button type="button" className="primary" disabled={!checkedOk || run.isPending} onClick={() => run.mutate(false)}>{t('import.run')}</button>
      </div>
      <p className="muted">{t('import.allOrNothing')}</p>
      <ErrorBanner error={run.error} />

      {report ? (
        <div role="region" aria-label={t('import.report')} aria-live="polite">
          {report.value.committed ? (
            <p className="banner ok">{t('import.done', { count: report.value.created })}</p>
          ) : report.value.failed > 0 ? (
            <p className="banner error">{t('import.problems', { count: report.value.failed })}</p>
          ) : (
            <p className="banner ok">{t('import.wouldCreate', { count: report.value.created })}</p>
          )}
          {report.value.failed > 0 ? (
            <label className="check">
              <input type="checkbox" checked={onlyErrors} onChange={(e) => setOnlyErrors(e.target.checked)} /> {t('import.onlyErrors')}
            </label>
          ) : null}
          {rows.length > 0 ? (
            <div className="table-wrap">
              <table>
                <caption className="visually-hidden">{t('import.report')}</caption>
                <thead>
                  <tr>
                    <th scope="col">{t('import.line')}</th>
                    <th scope="col">{t('import.code')}</th>
                    <th scope="col">{t('import.result')}</th>
                    <th scope="col">{t('import.problem')}</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.slice(0, 500).map((r) => (
                    <tr key={r.line}>
                      <td>{r.line}</td>
                      <td>{r.code}</td>
                      <td><Badge tone={r.outcome === 'Error' ? 'bad' : 'ok'}>{t(`import.outcome.${r.outcome}`)}</Badge></td>
                      <td>{r.errors.map((e) => <div key={e}>{e}</div>)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : null}
        </div>
      ) : null}
    </section>
  );
}
