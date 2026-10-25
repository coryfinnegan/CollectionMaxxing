// The scan session's context: where its items go, their status, what is being scanned and where.
// Set once per session in the SessionSheet and inherited by every capture; the last home location and status per
// library are remembered (MMKV), so a fresh session re-offers them. "Not placed" is always an explicit choice, never silence.
import type { ItemStatus, ScanChannel, ScanMediaHint, ScanSessionDto } from '@/api/generated/models';
import { MemoryKeyValueStore, type KeyValueStore } from '../tray/trayStore';

export type SessionMediaHint = Exclude<ScanMediaHint, null>;
export type SessionChannel = Exclude<ScanChannel, null>;
export type SessionItemStatus = Extract<ItemStatus, 'owned' | 'wishlist'>;

export type SessionContext = {
  /** Explicit: null is "Not placed", a choice the sheet shows, not a missing answer. */
  locationId: string | null;
  status: SessionItemStatus;
  mediaHint: SessionMediaHint;
  channel: SessionChannel;
};

/** What is remembered per library: the last home location and status. Media and channel start fresh every session. */
export type SessionMemory = { locationId: string | null; status: SessionItemStatus };

export const SESSION_STATUS_LABEL: Record<SessionItemStatus, string> = { owned: 'Owned', wishlist: 'Wishlist' };
export const SESSION_MEDIA_LABEL: Record<SessionMediaHint, string> = { books: 'Books', games: 'Games', movies: 'Movies', mixed: 'Mixed' };
export const SESSION_CHANNEL_LABEL: Record<SessionChannel, string> = { home: 'Home', in_store: 'In-store' };

const memoryKey = (libraryId: string) => `session-context-v1:${libraryId}`;
const LIBRARIES_KEY = 'session-context-v1:libraries';

function openStorage(): KeyValueStore {
  try {
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const { createMMKV } = require('react-native-mmkv') as typeof import('react-native-mmkv');
    return createMMKV({ id: 'session-context' });
  } catch {
    return new MemoryKeyValueStore();
  }
}

let storage: KeyValueStore | undefined;
const backend = (): KeyValueStore => (storage ??= openStorage());

function readLibraries(store: KeyValueStore): string[] {
  try {
    const parsed: unknown = JSON.parse(store.getString(LIBRARIES_KEY) ?? '[]');
    return Array.isArray(parsed) ? parsed.filter((x): x is string => typeof x === 'string') : [];
  } catch {
    return [];
  }
}

export function loadSessionMemory(libraryId: string): SessionMemory | null {
  try {
    const raw = backend().getString(memoryKey(libraryId));
    if (!raw) return null;
    const parsed = JSON.parse(raw) as Partial<SessionMemory>;
    if (parsed.locationId !== null && typeof parsed.locationId !== 'string') return null;
    if (parsed.status !== 'owned' && parsed.status !== 'wishlist') return null;
    return { locationId: parsed.locationId, status: parsed.status };
  } catch {
    return null; // a damaged entry is the same as nothing remembered
  }
}

/**
 * Remembers a finished sheet choice. Only home sessions count: an in-store scan must not move the last home spot,
 * which is where its Review starts.
 */
export function rememberSessionContext(libraryId: string, context: SessionContext): void {
  if (context.channel !== 'home') return;
  try {
    backend().set(memoryKey(libraryId), JSON.stringify({ locationId: context.locationId, status: context.status }));
    const ids = readLibraries(backend());
    if (!ids.includes(libraryId)) backend().set(LIBRARIES_KEY, JSON.stringify([...ids, libraryId]));
  } catch {
    // Storage full or unavailable: this session still works.
  }
}

/** What a fresh session in this library starts from: the last home spot, or a blank home scan. */
export function defaultSessionContext(libraryId: string): SessionContext {
  const memory = loadSessionMemory(libraryId);
  return { locationId: memory?.locationId ?? null, status: memory?.status ?? 'owned', mediaHint: 'mixed', channel: 'home' };
}

/** Sign-out, unpair and account deletion: forget every library's spot. */
export function clearSessionContextData() {
  const store = backend();
  for (const id of readLibraries(store)) store.remove(memoryKey(id));
  store.remove(LIBRARIES_KEY);
}

/** Tests swap in their own storage (and start clean). */
export function setSessionContextStorage(next: KeyValueStore | undefined) {
  storage = next;
}

const reviewStatus = (status: ItemStatus | null): SessionItemStatus | undefined =>
  status === 'owned' || status === 'wishlist' ? status : undefined;

/**
 * What Review starts from: the session's own context first (it is the cross-device truth), the remembered home spot
 * when the session names none. An in-store session always starts at the last home spot, with a banner saying the
 * locations are decided later.
 */
export function resolveReviewDefaults(
  session: Pick<ScanSessionDto, 'defaultLocationId' | 'defaultStatus' | 'channel'>,
  memory: SessionMemory | null,
): { addTo: string | null; status: SessionItemStatus; inStore: boolean } {
  return {
    addTo: session.defaultLocationId ?? memory?.locationId ?? null,
    status: reviewStatus(session.defaultStatus) ?? memory?.status ?? 'owned',
    inStore: session.channel === 'in_store',
  };
}
