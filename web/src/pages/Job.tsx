import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import type { AttachmentKind, ChecklistResult, ClosureCode, CostKind, SignatureRole } from '../api/types';
import { usePermissions } from '../api/hooks';
import { useAuth } from '../auth/AuthContext';
import { Badge, ErrorBanner, Field, ReasonDialog, useEnumLabel } from '../components/ui';
import { SummaryPanel } from '../components/AiAssist';
import { preparePhoto } from '../offline/image';
import { applyOps, completionBlocker, type OfflineJob, type Op, type Pending } from '../offline/model';
import { useOffline } from '../offline/OfflineProvider';
import { usePrefs } from '../lib/prefs';
import { workSourceKey } from '../lib/workSource';
import { priorityTone, workStatusTone } from './WorkOrders';
import { AuthImage, SignaturePad } from './WorkOrderEvidence';

const CLOSURE_CODES: ClosureCode[] = ['Fixed', 'NoFaultFound', 'TemporaryFix', 'PartsRequired', 'Duplicate', 'NotRequired'];

/** One job, worked from the phone. Everything the person does is saved on the device first and sent when a connection allows. */
export function JobPage() {
  const { id } = useParams();
  const { t } = useTranslation();
  const { displayName } = useAuth();
  const { date } = usePrefs();
  const { snapshot, ops, ready, enqueue, online } = useOffline();
  const statusLabel = useEnumLabel('workStatus');
  const priorityLabel = useEnumLabel('workPriority');
  const [dialog, setDialog] = useState<'hold' | 'complete' | null>(null);

  const stored = snapshot?.jobs.find((j) => j.order.id === id);
  if (!ready) return <p className="muted">{t('common.loading')}</p>;
  if (!stored) {
    return (
      <section>
        <p><Link to="/jobs">← {t('jobs.title')}</Link></p>
        <p className="banner">{t('jobs.notFound')}</p>
      </section>
    );
  }

  const job = applyOps(stored, ops, displayName ?? '');
  const o = job.order;
  const blocker = completionBlocker(job, snapshot?.requireCompletionSignature ?? false);
  const waiting = (itemId: string) => job.opIds.has(itemId);

  return (
    <section className="job">
      <p><Link to="/jobs">← {t('jobs.title')}</Link></p>
      <h1><bdi>{o.displayNumber}</bdi> · <bdi>{o.title}</bdi></h1>
      <p>
        <Badge tone={workStatusTone(o.status)}>{statusLabel(o.status)}</Badge> <Badge tone={priorityTone(o.priority)}>{priorityLabel(o.priority)}</Badge>{' '}
        {job.opIds.size > 0 ? <Badge tone="warn">{t('jobs.waiting')}</Badge> : null}
      </p>
      <p className="muted"><bdi>{o.assetName ?? ''}</bdi> <bdi>{o.locationName ?? ''}</bdi></p>
      {o.sourceKind ? <p className="muted">{t('jobs.raisedBy', { source: t(workSourceKey(o.sourceKind)) })}</p> : null}
      {o.description ? <p className="preline"><bdi>{o.description}</bdi></p> : null}
      {o.statusReason && o.status === 'OnHold' ? <p className="banner">{t('work.reason', { reason: o.statusReason })}</p> : null}
      {o.sla.resolutionDueAt && o.sla.state !== 'None' ? <p className="muted">{t('work.resolveBy')}: {date(o.sla.resolutionDueAt, true)}</p> : null}

      <div className="actions big" role="group" aria-label={t('work.actions')}>
        {o.allowed.includes('Started') ? <button type="button" className="primary" onClick={() => void enqueue('start', o.id, {})}>{t('workAction.Started')}</button> : null}
        {o.allowed.includes('Resumed') ? <button type="button" className="primary" onClick={() => void enqueue('resume', o.id, {})}>{t('workAction.Resumed')}</button> : null}
        {o.allowed.includes('Held') ? <button type="button" onClick={() => setDialog('hold')}>{t('workAction.Held')}</button> : null}
        {o.allowed.includes('Completed') ? <button type="button" className="primary" onClick={() => setDialog('complete')}>{t('workAction.Completed')}</button> : null}
      </div>
      {o.allowed.includes('Completed') && blocker ? <p className="banner" role="note">{t(blocker === 'checklist' ? 'jobs.blockedChecklist' : 'jobs.blockedSignature')}</p> : null}

      <Permits job={job} fetchedAt={snapshot?.fetchedAt ?? 0} />
      {online ? <SummaryPanel workOrderId={o.id} /> : null}
      <Checklist job={job} waiting={waiting} editable={o.status === 'InProgress' || o.status === 'OnHold'} />
      <Effort job={job} waiting={waiting} open={['InProgress', 'OnHold', 'Completed'].includes(o.status)} />
      <Photos job={job} ops={ops} waiting={waiting} open={!['Closed', 'Cancelled'].includes(o.status)} />
      <Signatures job={job} ops={ops} waiting={waiting} open={['InProgress', 'OnHold', 'Completed'].includes(o.status)} />
      <Comments job={job} waiting={waiting} />

      {dialog === 'hold' ? (
        <ReasonDialog title={t('workAction.Held')} label={t('common.reason')} confirmLabel={t('workAction.Held')}
          onConfirm={(reason) => { void enqueue('hold', o.id, { reason }); setDialog(null); }} onCancel={() => setDialog(null)} />
      ) : null}
      {dialog === 'complete' ? (
        <CompleteDialog blocker={blocker} onCancel={() => setDialog(null)} onDone={(code, notes) => { void enqueue('complete', o.id, { code, notes }); setDialog(null); }} />
      ) : null}
    </section>
  );
}

