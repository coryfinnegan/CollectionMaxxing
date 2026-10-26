// The session remembers where its items go: the last home location and status per library are kept, so a fresh
// session re-offers them; an in-store scan never moves the home spot.
import { MemoryKeyValueStore } from '../tray/trayStore';
import {
  clearSessionContextData,
  defaultSessionContext,
  loadSessionMemory,
  rememberSessionContext,
  resolveReviewDefaults,
  sessionCreateBody,
  setSessionContextStorage,
  type SessionContext,
} from './sessionContext';

const LIB_A = 'aaaaaaaa-0000-4000-8000-000000000000';
const LIB_B = 'bbbbbbbb-0000-4000-8000-000000000000';
const SHELF = 'c0000000-0000-4000-8000-000000000001';

const home = (over: Partial<SessionContext> = {}): SessionContext => ({
  locationId: SHELF,
  status: 'owned',
  mediaHint: 'mixed',
  channel: 'home',
  autoAddConfident: false,
  ...over,
});

beforeEach(() => setSessionContextStorage(new MemoryKeyValueStore()));
afterEach(() => setSessionContextStorage(undefined));

describe('session memory', () => {
  it('re-offers the last home location and status for a fresh session', () => {
    rememberSessionContext(LIB_A, home({ locationId: SHELF, status: 'wishlist', mediaHint: 'games' }));

    expect(loadSessionMemory(LIB_A)).toEqual({ locationId: SHELF, status: 'wishlist' });
    expect(defaultSessionContext(LIB_A)).toEqual({ locationId: SHELF, status: 'wishlist', mediaHint: 'mixed', channel: 'home', autoAddConfident: false });
  });

  it('offers a blank home context when nothing was remembered', () => {
    expect(loadSessionMemory(LIB_A)).toBeNull();
    expect(defaultSessionContext(LIB_A)).toEqual({ locationId: null, status: 'owned', mediaHint: 'mixed', channel: 'home', autoAddConfident: false });
  });

  it('keeps an explicit Not placed instead of forgetting it', () => {
    rememberSessionContext(LIB_A, home({ locationId: null }));

    expect(loadSessionMemory(LIB_A)).toEqual({ locationId: null, status: 'owned' });
  });

  it('keeps each library apart', () => {
    rememberSessionContext(LIB_A, home({ locationId: SHELF, status: 'wishlist' }));

    expect(loadSessionMemory(LIB_B)).toBeNull();
    expect(defaultSessionContext(LIB_B)).toEqual({ locationId: null, status: 'owned', mediaHint: 'mixed', channel: 'home', autoAddConfident: false });
  });

  it('lets an in-store scan leave the last home spot alone', () => {
    rememberSessionContext(LIB_A, home({ locationId: SHELF, status: 'owned' }));
    rememberSessionContext(LIB_A, { locationId: null, status: 'wishlist', mediaHint: 'mixed', channel: 'in_store', autoAddConfident: false });

    expect(loadSessionMemory(LIB_A)).toEqual({ locationId: SHELF, status: 'owned' });
  });

  it('treats a damaged entry as nothing remembered', () => {
    const storage = new MemoryKeyValueStore();
    storage.set(`session-context-v1:${LIB_A}`, '{oops');
    storage.set(`session-context-v1:${LIB_B}`, JSON.stringify({ locationId: 42, status: 'lent' }));
    setSessionContextStorage(storage);

    expect(loadSessionMemory(LIB_A)).toBeNull();
    expect(loadSessionMemory(LIB_B)).toBeNull();
  });

  it('forgets every library on sign-out', () => {
    rememberSessionContext(LIB_A, home());
    rememberSessionContext(LIB_B, home({ status: 'wishlist' }));

    clearSessionContextData();

    expect(loadSessionMemory(LIB_A)).toBeNull();
    expect(loadSessionMemory(LIB_B)).toBeNull();
  });
});

describe('review defaults', () => {
  it('starts from the session location and status', () => {
    const defaults = resolveReviewDefaults({ defaultLocationId: SHELF, defaultStatus: 'wishlist', channel: 'home' }, null);

    expect(defaults).toEqual({ addTo: SHELF, status: 'wishlist', inStore: false });
  });

  it('falls back to the remembered home spot when the session has none', () => {
    const memory = { locationId: SHELF, status: 'wishlist' as const };

    expect(resolveReviewDefaults({ defaultLocationId: null, defaultStatus: null, channel: 'home' }, memory)).toEqual({
      addTo: SHELF,
      status: 'wishlist',
      inStore: false,
    });
  });

  it('marks an in-store session and starts it at the last home spot', () => {
    const memory = { locationId: SHELF, status: 'owned' as const };

    expect(resolveReviewDefaults({ defaultLocationId: null, defaultStatus: null, channel: 'in_store' }, memory)).toEqual({
      addTo: SHELF,
      status: 'owned',
      inStore: true,
    });
  });

  it('starts Not placed when neither the session nor memory names a spot', () => {
    expect(resolveReviewDefaults({ defaultLocationId: null, defaultStatus: null, channel: 'in_store' }, null)).toEqual({
      addTo: null,
      status: 'owned',
      inStore: true,
    });
  });

  it('reads a status only Review understands', () => {
    const memory = { locationId: null, status: 'wishlist' as const };

    expect(resolveReviewDefaults({ defaultLocationId: null, defaultStatus: 'lent', channel: 'home' }, memory).status).toBe('wishlist');
    expect(resolveReviewDefaults({ defaultLocationId: null, defaultStatus: 'lent', channel: 'home' }, null).status).toBe('owned');
  });
});

describe('session create body', () => {
  it('opts into T1 only when confident matches auto-add', () => {
    expect(sessionCreateBody(home()).autoCommitTiers).toEqual(['T0']);
    expect(sessionCreateBody(home({ autoAddConfident: true })).autoCommitTiers).toEqual(['T0', 'T1']);
  });

  it('carries the context Review starts from', () => {
    expect(sessionCreateBody(home({ locationId: SHELF, status: 'wishlist', mediaHint: 'games', channel: 'home' }))).toEqual({
      defaultLocationId: SHELF,
      defaultStatus: 'wishlist',
      mediaHint: 'games',
      channel: 'home',
      autoCommitTiers: ['T0'],
    });
  });
});
