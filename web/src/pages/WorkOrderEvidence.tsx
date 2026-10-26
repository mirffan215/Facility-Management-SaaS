import { useEffect, useRef, useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ApiError } from '../api/client';
import { useAttachments, useChecklist, useChecklistTemplates, useCostLines, useCostSummary, useMe, usePermissions, useSignatures, useTimeEntries } from '../api/hooks';
import type { AttachmentKind, ChecklistItem, ChecklistResult, CostKind, SignatureRole, WorkAttachment, WorkOrder } from '../api/types';
import { useAuth } from '../auth/AuthContext';
import { Badge, ErrorBanner, Field, QueryState, ReasonDialog, useEnumLabel } from '../components/ui';
import { downloadBlob } from '../lib/csv';
import { formatNumber } from '../lib/format';
import { usePrefs } from '../lib/prefs';

/** Whether this person records the work itself: the assignee with work permission, or a manager. The server decides again on every call. */
function useDoesTheWork(order: WorkOrder) {
  const me = useMe().data;
  const { can } = usePermissions();
  return can('workorders.manage') || (order.assignedTo !== null && order.assignedTo === me?.userId && can('workorders.work'));
}

const LEDGER_OPEN = ['InProgress', 'OnHold', 'Completed'];

function useRefresh(orderId: string) {
  const client = useQueryClient();
  return () => client.invalidateQueries({ queryKey: ['work-evidence', orderId] });
}

// ---------------- checklist ----------------

export function ChecklistTab({ order }: { order: WorkOrder }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { can } = usePermissions();
  const refresh = useRefresh(order.id);
  const doer = useDoesTheWork(order);
  const resultLabel = useEnumLabel('checklistResult');
  const list = useChecklist(order.id);
  const attachable = ['Approved', 'Assigned', 'InProgress', 'OnHold'].includes(order.status) && (doer || can('workorders.assign'));
  const fillable = doer && ['InProgress', 'OnHold'].includes(order.status);
  const templates = useChecklistTemplates(attachable);
  const [templateId, setTemplateId] = useState('');

  const attach = useMutation({ mutationFn: () => api.post(`/work-orders/${order.id}/checklist`, { templateId }), onSuccess: async () => { setTemplateId(''); await refresh(); } });

  return (
    <>
      <h2>{t('evidence.checklist')}</h2>
      <QueryState query={list}>
        {list.data && list.data.length === 0 ? <p className="muted">{t('evidence.noChecklist')}</p> : null}
        <ul className="checklist" aria-label={t('evidence.checklist')}>
          {list.data?.map((i) => <ChecklistRow key={i.id} order={order} item={i} editable={fillable} label={resultLabel} />)}
        </ul>
      </QueryState>
      {attachable && templates.data ? (
        <form onSubmit={(e) => { e.preventDefault(); attach.mutate(); }}>
          <Field label={t('evidence.addChecklist')}>
            <select value={templateId} onChange={(e) => setTemplateId(e.target.value)}>
              <option value="">{t('common.choose')}</option>
              {templates.data.map((x) => <option key={x.id} value={x.id}>{x.name}</option>)}
            </select>
          </Field>
          <ErrorBanner error={attach.error} />
          <button type="submit" disabled={!templateId || attach.isPending}>{t('evidence.attach')}</button>
        </form>
      ) : null}
    </>
  );
}

