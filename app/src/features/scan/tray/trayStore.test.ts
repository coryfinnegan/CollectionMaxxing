import type { Batch } from './tray';
import { createTrayStore, flushTray, MemoryKeyValueStore, type FlushDeps } from './trayStore';

const code = (value: string) => ({ value, symbology: 'ean-13' });

const make = (storage = new MemoryKeyValueStore()) => {
  let n = 0;
  return { storage, store: createTrayStore({ storage, newKey: () => `key-${++n}`, now: () => 1000 }) };
};

const deps = (overrides: Partial<FlushDeps> = {}) => {
  const sent: { sessionId: string; batch: Batch }[] = [];
  const value: FlushDeps = {
    createSession: async () => 'session-1',
    sendBatch: async (sessionId, batch) => void sent.push({ sessionId, batch }),
    classify: () => ({ offline: true }),
    ...overrides,
  };
  return { sent, deps: value };
};

describe('tray store', () => {
  it('writes every change to storage and a new store reads it back (a killed app resumes)', async () => {
    const { store, storage } = make();
    store.getState().scan(code('A'));
    store.getState().scan(code('B'));
    store.getState().plusOne('A');
    store.getState().plan();
    store.getState().setSessionId('session-9');

    const restarted = make(storage).store.getState();
    expect(restarted.lines.map((l) => [l.code, l.copies])).toEqual([['A', 2], ['B', 1]]);
    expect(restarted.outbox).toHaveLength(1);
    expect(restarted.sessionId).toBe('session-9');
  });

  it('starts empty when the stored data is damaged', () => {
    const storage = new MemoryKeyValueStore();
    storage.set('scan-tray-v1', '{not json');
    expect(make(storage).store.getState().lines).toEqual([]);
  });

  it('sends a batch with its idempotency key and marks it sent', async () => {
    const { store } = make();
    store.getState().scan(code('A'));
    const { sent, deps: d } = deps();
    await flushTray(store, d);
    expect(sent.map((s) => [s.sessionId, s.batch.key])).toEqual([['session-1', 'key-1']]);
    expect(store.getState().outbox).toEqual([]);
    expect(store.getState().lines[0]?.sentCopies).toBe(1);
  });

  it('retries a failed batch with the same key, so the server never sees it as new', async () => {
    const { store } = make();
    store.getState().scan(code('A'));
    const keys: string[] = [];
    let attempts = 0;
    const d = deps({
      sendBatch: async (_s, batch) => {
        keys.push(batch.key);
        if (++attempts === 1) throw new TypeError('Network request failed'); // the reply was lost
      },
    }).deps;

    await flushTray(store, d);
    expect(store.getState().online).toBe(false);
    expect(store.getState().outbox).toHaveLength(1);

    await flushTray(store, d);
    expect(keys).toEqual(['key-1', 'key-1']);
    expect(store.getState().online).toBe(true);
    expect(store.getState().outbox).toEqual([]);
  });

  it('creates the session only once, even across failed attempts', async () => {
    const { store } = make();
    store.getState().scan(code('A'));
    let created = 0;
    let fail = true;
    const d = deps({
      createSession: async () => {
        created++;
        return 'session-1';
      },
      sendBatch: async () => {
        if (fail) throw new TypeError('offline');
      },
    }).deps;
    await flushTray(store, d);
    fail = false;
    await flushTray(store, d);
    expect(created).toBe(1);
  });

  it('runs one flush at a time', async () => {
    const { store } = make();
    store.getState().scan(code('A'));
    const { sent, deps: d } = deps();
    await Promise.all([flushTray(store, d), flushTray(store, d), flushTray(store, d)]);
    expect(sent).toHaveLength(1);
  });

  it('keeps going online with a friendly message for a refusal, and forgets a session the server lost', async () => {
    const { store } = make();
    store.getState().scan(code('A'));
    store.getState().setSessionId('gone');
    const d = deps({
      sendBatch: async () => {
        throw new Error('404');
      },
      classify: () => ({ offline: false, message: "That's no longer here. Refresh and try again.", resetSession: true }),
    }).deps;
    await flushTray(store, d);
    expect(store.getState().sessionId).toBeNull();
    expect(store.getState().online).toBe(true);
    expect(store.getState().problem).toMatch(/no longer here/);
  });

  it('after a commit closes the session, the scans still waiting go to a new session under their own key', async () => {
    const { store, storage } = make();
    store.getState().setSessionId('session-1');
    store.getState().scan(code('SENT'));
    store.getState().plan();
    store.getState().confirm('key-1');
    store.getState().scan(code('WAITING'));
    store.getState().plan();

    store.getState().closeSession();

    const restarted = make(storage).store; // what is left is saved, so a killed app finds it
    expect(restarted.getState().lines.map((l) => l.code)).toEqual(['WAITING']);
    expect(restarted.getState().sessionId).toBeNull();
    const { sent, deps: d } = deps({ createSession: async () => 'session-2' });
    await flushTray(restarted, d);
    expect(sent.map((s) => [s.sessionId, s.batch.key, s.batch.entries.map((e) => e.value)])).toEqual([['session-2', 'key-2', ['WAITING']]]);
    expect(restarted.getState().lines.map((l) => l.sentCopies)).toEqual([1]);
  });

  it('sends at most 50 codes per request', async () => {
    const { store } = make();
    for (let i = 0; i < 120; i++) store.getState().scan(code(`code-${i}`));
    const { sent, deps: d } = deps();
    await flushTray(store, d);
    expect(sent.map((s) => s.batch.entries.length)).toEqual([50, 50, 20]);
  });
});
