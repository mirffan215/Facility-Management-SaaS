import { useEffect, useState } from 'react';
import { Link, useParams, useSearchParams } from 'react-router-dom';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import { useAsset, useAssetClasses, useAssets, useDocuments, useLocationTree, useMeters, useReadings } from '../api/hooks';
import type { Asset, AssetDocument, AssetStatus, DocumentKind, Meter, MeterKind, MeterReading } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { AssetForm } from '../components/AssetForm';
import { Badge, Can, ErrorBanner, Field, QueryState, ReasonDialog, useEnumLabel } from '../components/ui';
import { downloadBlob } from '../lib/csv';
import { formatBytes } from '../lib/format';
import { breadcrumb } from '../lib/locations';
import { usePrefs } from '../lib/prefs';
import { CustomFieldValuesView } from './Locations';
import { statusTone } from './Assets';

const TABS = ['details', 'meters', 'documents', 'label'] as const;
type Tab = (typeof TABS)[number];

const NEXT_STATUSES: Record<AssetStatus, AssetStatus[]> = {
  Planned: ['Active', 'Retired'],
  Active: ['UnderMaintenance', 'OutOfService', 'Retired'],
  UnderMaintenance: ['Active', 'OutOfService', 'Retired'],
  OutOfService: ['Active', 'UnderMaintenance', 'Retired'],
  Retired: [],
};

export function AssetDetailPage() {
  const { id } = useParams();
  const { t } = useTranslation();
  const { name } = usePrefs();
  const statusLabel = useEnumLabel('assetStatus');
  const query = useAsset(id);
  const [params, setParams] = useSearchParams();
  const tab: Tab = TABS.find((x) => x === params.get('tab')) ?? 'details';
  const [editing, setEditing] = useState(false);
  const [changingStatus, setChangingStatus] = useState(false);

  return (
    <section>
      <p>
        <Link to="/assets">‹ {t('nav.assets')}</Link>
      </p>
      <QueryState query={query}>
        {query.data ? (
          <>
            <header className="toolbar">
              <h1>
                <span className="code">{query.data.code}</span> {name(query.data)}
              </h1>
              <div className="actions">
                <Badge tone={statusTone(query.data.status)}>{statusLabel(query.data.status)}</Badge>
                <Can permission="workorders.request">
                  <Link to={`/work-orders/new?tag=${query.data.tagCode}`} className="button">
                    {t('work.report')}
                  </Link>
                </Can>
                <Can permission="assets.manage">
                  {query.data.status !== 'Retired' ? (
                    <>
                      <button type="button" onClick={() => setEditing(true)}>
                        {t('common.edit')}
                      </button>
                      <button type="button" onClick={() => setChangingStatus(true)}>
                        {t('assets.changeStatus')}
                      </button>
                    </>
                  ) : null}
                </Can>
              </div>
            </header>
            {query.data.statusReason && (query.data.status === 'OutOfService' || query.data.status === 'Retired') ? (
              <p className="banner">{t('assets.statusReason', { reason: query.data.statusReason })}</p>
            ) : null}
            {editing ? (
              <AssetForm asset={query.data} onDone={() => setEditing(false)} />
            ) : (
              <>
                <div className="tabs" role="tablist" aria-label={t('assets.sections')}>
                  {TABS.map((x) => (
                    <button key={x} role="tab" type="button" aria-selected={tab === x} className={tab === x ? 'active' : ''} onClick={() => setParams({ tab: x })}>
                      {t(`assets.tabs.${x}`)}
                    </button>
                  ))}
                </div>
                <div role="tabpanel">
                  {tab === 'details' ? <DetailsTab asset={query.data} /> : null}
                  {tab === 'meters' ? <MetersTab asset={query.data} /> : null}
                  {tab === 'documents' ? <DocumentsTab asset={query.data} /> : null}
                  {tab === 'label' ? <LabelTab asset={query.data} /> : null}
                </div>
              </>
            )}
            {changingStatus ? <StatusDialog asset={query.data} onClose={() => setChangingStatus(false)} /> : null}
          </>
        ) : null}
      </QueryState>
    </section>
  );
}