function CompleteDialog({ blocker, onCancel, onDone }: { blocker: 'checklist' | 'signature' | null; onCancel: () => void; onDone: (code: ClosureCode, notes: string) => void }) {
  const { t } = useTranslation();
  const label = useEnumLabel('closureCode');
  const [code, setCode] = useState<ClosureCode>('Fixed');
  const [notes, setNotes] = useState('');
  return (
    <div className="overlay" onKeyDown={(e) => e.key === 'Escape' && onCancel()}>
      <div className="dialog" role="dialog" aria-modal="true" aria-label={t('workAction.Completed')}>
        <h2>{t('workAction.Completed')}</h2>
        {blocker ? <p className="banner error" role="alert">{t(blocker === 'checklist' ? 'jobs.blockedChecklist' : 'jobs.blockedSignature')}</p> : null}
        <form onSubmit={(e) => { e.preventDefault(); if (!blocker) onDone(code, notes.trim()); }}>
          <Field label={t('work.outcome')} required>
            <select value={code} onChange={(e) => setCode(e.target.value as ClosureCode)} autoFocus>{CLOSURE_CODES.map((c) => <option key={c} value={c}>{label(c)}</option>)}</select>
          </Field>
          <Field label={t('work.whatWasDone')} required><textarea value={notes} onChange={(e) => setNotes(e.target.value)} rows={4} maxLength={2000} /></Field>
          <div className="actions">
            <button type="button" onClick={onCancel}>{t('common.cancel')}</button>
            <button type="submit" className="primary" disabled={!!blocker || !notes.trim()}>{t('common.confirm')}</button>
          </div>
        </form>
      </div>
    </div>
  );
}

type View = OfflineJob & Pending;

/**
 * Permits tied to the job, as of the last download. They are shown, never changed, from here: a permit's lifecycle (approve, issue, start, close) is a safety record
 * that needs the person and the people who sign it to see the current state, so it is not queued. The status can be out of date; the page says when it was fetched.
 */
function Permits({ job, fetchedAt }: { job: View; fetchedAt: number }) {
  const { t } = useTranslation();
  const { date } = usePrefs();
  const { online } = useOffline();
  const { can } = usePermissions();
  const statusLabel = useEnumLabel('permitStatus');
  if (!job.permits || job.permits.length === 0) return null;
  const notStarted = job.permits.some((p) => p.status !== 'Started');
  return (
    <>
      <h2>{t('jobs.permits')}</h2>
      {job.order.allowed.includes('Started') && notStarted ? <p className="banner" role="note">{t('jobs.permitNotStarted')}</p> : null}
      <ul className="plain" aria-label={t('jobs.permits')}>
        {job.permits.map((p) => (
          <li key={p.id}>
            <strong>PTW-{String(p.number).padStart(4, '0')}</strong> · <bdi>{p.typeName}</bdi> <Badge tone={p.status === 'Started' ? 'ok' : 'warn'}>{statusLabel(p.status)}</Badge>
            <div className="muted"><bdi>{p.locationName}</bdi> · {date(p.plannedStart, true)} – {date(p.plannedEnd, true)}</div>
            {online && can('permits.read') ? <Link to={`/compliance?tab=permits&permit=${p.id}`}>{t('jobs.openPermit', { number: String(p.number).padStart(4, '0') })}</Link> : null}
          </li>
        ))}
      </ul>
      <p className="muted">{online ? t('jobs.permitsAsOf', { when: date(new Date(fetchedAt).toISOString(), true) }) : t('jobs.permitsOffline', { when: date(new Date(fetchedAt).toISOString(), true) })}</p>
    </>
  );
}

