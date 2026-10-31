// Feature-print store: what gets saved and when, the local match, the offline repeat, backup, restore and the wipe.
import { QueryClientProvider } from '@tanstack/react-query';
import { render, waitFor } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import type { CandidateDto } from '@/api/generated/models';
import { clearLocalData } from '@/features/me/localData';
import { getOwnedCache, MemoryOwnedStorage, setOwnedStorage } from '@/features/offline/ownedCache';
import { createOwnedIndex } from '@/features/offline/ownedIndex';
import { entry } from '@/features/offline/testData';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { identifyMock } from '@/mocks/identify';
import { scanMock } from '@/mocks/scan';
import { server } from '@/mocks/server';
import { useLibraryStore } from '@/state/library';
import { makeQueryClient } from '@/test/render';
import { useCardsStore } from '../cards/cardsStore';
import { SnapPipeline } from '../pipeline/SnapPipeline';
import { makeKit, readItem, readingOf, recordRequests, resetKit } from '../pipeline/testKit';
import { trackedPictures } from '../pictureFiles';
import { backupPrints, dropRemovedItems, recomputeStale, restBackup, restorePrints } from './backup';
import { FingerprintBridge } from './FingerprintBridge';
import { FakePrintsServer } from './fakeServer';
import { createLocalMatch, DEFAULT_MAX_DISTANCE, maxDistanceOf } from './matcher';
import { ConfirmationRecorder } from './recorder';
import { getFingerprintStore, setFingerprintStorage } from './store';
import { candidateOf, FakeVision, freshStore, LIB, printRow, REV } from './testKit';

beforeEach(() => {
  scanMock.reset();
  identifyMock.reset();
  useCardsStore.getState().reset();
});
afterEach(() => {
  resetKit();
  setFingerprintStorage(undefined);
  useLibraryStore.setState({ activeLibraryId: null });
});

const noVision = new FakeVision();
const noCover = async () => undefined; // no cover can be fetched in these tests

