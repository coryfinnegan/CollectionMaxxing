// The scanner's brain: camera codes -> stability filter -> tray (persisted) -> batches to the server, and the
// session's cards back via the event stream. It never touches a camera; it only knows a `ScannerSource`.
import { useEffect, useMemo, useRef, useState } from 'react';
import { useStore } from 'zustand';
import { useGetMe } from '@/api/generated/account/account';
import type { LibraryRole } from '@/api/generated/models';
import { haptics } from '@/lib/haptics';
import { shownCards, sortedCards, useCardsStore } from '../cards/cardsStore';
import type { ScannerSource } from '../scanner/ScannerSource';
import { createStabilityFilter } from '../tray/stability';
import { getTrayStore } from '../tray/storage';
import { flushTray, type TrayStore } from '../tray/trayStore';
import { lineStatus } from '../tray/tray';
import { flushDeps, followSession, loadSession, rejectCandidate, resumeCursor } from './sessionApi';
import type { StoreApi } from 'zustand/vanilla';

export type SessionTuning = { retryMs?: number; pollMs?: number; resendMs?: number; flashMs?: number };

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
  store = getTrayStore(),
  tuning = {},
}: {
  libraryId: string;
  sources: ScannerSource[];
  store?: StoreApi<TrayStore>;
  tuning?: SessionTuning;
}) {
  const { resendMs = 5000, flashMs = 1000 } = tuning;
  const deps = useMemo(() => flushDeps(libraryId), [libraryId]);
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

  // Camera -> tray. The "Nice scan!" tick happens here, before any network call.
  useEffect(() => {
    const filter = createStabilityFilter();
    const flush = () => void flushTray(store, deps);
    const unsubscribes = sources.map((source) =>
      source.start((seen) => {
        for (const value of filter.observe(seen.map((c) => c.value))) {
          const symbology = seen.find((c) => c.value === value)?.symbology ?? 'ean-13';
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
  }, [sources, store, deps]);

  // Anything waiting (including from before the app was killed) is sent now and retried until it gets through.
  useEffect(() => {
    void flushTray(store, deps);
    const timer = setInterval(() => void flushTray(store, deps), resendMs);
    return () => clearInterval(timer);
  }, [store, deps, resendMs]);

  // Follow the session: load what is there (a resumed session), then stream updates.
  useEffect(() => {
    if (!sessionId) return;
    let cancelled = false;
    let follower: { close(): void } | undefined;
    void (async () => {
      let cursor: string | undefined;
      try {
        const existing = await loadSession(sessionId);
        useCardsStore.getState().merge(existing, { announce: false });
        cursor = resumeCursor(existing);
      } catch {
        // The stream below will catch up; a failed snapshot is not fatal.
      }
      if (!cancelled) follower = followSession(sessionId, cursor, tuningRef.current);
    })();
    return () => {
      cancelled = true;
      follower?.close();
    };
  }, [sessionId]);

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
    /** Swipe to remove: gone from the tray and the cards, and rejected on the server so Review will not add it. */
    remove: (code: string) => {
      store.getState().remove(code);
      const { byId: current, remove } = useCardsStore.getState();
      for (const card of Object.values(current).filter((c) => c.rawCode === code)) {
        remove(card.id);
        if (sessionId) void rejectCandidate(sessionId, card.id).catch(() => undefined);
      }
    },
  };
}
