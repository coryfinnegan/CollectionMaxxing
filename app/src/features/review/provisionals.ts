// "Keep as unverified" (design §8(e), §11; ADR-030): the last resort for a T2 row. Keeping retires the row and
// makes a provisional record; the row's crop goes up with the keep, and only with the keep — successes never upload.
// A keep that cannot reach the server waits in the outbox instead of losing the row, and the next Review open sends it.
import type { CandidateDto, CreateProvisionalResultDto, ProvisionalDto } from '@/api/generated/models';
import { createProvisional, deleteProvisional, promoteProvisional, resolveProvisional, uploadCandidateCrop } from '@/api/generated/scanning/scanning';
import { useCardsStore } from '@/features/scan/cards/cardsStore';
import { ownArtworkUrl } from '@/features/scan/pipeline/artStep';
import type { PicSnap } from '@/features/scan/pipeline/pipelineStore';
import { MemoryKeyValueStore, type KeyValueStore } from '@/features/scan/tray/trayStore';
import { ApiError } from '@/lib/http';
import { friendlyError } from '@/lib/problem';

/** The server takes crops up to 1 MB; check here so a too-big file is never sent. */
export const MAX_CROP_BYTES = 1024 * 1024;

export const CROP_TOO_BIG = 'That crop is too big to send. The row stays in Review.';

/** Reads a stored crop as a file to send. The phone reads the file address; tests swap it. */
export type CropReader = (uri: string) => Promise<Blob>;

const fileReader: CropReader = async (uri) => (await fetch(uri)).blob();

let readCrop: CropReader = fileReader;

/** For tests: swap the crop reader, or put it back. */
export const setProvisionalCropReader = (next?: CropReader) => {
  readCrop = next ?? fileReader;
};

/**
 * The keep path's calls to the API, behind one seam. The rest implementation is the generated client; the tests
 * swap in a recording proxy, which is how they prove crops go up only for kept rows, never for successes.
 */
export interface ProvisionalTransport {
  uploadCrop(sessionId: string, candidateId: string, file: Blob): Promise<{ assetId: string; cropUrl: string }>;
  keep(sessionId: string, candidateId: string, cropAssetId?: string): Promise<CreateProvisionalResultDto>;
}

export const restProvisionalTransport: ProvisionalTransport = {
  async uploadCrop(sessionId, candidateId, file) {
    const response = await uploadCandidateCrop(sessionId, candidateId, { file });
    if (response.status !== 200) throw new ApiError(response.status, {});
    return response.data;
  },
  async keep(sessionId, candidateId, cropAssetId) {
    const response = await createProvisional(sessionId, { candidateId, cropAssetId });
    if (response.status !== 201) throw new ApiError(response.status, {});
    return response.data;
  },
};

let transport: ProvisionalTransport = restProvisionalTransport;

/** For tests: swap the transport (a recording proxy), or put the REST one back. */
export const setProvisionalTransport = (next?: ProvisionalTransport) => {
  transport = next ?? restProvisionalTransport;
};

export type KeepResult =
  | { ok: true; queued?: boolean }
  | { ok: false; reason: 'blocked' | 'nothing' | 'busy' | 'failed' | 'stale'; message?: string };

const inFlight = new Set<string>();

/** Anything but an HTTP answer, or a server-side failure: worth queueing and trying again. */
const isOffline = (error: unknown): boolean => !(error instanceof ApiError) || error.status >= 500;

/** Whether this row may be kept: anything still open. Accepted and added rows are successes, never keeps. */
export const keepable = (candidate: Pick<CandidateDto, 'decision' | 'createdItemId'>): boolean =>
  candidate.decision !== 'accepted' && !candidate.createdItemId;

/**
 * Keeps a T2 row as unverified. Uploads its crop first (when the phone still has it), then keeps; the retired row
 * merges into the cards store, so it leaves Review at once. Offline (or a 5xx): the keep is queued and the row stays
 * a visible T2 row until it lands. A viewer cannot: nothing is read and nothing is sent.
 */