describe('what is saved, and when', () => {
  const SESSION = 'session-1';

  /** A picture snap whose item `local-<id>` has a crop in the snap store, and a recorder watching the cards. */
  async function setup(ids: string[]) {
    const store = freshStore();
    const kit = makeKit();
    await kit.snaps.saveStill('snap-1', { uri: 'file:///still.jpg', width: 100, height: 100 });
    for (const id of ids) await kit.snaps.saveItem('snap-1', { localId: `local-${id}`, text: id }, `file:///cache/crop-${id}.jpg`);
    const vision = new FakeVision();
    ids.forEach((id, i) => vision.set(kit.snaps.list()[0]!.items[i]!.cropUri!, 10 + i));
    let server: CandidateDto[] = [];
    const recorder = new ConfirmationRecorder({
      store,
      vision,
      snaps: kit.snaps,
      cards: useCardsStore,
      sessionId: () => SESSION,
      loadSession: async () => server,
    });
    recorder.start();
    return { store, kit, vision, recorder, setServer: (next: CandidateDto[]) => (server = next) };
  }

  it('saves one print for the item and release when a picture card is confirmed and committed', async () => {
    const { store, recorder, setServer, vision, kit } = await setup(['1']);
    const card = candidateOf('1', { decision: 'accepted' });
    useCardsStore.getState().merge([card], { announce: false }); // + Add on the card (or ticked at Review)
    // The commit adds the item; Review then removes the card.
    setServer([{ ...card, createdItemId: 'item-1' }]);
    useCardsStore.getState().remove('1');
    await recorder.settled();

    expect(store.all()).toHaveLength(1);
    const [row] = store.all();
    expect(row).toMatchObject({ itemId: 'item-1', releaseId: card.release!.id, revision: REV, synced: false });
    expect(row!.print).toBe('10'); // the print of the stored crop
    expect(vision.printed).toEqual([kit.snaps.list()[0]!.items[0]!.cropUri]);
  });

  it('saves nothing for a scan alone: undecided cards, even after they leave', async () => {
    const { store, recorder, setServer } = await setup(['1', '2']);
    useCardsStore.getState().merge([candidateOf('1'), candidateOf('2')], { announce: false });
    setServer([candidateOf('1'), candidateOf('2')]);
    useCardsStore.getState().reset();
    await recorder.settled();

    expect(store.size).toBe(0);
  });

  it('saves nothing for a rejected candidate', async () => {
    const { store, recorder, setServer } = await setup(['1']);
    useCardsStore.getState().merge([candidateOf('1', { decision: 'accepted' })], { announce: false });
    useCardsStore.getState().merge([candidateOf('1', { decision: 'rejected', changeSeq: 2 })], { announce: false }); // Skip
    setServer([candidateOf('1', { decision: 'rejected' })]);
    useCardsStore.getState().remove('1');
    await recorder.settled();

    expect(store.size).toBe(0);
  });

  it('saves nothing for an Added card that was never committed (the scan was discarded)', async () => {
    const { store, recorder, setServer } = await setup(['1']);
    useCardsStore.getState().merge([candidateOf('1', { decision: 'accepted' })], { announce: false });
    setServer([candidateOf('1', { decision: 'accepted', createdItemId: null })]);
    useCardsStore.getState().reset();
    await recorder.settled();

    expect(store.size).toBe(0);
  });

  it('skips quietly a confirmed card whose crop is gone or cannot be printed', async () => {
    const { store, recorder, setServer, vision } = await setup(['1', '2']);
    vision.fail = true;
    const a = candidateOf('1', { decision: 'accepted' });
    const b = candidateOf('9', { decision: 'accepted', localId: 'never-saved' });
    useCardsStore.getState().merge([a, b], { announce: false });
    setServer([{ ...a, createdItemId: 'item-1' }, { ...b, createdItemId: 'item-9' }]);
    useCardsStore.getState().reset();
    await recorder.settled();

    expect(store.size).toBe(0);
  });

  it('keeps no picture file of its own: the crop it prints is the snap store copy', async () => {
    const { recorder, setServer } = await setup(['1']);
    const card = candidateOf('1', { decision: 'accepted' });
    useCardsStore.getState().merge([card], { announce: false });
    setServer([{ ...card, createdItemId: 'item-1' }]);
    useCardsStore.getState().remove('1');
    await recorder.settled();

    expect(trackedPictures()).toEqual([]);
  });
});

describe('the local match', () => {
  const store = () => {
    const s = freshStore();
    s.put(printRow(10, { print: '10' }));
    s.put(printRow(50, { print: '50' }));
    return s;
  };

  it('returns the release of a stored print within the threshold', async () => {
    const vision = new FakeVision().set('file:///crop.jpg', 10.1);
    const match = createLocalMatch({ store: store(), vision, maxDistance: () => 0.5 });
    expect(await match({ uri: 'file:///crop.jpg' })).toBe(printRow(10).releaseId);
  });

  it('returns the closest one when two are within the threshold', async () => {
    const s = store();
    s.put(printRow(11, { print: '11' }));
    const vision = new FakeVision().set('file:///crop.jpg', 10.8);
    expect(await createLocalMatch({ store: s, vision, maxDistance: () => 5 })({ uri: 'file:///crop.jpg' })).toBe(printRow(11).releaseId);
  });

  it('returns nothing when every stored print is beyond the threshold', async () => {
    const vision = new FakeVision().set('file:///crop.jpg', 30);
    expect(await createLocalMatch({ store: store(), vision, maxDistance: () => 0.5 })({ uri: 'file:///crop.jpg' })).toBeUndefined();
  });

  it('never compares a print of another revision (FakeVision refuses to)', async () => {
    const s = freshStore();
    s.put(printRow(10, { print: '10', revision: 7 }));
    const vision = new FakeVision().set('file:///crop.jpg', 10, REV);
    expect(await createLocalMatch({ store: s, vision, maxDistance: () => 0.5 })({ uri: 'file:///crop.jpg' })).toBeUndefined();
    // And one of the device's revision next to it is still found.
    s.put(printRow(10, { itemId: 'other', releaseId: 'other-release', print: '10' }));
    expect(await createLocalMatch({ store: s, vision, maxDistance: () => 0.5 })({ uri: 'file:///crop.jpg' })).toBe('other-release');
  });

  it('prints nothing when the store is empty', async () => {
    const vision = new FakeVision();
    expect(await createLocalMatch({ store: freshStore(), vision })({ uri: 'file:///crop.jpg' })).toBeUndefined();
    expect(vision.printed).toEqual([]);
  });

  it('reads the threshold from the recognition config, with a default in TypeScript', () => {
    expect(maxDistanceOf({ fingerprints: { maxDistance: 0.2 } })).toBe(0.2);
    expect(maxDistanceOf({})).toBe(DEFAULT_MAX_DISTANCE);
    expect(maxDistanceOf({ fingerprints: { maxDistance: 'wide' } })).toBe(DEFAULT_MAX_DISTANCE);
    expect(maxDistanceOf({ fingerprints: { maxDistance: -1 } })).toBe(DEFAULT_MAX_DISTANCE);
  });
});

