// What the picture pipeline remembers between launches: for each snap, what the phone read (text and fields, never the
// image) and whether the server has it yet. A snap read offline stays here until it is sent; a killed app loses nothing.
import { createStore, type StoreApi } from 'zustand/vanilla';
import type { IdentifyItem } from '@/api/generated/models';
import { MemoryKeyValueStore, type KeyValueStore } from '@/features/scan/tray/trayStore';
import type { ShotKind } from '../shotkind/shotKind';
import type { SnapTrigger } from '../autosnap/types';
import type { EngineName, FellBackFrom } from '../recognition/types';

const STORAGE_KEY = 'scan-pipeline-v1';

export interface PicItem {
  /** The id the phone gave this item; the server echoes it on the candidate. */
  localId: string;
  /** What was read: shown on the receipt until the server names it. */
  text: string;
  title: string;
  /** Exactly what is sent. */
  payload: IdentifyItem;
  /** The server's candidate, once `identify` has answered. */
  candidateId?: string;
  /** A small crop of this item, kept on the phone. */
  thumbUri?: string;
}

export interface PicSnap {
  snapId: string;
  /** The library whose scanner took the picture: one store holds every library's snaps, kept apart by this. */
  libraryId: string;
  trigger: SnapTrigger;
  /** What the framing said this shot was; absent on snaps stored before classification. */
  shotKind?: ShotKind;
  /** The phone's own id for this snap's segment, shared by every item sent from it. */
  segmentId?: string;
  createdAt: number;
  /** The still, as a thumbnail for the first receipt line. */
  thumbUri: string;
  /** `reading` while the phone reads it; `read` once the items are final. */
  phase: 'reading' | 'read';
  /**
   * The snapId the server knows this picture by, when it is not `snapId`: a new scan session cannot use the id the old one
   * holds, so a snap waiting to be sent gets a new one then. The phone's own key (the snap store, the receipt) stays `snapId`.
   */
  sendId?: string;
  /** False until `identify` has answered for this snap. */
  sent: boolean;
  /** The scan session it was sent to; once that session is over (committed, discarded) the snap leaves the receipt. */
  sessionId?: string;
  items: PicItem[];
  unreadableCount: number;
  engine?: EngineName;
  fellBackFrom?: FellBackFrom;
  /** Review's "Snap cover": this picture was shot for one T2 row, which it upgrades in place. Absent on older snaps. */
  boundCandidateId?: string;
}

export interface PipelineState {
  snaps: PicSnap[];
  /** How many items the last finished read could not read, for "Get closer for these N". Cleared by the chip or the next snap. */
  unreadableCount: number;
}

export type PipelineStore = PipelineState & {
  addSnap(snap: PicSnap): void;
  upsertItem(snapId: string, item: PicItem): void;
  finishRead(snapId: string, update: { items: PicItem[]; unreadableCount: number; engine: EngineName; fellBackFrom?: FellBackFrom }): void;
  /** The picture shown on the snap's own line, until its items have crops of their own. */
  setSnapThumb(snapId: string, thumbUri: string): void;
  setThumb(snapId: string, localId: string, thumbUri: string): void;
  setPayload(snapId: string, localId: string, payload: IdentifyItem): void;
  setSendId(snapId: string, sendId: string): void;
  markSent(snapId: string, sessionId: string, candidateIds: Record<string, string>): void;
  removeSnap(snapId: string): void;
  removeItem(snapId: string, localId: string): void;
  dismissUnreadable(): void;
  /** Drops snaps that were sent to a session other than `sessionId`: that scan is over. */
  sweep(sessionId: string | null, libraryId: string): void;
  /** Drops one library's snaps and keeps the others': Discard. */
  clearLibrary(libraryId: string): void;
  clear(): void;
};

function load(storage: KeyValueStore): PipelineState {
  try {
    const raw = storage.getString(STORAGE_KEY);
    if (!raw) return { snaps: [], unreadableCount: 0 };
    const parsed = JSON.parse(raw) as Partial<PipelineState>;
    const snaps = Array.isArray(parsed.snaps) ? parsed.snaps : [];
    return {
      // Snaps saved before they were tagged with a library cannot be attributed to one, so they go rather than leak across.
      snaps: snaps.filter((s): s is PicSnap => typeof (s as Partial<PicSnap>).libraryId === 'string'),
      unreadableCount: typeof parsed.unreadableCount === 'number' ? parsed.unreadableCount : 0,
    };
  } catch {
    return { snaps: [], unreadableCount: 0 }; // a damaged file must not stop the scanner opening
  }
}