export async function keepAsUnverified(args: {
  canEdit: boolean;
  libraryId: string;
  sessionId: string | null;
  candidate: CandidateDto;
  cropUri: string | undefined;
}): Promise<KeepResult> {
  const { canEdit, libraryId, sessionId, candidate, cropUri } = args;
  if (!canEdit || !keepable(candidate)) return { ok: false, reason: 'blocked' };
  if (!sessionId) return { ok: false, reason: 'nothing' };
  if (inFlight.has(candidate.id)) return { ok: false, reason: 'busy' };
  inFlight.add(candidate.id);
  try {
    let cropAssetId: string | undefined;
    if (cropUri) {
      let file: Blob;
      try {
        file = await readCrop(cropUri);
      } catch {
        file = null as never; // the crop is gone (pruned, wiped): the row is kept without it
      }
      if (file) {
        if (file.size > MAX_CROP_BYTES) return { ok: false, reason: 'failed', message: CROP_TOO_BIG };
        try {
          ({ assetId: cropAssetId } = await transport.uploadCrop(sessionId, candidate.id, file));
        } catch (error) {
          if (!isOffline(error)) throw error;
          getProvisionalOutbox().enqueue({ libraryId, sessionId, candidateId: candidate.id, cropUri });
          return { ok: true, queued: true };
        }
      }
    }
    try {
      const kept = await transport.keep(sessionId, candidate.id, cropAssetId);
      useCardsStore.getState().merge([kept.candidate], { announce: false });
      return { ok: true };
    } catch (error) {
      if (error instanceof ApiError && error.status === 409) return { ok: false, reason: 'stale' };
      if (!isOffline(error)) throw error;
      getProvisionalOutbox().enqueue({ libraryId, sessionId, candidateId: candidate.id, cropUri });
      return { ok: true, queued: true };
    }
  } catch (error) {
    return { ok: false, reason: 'failed', message: friendlyError(error, "Couldn't keep that row. It stays in Review.") };
  } finally {
    inFlight.delete(candidate.id);
  }
}

/** One keep that could not reach the server. The id is the candidate: keeping twice queues once. */
export interface QueuedKeep {
  libraryId: string;
  sessionId: string;
  candidateId: string;
  cropUri?: string;
  enqueuedAt: number;
}

const OUTBOX_KEY = 'provisional-outbox-v1';

export interface ProvisionalOutbox {
  list(libraryId: string): QueuedKeep[];
  enqueue(entry: Omit<QueuedKeep, 'enqueuedAt'>): void;
  remove(candidateId: string): void;
  clearLibrary(libraryId: string): void;
  clear(): void;
}

export function createProvisionalOutbox({ storage, now = Date.now }: { storage: KeyValueStore; now?: () => number }): ProvisionalOutbox {
  const load = (): QueuedKeep[] => {
    try {
      const parsed: unknown = JSON.parse(storage.getString(OUTBOX_KEY) ?? '[]');
      return Array.isArray(parsed) ? (parsed as QueuedKeep[]) : [];
    } catch {
      return [];
    }
  };
  const save = (entries: QueuedKeep[]) => {
    try {
      storage.set(OUTBOX_KEY, JSON.stringify(entries));
    } catch {
      // Storage full or unavailable: the queue holds for this run.
    }
  };
  return {
    list: (libraryId) => load().filter((e) => e.libraryId === libraryId),
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
    return createMMKV({ id: 'provisional-outbox' });
  } catch {
    return new MemoryKeyValueStore();
  }
}

let outbox: ProvisionalOutbox | undefined;

/** The keep outbox for this app run. Queued keeps survive the app being killed. */
export const getProvisionalOutbox = (): ProvisionalOutbox => (outbox ??= createProvisionalOutbox({ storage: openOutboxStorage() }));

export const setProvisionalOutbox = (next: ProvisionalOutbox | undefined) => {
  outbox = next;
};

/**
 * Sends one library's queued keeps, oldest first. A keep that lands merges its retired row; a 409 means it landed
 * before (a lost answer): the entry is dropped and the caller reloads. A 4xx drops the entry (retrying a refused
 * request never helps: the session is gone); offline or a 5xx stops the flush. Returns what is still queued.
 */