describe('offline', () => {
  it('a repeat snap gets its local match with no request made, and the identify waits as Queued', async () => {
    const store = freshStore();
    store.put(printRow(3, { print: '0', synced: true }));
    const kit = makeKit({ localMatch: createLocalMatch({ store, vision: new FakeVision(), maxDistance: () => 0.5 }) });
    kit.reader.script(readingOf([readItem('Dune', { box: { x: 0.05, y: 0.1, width: 0.2, height: 0.7 } })]));
    server.use(http.post('*/api/v1/scan-sessions/:id/identify', () => HttpResponse.error()), http.post('*/api/v1/libraries/:id/scan-sessions', () => HttpResponse.error()));
    const requests = recordRequests();

    const pipeline = new SnapPipeline(kit.deps);
    await pipeline.onSnap(await kit.snapEvent());
    await pipeline.flush();

    const snap = kit.store.getState().snaps[0]!;
    expect(snap.items[0]!.payload.localMatchReleaseId).toBe(printRow(3).releaseId); // the hit is ready to go with the identify
    expect(snap.sent).toBeFalsy(); // Queued: nothing was answered
    const seen = await requests.all();
    requests.stop();
    // The match itself asked nobody: the only requests are the failed attempts to reach the server.
    expect(seen.every((r) => /scan-sessions/.test(r.url))).toBe(true);
    expect(seen.some((r) => /feature-prints|artwork|catalog/.test(r.url))).toBe(false);
  });
});

describe('backup', () => {
  it('uploads only unsynced prints, in batches of at most 200, and marks them synced', async () => {
    const store = freshStore();
    for (let n = 1; n <= 450; n++) store.put(printRow(n, { synced: n <= 50 })); // 50 are already on the PC
    const pcs = new FakePrintsServer().use();

    const result = await backupPrints({ store, canEdit: true, remote: restBackup(LIB) });

    expect(result).toEqual({ ok: true, sent: 400 });
    expect(pcs.puts.map((p) => p.prints.length)).toEqual([200, 200]);
    expect(pcs.puts.flatMap((p) => p.prints).some((p) => Number(p.data) <= 50)).toBe(false);
    expect(store.unsynced()).toEqual([]);
  });

  it('retries a failed batch next time, and the batches before it stay synced', async () => {
    const store = freshStore();
    for (let n = 1; n <= 250; n++) store.put(printRow(n));
    const pcs = new FakePrintsServer().use();
    pcs.failPuts = 0;
    let calls = 0;
    const remote = restBackup(LIB);
    const flaky = {
      ...remote,
      put: async (prints: Parameters<typeof remote.put>[0]) => {
        calls += 1;
        if (calls === 2) throw new Error('connection lost');
        return remote.put(prints);
      },
    };

    const first = await backupPrints({ store, canEdit: true, remote: flaky });
    expect(first.ok).toBe(false);
    expect(store.unsynced()).toHaveLength(50); // the first 200 went; the failed batch is still waiting

    const second = await backupPrints({ store, canEdit: true, remote });
    expect(second).toEqual({ ok: true, sent: 50 });
    expect(store.unsynced()).toEqual([]);
    expect(pcs.rows).toHaveLength(250);
  });

  it('a server error leaves the prints unsynced and the next run sends them again', async () => {
    const store = freshStore();
    store.put(printRow(1));
    const pcs = new FakePrintsServer().use();
    pcs.failPuts = 1;

    expect((await backupPrints({ store, canEdit: true, remote: restBackup(LIB) })).ok).toBe(false);
    expect(store.unsynced()).toHaveLength(1);
    expect((await backupPrints({ store, canEdit: true, remote: restBackup(LIB) })).ok).toBe(true);
    expect(store.unsynced()).toEqual([]);
  });

  it('a viewer never calls PUT: prints stay local only', async () => {
    const store = freshStore();
    store.put(printRow(1));
    const pcs = new FakePrintsServer().use();
    const requests = recordRequests();

    const result = await backupPrints({ store, canEdit: false, remote: restBackup(LIB) });

    expect(result).toEqual({ ok: true, sent: 0 });
    expect(pcs.puts).toEqual([]);
    expect(await requests.all()).toEqual([]);
    requests.stop();
    expect(store.unsynced()).toHaveLength(1);
  });
});

