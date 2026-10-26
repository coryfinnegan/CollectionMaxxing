// The milestone moment (#362): which rung, if any, the Great Hall celebrates.
// The first sight of a rung seeds quietly, so shelves built before this issue
// never fanfare themselves; only a later climb celebrates, and it keeps
// celebrating until dismissed. Kept per library on this device.
import { useCallback, useMemo, useState } from 'react';

type StringStore = { getString(key: string): string | undefined; set(key: string, value: string): void };

function memoryStore(): StringStore {
  const map = new Map<string, string>();
  return { getString: (key) => map.get(key), set: (key, value) => void map.set(key, value) };
}

function openMilestoneSeenStorage(): StringStore {
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { createMMKV } = require('react-native-mmkv') as typeof import('react-native-mmkv');
    return createMMKV({ id: 'milestone-seen' });
  } catch {
    return memoryStore();
  }
}

let storage: StringStore | undefined;

export const getMilestoneSeenStorage = (): StringStore => (storage ??= openMilestoneSeenStorage());
export const setMilestoneSeenStorage = (next: StringStore | undefined) => {
  storage = next;
};

const keyFor = (libraryId: string) => `milestone-seen:${libraryId}`;

/**
 * The rung to celebrate right now, or null to stay quiet. Seeds on the first
 * sight (including below the first rung, stored as 'none') and reseeds a
 * ragged store; persists as a side effect of reading.
 */
export function milestoneMoment(libraryId: string, highest: number | undefined): number | null {
  const store = getMilestoneSeenStorage();
  const raw = store.getString(keyFor(libraryId));
  if (raw === undefined) {
    store.set(keyFor(libraryId), highest === undefined ? 'none' : String(highest));
    return null;
  }
  if (highest === undefined) return null;
  const stored = raw === 'none' ? -1 : Number(raw);
  if (!Number.isFinite(stored)) {
    store.set(keyFor(libraryId), String(highest));
    return null;
  }
  return highest > stored ? highest : null;
}

/** The rung has been seen; it never celebrates again. */
export function dismissMilestone(libraryId: string, rung: number): void {
  getMilestoneSeenStorage().set(keyFor(libraryId), String(rung));
}

/** The rung the hall celebrates until dismissed, then never again. */
export function useMilestoneCelebration(libraryId: string, highest: number | undefined): { rung: number | null; dismiss(): void } {
  // The moment only moves when the rung does, so memoizing on the inputs is safe across renders.
  const moment = useMemo(() => milestoneMoment(libraryId, highest), [libraryId, highest]);
  const [dismissed, setDismissed] = useState<number | null>(null);
  const rung = moment !== null && moment !== dismissed ? moment : null;
  const dismiss = useCallback(() => {
    const active = milestoneMoment(libraryId, highest);
    if (active !== null) {
      dismissMilestone(libraryId, active);
      setDismissed(active);
    }
  }, [libraryId, highest]);
  return { rung, dismiss };
}
