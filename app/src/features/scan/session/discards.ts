// A discarded scan stays discarded: the reject the phone owes the server waits here until it lands (MMKV, so a
// killed app loses nothing), and a discarded candidate never renders again — not from a snapshot, not from the stream.
import type { StoreApi } from 'zustand/vanilla';
import { decideCandidate } from '@/api/generated/scanning/scanning';
import type { CandidateDto } from '@/api/generated/models';
import { ApiError } from '@/lib/http';
import { useCardsStore } from '../cards/cardsStore';
import { getPipelineStore, type PicSnap } from '../pipeline/pipelineStore';
import { MemoryKeyValueStore, type KeyValueStore, type TrayStore } from '../tray/trayStore';

/** One reject the server has not confirmed yet. The id is the candidate: discarding twice queues once. */
export interface QueuedDiscard {
  libraryId: string;
  sessionId: string;
  candidateId: string;
  enqueuedAt: number;
}

const OUTBOX_KEY = 'discard-outbox-v1';

export interface DiscardOutbox {
  list(libraryId: string): QueuedDiscard[];
  has(candidateId: string): boolean;
  enqueue(entry: Omit<QueuedDiscard, 'enqueuedAt'>): void;
  remove(candidateId: string): void;
  clearLibrary(libraryId: string): void;
  clear(): void;
}

export function createDiscardOutbox({ storage, now = Date.now }: { storage: KeyValueStore; now?: () => number }): DiscardOutbox {
  const load = (): QueuedDiscard[] => {
    try {
      const parsed: unknown = JSON.parse(storage.getString(OUTBOX_KEY) ?? '[]');
      return Array.isArray(parsed) ? (parsed as QueuedDiscard[]) : [];
    } catch {
      return [];
    }
  };
  const save = (entries: QueuedDiscard[]) => {
    try {
      storage.set(OUTBOX_KEY, JSON.stringify(entries));
    } catch {
      // Storage full or unavailable: the queue holds for this run.
    }
  };
  return {
    list: (libraryId) => load().filter((e) => e.libraryId === libraryId),
    has: (candidateId) => load().some((e) => e.candidateId === candidateId),
    enqueue: (entry) => save([...load().filter((e) => e.candidateId !== entry.candidateId), { ...entry, enqueuedAt: now() }]),
    remove: (candidateId) => save(load().filter((e) => e.candidateId !== candidateId)),
    clearLibrary: (libraryId) => save(load().filter((e) => e.libraryId !== libraryId)),
    clear: () => save([]),
  };
}

function openOutboxStorage(): KeyValueStore {
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { createMMKV } = require('react-native-mmkv') as typeof import('react-native-mmkv');
    return createMMKV({ id: 'discard-outbox' });
  } catch {
    return new MemoryKeyValueStore();
  }
}

let outbox: DiscardOutbox | undefined;

/** The discard outbox for this app run. Queued discards survive the app being killed. */
export const getDiscardOutbox = (): DiscardOutbox => (outbox ??= createDiscardOutbox({ storage: openOutboxStorage() }));

export const setDiscardOutbox = (next: DiscardOutbox | undefined) => {
  outbox = next;
};

/** Whether this candidate was discarded on the phone and the server has not confirmed it yet. */
export const isDiscarded = (candidateId: string): boolean => getDiscardOutbox().has(candidateId);

/** Drops discarded candidates: a snapshot or an event never brings one back. */
export function withoutDiscarded(candidates: readonly CandidateDto[]): CandidateDto[] {
  return candidates.filter((c) => !isDiscarded(c.id));
}

/** A snapshot that already calls a queued candidate rejected is the confirmation: the queue lets it go. */
export function pruneDiscarded(candidates: readonly Pick<CandidateDto, 'id' | 'decision'>[]): void {
  const queue = getDiscardOutbox();
  for (const c of candidates) {
    if (c.decision === 'rejected') queue.remove(c.id);
  }
}

const defaultReject = async (sessionId: string, candidateId: string): Promise<void> => {
  const response = await decideCandidate(sessionId, candidateId, { decision: 'rejected' });
  if (response.status !== 200) throw new ApiError(response.status, {});
};

/**
 * Whether a failed request is worth sending again later, as is: no connection, a 5xx, or a refusal that may pass by itself —
 * not signed in right now (401), a timeout (408), too many requests (429). Any other 4xx is a real "no" (403 not allowed,
 * 404 the session or candidate is gone, 409 it changed or was done already), and sending it again never helps.
 */
export const isTryAgainLater = (error: unknown): boolean =>
  !(error instanceof ApiError) || error.status >= 500 || error.status === 401 || error.status === 408 || error.status === 429;

/**
 * Sends one library's queued discards, oldest first. A refusal that is final (403, 404, 409 and the like: the session is
 * gone, or the candidate with it) drops the entry; anything that may pass (offline, a 5xx, 401, 408, 429) stops the flush
 * and keeps the entry. Returns what is queued still.
 */
export async function flushDiscardOutbox({
  libraryId,
  reject = defaultReject,
}: {
  libraryId: string;
  reject?: (sessionId: string, candidateId: string) => Promise<void>;
}): Promise<number> {
  const queue = getDiscardOutbox();
  const entries = queue.list(libraryId).sort((a, b) => a.enqueuedAt - b.enqueuedAt);
  for (const entry of entries) {
    try {
      await reject(entry.sessionId, entry.candidateId);
      queue.remove(entry.candidateId);
    } catch (error) {
      if (isTryAgainLater(error)) return queue.list(libraryId).length;
      queue.remove(entry.candidateId);
    }
  }
  return 0;
}

/**
 * Drops a discarded candidate from everything the phone keeps: its card, its tray line (by the printed code), and
 * its picture's item (by the server's candidate id, else the phone's own local id). Other libraries and other rows
 * stay. The tray and the pipeline persist their stores, so a restart finds nothing to resend.
 */
export function dropCandidateLocally({
  tray,
  libraryId,
  candidate,
}: {
  tray: StoreApi<TrayStore>;
  libraryId: string;
  candidate: Pick<CandidateDto, 'id' | 'rawCode' | 'localId'>;
}): void {
  useCardsStore.getState().remove(candidate.id);
  if (candidate.rawCode) tray.getState().remove(candidate.rawCode);
  dropPictureItem((snap) => snap.libraryId === libraryId, candidate);
}

/**
 * Takes a candidate's picture item (and the phone's own card for it) off the snaps sent to this session. A row that leaves
 * Review without being discarded, such as a skip, would otherwise leave its item counting as "processing" for ever.
 */
export function dropPictureItemLocally(sessionId: string, candidate: Pick<CandidateDto, 'id' | 'localId'>): void {
  dropPictureItem((snap) => snap.sessionId === sessionId, candidate);
}

function dropPictureItem(inSnap: (snap: PicSnap) => boolean, candidate: Pick<CandidateDto, 'id' | 'localId'>): void {
  const pipeline = getPipelineStore();
  for (const snap of pipeline.getState().snaps) {
    if (!inSnap(snap)) continue;
    const item = snap.items.find((i) => i.candidateId === candidate.id || (candidate.localId != null && i.localId === candidate.localId));
    if (!item) continue;
    pipeline.getState().removeItem(snap.snapId, item.localId);
    useCardsStore.getState().dropLocal(snap.snapId, item.localId);
  }
}
