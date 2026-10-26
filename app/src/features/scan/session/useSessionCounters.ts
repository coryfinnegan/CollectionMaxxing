// The bar's numbers as a hook: the session's last snapshot plus what the phone holds that the server has not seen yet.
import { useMemo } from 'react';
import { useStore } from 'zustand';
import { getPipelineStore, type PicSnap } from '../pipeline/pipelineStore';
import { getTrayStore, useTrayValue } from '../tray/storage';
import type { TrayState } from '../tray/tray';
import { getSessionCountersStore, mergeCounters, ZERO_COUNTERS, type SessionCounters } from './sessionCounters';

/** What the phone holds that the server has not seen: unsent codes plus items of unsent snaps (a snap counts once it exists). */
export function pendingLocalCount(tray: Pick<TrayState, 'lines'>, snaps: PicSnap[], libraryId: string): number {
  const codes = tray.lines.filter((l) => l.sentCopies < l.copies).length;
  const pictures = snaps
    .filter((s) => s.libraryId === libraryId && !s.sent)
    .reduce((sum, s) => sum + Math.max(1, s.items.length), 0);
  return codes + pictures;
}

export function useSessionCounters(
  libraryId: string,
  sessionId: string | null,
): { counters: SessionCounters; offline: boolean; hasSnapshot: boolean } {
  const tray = getTrayStore(libraryId);
  const lines = useTrayValue(tray, (s) => s.lines, []);
  const online = useTrayValue(tray, (s) => s.online, true);
  const snaps = useStore(getPipelineStore(), (s) => s.snaps);
  const snapshot = useStore(getSessionCountersStore(), (s) => (sessionId ? s.snapshots[sessionId] : undefined));
  return useMemo(() => {
    const counters = mergeCounters(snapshot?.counters ?? ZERO_COUNTERS, pendingLocalCount({ lines }, snaps, libraryId));
    return { counters, offline: !online, hasSnapshot: !!snapshot };
  }, [snapshot, lines, snaps, libraryId, online]);
}
