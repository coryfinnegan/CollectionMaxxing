// Runs the close-scanner machine: reads the tray, abandons the session on Discard, and navigates.
import { useRef, useState } from 'react';
import type { StoreApi } from 'zustand/vanilla';
import { abandonScanSession } from '@/api/generated/scanning/scanning';
import { ApiError } from '@/lib/http';
import { friendlyError } from '@/lib/problem';
import { useCardsStore } from '../cards/cardsStore';
import { clearPipelineData } from '../pipeline/clearPipelineData';
import { getPipelineStore } from '../pipeline/pipelineStore';
import { getTrayStore } from '../tray/storage';
import type { TrayStore } from '../tray/trayStore';
import { closeStep, initialClose, type CloseEffect, type CloseEvent, type CloseState } from './closeMachine';

type Deps = { store?: StoreApi<TrayStore>; libraryId?: string; onLeave(): void; onReview(): void };

/** How many scanned items have not been added yet: barcode copies, and the items read from this library's pictures. */
export const unreviewedCount = (store: StoreApi<TrayStore>, libraryId: string | undefined) =>
  store.getState().lines.reduce((sum, l) => sum + l.copies, 0) +
  getPipelineStore()
    .getState()
    .snaps.filter((s) => libraryId === undefined || s.libraryId === libraryId)
    .reduce((sum, s) => sum + s.items.length, 0);

export function useCloseScanner({ store, libraryId, onLeave, onReview }: Deps) {
  // Without a tray (no library yet) there is nothing waiting: Close just leaves.
  const tray = store ?? (libraryId ? getTrayStore(libraryId) : undefined);
  const [state, setState] = useState<CloseState>(initialClose);
  // The machine's latest state, so two quick taps in one frame are judged against the real current phase.
  const current = useRef<CloseState>(initialClose);

  function dispatch(event: CloseEvent) {
    const next = closeStep(current.current, event);
    current.current = next.state;
    setState(next.state);
    run(next.effect);
  }

  function run(effect: CloseEffect) {
    if (effect === 'leave') onLeave();
    else if (effect === 'review') onReview();
    else if (effect === 'discard') void discard();
  }

  async function discard() {
    const sessionId = tray?.getState().sessionId;
    try {
      if (sessionId) await abandonScanSession(sessionId);
    } catch (error) {
      // A viewer cannot abandon (403) and a session that is already gone (404) needs nothing; anything else is worth another try.
      if (!(error instanceof ApiError && (error.status === 403 || error.status === 404))) {
        dispatch({ type: 'discardFailed', message: `${friendlyError(error)} Your scan is still here.` });
        return;
      }
    }
    tray?.getState().clear();
    useCardsStore.getState().reset();
    await clearPipelineData(libraryId).catch(() => undefined); // no still, crop or box of this library outlives a discarded scan
    dispatch({ type: 'discarded' });
  }

  const unreviewed = tray ? unreviewedCount(tray, libraryId) : 0;
  return {
    state,
    request: () => dispatch({ type: 'close', unreviewed }),
    choose: (choice: 'review' | 'keep' | 'discard') => dispatch({ type: 'choose', choice }),
    dismiss: () => dispatch({ type: 'dismiss' }),
    unreviewed,
  };
}
