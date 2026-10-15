// The hooks the scanner and the item screens use: `useOwnedLookup` answers "do I own this?" (server first, the local copy
// when offline) and `useOwnedSync` keeps the local copy fresh. Wiring them into the scanner screen is the lead's step.
import { onlineManager, useQueryClient } from '@tanstack/react-query';
import { useCallback, useEffect, useMemo, useSyncExternalStore } from 'react';
import { AppState } from 'react-native';
import { useGetOwnership } from '@/api/generated/ownership/ownership';
import type { GetOwnershipParams } from '@/api/generated/models';
import { useLibraryStore } from '@/state/library';
import { NOT_IN_LIBRARY, staleCopyMessage } from './copy';
import { getOwnedCache } from './ownedCache';
import type { OwnedCopy, OwnedLookup, OwnedQuery } from './ownedIndex';
import { syncLibrary } from './sync';

const noopSubscribe = () => () => {};

function useOnline(): boolean {
  return useSyncExternalStore(
    (listener) => onlineManager.subscribe(listener),
    () => onlineManager.isOnline(),
    () => true,
  );
}

function useCacheState(libraryId: string | null) {
  const cache = libraryId ? getOwnedCache(libraryId) : null;
  const subscribe = useCallback((listener: () => void) => (cache ? cache.subscribe(listener) : noopSubscribe()), [cache]);
  return useSyncExternalStore(subscribe, () => cache?.state ?? null, () => null);
}

export type OwnedAnswer = OwnedLookup & {
  /** Where the answer came from. The server wins whenever it answers. */
  source: 'server' | 'local';
  /** True once there is an answer and nothing is owned: show "Not in your library". */
  notInLibrary: boolean;
  notInLibraryText: string;
  /** Shown on the card when the local copy answered, e.g. "Based on your last sync, 2 h ago"; otherwise null. */
  staleMessage: string | null;
  /** When the local copy last finished a sync. */
  syncedAt: number | null;
};

const toServerKey = (query: OwnedQuery): GetOwnershipParams | null => {
  const q = typeof query === 'string' ? { code: query } : query;
  if (q.releaseId) return { releaseId: q.releaseId };
  if (q.code?.trim()) return { code: q.code };
  if (q.workId) return { workId: q.workId };
  return null;
};

const fromServer = (c: { itemId: string; releaseId: string; workId: string; title: string; format: string; platform?: string | null; locationPath?: string | null }): OwnedCopy => ({
  itemId: c.itemId,
  releaseId: c.releaseId,
  workId: c.workId,
  title: c.title,
  format: c.format,
  platform: c.platform ?? null,
  locationPath: c.locationPath ?? null,
});

/**
 * "Do I own this?" for a scanned code, a release or a work. The answer from the local copy is there at once; when the
 * server answers it replaces it. Offline (or if the server cannot be reached) the local copy answers and
 * `staleMessage` says how old it is. Works for every role: it only reads.
 */
export function useOwnedLookup(query: OwnedQuery | null, now: () => number = Date.now): OwnedAnswer {
  const libraryId = useLibraryStore((s) => s.activeLibraryId);
  const state = useCacheState(libraryId);
  const online = useOnline();
  const params = query ? toServerKey(query) : null;
  const server = useGetOwnership(libraryId ?? '', params ?? {}, {
    query: { enabled: online && !!libraryId && !!params, retry: false },
  });

  const local = useMemo<OwnedLookup>(
    () => (state && query ? state.index.lookup(query) : { owned: [], softMatches: [] }),
    [state, query],
  );

  const answer = server.data?.status === 200 ? server.data.data : null;
  if (answer) {
    const owned = answer.copies.map(fromServer);
    const softMatches = answer.sameWorkOtherFormat.map(fromServer);
    return { owned, softMatches, source: 'server', notInLibrary: owned.length === 0, notInLibraryText: NOT_IN_LIBRARY, staleMessage: null, syncedAt: state?.syncedAt ?? null };
  }

  const waiting = online && !!params && (server.isPending || server.isFetching) && !server.isError;
  return {
    ...local,
    source: 'local',
    notInLibrary: !waiting && !!query && local.owned.length === 0,
    notInLibraryText: NOT_IN_LIBRARY,
    staleMessage: query && !waiting ? staleCopyMessage(state?.syncedAt ?? null, now()) : null,
    syncedAt: state?.syncedAt ?? null,
  };
}

const RETRY_MS = [15_000, 30_000, 60_000, 120_000, 300_000];

/** Mutations whose success changes what the library owns; the copy syncs after each. */
const OWNERSHIP_MUTATIONS = new Set(['createItem', 'deleteItem', 'restoreItem', 'updateItem', 'bulkUpdateItems', 'commitScanSession', 'decideCandidate']);

/**
 * Keeps the local copy fresh: syncs on start, on coming to the foreground, and after the user adds or deletes items. A
 * failed sync keeps the old copy and tries again quietly, a little later each time. Mount it once, high in the tree.
 */
export function useOwnedSync(): { syncNow: () => Promise<void> } {
  const libraryId = useLibraryStore((s) => s.activeLibraryId);
  const queryClient = useQueryClient();

  const syncNow = useCallback(async () => {
    if (libraryId) await syncLibrary(libraryId);
  }, [libraryId]);

  useEffect(() => {
    if (!libraryId) return;
    let stopped = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let failures = 0;

    const run = async () => {
      clearTimeout(timer);
      const result = await syncLibrary(libraryId);
      if (stopped) return;
      if (result.ok) {
        failures = 0;
      } else {
        timer = setTimeout(() => void run(), RETRY_MS[Math.min(failures, RETRY_MS.length - 1)]);
        failures += 1;
      }
    };

    void run();
    const appState = AppState.addEventListener('change', (next) => {
      if (next === 'active') void run();
    });
    const mutations = queryClient.getMutationCache().subscribe((event) => {
      if (event.type !== 'updated' || event.action.type !== 'success') return;
      const name = event.mutation.options.mutationKey?.[0];
      if (typeof name === 'string' && OWNERSHIP_MUTATIONS.has(name)) void run();
    });

    return () => {
      stopped = true;
      clearTimeout(timer);
      appState.remove();
      mutations();
    };
  }, [libraryId, queryClient]);

  return { syncNow };
}
