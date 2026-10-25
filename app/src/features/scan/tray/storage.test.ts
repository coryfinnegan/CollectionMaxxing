// Each library has its own scan tray: unflushed scans (and their session) in Library A never appear in,
// flush from, or commit into Library B. Keyed blobs in one store, like the offline owned copy.
import { clearTrayData, getTrayStore, setTrayStorage, setTrayStore } from './storage';
import { flushTray, MemoryKeyValueStore, type FlushDeps } from './trayStore';

const A = 'aaaaaaaa-0000-4000-8000-000000000000';
const B = 'bbbbbbbb-0000-4000-8000-000000000000';
const code = (value: string) => ({ value, symbology: 'ean-13' });

let storage: MemoryKeyValueStore;

beforeEach(() => {
  storage = new MemoryKeyValueStore();
  setTrayStorage(storage);
});

afterEach(() => {
  setTrayStore(undefined);
});

describe("each library's tray", () => {
  it('keeps unflushed scans apart: switching libraries shows an empty tray, switching back restores it', () => {
    getTrayStore(A).getState().scan(code('from-A'));
    getTrayStore(A).getState().setSessionId('session-A');

    expect(getTrayStore(B).getState().lines).toEqual([]);
    expect(getTrayStore(B).getState().sessionId).toBeNull();

    expect(getTrayStore(A).getState().lines.map((l) => l.code)).toEqual(['from-A']);
    expect(getTrayStore(A).getState().sessionId).toBe('session-A');
  });

  it('flushes only the active library: nothing from A is sent while viewing B', async () => {
    getTrayStore(A).getState().scan(code('from-A'));
    getTrayStore(A).getState().setSessionId('session-A');
    const sent: string[] = [];
    const deps: FlushDeps = {
      createSession: async () => 'session-B',
      sendBatch: async (sessionId, batch) => void sent.push(`${sessionId}:${batch.entries.map((e) => e.value).join(',')}`),
      classify: () => ({ offline: true }),
    };

    await flushTray(getTrayStore(B), deps);

    expect(sent).toEqual([]);
    expect(getTrayStore(A).getState().lines.map((l) => l.code)).toEqual(['from-A']);
  });

  it('resumes each library separately after the app was killed', () => {
    getTrayStore(A).getState().scan(code('from-A'));
    getTrayStore(A).getState().setSessionId('session-A');
    getTrayStore(B).getState().scan(code('from-B'));

    setTrayStorage(storage); // a restart drops the live stores but keeps what was saved

    expect(getTrayStore(A).getState().lines.map((l) => l.code)).toEqual(['from-A']);
    expect(getTrayStore(A).getState().sessionId).toBe('session-A');
    expect(getTrayStore(B).getState().lines.map((l) => l.code)).toEqual(['from-B']);
  });

  it('keeps the pre-update tray for the library open after the update, then forgets the global copy', () => {
    storage.set('scan-tray-v1', JSON.stringify({ lines: [{ code: 'old', symbology: 'ean-13', copies: 1, sentCopies: 0 }], outbox: [], sessionId: 'session-old' }));

    expect(getTrayStore(A).getState().lines.map((l) => l.code)).toEqual(['old']);
    expect(getTrayStore(A).getState().sessionId).toBe('session-old');
    expect(storage.getString('scan-tray-v1')).toBeUndefined();
    expect(getTrayStore(B).getState().lines).toEqual([]);
  });

  it('forgets every library’s tray on sign-out, including libraries not opened in this run', () => {
    getTrayStore(A).getState().scan(code('from-A'));
    storage.set('scan-tray-v1:some-library-never-opened', JSON.stringify({ lines: [], outbox: [], sessionId: 'session-x' }));
    storage.set('scan-tray-v1:libraries', JSON.stringify([A, 'some-library-never-opened']));
    storage.set('scan-tray-v1', JSON.stringify({ lines: [], outbox: [], sessionId: 'session-legacy' }));

    clearTrayData();

    expect(getTrayStore(A).getState().lines).toEqual([]);
    expect(getTrayStore(A).getState().sessionId).toBeNull();
    expect(storage.getString('scan-tray-v1:some-library-never-opened')).toBeUndefined();
    expect(storage.getString('scan-tray-v1:libraries')).toBeUndefined();
    expect(storage.getString('scan-tray-v1')).toBeUndefined();
  });

  it('lets tests swap in one tray that stands in for every library', () => {
    const tray = getTrayStore(A);
    tray.getState().scan(code('from-A'));
    setTrayStore(tray);

    expect(getTrayStore(B)).toBe(tray);
  });
});
