// The session's live counters on the phone (docs/16 §5): the server's last snapshot plus the local queue laid over it.
// Offline, the bar shows the local queue against the last snapshot it saw, badged "offline". Snapshots are kept per
// session and survive the app being killed, so an airplane-mode session still counts after a restart.
import { createStore, type StoreApi } from 'zustand/vanilla';
import type { SessionCountersDto } from '@/api/generated/models';
import { MemoryKeyValueStore, type KeyValueStore } from '@/features/scan/tray/trayStore';

export interface SessionCounters {
  captured: number;
  added: number;
  toReview: number;
  owned: number;
  skipped: number;
}

export const ZERO_COUNTERS: SessionCounters = { captured: 0, added: 0, toReview: 0, owned: 0, skipped: 0 };

const isCounters = (value: unknown): value is SessionCountersDto => {
  const v = value as Partial<SessionCountersDto> | null | undefined;
  return (
    !!v &&
    typeof v.captured === 'number' &&
    typeof v.added === 'number' &&
    typeof v.toReview === 'number' &&
    typeof v.owned === 'number' &&
    typeof v.skipped === 'number'
  );
};

export const countersOf = (value: unknown): SessionCounters | undefined => (isCounters(value) ? { ...value } : undefined);

/** Every candidate in exactly one bucket: captured is added + to-review + owned + skipped. */
export const reconciles = (c: SessionCounters): boolean => c.captured === c.added + c.toReview + c.owned + c.skipped;

/**
 * The bar's numbers: the snapshot's truth plus what the phone holds that the server has not seen yet. Queued work is
 * captured but unclassified, so it lands in to-review until the server answers; added/owned/skipped only move server-side.
 */
export function mergeCounters(snapshot: SessionCounters, pendingLocal: number): SessionCounters {
  return { ...snapshot, captured: snapshot.captured + pendingLocal, toReview: snapshot.toReview + pendingLocal };
}

export interface CountersSnapshot {
  counters: SessionCounters;
  changeSeq: number;
}

export type SessionCountersStore = {
  snapshots: Record<string, CountersSnapshot>;
  /** Keeps the newest snapshot per session; an older change counter never overwrites a newer one. */
  seed(sessionId: string, counters: SessionCounters, changeSeq: number): void;
  clearSession(sessionId: string): void;
  reset(): void;
};

const STORAGE_KEY = 'session-counters-v1';

function load(storage: KeyValueStore): Record<string, CountersSnapshot> {
  try {
    const parsed: unknown = JSON.parse(storage.getString(STORAGE_KEY) ?? '{}');
    if (!parsed || typeof parsed !== 'object') return {};
    return Object.fromEntries(
      Object.entries(parsed as Record<string, unknown>).flatMap(([id, value]): [string, CountersSnapshot][] => {
        const v = value as Partial<CountersSnapshot> | null | undefined;
        const counters = countersOf(v?.counters);
        return v && counters && typeof v.changeSeq === 'number' ? [[id, { counters, changeSeq: v.changeSeq }]] : [];
      }),
    );
  } catch {
    return {}; // a damaged file must not stop the scanner opening
  }
}

export function createSessionCountersStore({ storage }: { storage: KeyValueStore }): StoreApi<SessionCountersStore> {
  const store = createStore<SessionCountersStore>()((set, get) => ({
    snapshots: load(storage),
    seed: (sessionId, counters, changeSeq) => {
      const current = get().snapshots[sessionId];
      if (current && current.changeSeq >= changeSeq) return;
      set({ snapshots: { ...get().snapshots, [sessionId]: { counters, changeSeq } } });
    },
    clearSession: (sessionId) => {
      if (!get().snapshots[sessionId]) return;
      const { [sessionId]: _dropped, ...rest } = get().snapshots;
      set({ snapshots: rest });
    },
    reset: () => set({ snapshots: {} }),
  }));
  store.subscribe((state) => {
    try {
      storage.set(STORAGE_KEY, JSON.stringify(state.snapshots));
    } catch {
      // Storage full or unavailable: the counters still work for this run.
    }
  });
  return store;
}

function openStorage(): KeyValueStore {
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { createMMKV } = require('react-native-mmkv') as typeof import('react-native-mmkv');
    return createMMKV({ id: 'session-counters' });
  } catch {
    return new MemoryKeyValueStore();
  }
}

let current: StoreApi<SessionCountersStore> | undefined;

export const getSessionCountersStore = (): StoreApi<SessionCountersStore> => (current ??= createSessionCountersStore({ storage: openStorage() }));

export const setSessionCountersStore = (next: StoreApi<SessionCountersStore> | undefined) => {
  current = next;
};

/**
 * Seeds the store from a session snapshot or a `session.counters` event. Tolerant: a body without counters
 * (or with a partial one) seeds nothing rather than zeroing the bar.
 */
export function seedSessionCounters(sessionId: string, body: { counters?: unknown; changeSeq?: unknown }): void {
  const counters = countersOf(body.counters);
  if (!counters) return;
  const changeSeq = typeof body.changeSeq === 'number' ? body.changeSeq : 0;
  getSessionCountersStore().getState().seed(sessionId, counters, changeSeq);
}

/** Seeds the store from a whole-session snapshot, stamped with the newest change it carries. */
export function seedFromSnapshot(sessionId: string, snapshot: { counters?: unknown; candidates: { changeSeq: number }[] }): void {
  const counters = countersOf(snapshot.counters);
  if (!counters) return;
  const changeSeq = snapshot.candidates.reduce((max, c) => Math.max(max, c.changeSeq), 0);
  getSessionCountersStore().getState().seed(sessionId, counters, changeSeq);
}
