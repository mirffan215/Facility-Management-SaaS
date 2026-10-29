import type {
  AttachmentKind, ChecklistItem, ChecklistResult, ClosureCode, CostKind, CostLine, SignatureRole, TimeEntry, WorkAttachment, WorkOrder, WorkOrderAction,
  WorkOrderComment, WorkOrderEvent, WorkPermitRef, WorkSignature,
} from '../api/types';

/** One job as the phone keeps it: what the server last said. */
export interface OfflineJob {
  order: WorkOrder;
  events: WorkOrderEvent[];
  comments: WorkOrderComment[];
  checklist: ChecklistItem[];
  signatures: WorkSignature[];
  attachments: WorkAttachment[];
  time: TimeEntry[];
  costs: CostLine[];
  currencyCode: string | null;
  /** Permits tied to the job as of the last download. Read-only here: a permit is never changed from the queue. Absent when the person may not read permits. */
  permits?: WorkPermitRef[] | null;
}

export interface Snapshot {
  userKey: string;
  jobs: OfflineJob[];
  /** The server's clock at download, and the device's: the difference corrects times the phone reports later. */
  serverTime: string;
  fetchedAt: number;
  clockOffsetMs: number;
  requireCompletionSignature: boolean;
}

export type OpKind = 'start' | 'hold' | 'resume' | 'complete' | 'checklist' | 'time' | 'cost' | 'comment' | 'signature' | 'photo';

export interface OpPayloads {
  start: Record<string, never>;
  hold: { reason: string };
  resume: Record<string, never>;
  complete: { code: ClosureCode; notes: string };
  checklist: { itemId: string; result: ChecklistResult; note: string | null };
  time: { workedOn: string; minutes: number; note: string | null };
  cost: { kind: CostKind; description: string; reference: string | null; quantity: number; unitCost: number };
  comment: { body: string; isInternal: boolean };
  signature: { role: SignatureRole; signerName: string; imageBase64: string };
  photo: { kind: AttachmentKind; description: string | null; fileName: string; contentType: string; size: number };
}

/** Something the person did that has not reached the server yet. Its id doubles as the idempotency key, so a retry never does it twice. */
export interface Op<K extends OpKind = OpKind> {
  id: string;
  seq: number;
  userKey: string;
  workOrderId: string;
  kind: K;
  payload: OpPayloads[K];
  /** When it was done, by the phone's clock corrected to the server's. */
  performedAt: string;
  createdAt: number;
  state: 'pending' | 'failed';
  attempts: number;
  error?: string;
}

export const LIFECYCLE: OpKind[] = ['start', 'hold', 'resume', 'complete'];

/** The field actions the phone offers for each status. The server still decides when the queue is sent. */
export function fieldActions(status: WorkOrder['status']): WorkOrderAction[] {
  switch (status) {
    case 'Assigned': return ['Started'];
    case 'InProgress': return ['Held', 'Completed'];
    case 'OnHold': return ['Resumed'];
    default: return [];
  }
}

const money = (n: number) => Math.round((n + Number.EPSILON) * 100) / 100;

export interface Pending {
  /** Ids of items that exist only on this phone so far. */
  opIds: Set<string>;
}

/**
 * What the person should see now: the server's last word with their unsent actions laid on top, in the order they did them. Pure: the same
 * snapshot and queue always give the same view, and a failed action (which did not happen) is not laid on.
 */
export function applyOps(job: OfflineJob, ops: Op[], person: string, photoUrl: (op: Op) => string | null = () => null): OfflineJob & Pending {
  const view: OfflineJob & Pending = {
    ...job,
    order: { ...job.order },
    comments: [...job.comments],
    checklist: job.checklist.map((i) => ({ ...i })),
    signatures: [...job.signatures],
    attachments: [...job.attachments],
    time: [...job.time],
    costs: [...job.costs],
    opIds: new Set(),
  };

  for (const op of ops) {
    if (op.workOrderId !== job.order.id || op.state === 'failed') continue;
    view.opIds.add(op.id);
    switch (op.kind) {
      case 'start': view.order.status = 'InProgress'; view.order.startedAt ??= op.performedAt; break;
      case 'resume': view.order.status = 'InProgress'; view.order.statusReason = null; break;
      case 'hold': view.order.status = 'OnHold'; view.order.statusReason = (op.payload as OpPayloads['hold']).reason; break;
      case 'complete': {
        const p = op.payload as OpPayloads['complete'];
        view.order.status = 'Completed';
        view.order.completedAt = op.performedAt;
        view.order.closureCode = p.code;
        view.order.closureNotes = p.notes;
        break;
      }
      case 'checklist': {
        const p = op.payload as OpPayloads['checklist'];
        const item = view.checklist.find((i) => i.id === p.itemId);
        if (item) {
          item.result = p.result;
          item.note = p.note;
          item.resultAt = p.result === 'Pending' ? null : op.performedAt;
          item.resultByName = person;
        }
        break;
      }
      case 'time': {
        const p = op.payload as OpPayloads['time'];
        view.time.push({ id: op.id, workerId: '', workerName: person, workedOn: p.workedOn, minutes: p.minutes, hourlyRate: 0, labourCost: 0, note: p.note, recordedAt: op.performedAt, isVoided: false, voidReason: null });
        break;
      }
      case 'cost': {
        const p = op.payload as OpPayloads['cost'];
        view.costs.push({ id: op.id, kind: p.kind, description: p.description, reference: p.reference, quantity: p.quantity, unitCost: p.unitCost, total: money(p.quantity * p.unitCost), recordedByName: person, recordedAt: op.performedAt, isVoided: false, voidReason: null });
        break;
      }
      case 'comment': {
        const p = op.payload as OpPayloads['comment'];
        view.comments.push({ id: op.id, authorId: '', authorName: person, at: op.performedAt, body: p.body, isInternal: p.isInternal });
        break;
      }
      case 'signature': {
        const p = op.payload as OpPayloads['signature'];
        view.signatures.push({ id: op.id, role: p.role, signerName: p.signerName, signedAt: op.performedAt, recordedByName: person });
        break;
      }
      case 'photo': {
        const p = op.payload as OpPayloads['photo'];
        view.attachments.push({ id: op.id, kind: p.kind, fileName: p.fileName, contentType: p.contentType, sizeBytes: p.size, description: p.description, uploadedAt: op.performedAt, uploadedByName: person, uploadedBy: '' });
        void photoUrl;
        break;
      }
    }
  }

  view.order.allowed = fieldActions(view.order.status);
  return view;
}

/** Why a job cannot be completed on the phone yet, or null. Mirrors the server's rule so the person is told before the queue is sent. */
export function completionBlocker(job: OfflineJob & Pending, requireSignature: boolean): 'checklist' | 'signature' | null {
  if (job.checklist.some((i) => i.required && i.result === 'Pending')) return 'checklist';
  if (requireSignature && !job.signatures.some((s) => s.role === 'Technician')) return 'signature';
  return null;
}
