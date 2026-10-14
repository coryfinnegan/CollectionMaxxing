// The tray as a store that survives the app being killed: every change is written to a key-value store
// (MMKV on the phone) and read back when the store is created. `flushTray` sends what is waiting.
import { createStore, type StoreApi } from 'zustand/vanilla';
import {
  addCopy,
  DEFAULT_COOLDOWN_MS,
  emptyTray,
  markBatchSent,
  planBatches,
  removeLine,
  scanCode,
  type Batch,
  type ScanResult,
  type TrayState,
} from './tray';

/** The slice of MMKV the tray needs. */
export interface KeyValueStore {
  getString(key: string): string | undefined;
  set(key: string, value: string): void;
  remove(key: string): unknown;
}

export class MemoryKeyValueStore implements KeyValueStore {
  private readonly values = new Map<string, string>();
  getString(key: string) {
    return this.values.get(key);
  }
  set(key: string, value: string) {
    this.values.set(key, value);
  }
  remove(key: string) {
    return this.values.delete(key);
  }
}

const STORAGE_KEY = 'scan-tray-v1';

export type TrayStore = TrayState & {
  /** False after a send failed because the server could not be reached; lines then show QUEUED. */
  online: boolean;
  /** The last send problem that is not just being offline, as a friendly sentence. */
  problem: string | null;
  scan(code: { value: string; symbology: string }): ScanResult;
  plusOne(code: string): void;
  remove(code: string): void;
  clear(): void;
  /** Used by `flushTray`. */
  setSessionId(id: string | null): void;
  plan(): void;
  confirm(batchKey: string): void;
  setOnline(online: boolean, problem?: string | null): void;
};

export type TrayDeps = {
  storage: KeyValueStore;
  now?: () => number;
  newKey?: () => string;
  cooldownMs?: number;
};

const randomKey = () =>
  'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
    const r = Math.floor(Math.random() * 16);
    return (c === 'x' ? r : (r & 0x3) | 0x8).toString(16);
  });

function load(storage: KeyValueStore): TrayState {
  try {
    const raw = storage.getString(STORAGE_KEY);
    if (!raw) return emptyTray();
    const parsed = JSON.parse(raw) as Partial<TrayState>;
    return {
      lines: Array.isArray(parsed.lines) ? parsed.lines : [],
      outbox: Array.isArray(parsed.outbox) ? parsed.outbox : [],
      sessionId: typeof parsed.sessionId === 'string' ? parsed.sessionId : null,
    };
  } catch {
    return emptyTray(); // A damaged file must not stop the scanner opening.
  }
}

export function createTrayStore({ storage, now = Date.now, newKey = randomKey, cooldownMs = DEFAULT_COOLDOWN_MS }: TrayDeps): StoreApi<TrayStore> {
  const store = createStore<TrayStore>()((set, get) => {
    const update = (next: TrayState) => set({ lines: next.lines, outbox: next.outbox, sessionId: next.sessionId });
    const snapshot = (): TrayState => {
      const { lines, outbox, sessionId } = get();
      return { lines, outbox, sessionId };
    };
    return {
      ...load(storage),
      online: true,
      problem: null,
      scan(code) {
        const { state, result } = scanCode(snapshot(), code, now(), cooldownMs);
        update(state);
        return result;
      },
      plusOne: (code) => update(addCopy(snapshot(), code)),
      remove: (code) => update(removeLine(snapshot(), code)),
      clear: () => update(emptyTray()),
      setSessionId: (id) => update({ ...snapshot(), sessionId: id }),
      plan: () => update(planBatches(snapshot(), newKey)),
      confirm: (key) => update(markBatchSent(snapshot(), key)),
      setOnline: (online, problem = null) => set({ online, problem }),
    };
  });
  store.subscribe((state) => {
    const { lines, outbox, sessionId } = state;
    try {
      storage.set(STORAGE_KEY, JSON.stringify({ lines, outbox, sessionId }));
    } catch {
      // Storage full or unavailable: the tray still works for this run.
    }
  });
  return store;
}

export type FlushDeps = {
  createSession(): Promise<string>;
  /** Sends one batch with its idempotency key. Resolves when the server has taken it. */
  sendBatch(sessionId: string, batch: Batch): Promise<void>;
  /** Maps a failure to `offline` (retry later, show QUEUED) or a message to show. */
  classify(error: unknown): { offline: true } | { offline: false; message: string; resetSession?: boolean };
};

const inFlight = new WeakMap<object, Promise<void>>();

/**
 * Sends everything waiting: makes a session if needed, plans batches, and sends them in order.
 * Safe to call often (only one run at a time per store); a failed batch stays put with the same key.
 */
export function flushTray(store: StoreApi<TrayStore>, deps: FlushDeps): Promise<void> {
  const running = inFlight.get(store);
  if (running) return running;
  const run = (async () => {
    store.getState().plan();
    if (store.getState().outbox.length === 0) return;
    try {
      let sessionId = store.getState().sessionId;
      if (!sessionId) {
        sessionId = await deps.createSession();
        store.getState().setSessionId(sessionId);
      }
      while (store.getState().outbox.length > 0) {
        const batch = store.getState().outbox[0]!;
        await deps.sendBatch(sessionId, batch);
        store.getState().confirm(batch.key);
      }
      store.getState().setOnline(true);
    } catch (error) {
      const outcome = deps.classify(error);
      if (outcome.offline) store.getState().setOnline(false);
      else {
        if (outcome.resetSession) store.getState().setSessionId(null);
        store.getState().setOnline(true, outcome.message);
      }
    }
  })().finally(() => inFlight.delete(store));
  inFlight.set(store, run);
  return run;
}
