// The receipt tray as plain data and pure functions. It knows nothing about cameras, React or the network.
// A "line" is one barcode the phone has read, with how many copies were scanned. Lines are sent to the
// server in batches (the "outbox"); a batch keeps its Idempotency-Key until the server confirms it, so a
// retry after a dropped connection can never create the same candidates twice.

export const MAX_BATCH = 50;
/** The same code seen again within this long adds nothing and makes no noise. */
export const DEFAULT_COOLDOWN_MS = 5000;

export type TrayLine = {
  code: string;
  symbology: string;
  /** How many copies were scanned (an explicit +1 adds one). */
  copies: number;
  /** How many of those copies the server has confirmed. */
  sentCopies: number;
  addedAt: number;
  lastSeenAt: number;
};

export type BatchEntry = { value: string; symbology: string; copies: number };
export type Batch = { key: string; entries: BatchEntry[] };

export type TrayState = {
  lines: TrayLine[];
  outbox: Batch[];
  /** The server's scan session, once one exists. Persisted so a killed app resumes the same session. */
  sessionId: string | null;
};

export const emptyTray = (): TrayState => ({ lines: [], outbox: [], sessionId: null });

export type ScanResult = 'added' | 'ignored' | 'duplicate';

/**
 * A code the stability filter let through.
 * - new code: a line is added.
 * - same code again inside the cooldown: ignored silently.
 * - same code again after the cooldown: `duplicate` (the screen can say so); no copy is added without +1.
 */
export function scanCode(
  state: TrayState,
  code: { value: string; symbology: string },
  now: number,
  cooldownMs: number = DEFAULT_COOLDOWN_MS,
): { state: TrayState; result: ScanResult } {
  const existing = state.lines.find((l) => l.code === code.value);
  if (!existing) {
    const line: TrayLine = {
      code: code.value,
      symbology: code.symbology,
      copies: 1,
      sentCopies: 0,
      addedAt: now,
      lastSeenAt: now,
    };
    return { state: { ...state, lines: [...state.lines, line] }, result: 'added' };
  }
  const within = now - existing.lastSeenAt < cooldownMs;
  const lines = state.lines.map((l) => (l === existing ? { ...l, lastSeenAt: now } : l));
  return { state: { ...state, lines }, result: within ? 'ignored' : 'duplicate' };
}

/** An explicit +1: a second copy of something already in the tray. */
export function addCopy(state: TrayState, code: string): TrayState {
  return { ...state, lines: state.lines.map((l) => (l.code === code ? { ...l, copies: l.copies + 1 } : l)) };
}

/** Swipe to remove. Copies that have not been sent are also dropped from waiting batches. */
export function removeLine(state: TrayState, code: string): TrayState {
  const outbox = state.outbox
    .map((b) => ({ ...b, entries: b.entries.filter((e) => e.value !== code) }))
    .filter((b) => b.entries.length > 0);
  return { ...state, lines: state.lines.filter((l) => l.code !== code), outbox };
}

const inOutbox = (state: TrayState, code: string) =>
  state.outbox.reduce((sum, b) => sum + b.entries.filter((e) => e.value === code).reduce((s, e) => s + e.copies, 0), 0);

/** Copies that are neither confirmed nor already waiting in a batch. */
export const unplannedCopies = (state: TrayState, line: TrayLine) =>
  line.copies - line.sentCopies - inOutbox(state, line.code);

/** Moves everything not yet planned into new batches of at most `MAX_BATCH` codes, each with its own key. */
export function planBatches(state: TrayState, newKey: () => string, max: number = MAX_BATCH): TrayState {
  const fresh: BatchEntry[] = state.lines
    .map((l) => ({ value: l.code, symbology: l.symbology, copies: unplannedCopies(state, l) }))
    .filter((e) => e.copies > 0);
  if (fresh.length === 0) return state;
  const batches: Batch[] = [];
  for (let i = 0; i < fresh.length; i += max) batches.push({ key: newKey(), entries: fresh.slice(i, i + max) });
  return { ...state, outbox: [...state.outbox, ...batches] };
}

/** The server confirmed this batch: forget it and count its copies as sent. */
export function markBatchSent(state: TrayState, key: string): TrayState {
  const batch = state.outbox.find((b) => b.key === key);
  if (!batch) return state;
  const lines = state.lines.map((l) => {
    const sent = batch.entries.filter((e) => e.value === l.code).reduce((s, e) => s + e.copies, 0);
    return sent > 0 ? { ...l, sentCopies: l.sentCopies + sent } : l;
  });
  return { ...state, lines, outbox: state.outbox.filter((b) => b.key !== key) };
}

/**
 * The session was committed and is closed. A line the server has in full is done and goes. What it never received stays,
 * counted from zero, with its waiting batches and their keys, and the next flush sends it to a new session: a commit
 * must never lose a scan the phone is still holding.
 */
export function closeSession(state: TrayState): TrayState {
  const lines = state.lines.filter((l) => l.sentCopies < l.copies).map((l) => ({ ...l, copies: l.copies - l.sentCopies, sentCopies: 0 }));
  return { lines, outbox: state.outbox, sessionId: null };
}

export type LineStatus = 'sent' | 'sending' | 'queued';

/** `queued` means it is waiting for a connection; `sending` means we are online and it is on its way. */
export const lineStatus = (line: TrayLine, online: boolean): LineStatus =>
  line.sentCopies >= line.copies ? 'sent' : online ? 'sending' : 'queued';

export const totalCopies = (state: TrayState) => state.lines.reduce((sum, l) => sum + l.copies, 0);