function ChecklistRow({ order, item, editable, label }: { order: WorkOrder; item: ChecklistItem; editable: boolean; label: (r: string) => string }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { date } = usePrefs();
  const refresh = useRefresh(order.id);
  const [result, setResult] = useState<ChecklistResult>(item.result);
  const [note, setNote] = useState(item.note ?? '');
  const save = useMutation({ mutationFn: (next: ChecklistResult) => api.post(`/work-orders/${order.id}/checklist/${item.id}`, { result: next, note: note.trim() || null }), onSuccess: refresh });
  const error = save.error instanceof ApiError ? save.error : null;
  const tone = item.result === 'Done' ? 'ok' : item.result === 'Failed' ? 'bad' : item.result === 'NotApplicable' ? 'neutral' : 'warn';

  return (
    <li>
      <div>
        <strong><bdi>{item.text}</bdi></strong> {item.required ? <Badge tone="neutral">{t('evidence.required')}</Badge> : null} <Badge tone={tone}>{label(item.result)}</Badge>
      </div>
      {item.note ? <div className="muted"><bdi>{item.note}</bdi></div> : null}
      {item.resultAt ? <div className="muted">{item.resultByName} · {date(item.resultAt, true)}</div> : null}
      {editable ? (
        <div className="row">
          <Field label={`${t('evidence.result')}: ${item.text}`} error={error?.field('result')}>
            <select value={result} onChange={(e) => { const next = e.target.value as ChecklistResult; setResult(next); if (next !== 'Failed') save.mutate(next); }}>
              {(['Pending', 'Done', 'Failed', 'NotApplicable'] as ChecklistResult[]).map((r) => <option key={r} value={r}>{label(r)}</option>)}
            </select>
          </Field>
          <Field label={t('evidence.note')} error={error?.field('note')}>
            <input value={note} onChange={(e) => setNote(e.target.value)} maxLength={500} onBlur={() => result === 'Failed' && note.trim() && save.mutate(result)} />
          </Field>
          {result === 'Failed' ? <button type="button" disabled={save.isPending || !note.trim()} onClick={() => save.mutate('Failed')}>{t('common.save')}</button> : null}
        </div>
      ) : null}
      <ErrorBanner error={error?.status === 400 ? null : save.error} />
    </li>
  );
}

// ---------------- time and costs ----------------

