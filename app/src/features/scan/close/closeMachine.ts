// Closing the scanner (docs/06 M3) as a small state machine, so the rules are testable without a screen.
// With nothing scanned it leaves at once. Otherwise it asks: Review, Keep for later, or Discard.
export type CloseState = { phase: 'idle' | 'asking' | 'discarding' | 'failed'; error?: string };

export type CloseEvent =
  | { type: 'close'; unreviewed: number }
  | { type: 'choose'; choice: 'review' | 'keep' | 'discard' }
  | { type: 'discarded' }
  | { type: 'discardFailed'; message: string }
  | { type: 'dismiss' };

/** What the screen must do now. `discard` abandons the session and clears the tray, then sends `discarded` or `discardFailed`. */
export type CloseEffect = 'leave' | 'review' | 'discard' | undefined;

export const initialClose: CloseState = { phase: 'idle' };

export function closeStep(state: CloseState, event: CloseEvent): { state: CloseState; effect: CloseEffect } {
  switch (event.type) {
    case 'close':
      return event.unreviewed > 0 ? { state: { phase: 'asking' }, effect: undefined } : { state: initialClose, effect: 'leave' };
    case 'choose':
      if (state.phase !== 'asking' && state.phase !== 'failed') return { state, effect: undefined };
      if (event.choice === 'review') return { state: initialClose, effect: 'review' };
      if (event.choice === 'keep') return { state: initialClose, effect: 'leave' };
      return { state: { phase: 'discarding' }, effect: 'discard' };
    case 'discarded':
      return { state: initialClose, effect: 'leave' };
    case 'discardFailed':
      return { state: { phase: 'failed', error: event.message }, effect: undefined };
    case 'dismiss':
      return state.phase === 'discarding' ? { state, effect: undefined } : { state: initialClose, effect: undefined };
  }
}