describe('restore', () => {
  const wire = (n: number) => {
    const r = printRow(n);
    return { itemId: r.itemId, releaseId: r.releaseId, revision: r.revision, data: r.print, createdAt: r.createdAt };
  };

  it('on an empty store pulls every page with since and cursor, then matches without more network', async () => {
    const pcs = new FakePrintsServer().use();
    pcs.rows = Array.from({ length: 450 }, (_, i) => wire(i + 1));
    const store = freshStore();

    const result = await restorePrints({ store, remote: restBackup(LIB) });

    expect(result).toEqual({ ok: true, pulled: 450 });
    expect(pcs.gets.map((g) => g.cursor)).toEqual([null, '200', '400']);
    expect(pcs.gets[0]!.since).toBeNull();
    expect(pcs.gets.every((g) => g.limit === '200')).toBe(true);
    expect(store.size).toBe(450);
    expect(store.unsynced()).toEqual([]); // they came from the PC
    expect(store.restoredThrough).toBe(printRow(450).createdAt);

    const requests = recordRequests();
    const vision = new FakeVision().set('file:///crop.jpg', 120.1);
    expect(await createLocalMatch({ store, vision, maxDistance: () => 0.5 })({ uri: 'file:///crop.jpg' })).toBe(printRow(120).releaseId);
    expect(await requests.all()).toEqual([]);
    requests.stop();
  });

  it('a later restore asks only for what is newer', async () => {
    const pcs = new FakePrintsServer().use();
    pcs.rows = [wire(1), wire(2)];
    const store = freshStore();
    await restorePrints({ store, remote: restBackup(LIB) });
    pcs.rows.push(wire(3));

    const result = await restorePrints({ store, remote: restBackup(LIB) });

    expect(result).toEqual({ ok: true, pulled: 1 });
    expect(pcs.gets.at(-1)!.since).toBe(printRow(2).createdAt);
    expect(store.size).toBe(3);
  });

  it('a failed page leaves the restore point where it was, and the next restore starts again', async () => {
    const pcs = new FakePrintsServer().use();
    pcs.pageSize = 1;
    pcs.rows = [wire(1), wire(2)];
    const store = freshStore();
    let calls = 0;
    const remote = restBackup(LIB);

    const result = await restorePrints({
      store,
      remote: { ...remote, list: (args) => (++calls === 2 ? Promise.reject(new Error('offline')) : remote.list(args)) },
    });

    expect(result.ok).toBe(false);
    expect(store.restoredThrough).toBeNull();
    expect((await restorePrints({ store, remote })).ok).toBe(true);
    expect(store.size).toBe(2);
  });

  it('never overwrites a print this phone has not backed up yet', async () => {
    const pcs = new FakePrintsServer().use();
    pcs.rows = [{ ...wire(1), data: 'old' }];
    const store = freshStore();
    store.put(printRow(1, { print: 'new' }));

    await restorePrints({ store, remote: restBackup(LIB) });

    expect(store.get(printRow(1).itemId)!.print).toBe('new');
  });
});