function StatusDialog({ asset, onClose }: { asset: Asset; onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const client = useQueryClient();
  const label = useEnumLabel('assetStatus');
  const options = NEXT_STATUSES[asset.status];
  const [status, setStatus] = useState<AssetStatus>(options[0]);
  const [reason, setReason] = useState('');
  const needsReason = status === 'OutOfService' || status === 'Retired';

  const change = useMutation({
    mutationFn: () => api.post<Asset>(`/assets/${asset.id}/status`, { status, reason: reason.trim() || null, rowVersion: asset.rowVersion }),
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: ['assets'] });
      onClose();
    },
  });
  const error = change.error instanceof ApiError ? change.error : null;

  return (
    <div className="overlay" onKeyDown={(e) => e.key === 'Escape' && onClose()}>
      <div className="dialog" role="dialog" aria-modal="true" aria-label={t('assets.changeStatus')}>
        <h2>{t('assets.changeStatus')}</h2>
        <form
          onSubmit={(e) => {
            e.preventDefault();
            change.mutate();
          }}
        >
          <Field label={t('assets.newStatus')}>
            <select value={status} onChange={(e) => setStatus(e.target.value as AssetStatus)} autoFocus>
              {options.map((s) => (
                <option key={s} value={s}>
                  {label(s)}
                </option>
              ))}
            </select>
          </Field>
          <Field label={t('common.reason')} required={needsReason} error={error?.field('reason')}>
            <textarea value={reason} onChange={(e) => setReason(e.target.value)} rows={3} maxLength={500} />
          </Field>
          {status === 'Retired' ? <p className="banner">{t('assets.retireWarning')}</p> : null}
          <ErrorBanner error={error?.field('reason') ? null : change.error} />
          <div className="actions">
            <button type="button" onClick={onClose}>
              {t('common.cancel')}
            </button>
            <button type="submit" className="primary" disabled={change.isPending || (needsReason && reason.trim() === '')}>
              {t('common.confirm')}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

function DetailsTab({ asset }: { asset: Asset }) {
  const { t } = useTranslation();
  const { name, date } = usePrefs();
  const critLabel = useEnumLabel('criticality');
  const warrantyLabel = useEnumLabel('warranty');
  const tree = useLocationTree();
  const classes = useAssetClasses();
  const parts = useAssets({ parentAssetId: asset.id, includeRetired: true, pageSize: 50 });
  const parent = useAsset(asset.parentAssetId ?? undefined);
  const cls = classes.data?.find((c) => c.id === asset.assetClassId);

  return (
    <>
      <dl>
        <dt>{t('assets.class')}</dt>
        <dd>{cls ? name(cls) : ''}</dd>
        <dt>{t('assets.location')}</dt>
        <dd>
          <Link to={`/locations?id=${asset.locationId}`}>{breadcrumb(tree.data ?? [], asset.locationId, name)}</Link>
        </dd>
        {parent.data ? (
          <>
            <dt>{t('assets.partOf')}</dt>
            <dd>
              <Link to={`/assets/${parent.data.id}`}>
                {parent.data.code} · {name(parent.data)}
              </Link>
            </dd>
          </>
        ) : null}
        <dt>{t('assets.criticality')}</dt>
        <dd>{critLabel(asset.criticality)}</dd>
        <dt>{t('assets.nameAr')}</dt>
        <dd {...(asset.nameAr ? { lang: 'ar', dir: 'rtl' } : {})}>{asset.nameAr ?? '—'}</dd>
        <dt>{t('assets.manufacturer')}</dt>
        <dd>{asset.manufacturer ?? '—'}</dd>
        <dt>{t('assets.model')}</dt>
        <dd>{asset.model ?? '—'}</dd>
        <dt>{t('assets.serial')}</dt>
        <dd>{asset.serialNumber ?? '—'}</dd>
        <dt>{t('assets.installDate')}</dt>
        <dd>{asset.installDate ? date(asset.installDate) : '—'}</dd>
        <dt>{t('assets.warranty')}</dt>
        <dd>{asset.warranty === 'None' ? '—' : `${warrantyLabel(asset.warranty)} · ${date(asset.warrantyExpiry)}`}</dd>
        <CustomFieldValuesView target="Asset" values={asset.customFields} date={date} />
      </dl>
      <h2>{t('assets.parts')}</h2>
      <QueryState query={parts}>
        {parts.data && parts.data.items.length > 0 ? (
          <ul>
            {parts.data.items.map((p) => (
              <li key={p.id}>
                <Link to={`/assets/${p.id}`}>
                  {p.code} · {name(p)}
                </Link>
              </li>
            ))}
          </ul>
        ) : (
          <p className="muted">{t('assets.noParts')}</p>
        )}
      </QueryState>
    </>
  );
}

function MetersTab({ asset }: { asset: Asset }) {
  const { t } = useTranslation();
  const { name, date, num } = usePrefs();
  const kindLabel = useEnumLabel('meterKind');
  const meters = useMeters(asset.id);
  const [selected, setSelected] = useState<string | undefined>();
  const [adding, setAdding] = useState(false);
  const active = (meters.data ?? []).filter((m) => !m.isArchived);
  const current = active.find((m) => m.id === selected) ?? active[0];
  const retired = asset.status === 'Retired';

  return (
    <QueryState query={meters}>
      <div className="toolbar">
        <h2>{t('meters.title')}</h2>
        <Can permission="meters.manage">
          {!retired ? (
            <button type="button" onClick={() => setAdding(true)}>
              {t('meters.add')}
            </button>
          ) : null}
        </Can>
      </div>
      {active.length === 0 ? <p className="muted">{t('meters.empty')}</p> : null}
      <div className="cards">
        {active.map((m) => (
          <button key={m.id} type="button" className={`card ${current?.id === m.id ? 'selected' : ''}`} aria-pressed={current?.id === m.id} onClick={() => setSelected(m.id)}>
            <strong>{name(m)}</strong>
            <span className="muted">{kindLabel(m.kind)} · {m.unit}</span>
            <span className="value">{m.lastValue === null ? t('meters.noReadings') : `${num(m.lastValue)} ${m.unit}`}</span>
            {m.lastReadAt ? <span className="muted">{date(m.lastReadAt, true)}</span> : null}
          </button>
        ))}
      </div>
      {current ? <MeterDetail key={current.id} meter={current} disabled={retired} /> : null}
      {adding ? <AddMeterDialog assetId={asset.id} onClose={() => setAdding(false)} /> : null}
    </QueryState>
  );
}

function AddMeterDialog({ assetId, onClose }: { assetId: string; onClose: () => void }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const client = useQueryClient();
  const kindLabel = useEnumLabel('meterKind');
  const [name, setName] = useState('');
  const [nameAr, setNameAr] = useState('');
  const [unit, setUnit] = useState('');
  const [kind, setKind] = useState<MeterKind>('Cumulative');
  const [min, setMin] = useState('');
  const [max, setMax] = useState('');

  const create = useMutation({
    mutationFn: () =>
      api.post<Meter>(`/assets/${assetId}/meters`, { name: name.trim(), nameAr: nameAr.trim() || null, unit: unit.trim(), kind, minValue: min === '' ? null : Number(min), maxValue: max === '' ? null : Number(max) }),
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: ['meters', assetId] });
      onClose();
    },
  });
  const errors = create.error instanceof ApiError ? create.error.fieldErrors : {};

  return (
    <div className="overlay" onKeyDown={(e) => e.key === 'Escape' && onClose()}>
      <div className="dialog" role="dialog" aria-modal="true" aria-label={t('meters.add')}>
        <h2>{t('meters.add')}</h2>
        <form onSubmit={(e) => { e.preventDefault(); create.mutate(); }} noValidate>
          <Field label={t('meters.name')} error={errors.name?.[0]} required><input value={name} onChange={(e) => setName(e.target.value)} maxLength={200} autoFocus /></Field>
          <Field label={t('meters.nameAr')} error={errors.nameAr?.[0]}><input value={nameAr} onChange={(e) => setNameAr(e.target.value)} maxLength={200} lang="ar" dir="rtl" /></Field>
          <Field label={t('meters.unit')} error={errors.unit?.[0]} required hint={t('meters.unitHint')}><input value={unit} onChange={(e) => setUnit(e.target.value)} maxLength={20} /></Field>
          <Field label={t('meters.kind')} error={errors.kind?.[0]} hint={t(`meters.kindHint.${kind}`)}>
            <select value={kind} onChange={(e) => setKind(e.target.value as MeterKind)}>
              {(['Cumulative', 'Gauge'] as MeterKind[]).map((k) => <option key={k} value={k}>{kindLabel(k)}</option>)}
            </select>
          </Field>
          <div className="row">
            <Field label={t('meters.min')}><input type="number" step="any" value={min} onChange={(e) => setMin(e.target.value)} /></Field>
            <Field label={t('meters.max')} error={errors.maxValue?.[0]}><input type="number" step="any" value={max} onChange={(e) => setMax(e.target.value)} /></Field>
          </div>
          <ErrorBanner error={Object.keys(errors).length ? null : create.error} />
          <div className="actions">
            <button type="button" onClick={onClose}>{t('common.cancel')}</button>
            <button type="submit" className="primary" disabled={create.isPending}>{t('common.save')}</button>
          </div>
        </form>
      </div>
    </div>
  );
}

