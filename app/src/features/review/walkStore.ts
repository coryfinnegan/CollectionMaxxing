// The walk's memory (#833): per scan session, the order, the answers and the card on screen, saved on the phone so the walk
// survives a kill and a trip to the camera. Nothing here goes to the server; the answers themselves are sent by useReview.
import { createStore, type StoreApi } from 'zustand/vanilla';
import { MemoryKeyValueStore, type KeyValueStore } from '@/features/scan/tray/trayStore';
import { emptyWalk, type WalkState } from './walk';

const STORAGE_KEY = 'review-walk-v1';
/** A few sessions is plenty: a session's walk only matters until it is added. */
const KEEP = 5;

export interface WalkStore {
  walks: Record<string, WalkState>;
  /** The walks in the order they were last changed, newest last (the oldest go first). */
  recent: string[];
  update(sessionId: string, change: (walk: WalkState) => WalkState): void;
  forget(sessionId: string): void;
}

function load(storage: KeyValueStore): Pick<WalkStore, 'walks' | 'recent'> {
  try {
    const raw = storage.getString(STORAGE_KEY);
    const parsed = raw ? (JSON.parse(raw) as Partial<WalkStore>) : {};
    const walks = parsed.walks && typeof parsed.walks === 'object' ? parsed.walks : {};
    const recent = Array.isArray(parsed.recent) ? parsed.recent.filter((id): id is string => typeof id === 'string' && !!walks[id]) : [];
    return { walks, recent };
  } catch {
    return { walks: {}, recent: [] }; // a damaged file is a fresh walk, never a crash
  }
}

export function createWalkStore({ storage }: { storage: KeyValueStore }): StoreApi<WalkStore> {
  const store = createStore<WalkStore>()((set, get) => ({
    ...load(storage),
    update: (sessionId, change) => {
      const before = get().walks[sessionId] ?? emptyWalk();
      const after = change(before);
      if (after === before && get().walks[sessionId]) return;
      const recent = [...get().recent.filter((id) => id !== sessionId), sessionId];
      const dropped = recent.slice(0, Math.max(0, recent.length - KEEP));
      const walks = { ...get().walks, [sessionId]: after };
      for (const id of dropped) delete walks[id];
      set({ walks, recent: recent.slice(dropped.length) });
    },
    forget: (sessionId) => {
      if (!get().walks[sessionId]) return;
      const { [sessionId]: _gone, ...walks } = get().walks;
      set({ walks, recent: get().recent.filter((id) => id !== sessionId) });
    },
  }));
  store.subscribe((state) => {
    try {
      storage.set(STORAGE_KEY, JSON.stringify({ walks: state.walks, recent: state.recent }));
    } catch {
      // storage full or unavailable: the walk still works for this run
    }
  });
  return store;
}

function openStorage(): KeyValueStore {
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { createMMKV } = require('react-native-mmkv') as typeof import('react-native-mmkv');
    return createMMKV({ id: 'review-walk' });
  } catch {
    return new MemoryKeyValueStore();
  }
}

let current: StoreApi<WalkStore> | undefined;

export const getWalkStore = (): StoreApi<WalkStore> => (current ??= createWalkStore({ storage: openStorage() }));

export const setWalkStore = (next: StoreApi<WalkStore> | undefined) => {
  current = next;
};