export function CostsTab({ order }: { order: WorkOrder }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { date, lang } = usePrefs();
  const refresh = useRefresh(order.id);
  const doer = useDoesTheWork(order);
  const kindLabel = useEnumLabel('costKind');
  const time = useTimeEntries(order.id, true);
  const costs = useCostLines(order.id, true);
  const summary = useCostSummary(order.id, true);
  const open = doer && LEDGER_OPEN.includes(order.status);
  const money = (n: number) => `${formatNumber(n, lang, 2)} ${summary.data?.currencyCode ?? ''}`;

  const today = new Date().toISOString().slice(0, 10);
  const [workedOn, setWorkedOn] = useState(today);
  const [minutes, setMinutes] = useState('');
  const [timeNote, setTimeNote] = useState('');
  const [kind, setKind] = useState<CostKind>('Material');
  const [description, setDescription] = useState('');
  const [reference, setReference] = useState('');
  const [quantity, setQuantity] = useState('1');
  const [unitCost, setUnitCost] = useState('');
  const [voiding, setVoiding] = useState<{ type: 'time' | 'costs'; id: string } | null>(null);

  const addTime = useMutation({
    mutationFn: () => api.post(`/work-orders/${order.id}/time`, { workedOn, minutes: Number(minutes), note: timeNote.trim() || null }),
    onSuccess: async () => { setMinutes(''); setTimeNote(''); await refresh(); },
  });
  const addCost = useMutation({
    mutationFn: () => api.post(`/work-orders/${order.id}/costs`, { kind, description: description.trim(), reference: reference.trim() || null, quantity: Number(quantity), unitCost: Number(unitCost) }),
    onSuccess: async () => { setDescription(''); setReference(''); setQuantity('1'); setUnitCost(''); await refresh(); },
  });
  const voidIt = useMutation({
    mutationFn: ({ reason }: { reason: string }) => api.post(`/work-orders/${order.id}/${voiding!.type}/${voiding!.id}/void`, { reason }),
    onSuccess: async () => { setVoiding(null); await refresh(); },
  });
  const timeError = addTime.error instanceof ApiError ? addTime.error : null;
  const costError = addCost.error instanceof ApiError ? addCost.error : null;

  return (
    <>
      <h2>{t('evidence.summary')}</h2>
      <QueryState query={summary}>
        {summary.data ? (
          <dl className="details" aria-label={t('evidence.summary')}>
            <dt>{t('evidence.labour')}</dt><dd>{formatNumber(summary.data.minutes / 60, lang, 2)} {t('evidence.hoursUnit')} · {money(summary.data.labourCost)}</dd>
            <dt>{kindLabel('Material')}</dt><dd>{money(summary.data.material)}</dd>
            <dt>{kindLabel('Contractor')}</dt><dd>{money(summary.data.contractor)}</dd>
            <dt>{kindLabel('Other')}</dt><dd>{money(summary.data.other)}</dd>
            <dt><strong>{t('evidence.total')}</strong></dt><dd><strong>{money(summary.data.total)}</strong></dd>
          </dl>
        ) : null}
      </QueryState>

      <h2>{t('evidence.time')}</h2>
      {open ? (
        <form onSubmit={(e) => { e.preventDefault(); addTime.mutate(); }} aria-label={t('evidence.addTime')}>
          <div className="row">
            <Field label={t('evidence.workedOn')} error={timeError?.field('workedOn')} required><input type="date" value={workedOn} onChange={(e) => setWorkedOn(e.target.value)} /></Field>
            <Field label={t('evidence.minutes')} error={timeError?.field('minutes')} required><input type="number" min={1} max={1440} value={minutes} onChange={(e) => setMinutes(e.target.value)} /></Field>
            <Field label={t('evidence.note')} error={timeError?.field('note')}><input value={timeNote} onChange={(e) => setTimeNote(e.target.value)} maxLength={500} /></Field>
          </div>
          <ErrorBanner error={timeError?.status === 400 ? null : addTime.error} />
          <button type="submit" className="primary" disabled={addTime.isPending || !minutes}>{t('evidence.addTime')}</button>
        </form>
      ) : null}
      <QueryState query={time}>
        {time.data && time.data.length === 0 ? <p className="muted">{t('evidence.noTime')}</p> : (
          <div className="table-wrap"><table>
            <caption className="visually-hidden">{t('evidence.time')}</caption>
            <thead><tr><th scope="col">{t('evidence.workedOn')}</th><th scope="col">{t('evidence.worker')}</th><th scope="col">{t('evidence.minutes')}</th><th scope="col">{t('evidence.cost')}</th><th scope="col">{t('evidence.note')}</th><th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th></tr></thead>
            <tbody>
              {time.data?.map((e) => (
                <tr key={e.id} className={e.isVoided ? 'voided' : undefined}>
                  <td>{date(e.workedOn)}</td><td><bdi>{e.workerName ?? '—'}</bdi></td><td>{e.minutes}</td><td>{money(e.labourCost)}</td>
                  <td><bdi>{e.note ?? ''}</bdi> {e.isVoided ? <Badge tone="bad">{t('evidence.voided')}: {e.voidReason}</Badge> : null}</td>
                  <td>{open && !e.isVoided ? <button type="button" onClick={() => setVoiding({ type: 'time', id: e.id })}>{t('evidence.void')}</button> : null}</td>
                </tr>
              ))}
            </tbody>
          </table></div>
        )}
      </QueryState>

      <h2>{t('evidence.costs')}</h2>
      {open ? (
        <form onSubmit={(e) => { e.preventDefault(); addCost.mutate(); }} aria-label={t('evidence.addCost')}>
          <div className="row">
            <Field label={t('evidence.kind')} error={costError?.field('kind')}>
              <select value={kind} onChange={(e) => setKind(e.target.value as CostKind)}>{(['Material', 'Contractor', 'Other'] as CostKind[]).map((k) => <option key={k} value={k}>{kindLabel(k)}</option>)}</select>
            </Field>
            <Field label={t('evidence.description')} error={costError?.field('description')} required><input value={description} onChange={(e) => setDescription(e.target.value)} maxLength={300} /></Field>
            <Field label={t('evidence.reference')} error={costError?.field('reference')} hint={t('evidence.referenceHint')}><input value={reference} onChange={(e) => setReference(e.target.value)} maxLength={100} /></Field>
          </div>
          <div className="row">
            <Field label={t('evidence.quantity')} error={costError?.field('quantity')} required><input type="number" min={0} step="any" value={quantity} onChange={(e) => setQuantity(e.target.value)} /></Field>
            <Field label={t('evidence.unitCost')} error={costError?.field('unitCost')} required><input type="number" min={0} step="any" value={unitCost} onChange={(e) => setUnitCost(e.target.value)} /></Field>
          </div>
          <ErrorBanner error={costError?.status === 400 ? null : addCost.error} />
          <button type="submit" className="primary" disabled={addCost.isPending || !description.trim() || unitCost === ''}>{t('evidence.addCost')}</button>
        </form>
      ) : null}
      <QueryState query={costs}>
        {costs.data && costs.data.length === 0 ? <p className="muted">{t('evidence.noCosts')}</p> : (
          <div className="table-wrap"><table>
            <caption className="visually-hidden">{t('evidence.costs')}</caption>
            <thead><tr><th scope="col">{t('evidence.kind')}</th><th scope="col">{t('evidence.description')}</th><th scope="col">{t('evidence.reference')}</th><th scope="col">{t('evidence.quantity')}</th><th scope="col">{t('evidence.unitCost')}</th><th scope="col">{t('evidence.total')}</th><th scope="col"><span className="visually-hidden">{t('common.actions')}</span></th></tr></thead>
            <tbody>
              {costs.data?.map((c) => (
                <tr key={c.id} className={c.isVoided ? 'voided' : undefined}>
                  <td>{kindLabel(c.kind)}</td><td><bdi>{c.description}</bdi> {c.isVoided ? <Badge tone="bad">{t('evidence.voided')}: {c.voidReason}</Badge> : null}</td><td><bdi>{c.reference ?? ''}</bdi></td>
                  <td>{formatNumber(c.quantity, lang, 3)}</td><td>{formatNumber(c.unitCost, lang, 4)}</td><td>{money(c.total)}</td>
                  <td>{open && !c.isVoided ? <button type="button" onClick={() => setVoiding({ type: 'costs', id: c.id })}>{t('evidence.void')}</button> : null}</td>
                </tr>
              ))}
            </tbody>
          </table></div>
        )}
      </QueryState>
      {voiding ? <ReasonDialog title={t('evidence.void')} label={t('common.reason')} confirmLabel={t('evidence.void')} busy={voidIt.isPending} error={voidIt.error} onConfirm={(reason) => voidIt.mutate({ reason })} onCancel={() => setVoiding(null)} /> : null}
    </>
  );
}