function Waiting({ on }: { on: boolean }) {
  const { t } = useTranslation();
  return on ? <Badge tone="warn">{t('jobs.waiting')}</Badge> : null;
}

function Checklist({ job, waiting, editable }: { job: View; waiting: (id: string) => boolean; editable: boolean }) {
  const { t } = useTranslation();
  const { enqueue, ops } = useOffline();
  const label = useEnumLabel('checklistResult');
  // An answer waiting to be sent is marked per line: find the queued checklist action for it.
  const queued = (itemId: string) => ops.some((o) => o.kind === 'checklist' && o.state === 'pending' && (o.payload as { itemId: string }).itemId === itemId && job.opIds.has(o.id));
  void waiting;
  const [notes, setNotes] = useState<Record<string, string>>({});
  if (job.checklist.length === 0) return null;
  return (
    <>
      <h2>{t('evidence.checklist')}</h2>
      <ul className="checklist" aria-label={t('evidence.checklist')}>
        {job.checklist.map((i) => (
          <li key={i.id}>
            <strong><bdi>{i.text}</bdi></strong> {i.required ? <Badge tone="neutral">{t('evidence.required')}</Badge> : null} <Waiting on={queued(i.id)} />
            {editable ? (
              <div className="row">
                <Field label={`${t('evidence.result')}: ${i.text}`}>
                  <select value={i.result} onChange={(e) => {
                    const result = e.target.value as ChecklistResult;
                    const note = (notes[i.id] ?? i.note ?? '').trim() || null;
                    if (result === 'Failed' && !note) return;
                    void enqueue('checklist', job.order.id, { itemId: i.id, result, note });
                  }}>
                    {(['Pending', 'Done', 'Failed', 'NotApplicable'] as ChecklistResult[]).map((r) => <option key={r} value={r}>{label(r)}</option>)}
                  </select>
                </Field>
                <Field label={t('evidence.note')} hint={t('jobs.failedNeedsNote')}>
                  <input value={notes[i.id] ?? i.note ?? ''} onChange={(e) => setNotes({ ...notes, [i.id]: e.target.value })} maxLength={500} />
                </Field>
              </div>
            ) : <span className="muted"> {label(i.result)}{i.note ? ` — ${i.note}` : ''}</span>}
          </li>
        ))}
      </ul>
    </>
  );
}

