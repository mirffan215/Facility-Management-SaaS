import { beforeEach, describe, expect, it } from 'vitest';
import type { WorkOrder } from '../api/types';
import { OfflineEngine, type EngineApi } from './engine';
import { applyOps, completionBlocker, fieldActions, type OfflineJob, type Op } from './model';
import { MemoryStore } from './store';

const order = (patch: Partial<WorkOrder> = {}): WorkOrder => ({
  id: 'wo1', number: 1, displayNumber: 'WO-000001', title: 'Pump', description: null, type: 'Corrective', priority: 'High', status: 'Assigned',
  locationId: 'l', locationName: 'HQ', assetId: null, assetName: null, categoryId: null, categoryName: null, requestedBy: 'r', requestedByName: 'Rami',
  requestedAt: '2026-11-01T08:00:00Z', assignedTo: 't', assignedToName: 'Tina', dueAt: null, isOverdue: false, startedAt: null, completedAt: null, verifiedAt: null,
  closedAt: null, statusReason: null, closureCode: null, closureNotes: null, allowed: ['Started', 'Edited'], rowVersion: 'AA==',
  sla: { state: 'None', responseDueAt: null, resolutionDueAt: null, respondedAt: null, responseBreached: false, paused: false }, ...patch,
});

const job = (patch: Partial<OfflineJob> = {}): OfflineJob => ({
  order: order(), events: [], comments: [], currencyCode: 'SAR', signatures: [], attachments: [], time: [], costs: [],
  checklist: [
    { id: 'c1', position: 1, text: 'Isolate', required: true, result: 'Pending', note: null, resultByName: null, resultAt: null },
    { id: 'c2', position: 2, text: 'Photo', required: false, result: 'Pending', note: null, resultByName: null, resultAt: null },
  ], ...patch,
});

let seq = 0;
const op = <K extends Op['kind']>(kind: K, payload: Op['payload'], patch: Partial<Op> = {}): Op => ({
  id: `op-${++seq}`, seq, userKey: 'u', workOrderId: 'wo1', kind, payload, performedAt: '2026-11-01T09:00:00Z', createdAt: 0, state: 'pending', attempts: 0, ...patch,
});

describe('applyOps', () => {
  it('shows the server state unchanged when nothing is waiting, with the field actions for the status', () => {
    const view = applyOps(job(), [], 'Tina');
    expect(view.order.status).toBe('Assigned');
    expect(view.order.allowed).toEqual(['Started']);
    expect(view.opIds.size).toBe(0);
  });

  it('lays unsent actions on top, in order, without touching the stored job', () => {
    const base = job();
    const ops = [
      op('start', {}),
      op('checklist', { itemId: 'c1', result: 'Done', note: null }),
      op('time', { workedOn: '2026-11-01', minutes: 30, note: 'bolts' }),
      op('cost', { kind: 'Material', description: 'Belt', reference: null, quantity: 2, unitCost: 10.555 }),
      op('comment', { body: 'on site', isInternal: false }),
      op('hold', { reason: 'parts' }),
    ];
    const view = applyOps(base, ops, 'Tina');

    expect(view.order.status).toBe('OnHold');
    expect(view.order.statusReason).toBe('parts');
    expect(view.order.allowed).toEqual(['Resumed']);
    expect(view.checklist[0]).toMatchObject({ result: 'Done', resultByName: 'Tina', resultAt: '2026-11-01T09:00:00Z' });
    expect(view.time.at(-1)).toMatchObject({ minutes: 30, note: 'bolts' });
    expect(view.costs.at(-1)?.total).toBe(21.11);
    expect(view.comments.at(-1)?.body).toBe('on site');
    expect(view.opIds.size).toBe(6);
    expect(base.order.status).toBe('Assigned'); // the stored job is untouched
    expect(base.checklist[0].result).toBe('Pending');
  });

  it('completing records the outcome and removes every field action', () => {
    const view = applyOps(job({ order: order({ status: 'InProgress' }) }), [op('complete', { code: 'Fixed', notes: 'done' })], 'Tina');
    expect(view.order).toMatchObject({ status: 'Completed', closureCode: 'Fixed', closureNotes: 'done' });
    expect(view.order.allowed).toEqual([]);
  });

  it('ignores actions that failed (they did not happen) and actions for other jobs', () => {
    const view = applyOps(job(), [op('start', {}, { state: 'failed' }), op('start', {}, { workOrderId: 'other' })], 'Tina');
    expect(view.order.status).toBe('Assigned');
    expect(view.opIds.size).toBe(0);
  });

  it('knows which field actions each status offers', () => {
    expect(fieldActions('Approved')).toEqual([]);
    expect(fieldActions('InProgress')).toEqual(['Held', 'Completed']);
    expect(fieldActions('Closed')).toEqual([]);
  });

  it('blocks completion on the phone for the same reasons the server would', () => {
    const view = applyOps(job({ order: order({ status: 'InProgress' }) }), [], 'Tina');
    expect(completionBlocker(view, false)).toBe('checklist');
    const answered = applyOps(view, [op('checklist', { itemId: 'c1', result: 'Done', note: null })], 'Tina');
    expect(completionBlocker(answered, false)).toBeNull();
    expect(completionBlocker(answered, true)).toBe('signature');
    const signed = applyOps(answered, [op('signature', { role: 'Technician', signerName: 'Tina', imageBase64: 'x' })], 'Tina');
    expect(completionBlocker(signed, true)).toBeNull();
  });
});

