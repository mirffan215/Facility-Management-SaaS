import type { KeyValueStore } from './store';
import type { Op, OpKind, OpPayloads, Snapshot, OfflineJob } from './model';

/** The only thing the engine needs from the network layer: a fetch that adds the token and does not throw for statuses. */
export interface EngineApi {
  raw(path: string, init?: RequestInit): Promise<Response>;
}

export interface EngineState {
  pending: number;
  failed: number;
  syncing: boolean;
  /** The server refused the token: the queue is kept, and it is sent after the person signs in again. */
  authRequired: boolean;
  lastSyncAt: number | null;
  lastError: string | null;
}

export interface SyncResult {
  sent: number;
  failed: number;
  stoppedBecause: 'network' | 'auth' | 'server' | 'busy' | null;
}

interface BundleResponse {
  serverTime: string;
  jobs: OfflineJob[];
  requireCompletionSignature?: boolean;
}

const pad = (n: number) => n.toString().padStart(12, '0');

/**
 * The phone's side of the sync contract (ADR-0013). Actions are queued on the device, each with an id that is also its idempotency key, and
 * sent in the order they were done. A failure that retrying cannot fix (the job was reassigned, a rule refused it) is set aside for the person
 * to see; the rest of the queue carries on. A failure that is about the connection or the session stops the run and keeps everything.
 */
export class OfflineEngine {
  state: EngineState = { pending: 0, failed: 0, syncing: false, authRequired: false, lastSyncAt: null, lastError: null };

  private readonly listeners = new Set<(s: EngineState) => void>();
  private running: Promise<SyncResult> | null = null;
  private readonly now: () => number;
  private readonly newId: () => string;
  private offsetMs = 0;

  constructor(private readonly o: { userKey: string; store: KeyValueStore; api: EngineApi; now?: () => number; newId?: () => string }) {
    this.now = o.now ?? (() => Date.now());
    this.newId = o.newId ?? (() => crypto.randomUUID());
  }