export function createPipelineStore({ storage }: { storage: KeyValueStore }): StoreApi<PipelineStore> {
  const store = createStore<PipelineStore>()((set, get) => {
    const edit = (snapId: string, change: (snap: PicSnap) => PicSnap) =>
      set({ snaps: get().snaps.map((s) => (s.snapId === snapId ? change(s) : s)) });
    return {
      ...load(storage),
      addSnap: (snap) => set({ snaps: [...get().snaps.filter((s) => s.snapId !== snap.snapId), snap], unreadableCount: 0 }),
      upsertItem: (snapId, item) =>
        edit(snapId, (s) => ({
          ...s,
          items: s.items.some((i) => i.localId === item.localId)
            ? s.items.map((i) => (i.localId === item.localId ? { ...i, ...item, thumbUri: item.thumbUri ?? i.thumbUri } : i))
            : [...s.items, item],
        })),
      finishRead: (snapId, { items, unreadableCount, engine, fellBackFrom }) => {
        // The final reading supersedes what streamed (after a fallback or a merge of halves); crops already made are kept.
        edit(snapId, (s) => ({
          ...s,
          phase: 'read',
          items: items.map((item) => ({ ...item, thumbUri: item.thumbUri ?? s.items.find((i) => i.localId === item.localId)?.thumbUri })),
          unreadableCount,
          engine,
          ...(fellBackFrom ? { fellBackFrom } : {}),
        }));
        set({ unreadableCount });
      },
      setSnapThumb: (snapId, thumbUri) => edit(snapId, (s) => ({ ...s, thumbUri })),
      setThumb: (snapId, localId, thumbUri) =>
        edit(snapId, (s) => ({ ...s, items: s.items.map((i) => (i.localId === localId ? { ...i, thumbUri } : i)) })),
      setPayload: (snapId, localId, payload) =>
        edit(snapId, (s) => ({ ...s, items: s.items.map((i) => (i.localId === localId ? { ...i, payload } : i)) })),
      setSendId: (snapId, sendId) => edit(snapId, (s) => ({ ...s, sendId })),
      markSent: (snapId, sessionId, candidateIds) =>
        edit(snapId, (s) => ({
          ...s,
          sent: true,
          sessionId,
          items: s.items.map((i) => (candidateIds[i.localId] ? { ...i, candidateId: candidateIds[i.localId] } : i)),
        })),
      removeSnap: (snapId) => set({ snaps: get().snaps.filter((s) => s.snapId !== snapId) }),
      removeItem: (snapId, localId) =>
        set({
          snaps: get()
            .snaps.map((s) => (s.snapId === snapId ? { ...s, items: s.items.filter((i) => i.localId !== localId) } : s))
            .filter((s) => s.items.length > 0 || s.phase === 'reading'),
        }),
      dismissUnreadable: () => set({ unreadableCount: 0 }),
      sweep: (sessionId, libraryId) => {
        const snaps = get().snaps.filter((s) => s.libraryId !== libraryId || !s.sent || s.sessionId === sessionId);
        if (snaps.length !== get().snaps.length) set({ snaps });
      },
      clearLibrary: (libraryId) => {
        const snaps = get().snaps.filter((s) => s.libraryId !== libraryId);
        if (snaps.length !== get().snaps.length) set({ snaps });
      },
      clear: () => set({ snaps: [], unreadableCount: 0 }),
    };
  });
  store.subscribe((state) => {
    try {
      storage.set(STORAGE_KEY, JSON.stringify({ snaps: state.snaps, unreadableCount: state.unreadableCount }));
    } catch {
      // Storage full or unavailable: the pipeline still works for this run.
    }
  });
  return store;
}

function openStorage(): KeyValueStore {
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { createMMKV } = require('react-native-mmkv') as typeof import('react-native-mmkv');
    return createMMKV({ id: 'scan-pipeline' });
  } catch {
    return new MemoryKeyValueStore();
  }
}

let current: StoreApi<PipelineStore> | undefined;

export const getPipelineStore = (): StoreApi<PipelineStore> => (current ??= createPipelineStore({ storage: openStorage() }));

export const setPipelineStore = (next: StoreApi<PipelineStore> | undefined) => {
  current = next;
};