function Effort({ job, waiting, open }: { job: View; waiting: (id: string) => boolean; open: boolean }) {
  const { t } = useTranslation();
  const { enqueue } = useOffline();
  const { date } = usePrefs();
  const kindLabel = useEnumLabel('costKind');
  const today = new Date().toISOString().slice(0, 10);
  const [workedOn, setWorkedOn] = useState(today);
  const [minutes, setMinutes] = useState('');
  const [kind, setKind] = useState<CostKind>('Material');
  const [description, setDescription] = useState('');
  const [quantity, setQuantity] = useState('1');
  const [unitCost, setUnitCost] = useState('');
  const totalMinutes = job.time.filter((e) => !e.isVoided).reduce((n, e) => n + e.minutes, 0);

  return (
    <>
      <h2>{t('jobs.timeAndParts')}</h2>
      <p className="muted">{t('jobs.timeTotal', { minutes: totalMinutes })}</p>
      <ul className="plain">
        {job.time.filter((e) => !e.isVoided).map((e) => <li key={e.id}>{date(e.workedOn)} · {t('jobs.minutesShort', { count: e.minutes })} <bdi>{e.note ?? ''}</bdi> <Waiting on={waiting(e.id)} /></li>)}
        {job.costs.filter((c) => !c.isVoided).map((c) => <li key={c.id}>{kindLabel(c.kind)} · <bdi>{c.description}</bdi> · {c.quantity} × {c.unitCost} = {c.total} {job.currencyCode ?? ''} <Waiting on={waiting(c.id)} /></li>)}
      </ul>
      {open ? (
        <>
          <form onSubmit={(e) => { e.preventDefault(); void enqueue('time', job.order.id, { workedOn, minutes: Number(minutes), note: null }); setMinutes(''); }} aria-label={t('evidence.addTime')}>
            <div className="row">
              <Field label={t('evidence.workedOn')}><input type="date" value={workedOn} onChange={(e) => setWorkedOn(e.target.value)} /></Field>
              <Field label={t('evidence.minutes')}><input type="number" min={1} max={1440} value={minutes} onChange={(e) => setMinutes(e.target.value)} /></Field>
            </div>
            <button type="submit" disabled={!minutes || Number(minutes) < 1}>{t('evidence.addTime')}</button>
          </form>
          <form onSubmit={(e) => { e.preventDefault(); void enqueue('cost', job.order.id, { kind, description: description.trim(), reference: null, quantity: Number(quantity), unitCost: Number(unitCost) }); setDescription(''); setUnitCost(''); setQuantity('1'); }} aria-label={t('evidence.addCost')}>
            <div className="row">
              <Field label={t('evidence.kind')}><select value={kind} onChange={(e) => setKind(e.target.value as CostKind)}>{(['Material', 'Contractor', 'Other'] as CostKind[]).map((k) => <option key={k} value={k}>{kindLabel(k)}</option>)}</select></Field>
              <Field label={t('evidence.description')}><input value={description} onChange={(e) => setDescription(e.target.value)} maxLength={300} /></Field>
            </div>
            <div className="row">
              <Field label={t('evidence.quantity')}><input type="number" min={0} step="any" value={quantity} onChange={(e) => setQuantity(e.target.value)} /></Field>
              <Field label={t('evidence.unitCost')}><input type="number" min={0} step="any" value={unitCost} onChange={(e) => setUnitCost(e.target.value)} /></Field>
            </div>
            <button type="submit" disabled={!description.trim() || unitCost === '' || Number(quantity) <= 0}>{t('evidence.addCost')}</button>
          </form>
        </>
      ) : null}
    </>
  );
}

function PendingPhoto({ opId, alt }: { opId: string; alt: string }) {
  const { blobFor } = useOffline();
  const [url, setUrl] = useState<string | null>(null);
  useEffect(() => {
    let made: string | null = null;
    let cancelled = false;
    void blobFor(opId).then((b) => {
      if (cancelled || !b) return;
      made = URL.createObjectURL(b);
      setUrl(made);
    });
    return () => {
      cancelled = true;
      if (made) URL.revokeObjectURL(made);
    };
  }, [blobFor, opId]);
  return url ? <img src={url} alt={alt} className="thumb" /> : <span className="thumb placeholder" aria-hidden="true" />;
}

