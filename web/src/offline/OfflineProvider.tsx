import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useAuth } from '../auth/AuthContext';
import { OfflineEngine, type EngineState } from './engine';
import type { Op, OpKind, OpPayloads, Snapshot } from './model';
import { IdbStore } from './store';

interface OfflineValue {
  ready: boolean;
  /** The browser's idea of whether there is a connection. A failed send also stops a sync, whatever this says. */
  online: boolean;
  state: EngineState;
  snapshot: Snapshot | undefined;
  ops: Op[];
  enqueue: <K extends OpKind>(kind: K, workOrderId: string, payload: OpPayloads[K], blob?: Blob) => Promise<void>;
  syncNow: () => Promise<void>;
  retry: (opId: string) => Promise<void>;
  discard: (opId: string) => Promise<void>;
  /** Forget this person's saved jobs and unsent work on this device. */
  forgetEverything: () => Promise<void>;
  blobFor: (opId: string) => Promise<Blob | undefined>;
}

const EMPTY: EngineState = { pending: 0, failed: 0, syncing: false, authRequired: false, lastSyncAt: null, lastError: null };

const OfflineContext = createContext<OfflineValue | null>(null);

export function useOffline(): OfflineValue {
  const value = useContext(OfflineContext);
  if (!value) throw new Error('useOffline must be used inside <OfflineProvider>.');
  return value;
}

/** How often to try again while there is unsent work (and a connection might have come back). */
const RETRY_MS = 30_000;

/**
 * Holds the technician's saved jobs and unsent work for the signed-in person, and keeps them in step with the server whenever a connection
 * is available. The engine does the sending; this component only wires it to the browser (connectivity, focus, a timer) and to React.
 */
export function OfflineProvider({ children }: { children: ReactNode }) {
  const { api, userKey, status, offlineSession } = useAuth();
  const client = useQueryClient();
  const [online, setOnline] = useState(() => navigator.onLine);
  const [state, setState] = useState<EngineState>(EMPTY);
  const [snapshot, setSnapshot] = useState<Snapshot | undefined>();
  const [ops, setOps] = useState<Op[]>([]);
  const [ready, setReady] = useState(false);
  const engine = useRef<OfflineEngine | null>(null);

  const reload = useCallback(async () => {
    const e = engine.current;
    if (!e) return;
    const [s, o] = await Promise.all([e.snapshot(), e.ops()]);
    setSnapshot(s);
    setOps(o);
  }, []);

  useEffect(() => {
    if (status !== 'signedIn' || !userKey) {
      engine.current = null;
      setReady(false);
      setSnapshot(undefined);
      setOps([]);
      setState(EMPTY);
      return;
    }

    const e = new OfflineEngine({ userKey, store: new IdbStore(), api });
    engine.current = e;
    const unsubscribe = e.subscribe((s) => {
      setState(s);
      void reload();
    });
    void e.load().then(async () => {
      setState(e.state);
      await reload();
      setReady(true);
      if (navigator.onLine && !offlineSession) void sync();
    });
    return unsubscribe;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [status, userKey, api]);

  const sync = useCallback(async () => {
    const e = engine.current;
    if (!e) return;
    const result = await e.sync();
    if (result.sent > 0 || result.stoppedBecause === null) await client.invalidateQueries({ queryKey: ['work-orders'] });
    await reload();
  }, [client, reload]);

  useEffect(() => {
    const up = () => { setOnline(true); void sync(); };
    const down = () => setOnline(false);
    const visible = () => { if (document.visibilityState === 'visible' && navigator.onLine) void sync(); };
    window.addEventListener('online', up);
    window.addEventListener('offline', down);
    document.addEventListener('visibilitychange', visible);
    return () => {
      window.removeEventListener('online', up);
      window.removeEventListener('offline', down);
      document.removeEventListener('visibilitychange', visible);
    };
  }, [sync]);

  // While work is waiting, keep trying: the connection may have come back without the browser saying so.
  useEffect(() => {
    if (state.pending === 0 || state.authRequired) return;
    const timer = setInterval(() => { if (navigator.onLine) void sync(); }, RETRY_MS);
    return () => clearInterval(timer);
  }, [state.pending, state.authRequired, sync]);

  const value = useMemo<OfflineValue>(
    () => ({
      ready, online, state, snapshot, ops,
      enqueue: async (kind, workOrderId, payload, blob) => {
        await engine.current?.enqueue(kind, workOrderId, payload, blob);
        await reload();
        if (navigator.onLine && !offlineSession) void sync();
      },
      syncNow: sync,
      retry: async (id) => { await engine.current?.retry(id); await reload(); if (navigator.onLine) void sync(); },
      discard: async (id) => { await engine.current?.discard(id); await reload(); },
      forgetEverything: async () => { await engine.current?.clear(); await reload(); },
      blobFor: async (id) => engine.current?.blob(id),
    }),
    [ready, online, state, snapshot, ops, reload, sync, offlineSession],
  );

  return <OfflineContext.Provider value={value}>{children}</OfflineContext.Provider>;
}
