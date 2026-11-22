// Wakes the server's shelf reader when the scanner opens (#832). The reader frees the graphics card after five idle minutes, so a
// first snap of a session would otherwise pay the model load (about 50 s). The call is fire and forget: nothing waits for it, nothing
// shows, and no failure reaches the camera.
import { warmShelfReader } from '@/api/generated/recognition/recognition';

/** The reader sleeps after five idle minutes; a scanner closed for that long needs waking again. */
export const WARM_AGAIN_AFTER_MS = 5 * 60 * 1000;

export interface ReaderWarmer {
  /** The scanner came to the front. Warms the reader when the server reads shelves and it has not been warmed this session of use. */
  opened(): void;
  /** The scanner went away (another tab, the app in the background, closed). */
  closed(): void;
}

export interface ReaderWarmerDeps {
  /** Whether the server reads shelves (the recognition config's `shelfReader`). Read at each open: the config may arrive late. */
  enabled: () => boolean;
  /** Sends the warm-up. Its failure is swallowed. */
  send: () => Promise<unknown>;
  now: () => number;
}

export function createReaderWarmer({ enabled, send, now }: ReaderWarmerDeps): ReaderWarmer {
  let warmedYet = false;
  let showing = 0; // scanner screens showing now: the Scan tab and the modal may overlap
  let closedAt: number | undefined;
  return {
    opened() {
      const away = showing === 0 && closedAt !== undefined ? now() - closedAt : 0;
      showing += 1;
      try {
        if (!enabled()) return;
        if (warmedYet && away < WARM_AGAIN_AFTER_MS) return;
        warmedYet = true;
        void Promise.resolve(send()).catch(() => undefined);
      } catch {
        // A throw while reading the config or starting the call is as unimportant as a failure of the call: the camera never hears of it.
      }
    },
    closed() {
      showing = Math.max(0, showing - 1);
      if (showing === 0) closedAt = now();
    },
  };
}

let shared: ReaderWarmer | undefined;

/** The app's one warmer, shared by every scanner screen, so the five-minute rule holds across the Scan tab and the modal. */
export function appReaderWarmer(enabled: () => boolean): ReaderWarmer {
  shared ??= createReaderWarmer({ enabled, send: () => warmShelfReader(), now: Date.now });
  return shared;
}

/** Forgets what was warmed (tests). */
export function resetAppReaderWarmer(): void {
  shared = undefined;
}
