import { MemoryKeyValueStore } from '../tray/trayStore';
import { MemorySnapFiles, SnapStore, type SnapFiles } from './snapStore';

const PHOTO = { uri: 'file:///cache/ImageManipulator/a.jpg', width: 2000, height: 1500 };
const DAY = 24 * 60 * 60 * 1000;

function build(now: { t: number }, retention?: { maxAgeMs: number; maxSnaps: number }, files: SnapFiles = new MemorySnapFiles()) {
  const storage = new MemoryKeyValueStore();
  const store = new SnapStore({ files, storage, now: () => now.t, retention });
  return { store, files, storage };
}

describe('the snap store', () => {
  it('keeps the still, and each item with its box, read text and crop', async () => {
    const { store, files } = build({ t: 1 });
    await store.saveStill('s1', PHOTO);
    await store.saveItem('s1', { localId: 'a', text: 'Dune', box: { x: 0.1, y: 0.1, width: 0.2, height: 0.5 } }, 'file:///cache/crop-a.jpg');
    await store.saveItem('s1', { localId: 'b', text: 'Emma' });

    const [snap] = store.list();
    expect(snap!.stillUri).toBe('file:///snaps/s1-still.jpg');
    expect(snap!.items).toEqual([
      { localId: 'a', text: 'Dune', box: { x: 0.1, y: 0.1, width: 0.2, height: 0.5 }, cropUri: 'file:///snaps/s1-0.jpg' },
      { localId: 'b', text: 'Emma' },
    ]);
    expect((files as MemorySnapFiles).files.size).toBe(2); // the still and one crop
  });

  it('comes back after a restart', async () => {
    const now = { t: 1 };
    const first = build(now);
    await first.store.saveStill('s1', PHOTO);
    const second = new SnapStore({ files: first.files, storage: first.storage, now: () => now.t });
    expect(second.get('s1')?.width).toBe(2000);
  });

  it('drops snaps older than a week', async () => {
    const now = { t: 0 };
    const { store, files } = build(now);
    await store.saveStill('old', PHOTO);
    now.t = 8 * DAY;
    await store.saveStill('new', PHOTO);
    expect(store.list().map((s) => s.snapId)).toEqual(['new']);
    expect([...(files as MemorySnapFiles).files.keys()]).toEqual(['new-still.jpg']);
  });

  it('keeps the newest 50 snaps', async () => {
    const now = { t: 1 };
    const { store } = build(now);
    for (let i = 0; i < 53; i += 1) {
      now.t += 1000;
      await store.saveStill(`s${i}`, PHOTO);
    }
    expect(store.list()).toHaveLength(50);
    expect(store.get('s0')).toBeUndefined();
    expect(store.get('s52')).toBeDefined();
  });

  it('forgets one snap and its files', async () => {
    const { store, files } = build({ t: 1 });
    await store.saveStill('s1', PHOTO);
    await store.saveItem('s1', { localId: 'a', text: 'Dune' }, 'file:///crop.jpg');
    await store.saveStill('s2', PHOTO);
    await store.forget('s1');
    expect(store.list().map((s) => s.snapId)).toEqual(['s2']);
    expect([...(files as MemorySnapFiles).files.keys()]).toEqual(['s2-still.jpg']);
  });

  it('wipes every still, crop and box, and the index', async () => {
    const { store, files, storage } = build({ t: 1 });
    await store.saveStill('s1', PHOTO);
    await store.saveItem('s1', { localId: 'a', text: 'Dune', box: { x: 0, y: 0, width: 1, height: 1 } }, 'file:///crop.jpg');
    await store.clear();
    expect(store.list()).toEqual([]);
    expect((files as MemorySnapFiles).files.size).toBe(0);
    expect(storage.getString('snap-store-v1')).toBeUndefined();
  });

  it('is not undone by a save that was already under way when it was wiped', async () => {
    const { store, files } = build({ t: 1 });
    await store.saveStill('s1', PHOTO);
    const saving = store.saveItem('s1', { localId: 'a', text: 'Dune' }, 'file:///crop.jpg');
    await store.clear();
    await saving;
    expect(store.list()).toEqual([]);
    expect((files as MemorySnapFiles).files.size).toBe(0);
  });

  it('still lets the snap be read when the picture cannot be copied', async () => {
    const broken: SnapFiles = {
      put: async () => {
        throw new Error('disk full');
      },
      remove: async () => undefined,
      removeAll: async () => undefined,
    };
    const { store } = build({ t: 1 }, undefined, broken);
    await expect(store.saveStill('s1', PHOTO)).resolves.toBeUndefined();
    expect(store.list()).toEqual([]);
  });

  it('starts empty from a damaged index', () => {
    const storage = new MemoryKeyValueStore();
    storage.set('snap-store-v1', '{not json');
    expect(new SnapStore({ files: new MemorySnapFiles(), storage }).list()).toEqual([]);
  });
});