  subscribe(listener: (s: EngineState) => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  private set(patch: Partial<EngineState>) {
    this.state = { ...this.state, ...patch };
    for (const l of this.listeners) l(this.state);
  }

  /** Reads what is stored: call once at start. */
  async load(): Promise<void> {
    const snapshot = await this.snapshot();
    if (snapshot) this.offsetMs = snapshot.clockOffsetMs;
    await this.recount();
  }

  async snapshot(): Promise<Snapshot | undefined> {
    return this.o.store.get<Snapshot>(`snap:${this.o.userKey}`);
  }

  async ops(): Promise<Op[]> {
    const keys = await this.o.store.keys(`op:${this.o.userKey}:`);
    const ops = await Promise.all(keys.map((k) => this.o.store.get<Op>(k)));
    return ops.filter((x): x is Op => x !== undefined);
  }

  async blob(opId: string): Promise<Blob | undefined> {
    return this.o.store.get<Blob>(`blob:${opId}`);
  }

  /** The time to report for something done now: the phone's clock corrected by how far it was from the server's at the last download. */
  correctedNow(): Date {
    return new Date(this.now() + this.offsetMs);
  }

  async enqueue<K extends OpKind>(kind: K, workOrderId: string, payload: OpPayloads[K], blob?: Blob): Promise<Op<K>> {
    const seqKey = `seq:${this.o.userKey}`;
    const seq = ((await this.o.store.get<number>(seqKey)) ?? 0) + 1;
    await this.o.store.put(seqKey, seq);
    const op: Op<K> = {
      id: `op-${this.newId()}`, seq, userKey: this.o.userKey, workOrderId, kind, payload,
      performedAt: this.correctedNow().toISOString(), createdAt: this.now(), state: 'pending', attempts: 0,
    };
    if (blob) await this.o.store.put(`blob:${op.id}`, blob);
    await this.o.store.put(`op:${this.o.userKey}:${pad(seq)}`, op);
    await this.recount();
    return op;
  }

  async retry(opId: string): Promise<void> {
    const op = (await this.ops()).find((x) => x.id === opId);
    if (!op) return;
    await this.o.store.put(this.opKey(op), { ...op, state: 'pending', error: undefined });
    await this.recount();
  }

  async discard(opId: string): Promise<void> {
    const op = (await this.ops()).find((x) => x.id === opId);
    if (!op) return;
    await this.remove(op);
    await this.recount();
  }

  /** Forgets everything on this device for this person (sign-out). */
  async clear(): Promise<void> {
    for (const op of await this.ops()) await this.remove(op);
    await this.o.store.delete(`snap:${this.o.userKey}`);
    await this.o.store.delete(`seq:${this.o.userKey}`);
    this.offsetMs = 0;
    await this.recount();
  }

  /** Sends what is queued, then downloads the jobs again. One run at a time; a second call waits for the first. */
  sync(): Promise<SyncResult> {
    if (this.running) return this.running;
    this.set({ syncing: true });
    this.running = this.runSync().finally(() => {
      this.running = null;
      this.set({ syncing: false });
    });
    return this.running;
  }

  private async runSync(): Promise<SyncResult> {
    const result = await this.flush();
    if (result.stoppedBecause === null) {
      try {
        await this.refresh();
        this.set({ lastSyncAt: this.now(), lastError: null });
      } catch (e) {
        result.stoppedBecause = e instanceof AuthError ? 'auth' : 'network';
        this.set({ authRequired: e instanceof AuthError });
      }
    }

    return result;
  }

  async flush(): Promise<SyncResult> {
    const result: SyncResult = { sent: 0, failed: 0, stoppedBecause: null };
    for (const op of (await this.ops()).filter((x) => x.state === 'pending')) {
      let response: Response;
      try {
        response = await this.send(op);
      } catch (e) {
        if (e instanceof MissingBlobError) {
          await this.o.store.put(this.opKey(op), { ...op, state: 'failed', error: 'The photo is no longer on this device.' });
          result.failed++;
          continue;
        }

        result.stoppedBecause = 'network';
        this.set({ lastError: 'network' });
        break;
      }

      if (response.ok) {
        await this.remove(op);
        result.sent++;
        this.set({ authRequired: false });
      } else if (response.status === 401 || response.headers.get('X-No-Token')) {
        result.stoppedBecause = 'auth';
        this.set({ authRequired: true });
        break;
      } else if ((response.status === 409 && response.headers.has('Retry-After')) || response.status >= 500 || response.status === 408 || response.status === 429) {
        await this.o.store.put(this.opKey(op), { ...op, attempts: op.attempts + 1 });
        result.stoppedBecause = response.status >= 500 ? 'server' : 'busy';
        break;
      } else {
        await this.o.store.put(this.opKey(op), { ...op, state: 'failed', attempts: op.attempts + 1, error: await describe(response) });
        result.failed++;
      }
    }

    await this.recount();
    return result;
  }

  /** Downloads the person's open jobs and keeps them. */
  async refresh(): Promise<void> {
    const response = await this.o.api.raw('/work-orders/offline/jobs');
    if (response.status === 401) throw new AuthError();
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const bundle = (await response.json()) as BundleResponse;
    const fetchedAt = this.now();
    this.offsetMs = Date.parse(bundle.serverTime) - fetchedAt;
    const snapshot: Snapshot = {
      userKey: this.o.userKey, jobs: bundle.jobs, serverTime: bundle.serverTime, fetchedAt, clockOffsetMs: this.offsetMs,
      requireCompletionSignature: bundle.requireCompletionSignature ?? false,
    };
    await this.o.store.put(`snap:${this.o.userKey}`, snapshot);
  }

  private opKey(op: Op) {
    return `op:${this.o.userKey}:${pad(op.seq)}`;
  }

  private async remove(op: Op) {
    await this.o.store.delete(this.opKey(op));
    await this.o.store.delete(`blob:${op.id}`);
  }

  private async recount() {
    const ops = await this.ops();
    this.set({ pending: ops.filter((x) => x.state === 'pending').length, failed: ops.filter((x) => x.state === 'failed').length });
  }

  private async send(op: Op): Promise<Response> {
    const base = `/work-orders/${op.workOrderId}`;
    const headers: Record<string, string> = { 'Idempotency-Key': op.id };
    const json = (path: string, body: unknown, extra: Record<string, string> = {}) =>
      this.o.api.raw(path, { method: 'POST', headers: { ...headers, ...extra, 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    const when = { 'X-Performed-At': op.performedAt };

    switch (op.kind) {
      case 'start': return json(`${base}/start`, {}, when);
      case 'resume': return json(`${base}/resume`, {}, when);
      case 'hold': return json(`${base}/hold`, { reason: (op.payload as OpPayloads['hold']).reason }, when);
      case 'complete': return json(`${base}/complete`, op.payload, when);
      case 'checklist': {
        const p = op.payload as OpPayloads['checklist'];
        return json(`${base}/checklist/${p.itemId}`, { result: p.result, note: p.note });
      }
      case 'time': return json(`${base}/time`, op.payload);
      case 'cost': return json(`${base}/costs`, op.payload);
      case 'comment': return json(`${base}/comments`, op.payload);
      case 'signature': return json(`${base}/signatures`, op.payload);
      case 'photo': {
        const p = op.payload as OpPayloads['photo'];
        const blob = await this.o.store.get<Blob>(`blob:${op.id}`);
        if (!blob) throw new MissingBlobError();
        const form = new FormData();
        form.append('file', blob, p.fileName);
        form.append('kind', p.kind);
        if (p.description) form.append('description', p.description);
        return this.o.api.raw(`${base}/attachments`, { method: 'POST', headers, body: form });
      }
    }
  }
}

class AuthError extends Error {}
class MissingBlobError extends Error {}

async function describe(response: Response): Promise<string> {
  try {
    const body = (await response.json()) as { title?: string; errors?: Record<string, string[]> };
    const first = body.errors ? Object.values(body.errors).flat()[0] : undefined;
    return first ?? body.title ?? `HTTP ${response.status}`;
  } catch {
    return `HTTP ${response.status}`;
  }
}
