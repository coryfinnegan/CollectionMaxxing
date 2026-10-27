// The scanner's brain: camera codes -> stability filter -> tray (persisted) -> batches to the server, and the
// session's cards back via the event stream. It never touches a camera; it only knows a `ScannerSource`.
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo, useRef, useState } from 'react';
import { useStore } from 'zustand';
import { useGetMe } from '@/api/generated/account/account';
import type { CreateScanSessionRequest, LibraryRole } from '@/api/generated/models';
import { invalidateLibraryItems } from '@/features/item/useItem';
import { haptics } from '@/lib/haptics';
import { ApiError } from '@/lib/http';
import { friendlyError } from '@/lib/problem';
import { shownCards, sortedCards, useCardsStore } from '../cards/cardsStore';
import { unreadableCopy } from '../cards/copy';
import { isReadableBarcode } from '../scanner/barcodes';
import type { ScannerSource } from '../scanner/ScannerSource';
import { createStabilityFilter } from '../tray/stability';
import { getTrayStore } from '../tray/storage';
import { flushTray, type TrayStore } from '../tray/trayStore';
import { lineStatus } from '../tray/tray';
import { useAutoCommitUndoStore } from './autoCommitUndo';
import { flushDeps, followSession, loadSessionSnapshot, resumeCursor } from './sessionApi';
import { flushDiscardOutbox, getDiscardOutbox, pruneDiscarded, withoutDiscarded } from './discards';
import { seedFromSnapshot } from './sessionCounters';
import type { StoreApi } from 'zustand/vanilla';

export type SessionTuning = { retryMs?: number; pollMs?: number; resendMs?: number; flashMs?: number };

/**
 * What a failed bound read says: a refusal names its reason (a barcode of another work, or no permission to
 * change the library); anything else stays generic so the camera waits for another read.
 */
const boundRefusal = (error: unknown): string => {
  if (error instanceof ApiError && error.status === 409 && error.problem.detail) return error.problem.detail;
  if (error instanceof ApiError && error.status === 403) return friendlyError(error);
  return "Couldn't send that scan. Try again.";
};

/** What the signed-in member may do in this library. Unknown until `GET /me` answers, so Add stays hidden until then. */
export function useLibraryRole(libraryId: string | undefined): { role: LibraryRole | undefined; canAdd: boolean } {
  const me = useGetMe();
  const memberships = me.data?.status === 200 ? me.data.data.memberships : [];
  const role = memberships.find((m) => m.libraryId === libraryId)?.role;
  return { role, canAdd: role === 'owner' || role === 'editor' };
}