// ---------------- engine ----------------

type Call = { path: string; init: RequestInit };

/** A scripted server: each call gets the next scripted response for it, or 200. Records what was sent. */
class FakeApi implements EngineApi {
  calls: Call[] = [];
  script: ((c: Call) => Response | Error | undefined)[] = [];
  bundle = { serverTime: '2026-11-01T09:00:00Z', jobs: [job()], requireCompletionSignature: false };

  async raw(path: string, init: RequestInit = {}): Promise<Response> {
    const call = { path, init };
    this.calls.push(call);
    const scripted = this.script.shift()?.(call);
    if (scripted instanceof Error) throw scripted;
    if (scripted) return scripted;
    if (path === '/work-orders/offline/jobs') return Response.json(this.bundle);
    return Response.json({});
  }
}

const problem = (status: number, title: string, errors?: Record<string, string[]>, headers?: Record<string, string>) =>
  new Response(JSON.stringify({ title, errors }), { status, headers: { 'Content-Type': 'application/json', ...headers } });

describe('OfflineEngine', () => {
  let store: MemoryStore;
  let api: FakeApi;
  let clock: number;
  let n: number;
  const engine = () => new OfflineEngine({ userKey: 'entra:alice', store, api, now: () => clock, newId: () => `id${++n}` });

  beforeEach(() => {
    store = new MemoryStore();
    api = new FakeApi();
    clock = Date.parse('2026-11-01T09:00:00Z');
    n = 0;
  });

  it('queues work on the device and sends it in the order it was done, each with its own idempotency key', async () => {
    const e = engine();
    await e.refresh();
    await e.enqueue('start', 'wo1', {});
    await e.enqueue('checklist', 'wo1', { itemId: 'c1', result: 'Done', note: null });
    await e.enqueue('time', 'wo1', { workedOn: '2026-11-01', minutes: 20, note: null });
    expect(e.state).toMatchObject({ pending: 3, failed: 0 });

    api.calls = [];
    const result = await e.sync();

    expect(result).toEqual({ sent: 3, failed: 0, stoppedBecause: null });
    expect(api.calls.map((c) => c.path)).toEqual(['/work-orders/wo1/start', '/work-orders/wo1/checklist/c1', '/work-orders/wo1/time', '/work-orders/offline/jobs']);
    const keys = api.calls.slice(0, 3).map((c) => new Headers(c.init.headers).get('Idempotency-Key'));
    expect(new Set(keys).size).toBe(3);
    expect(keys.every((k) => k && k.startsWith('op-id'))).toBe(true);
    expect(e.state).toMatchObject({ pending: 0, failed: 0, authRequired: false });
    expect(e.state.lastSyncAt).not.toBeNull();
  });

  it('reports lifecycle actions with the time they were done, corrected for a wrong phone clock', async () => {
    api.bundle.serverTime = '2026-11-01T09:00:00Z';
    const e = engine();
    clock = Date.parse('2026-11-01T09:10:00Z'); // the phone runs ten minutes fast
    await e.refresh();
    clock = Date.parse('2026-11-01T09:25:00Z'); // 25 phone-minutes later: really 09:15
    const queued = await e.enqueue('start', 'wo1', {});
    expect(queued.performedAt).toBe('2026-11-01T09:15:00.000Z');

    api.calls = [];
    await e.flush();
    const sent = api.calls[0];
    expect(new Headers(sent.init.headers).get('X-Performed-At')).toBe('2026-11-01T09:15:00.000Z');
  });

  it('keeps everything when the network drops, and resumes with the same keys', async () => {
    const e = engine();
    await e.enqueue('start', 'wo1', {});
    await e.enqueue('comment', 'wo1', { body: 'hi', isInternal: false });
    api.script = [() => new TypeError('Failed to fetch')];

    const first = await e.sync();
    expect(first).toMatchObject({ sent: 0, stoppedBecause: 'network' });
    expect(e.state).toMatchObject({ pending: 2, syncing: false });
    const firstKey = new Headers(api.calls[0].init.headers).get('Idempotency-Key');

    api.calls = [];
    const second = await e.sync();
    expect(second).toMatchObject({ sent: 2, stoppedBecause: null });
    expect(new Headers(api.calls[0].init.headers).get('Idempotency-Key')).toBe(firstKey); // a retry, not a new request
  });

  it('sets aside an action the server refused for good and carries on with the rest', async () => {
    const e = engine();
    await e.enqueue('complete', 'wo1', { code: 'Fixed', notes: 'x' });
    await e.enqueue('comment', 'wo1', { body: 'after', isInternal: false });
    api.script = [() => problem(400, 'Validation failed', { checklist: ['1 required checklist item(s) are not answered yet.'] })];

    const result = await e.sync();
    expect(result).toMatchObject({ sent: 1, failed: 1, stoppedBecause: null });
    const ops = await e.ops();
    expect(ops).toHaveLength(1);
    expect(ops[0]).toMatchObject({ kind: 'complete', state: 'failed', error: '1 required checklist item(s) are not answered yet.' });
    expect(e.state).toMatchObject({ pending: 0, failed: 1 });

    // The person can fix the cause and retry it, or let it go.
    await e.retry(ops[0].id);
    expect(e.state).toMatchObject({ pending: 1, failed: 0 });
    api.script = [() => problem(409, 'Complete is not possible on a work order that is Cancelled.')];
    await e.sync();
    await e.discard((await e.ops())[0].id);
    expect(await e.ops()).toEqual([]);
  });

  it('treats server errors, rate limits and "still processing" as try-later, not as failures', async () => {
    const e = engine();
    await e.enqueue('start', 'wo1', {});
    for (const [status, because] of [[503, 'server'], [429, 'busy'], [409, 'busy']] as const) {
      api.script = [() => problem(status, 'later', undefined, status === 409 ? { 'Retry-After': '2' } : undefined)];
      const r = await e.sync();
      expect(r.stoppedBecause).toBe(because);
      expect(e.state).toMatchObject({ pending: 1, failed: 0 });
    }

    expect((await e.ops())[0].attempts).toBe(3);
  });

  it('keeps the queue and asks for a sign-in when the session has expired', async () => {
    const e = engine();
    await e.enqueue('start', 'wo1', {});
    api.script = [() => new Response(null, { status: 401 })];

    expect((await e.sync()).stoppedBecause).toBe('auth');
    expect(e.state).toMatchObject({ pending: 1, authRequired: true });

    await e.sync(); // signed in again: sent
    expect(e.state).toMatchObject({ pending: 0, authRequired: false });
  });

  it('sends photos as uploads and survives a lost photo', async () => {
    const e = engine();
    const photo = await e.enqueue('photo', 'wo1', { kind: 'After', description: 'seal', fileName: 'seal.jpg', contentType: 'image/jpeg', size: 3 }, new Blob(['abc'], { type: 'image/jpeg' }));
    const lost = await e.enqueue('photo', 'wo1', { kind: 'After', description: null, fileName: 'gone.jpg', contentType: 'image/jpeg', size: 3 }, new Blob(['abc']));
    await store.delete(`blob:${lost.id}`);

    const result = await e.sync();
    expect(result).toMatchObject({ sent: 1, failed: 1 });
    const upload = api.calls.find((c) => c.path === '/work-orders/wo1/attachments')!;
    const form = upload.init.body as FormData;
    expect((form.get('file') as File).name).toBe('seal.jpg');
    expect(form.get('kind')).toBe('After');
    expect(new Headers(upload.init.headers).get('Idempotency-Key')).toBe(photo.id);
    expect(await e.blob(photo.id)).toBeUndefined(); // its bytes are released once sent
    expect((await e.ops())[0]).toMatchObject({ state: 'failed', error: 'The photo is no longer on this device.' });
  });

  it('runs one sync at a time', async () => {
    const e = engine();
    await e.enqueue('start', 'wo1', {});
    const [a, b] = await Promise.all([e.sync(), e.sync()]);
    expect(a).toBe(b);
    expect(api.calls.filter((c) => c.path.endsWith('/start'))).toHaveLength(1);
  });

  it('keeps each person’s jobs and queue apart on a shared device, and clears only theirs', async () => {
    const alice = engine();
    const bob = new OfflineEngine({ userKey: 'entra:bob', store, api, now: () => clock, newId: () => `b${++n}` });
    await alice.refresh();
    await alice.enqueue('start', 'wo1', {});
    await bob.refresh();
    await bob.enqueue('comment', 'wo2', { body: 'b', isInternal: false });

    expect(await alice.ops()).toHaveLength(1);
    expect(await bob.ops()).toHaveLength(1);

    await alice.clear();
    expect(await alice.ops()).toEqual([]);
    expect(await alice.snapshot()).toBeUndefined();
    expect(await bob.ops()).toHaveLength(1);
    expect(await bob.snapshot()).toBeDefined();
  });

  it('stores the downloaded jobs with the clock difference and reloads them next time', async () => {
    const e = engine();
    api.bundle.serverTime = '2026-11-01T09:05:00Z'; // the server is five minutes ahead of this phone
    await e.refresh();
    const snap = await e.snapshot();
    expect(snap?.clockOffsetMs).toBe(5 * 60 * 1000);
    expect(snap?.jobs).toHaveLength(1);

    const fresh = engine();
    await fresh.load();
    expect(fresh.correctedNow().toISOString()).toBe('2026-11-01T09:05:00.000Z');
  });
});