describe('removed items and other revisions', () => {
  it('drops the print of an item that is gone from the library on the next sync, but not one added since', () => {
    const store = freshStore();
    store.put(printRow(1, { synced: true, createdAt: '2026-10-01T00:00:00.000Z' }));
    store.put(printRow(2, { synced: true, createdAt: '2026-10-01T00:00:00.000Z' }));
    store.put(printRow(3, { createdAt: '2026-10-03T00:00:00.000Z' })); // confirmed after the last owned sync

    const dropped = dropRemovedItems({ store, ownedItemIds: new Set([printRow(1).itemId]), syncedAt: Date.parse('2026-10-02T00:00:00.000Z') });

    expect(dropped).toBe(1);
    expect(store.all().map((r) => r.itemId).sort()).toEqual([printRow(1).itemId, printRow(3).itemId]);
  });

  it('keeps a print of another revision, and recomputes it from the cover when it can', async () => {
    const store = freshStore();
    store.put(printRow(1, { revision: 1, synced: true }));
    store.put(printRow(2, { revision: 1, synced: true }));
    store.setDeviceRevision(2);
    const vision = new FakeVision().set('file:///covers/1.jpg', 99, 2);
    const discarded: string[] = [];

    await recomputeStale({
      store,
      vision,
      cover: async (releaseId) => (releaseId === printRow(1).releaseId ? 'file:///covers/1.jpg' : undefined), // item 2 has no cover
      discard: async (uri) => void discarded.push(uri),
    });

    expect(store.get(printRow(1).itemId)).toMatchObject({ revision: 2, print: '99', synced: false }); // to be backed up again
    expect(store.get(printRow(2).itemId)).toMatchObject({ revision: 1, print: '2' }); // kept, never matched
    expect(discarded).toEqual(['file:///covers/1.jpg']); // the downloaded cover is released
  });

  it('skips silently when the cover cannot be printed', async () => {
    const store = freshStore();
    store.put(printRow(1, { revision: 1, synced: true }));
    store.setDeviceRevision(2);
    const vision = new FakeVision();
    vision.fail = true;

    await recomputeStale({ store, vision, cover: async () => 'file:///covers/1.jpg', discard: async () => undefined });

    expect(store.get(printRow(1).itemId)!.revision).toBe(1);
  });
});

describe('sign-out and unpair', () => {
  it('wipe the store through the registered clearer', async () => {
    const client = makeQueryClient();
    useLibraryStore.setState({ activeLibraryId: MOCK_LIBRARY_ID });
    const store = getFingerprintStore({ userId: 'user-1', libraryId: LIB });
    store.put(printRow(1));
    store.put(printRow(2, { synced: true }));
    const other = getFingerprintStore({ userId: 'user-1', libraryId: 'another-library' });
    other.put(printRow(3));
    new FakePrintsServer().use();
    const view = await render(
      <QueryClientProvider client={client}>
        <FingerprintBridge vision={noVision} cover={noCover} />
      </QueryClientProvider>,
    );

    await clearLocalData(client); // what signing out, unpairing and deleting the account all run

    expect(store.size).toBe(0);
    expect(other.size).toBe(0);
    expect(getFingerprintStore({ userId: 'user-1', libraryId: LIB }).size).toBe(0); // and nothing comes back from storage
    view.unmount();
  });
});