export function useScanSession({
  libraryId,
  sources,
  store = getTrayStore(libraryId),
  tuning = {},
  sessionBody,
  boundCapture,
}: {
  libraryId: string;
  sources: ScannerSource[];
  store?: StoreApi<TrayStore>;
  tuning?: SessionTuning;
  /** The session context a new session is created with. Keep it referentially stable unless the context changed. */
  sessionBody?: CreateScanSessionRequest;
  /** Review's "Scan barcode": stable reads go to this row instead of the tray, one read only. */
  boundCapture?: { submit(code: { value: string; symbology: string }): Promise<void> };
}) {
  const { resendMs = 5000, flashMs = 1000 } = tuning;
  const deps = useMemo(() => flushDeps(libraryId, () => sessionBody ?? {}), [libraryId, sessionBody]);
  const [niceScanAt, setNiceScanAt] = useState<number | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const tuningRef = useRef(tuning);
  useEffect(() => {
    tuningRef.current = tuning;
  });

  const lines = useStore(store, (s) => s.lines);
  const online = useStore(store, (s) => s.online);
  const problem = useStore(store, (s) => s.problem);
  const sessionId = useStore(store, (s) => s.sessionId);
  const byId = useCardsStore((s) => s.byId);
  const order = useCardsStore((s) => s.order);
  const local = useCardsStore((s) => s.local);
  const cards = useMemo(() => sortedCards(shownCards({ byId, local }), order), [byId, local, order]);
  const queryClient = useQueryClient();
  const lastAutoAdd = useAutoCommitUndoStore((s) => s.last);
  // What this mount has already refreshed for: an add from before it mounted was refreshed when it arrived.
  const refreshedAutoAdd = useRef(lastAutoAdd);
  useEffect(() => {
    // A T0 auto-add lands server-side with no client mutation (#448): the library list refreshes itself
    // the moment the add arrives, not when the tab is opened. Only this session's adds: a switched-to
    // library never refreshes for another library's scan.
    if (lastAutoAdd && lastAutoAdd !== refreshedAutoAdd.current && lastAutoAdd.sessionId === sessionId) {
      refreshedAutoAdd.current = lastAutoAdd;
      void invalidateLibraryItems(queryClient, libraryId);
    }
  }, [lastAutoAdd, libraryId, queryClient, sessionId]);

  // Camera -> tray, or to the bound Review row. The "Nice scan!" tick happens here, before any network call.
  const boundSending = useRef(false);
  useEffect(() => {
    boundSending.current = false;
    const filter = createStabilityFilter();
    const flush = () => void flushTray(store, deps);
    const unsubscribes = sources.map((source) =>
      source.start((seen) => {
        for (const value of filter.observe(seen.map((c) => c.value))) {
          const symbology = seen.find((c) => c.value === value)?.symbology ?? 'ean-13';
          if (!isReadableBarcode(value)) {
            // A partial read or not a product code at all: a rescan prompt, never queued (#383).
            setNotice(unreadableCopy());
            continue;
          }
          if (boundCapture) {
            if (boundSending.current) continue; // one read only; the screen leaves on success
            boundSending.current = true;
            void haptics.scanned();
            setNiceScanAt(Date.now());
            setNotice(null);
            boundCapture.submit({ value, symbology }).catch((error: unknown) => {
              boundSending.current = false;
              setNotice(boundRefusal(error));
            });
            continue;
          }
          const result = store.getState().scan({ value, symbology });
          if (result === 'added') {
            void haptics.scanned();
            setNiceScanAt(Date.now());
            setNotice(null);
            flush();
          } else if (result === 'duplicate') {
            setNotice('Already in this scan. Use +1 for another copy.');
          }
        }
      }),
    );
    return () => unsubscribes.forEach((off) => off());
  }, [sources, store, deps, boundCapture]);

  // Anything waiting (including from before the app was killed) is sent now and retried until it gets through.
  useEffect(() => {
    void flushTray(store, deps);
    const timer = setInterval(() => void flushTray(store, deps), resendMs);
    return () => clearInterval(timer);
  }, [store, deps, resendMs]);

  // Follow the session: queued discards from before the app was killed land first, then load what is there
  // (a resumed session), then stream updates. Discarded and rejected candidates never merge back into cards.
  useEffect(() => {
    if (!sessionId) return;
    let cancelled = false;
    let follower: { close(): void } | undefined;
    void (async () => {
      let cursor: string | undefined;
      await flushDiscardOutbox({ libraryId }).catch(() => 0);
      try {
        const snapshot = await loadSessionSnapshot(sessionId);
        pruneDiscarded(snapshot.candidates);
        // Discarded and rejected candidates have no card: the snapshot confirms them, it never brings them back.
        useCardsStore.getState().merge(
          withoutDiscarded(snapshot.candidates).filter((c) => c.decision !== 'rejected'),
          { announce: false },
        );
        seedFromSnapshot(sessionId, snapshot);
        cursor = resumeCursor(snapshot.candidates);
      } catch {
        // The stream below will catch up; a failed snapshot is not fatal.
      }
      if (!cancelled) follower = followSession(sessionId, cursor, tuningRef.current);
    })();
    return () => {
      cancelled = true;
      follower?.close();
    };
  }, [libraryId, sessionId]);

  // The "Nice scan!" flash goes away by itself.
  useEffect(() => {
    if (niceScanAt === null) return;
    const timer = setTimeout(() => setNiceScanAt(null), flashMs);
    return () => clearTimeout(timer);
  }, [niceScanAt, flashMs]);

  const items = lines.reduce((sum, l) => sum + l.copies, 0);
  const processing = lines.filter((l) => {
    if (lineStatus(l, online) !== 'sent') return true;
    return cards.some((c) => c.rawCode === l.code && c.resolution === 'pending');
  }).length;

  return {
    lines,
    cards,
    online,
    problem,
    sessionId,
    notice,
    showNiceScan: niceScanAt !== null,
    items,
    processing,
    plusOne: (code: string) => {
      store.getState().plusOne(code);
      void flushTray(store, deps);
    },
    /** Swipe to remove: gone from the tray and the cards, and queued for reject so Review will not add it — even after a restart. */
    remove: (code: string) => {
      store.getState().remove(code);
      const { byId: current, remove } = useCardsStore.getState();
      const queue = getDiscardOutbox();
      for (const card of Object.values(current).filter((c) => c.rawCode === code)) {
        remove(card.id);
        if (sessionId) queue.enqueue({ libraryId, sessionId, candidateId: card.id });
      }
      if (sessionId) void flushDiscardOutbox({ libraryId }).catch(() => undefined);
    },
  };
}