export async function flushProvisionalOutbox(args: { libraryId: string; onStale?: () => void }): Promise<number> {
  const queue = getProvisionalOutbox();
  const entries = queue.list(args.libraryId).sort((a, b) => a.enqueuedAt - b.enqueuedAt);
  for (const entry of entries) {
    let cropAssetId: string | undefined;
    if (entry.cropUri) {
      let file: Blob | null = null;
      try {
        file = await readCrop(entry.cropUri);
      } catch {
        file = null; // the crop is gone: the row is still kept
      }
      if (file && file.size <= MAX_CROP_BYTES) {
        try {
          ({ assetId: cropAssetId } = await transport.uploadCrop(entry.sessionId, entry.candidateId, file));
        } catch (error) {
          if (!isOffline(error)) {
            queue.remove(entry.candidateId);
            continue;
          }
          return queue.list(args.libraryId).length;
        }
      }
    }
    try {
      const kept = await transport.keep(entry.sessionId, entry.candidateId, cropAssetId);
      queue.remove(entry.candidateId);
      useCardsStore.getState().merge([kept.candidate], { announce: false });
    } catch (error) {
      if (error instanceof ApiError && error.status === 409) {
        queue.remove(entry.candidateId);
        args.onStale?.();
        continue;
      }
      if (error instanceof ApiError && error.status < 500) {
        queue.remove(entry.candidateId);
        continue;
      }
      return queue.list(args.libraryId).length;
    }
  }
  return 0;
}

/** The phone's own crop for this row, by the local id the server echoed. */
export function thumbForLocalId(snaps: readonly PicSnap[], localId: string | null): string | undefined {
  if (!localId) return undefined;
  for (const snap of snaps) {
    const thumb = snap.items.find((i) => i.localId === localId)?.thumbUri;
    if (thumb) return thumb;
  }
  return undefined;
}

/**
 * The crop a Review row pairs with its entry: the phone's own first (always there in-session), else the kept one
 * from the server (web review, or a phone whose crops are gone). Null when neither exists.
 */
export function pairCropUri(candidate: Pick<CandidateDto, 'cropUrl'>, localThumbUri?: string | null): string | null {
  if (localThumbUri) return localThumbUri;
  if (!candidate.cropUrl) return null;
  return ownArtworkUrl(candidate.cropUrl) ?? null;
}

export interface DroppedSegment {
  snapId: string;
  localId: string;
  candidateId: string;
}

/**
 * The provisional-or-counted invariant as a check: every sent item must have a visible row (in the cards store), an
 * open provisional, or an explicit dismissal. Unsent items are fine (the receipt shows them). Empty means nothing
 * was dropped.
 */
export function findDroppedSegments(
  snaps: readonly Pick<PicSnap, 'snapId' | 'items'>[],
  candidates: readonly Pick<CandidateDto, 'id'>[],
  provisionals: readonly Pick<ProvisionalDto, 'candidateId' | 'resolvedAt'>[],
  dismissed: ReadonlySet<string>,
): DroppedSegment[] {
  const rows = new Set(candidates.map((c) => c.id));
  const kept = new Set(provisionals.filter((p) => p.resolvedAt === null).map((p) => p.candidateId));
  const dropped: DroppedSegment[] = [];
  for (const snap of snaps) {
    for (const item of snap.items) {
      const candidateId = item.candidateId;
      if (!candidateId) continue;
      if (rows.has(candidateId) || kept.has(candidateId) || dismissed.has(candidateId)) continue;
      dropped.push({ snapId: snap.snapId, localId: item.localId, candidateId });
    }
  }
  return dropped;
}

/** Promotes a kept row to a real item. The crop stays its cover. */
export async function promoteKept(libraryId: string, provisionalId: string, key: string): Promise<ProvisionalDto> {
  const response = await promoteProvisional(libraryId, provisionalId, {}, { headers: { 'Idempotency-Key': key } });
  if (response.status !== 200) throw new ApiError(response.status, {});
  return response.data;
}

/** Links a kept row to the item it is. */
export async function resolveKept(libraryId: string, provisionalId: string, itemId: string): Promise<ProvisionalDto> {
  const response = await resolveProvisional(libraryId, provisionalId, { itemId });
  if (response.status !== 200) throw new ApiError(response.status, {});
  return response.data;
}

/** Deletes a kept row ("this isn't anything"), crop included. */
export async function deleteKept(libraryId: string, provisionalId: string): Promise<void> {
  const response = await deleteProvisional(libraryId, provisionalId);
  if (response.status !== 204) throw new ApiError(response.status, {});
}
