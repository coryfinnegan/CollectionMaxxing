// The quest board (#362): three quiet quests derived from the shelves, with no
// deadlines, no expiry, no failure and no penalties anywhere in the shape —
// quests wait forever and only ever reward. The board itself is opt-in: it stays
// down until the person pins it, and the pin is kept on this device.
import { create } from 'zustand';

export type QuestId = 'full-shelf' | 'three-shelves' | 'full-series';

/** What the Great Hall already counts: the fullest shelf, the shelves in use, the deepest set of one work. */
export type QuestInput = { fullestShelf: number; shelvesUsed: number; bestSet: number };

export type QuestDef = {
  id: QuestId;
  title: string;
  goal: string;
  target: number;
  progressOf(input: QuestInput): number;
};

export const questDefs: QuestDef[] = [
  { id: 'full-shelf', title: 'Catalog one full shelf', goal: 'Ten items on one shelf.', target: 10, progressOf: (input) => input.fullestShelf },
  { id: 'three-shelves', title: 'Spread across three shelves', goal: 'Items on three shelves.', target: 3, progressOf: (input) => input.shelvesUsed },
  { id: 'full-series', title: 'Complete a series', goal: 'Three copies of one work.', target: 3, progressOf: (input) => input.bestSet },
];

export type QuestState = {
  id: QuestId;
  title: string;
  goal: string;
  progress: number;
  target: number;
  done: boolean;
  /** "7 of 10." on the way, "Done. It stays done." at the end. Never a date. */
  line: string;
};

/** Every quest with its progress line; ragged counts clamp at zero. */
export function questsFor(input: QuestInput): QuestState[] {
  return questDefs.map((def) => {
    const progress = Math.max(0, def.progressOf(input));
    const done = progress >= def.target;
    return {
      id: def.id,
      title: def.title,
      goal: def.goal,
      progress,
      target: def.target,
      done,
      line: done ? 'Done. It stays done.' : `${Math.min(progress, def.target)} of ${def.target}.`,
    };
  });
}

type StringStore = { getString(key: string): string | undefined; set(key: string, value: string): void };

function memoryStore(): StringStore {
  const map = new Map<string, string>();
  return { getString: (key) => map.get(key), set: (key, value) => void map.set(key, value) };
}

function openQuestStorage(id: string): StringStore {
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { createMMKV } = require('react-native-mmkv') as typeof import('react-native-mmkv');
    return createMMKV({ id });
  } catch {
    return memoryStore();
  }
}

const PIN_KEY = 'quest-board-pinned';
let pinStorage: StringStore | undefined;

export const getQuestBoardStorage = (): StringStore => (pinStorage ??= openQuestStorage('quest-board'));
export const setQuestBoardStorage = (next: StringStore | undefined) => {
  pinStorage = next;
};

type QuestBoardState = { pinned: boolean; set(pinned: boolean): void; reload(): void };

/** Down until the person pins it in the Great Hall; kept on this device. */
export const useQuestBoard = create<QuestBoardState>((set) => ({
  pinned: false,
  set: (pinned) => {
    getQuestBoardStorage().set(PIN_KEY, pinned ? 'pinned' : '');
    set({ pinned });
  },
  reload: () => set({ pinned: getQuestBoardStorage().getString(PIN_KEY) === 'pinned' }),
}));

const SEEN_KEY = 'quest-seen';
let seenStorage: StringStore | undefined;

export const getQuestSeenStorage = (): StringStore => (seenStorage ??= openQuestStorage('quest-seen'));
export const setQuestSeenStorage = (next: StringStore | undefined) => {
  seenStorage = next;
};

function readSeen(store: StringStore): QuestId[] | null {
  try {
    const raw = store.getString(SEEN_KEY);
    if (raw === undefined) return null;
    const parsed: unknown = JSON.parse(raw);
    return Array.isArray(parsed) ? (parsed as QuestId[]) : null;
  } catch {
    return null;
  }
}

/**
 * The quests that finished since the last look: the first view (or a ragged
 * store) seeds quietly and celebrates nothing, so only new finishes get a
 * moment. Finished ids persist as a side effect of reading.
 */
export function freshQuests(quests: QuestState[]): QuestState[] {
  const store = getQuestSeenStorage();
  const seen = readSeen(store);
  const done = quests.filter((quest) => quest.done);
  if (seen === null) {
    store.set(SEEN_KEY, JSON.stringify(done.map((quest) => quest.id)));
    return [];
  }
  const fresh = done.filter((quest) => !seen.includes(quest.id));
  if (fresh.length > 0) store.set(SEEN_KEY, JSON.stringify([...seen, ...fresh.map((quest) => quest.id)]));
  return fresh;
}