describe('the bridge', () => {
  it('restores on mount and backs up what was confirmed, for an editor', async () => {
    useLibraryStore.setState({ activeLibraryId: MOCK_LIBRARY_ID });
    server.use(http.get('*/api/v1/me', () => HttpResponse.json({ id: 'user-1', memberships: [{ libraryId: MOCK_LIBRARY_ID, role: 'editor' }] })));
    const pcs = new FakePrintsServer().use();
    const r = printRow(5);
    pcs.rows = [{ itemId: r.itemId, releaseId: r.releaseId, revision: r.revision, data: r.print, createdAt: r.createdAt }];
    const store = freshStore(LIB, 'user-1');
    store.put(printRow(6));
    const view = await render(
      <QueryClientProvider client={makeQueryClient()}>
        <FingerprintBridge vision={noVision} cover={noCover} />
      </QueryClientProvider>,
    );

    await waitFor(() => expect(store.size).toBe(2));
    await waitFor(() => expect(store.unsynced()).toEqual([]));
    expect(pcs.puts).toHaveLength(1);
    view.unmount();
  });

  it('prunes the print of an item that is sold or on the wishlist, and keeps the ones you have (owned, lent, missing)', async () => {
    setOwnedStorage(new MemoryOwnedStorage());
    useLibraryStore.setState({ activeLibraryId: MOCK_LIBRARY_ID });
    server.use(http.get('*/api/v1/me', () => HttpResponse.json({ id: 'user-1', memberships: [{ libraryId: MOCK_LIBRARY_ID, role: 'viewer' }] })));
    new FakePrintsServer().use();
    const store = freshStore(LIB, 'user-1');
    const created = '2026-10-01T00:00:00.000Z';
    const statuses = ['owned', 'lent', 'missing', 'sold', 'wishlist'] as const;
    statuses.forEach((_, i) => store.put(printRow(i + 1, { synced: true, createdAt: created })));
    // The owned copy has every item, each with its status: the snapshot carries the wishlist and sold items now.
    getOwnedCache(LIB).commit({
      index: createOwnedIndex(statuses.map((status, i) => entry({ itemId: printRow(i + 1).itemId, status }))),
      cursor: 'c',
      syncedAt: Date.parse('2026-10-02T00:00:00.000Z'),
    });
    const view = await render(
      <QueryClientProvider client={makeQueryClient()}>
        <FingerprintBridge vision={noVision} cover={noCover} />
      </QueryClientProvider>,
    );

    await waitFor(() => expect(store.size).toBe(3));
    expect(store.all().map((r) => r.itemId).sort()).toEqual([1, 2, 3].map((n) => printRow(n).itemId));
    view.unmount();
    setOwnedStorage(undefined);
  });

  it('for a viewer restores but never calls PUT', async () => {
    useLibraryStore.setState({ activeLibraryId: MOCK_LIBRARY_ID });
    server.use(http.get('*/api/v1/me', () => HttpResponse.json({ id: 'user-1', memberships: [{ libraryId: MOCK_LIBRARY_ID, role: 'viewer' }] })));
    const pcs = new FakePrintsServer().use();
    const r = printRow(5);
    pcs.rows = [{ itemId: r.itemId, releaseId: r.releaseId, revision: r.revision, data: r.print, createdAt: r.createdAt }];
    const store = freshStore(LIB, 'user-1');
    store.put(printRow(6));
    const view = await render(
      <QueryClientProvider client={makeQueryClient()}>
        <FingerprintBridge vision={noVision} cover={noCover} />
      </QueryClientProvider>,
    );

    await waitFor(() => expect(store.size).toBe(2));
    expect(pcs.puts).toEqual([]);
    view.unmount();
  });

  it('on a fresh sign-in restores the first library’s prints, with nothing stored', async () => {
    useLibraryStore.setState({ activeLibraryId: null });
    server.use(http.get('*/api/v1/me', () => HttpResponse.json({ id: 'user-1', memberships: [{ libraryId: MOCK_LIBRARY_ID, role: 'editor' }] })));
    const pcs = new FakePrintsServer().use();
    const r = printRow(5);
    pcs.rows = [{ itemId: r.itemId, releaseId: r.releaseId, revision: r.revision, data: r.print, createdAt: r.createdAt }];
    const store = freshStore(LIB, 'user-1');
    store.put(printRow(6));
    const view = await render(
      <QueryClientProvider client={makeQueryClient()}>
        <FingerprintBridge vision={noVision} cover={noCover} />
      </QueryClientProvider>,
    );

    await waitFor(() => expect(store.size).toBe(2));
    view.unmount();
  });
});
