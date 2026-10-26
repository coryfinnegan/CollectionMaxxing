// The phone's counter math: the server's snapshot plus the local queue, reconciling the same way the server's do.
import { MemoryKeyValueStore } from '../tray/trayStore';
import {
  createSessionCountersStore,
  getSessionCountersStore,
  mergeCounters,
  reconciles,
  seedSessionCounters,
  setSessionCountersStore,
  ZERO_COUNTERS,
  type SessionCounters,
} from './sessionCounters';

const SESSION = 'session-1';
const SNAPSHOT: SessionCounters = { captured: 10, added: 6, toReview: 2, owned: 1, skipped: 1 };

describe('counter math', () => {
  it('lays the local queue over the snapshot: captured and to-review grow, the rest stays server truth', () => {
    expect(mergeCounters(SNAPSHOT, 3)).toEqual({ captured: 13, added: 6, toReview: 5, owned: 1, skipped: 1 });
  });

  it('reconciles after the merge, the way the server reconciles at rest', () => {
    expect(reconciles(SNAPSHOT)).toBe(true);
    expect(reconciles(mergeCounters(SNAPSHOT, 3))).toBe(true);
    expect(reconciles(ZERO_COUNTERS)).toBe(true);
  });

  it('spots counters that do not reconcile', () => {
    expect(reconciles({ ...SNAPSHOT, captured: 11 })).toBe(false);
  });
});

describe('the counters store', () => {
  it('keeps the newest snapshot per session and ignores an older change counter', () => {
    const store = createSessionCountersStore({ storage: new MemoryKeyValueStore() });
    store.getState().seed(SESSION, SNAPSHOT, 9);
    store.getState().seed(SESSION, ZERO_COUNTERS, 4); // a replayed event must not zero the bar

    expect(store.getState().snapshots[SESSION]).toEqual({ counters: SNAPSHOT, changeSeq: 9 });
  });

  it('keeps sessions apart', () => {
    const store = createSessionCountersStore({ storage: new MemoryKeyValueStore() });
    store.getState().seed('a', SNAPSHOT, 1);
    store.getState().seed('b', ZERO_COUNTERS, 1);

    expect(store.getState().snapshots.a?.counters).toEqual(SNAPSHOT);
    expect(store.getState().snapshots.b?.counters).toEqual(ZERO_COUNTERS);
  });

  it('survives the app being killed', () => {
    const storage = new MemoryKeyValueStore();
    createSessionCountersStore({ storage }).getState().seed(SESSION, SNAPSHOT, 9);

    const restarted = createSessionCountersStore({ storage });
    expect(restarted.getState().snapshots[SESSION]).toEqual({ counters: SNAPSHOT, changeSeq: 9 });
  });

  it('starts empty on a damaged file', () => {
    const storage = new MemoryKeyValueStore();
    storage.set('session-counters-v1', '{nope');

    expect(createSessionCountersStore({ storage }).getState().snapshots).toEqual({});
  });
});

describe('seeding from the API', () => {
  beforeEach(() => setSessionCountersStore(createSessionCountersStore({ storage: new MemoryKeyValueStore() })));
  afterEach(() => setSessionCountersStore(undefined));

  it('seeds from a session snapshot', () => {
    seedSessionCounters(SESSION, { counters: SNAPSHOT, changeSeq: 9 });

    expect(getSessionCountersStore().getState().snapshots[SESSION]).toEqual({ counters: SNAPSHOT, changeSeq: 9 });
  });

  it('seeds nothing from a body without counters, rather than zeroing the bar', () => {
    seedSessionCounters(SESSION, { counters: SNAPSHOT, changeSeq: 9 });
    seedSessionCounters(SESSION, {});

    expect(getSessionCountersStore().getState().snapshots[SESSION]).toEqual({ counters: SNAPSHOT, changeSeq: 9 });
  });
});
