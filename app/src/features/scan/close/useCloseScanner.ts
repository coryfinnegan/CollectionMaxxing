// Runs the close-scanner machine: reads the tray, abandons the session on Discard, and navigates.
import { useRef, useState } from 'react';
import type { StoreApi } from 'zustand/vanilla';
import { abandonScanSession } from '@/api/generated/scanning/scanning';
import { ApiError } from '@/lib/http';
import { friendlyError } from '@/lib/problem';
import { useCardsStore } from '../cards/cardsStore';
import { getTrayStore } from '../tray/storage';
import type { TrayStore } from '../tray/trayStore';
import { closeStep, initialClose, type CloseEffect, type CloseEvent, type CloseState } from './closeMachine';

type Deps = { store?: StoreApi<TrayStore>; onLeave(): void; onReview(): void };

/** How many scanned items have not been added yet (copies count). */
export const unreviewedCount = (store: StoreApi<TrayStore>) => store.getState().lines.reduce((sum, l) => sum + l.copies, 0);

export function useCloseScanner({ store = getTrayStore(), onLeave, onReview }: Deps) {
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
    const sessionId = store.getState().sessionId;
    try {
      if (sessionId) await abandonScanSession(sessionId);
    } catch (error) {
      // A viewer cannot abandon (403) and a session that is already gone (404) needs nothing; anything else is worth another try.
      if (!(error instanceof ApiError && (error.status === 403 || error.status === 404))) {
        dispatch({ type: 'discardFailed', message: `${friendlyError(error)} Your scan is still here.` });
        return;
      }
    }
    store.getState().clear();
    useCardsStore.getState().reset();
    dispatch({ type: 'discarded' });
  }

  return {
    state,
    request: () => dispatch({ type: 'close', unreviewed: unreviewedCount(store) }),
    choose: (choice: 'review' | 'keep' | 'discard') => dispatch({ type: 'choose', choice }),
    dismiss: () => dispatch({ type: 'dismiss' }),
    unreviewed: unreviewedCount(store),
  };
}