// ---------------- photos and files ----------------

/** An image behind the sign-in: fetched with the token and shown from memory, so the address of the file never needs to be public. */
export function AuthImage({ path, alt }: { path: string; alt: string }) {
  const { api } = useAuth();
  const [src, setSrc] = useState<string | null>(null);
  useEffect(() => {
    let url: string | null = null;
    let cancelled = false;
    api.blob(path).then(({ blob }) => {
      if (cancelled) return;
      url = URL.createObjectURL(blob);
      setSrc(url);
    }, () => undefined);
    return () => {
      cancelled = true;
      if (url) URL.revokeObjectURL(url);
    };
  }, [api, path]);
  return src ? <img src={src} alt={alt} className="thumb" /> : <span className="thumb placeholder" aria-hidden="true" />;
}

const MAX_BYTES = 15 * 1024 * 1024;

export function FilesTab({ order }: { order: WorkOrder }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { date } = usePrefs();
  const { can } = usePermissions();
  const me = useMe().data;
  const refresh = useRefresh(order.id);
  const kindLabel = useEnumLabel('attachmentKind');
  const list = useAttachments(order.id);
  const doer = useDoesTheWork(order);
  const open = !['Closed', 'Cancelled'].includes(order.status);
  const mayAdd = open && (doer || can('workorders.assign') || order.requestedBy === me?.userId);
  const [file, setFile] = useState<File | null>(null);
  const [kind, setKind] = useState<AttachmentKind>('Before');
  const [description, setDescription] = useState('');
  const [removing, setRemoving] = useState<WorkAttachment | null>(null);
  const [downloadError, setDownloadError] = useState<unknown>(null);
  const input = useRef<HTMLInputElement>(null);

  const upload = useMutation({
    mutationFn: () => {
      const form = new FormData();
      form.append('file', file!);
      form.append('kind', kind);
      if (description.trim()) form.append('description', description.trim());
      return api.upload(`/work-orders/${order.id}/attachments`, form);
    },
    onSuccess: async () => { setFile(null); setDescription(''); if (input.current) input.current.value = ''; await refresh(); },
  });
  const remove = useMutation({
    mutationFn: ({ a, reason }: { a: WorkAttachment; reason: string }) => api.post(`/work-orders/attachments/${a.id}/delete`, { reason }),
    onSuccess: async () => { setRemoving(null); await refresh(); },
  });
  const download = async (a: WorkAttachment) => {
    setDownloadError(null);
    try {
      const { blob, fileName } = await api.blob(`/work-orders/attachments/${a.id}/download`);
      downloadBlob(fileName ?? a.fileName, blob);
    } catch (e) {
      setDownloadError(e);
    }
  };
  const tooBig = file !== null && file.size > MAX_BYTES;

  return (
    <>
      <h2>{t('evidence.files')}</h2>
      {mayAdd ? (
        <form onSubmit={(e) => { e.preventDefault(); upload.mutate(); }} aria-label={t('evidence.addFile')}>
          <div className="row">
            <Field label={t('evidence.file')} hint={t('evidence.fileHint')} error={tooBig ? t('errors.tooLarge') : undefined}>
              <input ref={input} type="file" accept="image/*,.pdf,.docx,.xlsx,.txt" capture="environment" onChange={(e) => setFile(e.target.files?.[0] ?? null)} />
            </Field>
            <Field label={t('evidence.kind')}>
              <select value={kind} onChange={(e) => setKind(e.target.value as AttachmentKind)}>{(['Before', 'After', 'Other'] as AttachmentKind[]).map((k) => <option key={k} value={k}>{kindLabel(k)}</option>)}</select>
            </Field>
            <Field label={t('evidence.description')}><input value={description} onChange={(e) => setDescription(e.target.value)} maxLength={500} /></Field>
          </div>
          <ErrorBanner error={upload.error} />
          <button type="submit" className="primary" disabled={!file || tooBig || upload.isPending}>{t('evidence.addFile')}</button>
        </form>
      ) : null}
      <ErrorBanner error={downloadError} />
      <QueryState query={list}>
        {list.data && list.data.length === 0 ? <p className="muted">{t('evidence.noFiles')}</p> : null}
        <ul className="files" aria-label={t('evidence.files')}>
          {list.data?.map((a) => (
            <li key={a.id}>
              {a.contentType.startsWith('image/') ? <AuthImage path={`/work-orders/attachments/${a.id}/download`} alt={a.description ?? a.fileName} /> : null}
              <div>
                <strong><bdi>{a.fileName}</bdi></strong> <Badge tone="neutral">{kindLabel(a.kind)}</Badge>
                {a.description ? <div><bdi>{a.description}</bdi></div> : null}
                <div className="muted"><bdi>{a.uploadedByName ?? '—'}</bdi> · {date(a.uploadedAt, true)}</div>
                <button type="button" onClick={() => void download(a)}>{t('evidence.download')}</button>{' '}
                {open && (can('workorders.manage') || a.uploadedBy === me?.userId) ? <button type="button" onClick={() => setRemoving(a)}>{t('evidence.remove')}</button> : null}
              </div>
            </li>
          ))}
        </ul>
      </QueryState>
      {removing ? <ReasonDialog title={t('evidence.remove')} label={t('common.reason')} confirmLabel={t('evidence.remove')} busy={remove.isPending} error={remove.error} onConfirm={(reason) => remove.mutate({ a: removing, reason })} onCancel={() => setRemoving(null)} /> : null}
    </>
  );
}