function Photos({ job, waiting, open }: { job: View; ops: Op[]; waiting: (id: string) => boolean; open: boolean }) {
  const { t } = useTranslation();
  const { enqueue, online } = useOffline();
  const kindLabel = useEnumLabel('attachmentKind');
  const [kind, setKind] = useState<AttachmentKind>('After');
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  const add = async (file: File | undefined) => {
    if (!file) return;
    setError(null);
    setBusy(true);
    try {
      const prepared = await preparePhoto(file);
      if (prepared.blob.size > 15 * 1024 * 1024) throw new Error(t('errors.tooLarge'));
      await enqueue('photo', job.order.id, { kind, description: null, fileName: prepared.fileName, contentType: prepared.contentType, size: prepared.blob.size }, prepared.blob);
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <>
      <h2>{t('evidence.files')}</h2>
      <ul className="files" aria-label={t('evidence.files')}>
        {job.attachments.map((a) => (
          <li key={a.id}>
            {waiting(a.id) ? <PendingPhoto opId={a.id} alt={a.fileName} /> : a.contentType.startsWith('image/') && online ? <AuthImage path={`/work-orders/attachments/${a.id}/download`} alt={a.fileName} /> : <span className="thumb placeholder" aria-hidden="true" />}
            <div><strong><bdi>{a.fileName}</bdi></strong> <Badge tone="neutral">{kindLabel(a.kind)}</Badge> <Waiting on={waiting(a.id)} /></div>
          </li>
        ))}
      </ul>
      {open ? (
        <div className="row">
          <Field label={t('evidence.kind')}><select value={kind} onChange={(e) => setKind(e.target.value as AttachmentKind)}>{(['Before', 'After', 'Other'] as AttachmentKind[]).map((k) => <option key={k} value={k}>{kindLabel(k)}</option>)}</select></Field>
          <Field label={t('jobs.takePhoto')}><input type="file" accept="image/*" capture="environment" disabled={busy} onChange={(e) => { void add(e.target.files?.[0]); e.target.value = ''; }} /></Field>
        </div>
      ) : null}
      <ErrorBanner error={error} />
    </>
  );
}

function Signatures({ job, ops, waiting, open }: { job: View; ops: Op[]; waiting: (id: string) => boolean; open: boolean }) {
  const { t } = useTranslation();
  const { enqueue, online } = useOffline();
  const roleLabel = useEnumLabel('signatureRole');
  const [role, setRole] = useState<SignatureRole>('Technician');
  const [name, setName] = useState('');
  const [image, setImage] = useState<string | null>(null);
  const [padKey, setPadKey] = useState(0);
  const localImage = (id: string) => (ops.find((o) => o.id === id)?.payload as { imageBase64?: string } | undefined)?.imageBase64;

  return (
    <>
      <h2>{t('evidence.signatures')}</h2>
      <ul className="files" aria-label={t('evidence.signatures')}>
        {job.signatures.map((s) => (
          <li key={s.id}>
            {waiting(s.id) && localImage(s.id) ? <img className="thumb" alt={t('evidence.signatureOf', { name: s.signerName })} src={localImage(s.id)!.startsWith('data:') ? localImage(s.id)! : `data:image/png;base64,${localImage(s.id)}`} />
              : online ? <AuthImage path={`/work-orders/signatures/${s.id}/image`} alt={t('evidence.signatureOf', { name: s.signerName })} /> : <span className="thumb placeholder" aria-hidden="true" />}
            <div><strong><bdi>{s.signerName}</bdi></strong> <Badge tone="neutral">{roleLabel(s.role)}</Badge> <Waiting on={waiting(s.id)} /></div>
          </li>
        ))}
      </ul>
      {open ? (
        <form onSubmit={(e) => { e.preventDefault(); if (image) { void enqueue('signature', job.order.id, { role, signerName: name.trim(), imageBase64: image }); setName(''); setImage(null); setPadKey((k) => k + 1); } }} aria-label={t('evidence.addSignature')}>
          <div className="row">
            <Field label={t('evidence.signingAs')}><select value={role} onChange={(e) => setRole(e.target.value as SignatureRole)}>{(['Technician', 'Customer'] as SignatureRole[]).map((r) => <option key={r} value={r}>{roleLabel(r)}</option>)}</select></Field>
            <Field label={t('evidence.printedName')} required><input value={name} onChange={(e) => setName(e.target.value)} maxLength={200} /></Field>
          </div>
          <SignaturePad key={padKey} onChange={setImage} />
          <button type="submit" className="primary" disabled={!image || !name.trim()}>{t('evidence.sign')}</button>
        </form>
      ) : null}
    </>
  );
}

function Comments({ job, waiting }: { job: View; waiting: (id: string) => boolean }) {
  const { t } = useTranslation();
  const { enqueue } = useOffline();
  const { date } = usePrefs();
  const [body, setBody] = useState('');
  return (
    <>
      <h2>{t('work.comments')}</h2>
      <ul className="comments">
        {job.comments.map((c) => (
          <li key={c.id}>
            <span className="muted"><bdi>{c.authorName ?? '—'}</bdi> · {date(c.at, true)} {c.isInternal ? <Badge tone="warn">{t('work.internal')}</Badge> : null} <Waiting on={waiting(c.id)} /></span>
            <div className="preline"><bdi>{c.body}</bdi></div>
          </li>
        ))}
      </ul>
      <form onSubmit={(e) => { e.preventDefault(); void enqueue('comment', job.order.id, { body: body.trim(), isInternal: false }); setBody(''); }}>
        <Field label={t('work.addComment')}><textarea value={body} onChange={(e) => setBody(e.target.value)} rows={2} maxLength={2000} /></Field>
        <button type="submit" disabled={!body.trim()}>{t('work.post')}</button>
      </form>
    </>
  );
}