function MeterDetail({ meter, disabled }: { meter: Meter; disabled: boolean }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { date, num } = usePrefs();
  const client = useQueryClient();
  const sourceLabel = useEnumLabel('readingSource');
  const [includeVoided, setIncludeVoided] = useState(false);
  const readings = useReadings(meter.id, includeVoided);
  const [voiding, setVoiding] = useState<MeterReading | null>(null);

  const voidReading = useMutation({
    mutationFn: ({ reading, reason }: { reading: MeterReading; reason: string }) => api.post<MeterReading>(`/meter-readings/${reading.id}/void`, { reason }),
    onSuccess: async () => {
      setVoiding(null);
      await Promise.all([client.invalidateQueries({ queryKey: ['readings', meter.id] }), client.invalidateQueries({ queryKey: ['meters', meter.assetId] })]);
    },
  });

  return (
    <div>
      <Can permission="meters.record">{!disabled ? <RecordReadingForm meter={meter} /> : null}</Can>
      <div className="toolbar">
        <h3>{t('meters.history')}</h3>
        <label className="check">
          <input type="checkbox" checked={includeVoided} onChange={(e) => setIncludeVoided(e.target.checked)} /> {t('meters.showVoided')}
        </label>
      </div>
      <QueryState query={readings}>
        {readings.data && readings.data.items.length === 0 ? (
          <p className="muted">{t('meters.noReadings')}</p>
        ) : (
          <div className="table-wrap">
            <table>
              <caption className="visually-hidden">{t('meters.history')}</caption>
              <thead>
                <tr>
                  <th scope="col">{t('meters.readAt')}</th>
                  <th scope="col">{t('meters.value')}</th>
                  <th scope="col">{t('meters.source')}</th>
                  <th scope="col">{t('meters.note')}</th>
                  <th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th>
                </tr>
              </thead>
              <tbody>
                {readings.data?.items.map((r) => (
                  <tr key={r.id} className={r.isVoided ? 'voided' : ''}>
                    <td>{date(r.readAt, true)}</td>
                    <td>
                      {num(r.value)} {meter.unit} {r.isReset ? <Badge tone="warn">{t('meters.reset')}</Badge> : null} {r.isVoided ? <Badge tone="neutral">{t('meters.voided')}</Badge> : null}
                    </td>
                    <td>{sourceLabel(r.source)}</td>
                    <td>{r.note}</td>
                    <td>
                      <Can permission="meters.manage">
                        {!r.isVoided ? (
                          <button type="button" onClick={() => setVoiding(r)}>
                            {t('meters.void')}
                          </button>
                        ) : null}
                      </Can>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </QueryState>
      {voiding ? (
        <ReasonDialog
          title={t('meters.voidTitle', { value: `${num(voiding.value)} ${meter.unit}` })}
          label={t('common.reason')}
          confirmLabel={t('meters.void')}
          busy={voidReading.isPending}
          error={voidReading.error}
          onCancel={() => setVoiding(null)}
          onConfirm={(reason) => voidReading.mutate({ reading: voiding, reason })}
        />
      ) : null}
    </div>
  );
}

function RecordReadingForm({ meter }: { meter: Meter }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const client = useQueryClient();
  const [value, setValue] = useState('');
  const [readAt, setReadAt] = useState('');
  const [note, setNote] = useState('');
  const [isReset, setIsReset] = useState(false);
  // One client id per attempt: a retry after a network drop is recognised by the server instead of being stored twice.
  const [clientId, setClientId] = useState(() => crypto.randomUUID());
  const [done, setDone] = useState(false);

  const record = useMutation({
    mutationFn: () =>
      api.post<MeterReading>(`/meters/${meter.id}/readings`, {
        value: Number(value), readAt: readAt ? new Date(readAt).toISOString() : null, note: note.trim() || null, isReset, clientId, source: 'Manual',
      }),
    onSuccess: async () => {
      setValue(''); setReadAt(''); setNote(''); setIsReset(false); setClientId(crypto.randomUUID()); setDone(true);
      await Promise.all([client.invalidateQueries({ queryKey: ['readings', meter.id] }), client.invalidateQueries({ queryKey: ['meters', meter.assetId] })]);
    },
  });
  const errors = record.error instanceof ApiError ? record.error.fieldErrors : {};
  useEffect(() => { if (done) { const id = setTimeout(() => setDone(false), 3000); return () => clearTimeout(id); } }, [done]);

  return (
    <form className="inline-form" onSubmit={(e) => { e.preventDefault(); setDone(false); record.mutate(); }} noValidate aria-label={t('meters.record')}>
      <h3>{t('meters.record')}</h3>
      <div className="row">
        <Field label={`${t('meters.value')} (${meter.unit})`} error={errors.value?.[0]} required>
          <input type="number" step="any" inputMode="decimal" value={value} onChange={(e) => setValue(e.target.value)} />
        </Field>
        <Field label={t('meters.readAtOptional')} error={errors.readAt?.[0]}>
          <input type="datetime-local" value={readAt} onChange={(e) => setReadAt(e.target.value)} />
        </Field>
        <Field label={t('meters.note')} error={errors.note?.[0]}>
          <input value={note} onChange={(e) => setNote(e.target.value)} maxLength={500} />
        </Field>
      </div>
      {meter.kind === 'Cumulative' ? (
        <label className="check">
          <input type="checkbox" checked={isReset} onChange={(e) => setIsReset(e.target.checked)} /> {t('meters.isReset')}
        </label>
      ) : null}
      <ErrorBanner error={Object.keys(errors).length ? null : record.error} />
      {done ? <p className="banner ok" role="status">{t('meters.recorded')}</p> : null}
      <button type="submit" className="primary" disabled={record.isPending || value.trim() === ''}>
        {t('meters.record')}
      </button>
    </form>
  );
}

const DOC_KINDS: DocumentKind[] = ['Manual', 'Warranty', 'Certificate', 'Photo', 'Drawing', 'Other'];
const MAX_UPLOAD_BYTES = 25 * 1024 * 1024;

function DocumentsTab({ asset }: { asset: Asset }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { date, lang } = usePrefs();
  const client = useQueryClient();
  const kindLabel = useEnumLabel('docKind');
  const docs = useDocuments(asset.id);
  const [kind, setKind] = useState<DocumentKind>('Manual');
  const [description, setDescription] = useState('');
  const [file, setFile] = useState<File | null>(null);
  const [deleting, setDeleting] = useState<AssetDocument | null>(null);
  const [downloadError, setDownloadError] = useState<unknown>(null);
  const retired = asset.status === 'Retired';

  const upload = useMutation({
    mutationFn: () => {
      const form = new FormData();
      form.append('file', file!);
      form.append('kind', kind);
      if (description.trim()) form.append('description', description.trim());
      return api.upload<AssetDocument>(`/assets/${asset.id}/documents`, form);
    },
    onSuccess: async () => {
      setFile(null); setDescription('');
      await client.invalidateQueries({ queryKey: ['documents', asset.id] });
    },
  });
  const remove = useMutation({
    mutationFn: ({ doc, reason }: { doc: AssetDocument; reason: string }) => api.post(`/documents/${doc.id}/delete`, { reason }),
    onSuccess: async () => {
      setDeleting(null);
      await client.invalidateQueries({ queryKey: ['documents', asset.id] });
    },
  });

  const download = async (doc: AssetDocument) => {
    setDownloadError(null);
    try {
      const { blob, fileName } = await api.blob(`/documents/${doc.id}/download`);
      downloadBlob(fileName ?? doc.fileName, blob);
    } catch (e) {
      setDownloadError(e);
    }
  };

  const tooBig = file !== null && file.size > MAX_UPLOAD_BYTES;

  return (
    <>
      <h2>{t('documents.title')}</h2>
      <Can permission="documents.manage">
        {!retired ? (
          <form className="inline-form" onSubmit={(e) => { e.preventDefault(); upload.mutate(); }} aria-label={t('documents.upload')}>
            <div className="row">
              <Field label={t('documents.file')} hint={t('documents.fileHint')} error={tooBig ? t('errors.tooLarge') : undefined}>
                <input type="file" accept=".pdf,.png,.jpg,.jpeg,.webp,.docx,.xlsx,.txt" onChange={(e) => setFile(e.target.files?.[0] ?? null)} />
              </Field>
              <Field label={t('documents.kind')}>
                <select value={kind} onChange={(e) => setKind(e.target.value as DocumentKind)}>
                  {DOC_KINDS.map((k) => <option key={k} value={k}>{kindLabel(k)}</option>)}
                </select>
              </Field>
              <Field label={t('documents.description')}>
                <input value={description} onChange={(e) => setDescription(e.target.value)} maxLength={500} />
              </Field>
            </div>
            <ErrorBanner error={upload.error} />
            <button type="submit" className="primary" disabled={!file || tooBig || upload.isPending}>{t('documents.upload')}</button>
          </form>
        ) : null}
      </Can>
      <ErrorBanner error={downloadError} />
      <QueryState query={docs}>
        {docs.data && docs.data.length === 0 ? (
          <p className="muted">{t('documents.empty')}</p>
        ) : (
          <div className="table-wrap">
            <table>
              <caption className="visually-hidden">{t('documents.title')}</caption>
              <thead>
                <tr>
                  <th scope="col">{t('documents.fileName')}</th>
                  <th scope="col">{t('documents.kind')}</th>
                  <th scope="col">{t('documents.size')}</th>
                  <th scope="col">{t('documents.uploadedAt')}</th>
                  <th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th>
                </tr>
              </thead>
              <tbody>
                {docs.data?.map((d) => (
                  <tr key={d.id}>
                    <td>
                      <bdi>{d.fileName}</bdi>
                      {d.description ? <div className="muted">{d.description}</div> : null}
                    </td>
                    <td>{kindLabel(d.kind)}</td>
                    <td>{formatBytes(d.sizeBytes, lang)}</td>
                    <td>{date(d.uploadedAt, true)}</td>
                    <td className="actions">
                      <button type="button" onClick={() => void download(d)}>{t('documents.download')}</button>
                      <Can permission="documents.manage">
                        <button type="button" onClick={() => setDeleting(d)}>{t('common.delete')}</button>
                      </Can>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </QueryState>
      {deleting ? (
        <ReasonDialog
          title={t('documents.deleteTitle', { name: deleting.fileName })}
          label={t('common.reason')}
          confirmLabel={t('common.delete')}
          busy={remove.isPending}
          error={remove.error}
          onCancel={() => setDeleting(null)}
          onConfirm={(reason) => remove.mutate({ doc: deleting, reason })}
        />
      ) : null}
    </>
  );
}

function LabelTab({ asset }: { asset: Asset }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const client = useQueryClient();
  const [src, setSrc] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [confirming, setConfirming] = useState(false);

  useEffect(() => {
    let url: string | null = null;
    let cancelled = false;
    setSrc(null);
    api.blob(`/assets/${asset.id}/qr.svg`).then(
      ({ blob }) => {
        if (cancelled) return;
        url = URL.createObjectURL(blob);
        setSrc(url);
      },
      (e) => !cancelled && setError(e),
    );
    return () => {
      cancelled = true;
      if (url) URL.revokeObjectURL(url);
    };
  }, [api, asset.id, asset.tagCode]);

  const reissue = useMutation({
    mutationFn: () => api.post<Asset>(`/assets/${asset.id}/reissue-tag`, { rowVersion: asset.rowVersion }),
    onSuccess: async () => {
      setConfirming(false);
      await client.invalidateQueries({ queryKey: ['assets'] });
    },
  });

  return (
    <div className="label-tab">
      <h2>{t('label.title')}</h2>
      <div className="label-card" id="print-label">
        {src ? <img src={src} alt={t('label.qrAlt', { code: asset.code })} width={200} height={200} /> : <p className="muted">{t('common.loading')}</p>}
        <div className="label-text">
          <strong>{asset.code}</strong>
          <span>{asset.name}</span>
          <code dir="ltr">{asset.tagCode}</code>
        </div>
      </div>
      <ErrorBanner error={error} />
      <div className="actions no-print">
        <button type="button" className="primary" onClick={() => window.print()}>{t('label.print')}</button>
        <Can permission="assets.manage">
          {asset.status !== 'Retired' ? <button type="button" onClick={() => setConfirming(true)}>{t('label.reissue')}</button> : null}
        </Can>
      </div>
      {confirming ? (
        <div className="overlay" onKeyDown={(e) => e.key === 'Escape' && setConfirming(false)}>
          <div className="dialog" role="dialog" aria-modal="true" aria-label={t('label.reissue')}>
            <h2>{t('label.reissue')}</h2>
            <p>{t('label.reissueWarning')}</p>
            <ErrorBanner error={reissue.error} />
            <div className="actions">
              <button type="button" onClick={() => setConfirming(false)}>{t('common.cancel')}</button>
              <button type="button" className="primary" disabled={reissue.isPending} onClick={() => reissue.mutate()} autoFocus>{t('label.reissue')}</button>
            </div>
          </div>
        </div>
      ) : null}
    </div>
  );
}

// Re-exported for tests that exercise status rules without rendering the page.
export { NEXT_STATUSES };