// ---------------- signatures ----------------

/** A drawing pad. Works with finger, pen and mouse; the printed name beside it is what a keyboard user types. */
export function SignaturePad({ onChange }: { onChange: (png: string | null) => void }) {
  const { t } = useTranslation();
  const canvas = useRef<HTMLCanvasElement>(null);
  const drawing = useRef(false);

  const point = (e: React.PointerEvent<HTMLCanvasElement>) => {
    const rect = e.currentTarget.getBoundingClientRect();
    return { x: ((e.clientX - rect.left) / rect.width) * e.currentTarget.width, y: ((e.clientY - rect.top) / rect.height) * e.currentTarget.height };
  };
  const start = (e: React.PointerEvent<HTMLCanvasElement>) => {
    const ctx = e.currentTarget.getContext('2d');
    if (!ctx) return;
    e.currentTarget.setPointerCapture(e.pointerId);
    drawing.current = true;
    ctx.lineWidth = 3; ctx.lineCap = 'round'; ctx.strokeStyle = '#000';
    const { x, y } = point(e);
    ctx.beginPath(); ctx.moveTo(x, y); ctx.lineTo(x + 0.1, y + 0.1); ctx.stroke();
  };
  const move = (e: React.PointerEvent<HTMLCanvasElement>) => {
    if (!drawing.current) return;
    const ctx = e.currentTarget.getContext('2d');
    if (!ctx) return;
    const { x, y } = point(e);
    ctx.lineTo(x, y); ctx.stroke();
  };
  const end = (e: React.PointerEvent<HTMLCanvasElement>) => {
    if (!drawing.current) return;
    drawing.current = false;
    onChange(e.currentTarget.toDataURL('image/png'));
  };
  const clear = () => {
    const c = canvas.current;
    c?.getContext('2d')?.clearRect(0, 0, c.width, c.height);
    onChange(null);
  };

  return (
    <div className="pad">
      <canvas ref={canvas} width={600} height={200} role="img" aria-label={t('evidence.padLabel')} onPointerDown={start} onPointerMove={move} onPointerUp={end} onPointerCancel={end} />
      <button type="button" onClick={clear}>{t('evidence.clearPad')}</button>
    </div>
  );
}

export function SignaturesTab({ order }: { order: WorkOrder }) {
  const { t } = useTranslation();
  const { api } = useAuth();
  const { date } = usePrefs();
  const refresh = useRefresh(order.id);
  const doer = useDoesTheWork(order);
  const roleLabel = useEnumLabel('signatureRole');
  const list = useSignatures(order.id);
  const open = doer && LEDGER_OPEN.includes(order.status);
  const [role, setRole] = useState<SignatureRole>('Technician');
  const [name, setName] = useState('');
  const [image, setImage] = useState<string | null>(null);
  const [padKey, setPadKey] = useState(0);

  const sign = useMutation({
    mutationFn: () => api.post(`/work-orders/${order.id}/signatures`, { role, signerName: name.trim(), imageBase64: image }),
    onSuccess: async () => { setName(''); setImage(null); setPadKey((k) => k + 1); await refresh(); },
  });
  const error = sign.error instanceof ApiError ? sign.error : null;

  return (
    <>
      <h2>{t('evidence.signatures')}</h2>
      <QueryState query={list}>
        {list.data && list.data.length === 0 ? <p className="muted">{t('evidence.noSignatures')}</p> : null}
        <ul className="files" aria-label={t('evidence.signatures')}>
          {list.data?.map((s) => (
            <li key={s.id}>
              <AuthImage path={`/work-orders/signatures/${s.id}/image`} alt={t('evidence.signatureOf', { name: s.signerName })} />
              <div>
                <strong><bdi>{s.signerName}</bdi></strong> <Badge tone="neutral">{roleLabel(s.role)}</Badge>
                <div className="muted">{date(s.signedAt, true)} · <bdi>{s.recordedByName ?? '—'}</bdi></div>
              </div>
            </li>
          ))}
        </ul>
      </QueryState>
      {open ? (
        <form onSubmit={(e) => { e.preventDefault(); sign.mutate(); }} aria-label={t('evidence.addSignature')}>
          <h3>{t('evidence.addSignature')}</h3>
          <div className="row">
            <Field label={t('evidence.signingAs')} error={error?.field('role')}>
              <select value={role} onChange={(e) => setRole(e.target.value as SignatureRole)}>{(['Technician', 'Customer'] as SignatureRole[]).map((r) => <option key={r} value={r}>{roleLabel(r)}</option>)}</select>
            </Field>
            <Field label={t('evidence.printedName')} error={error?.field('signerName')} required><input value={name} onChange={(e) => setName(e.target.value)} maxLength={200} /></Field>
          </div>
          <SignaturePad key={padKey} onChange={setImage} />
          {error?.field('image') ? <small className="error" role="alert">{error.field('image')}</small> : null}
          <ErrorBanner error={error?.status === 400 ? null : sign.error} />
          <button type="submit" className="primary" disabled={sign.isPending || !image || !name.trim()}>{t('evidence.sign')}</button>
        </form>
      ) : null}
    </>
  );
}
