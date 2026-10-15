import { HttpResponse, http } from 'msw';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { deleteOwned, ownershipMock, resetOwnershipMock, upsertOwned } from '@/mocks/ownership';
import { server } from '@/mocks/server';
import { clearOwnedData, getOwnedCache, MemoryOwnedStorage, OwnedCache, setOwnedStorage, type OwnedStorage } from './ownedCache';
import { syncLibrary, syncOwned, type FetchPage } from './sync';
import { entry } from './testData';

const LIB = MOCK_LIBRARY_ID;
const OTHER_LIB = '33333333-3333-4333-8333-333333333333';

let storage: MemoryOwnedStorage;
beforeEach(() => {
  resetOwnershipMock();
  storage = new MemoryOwnedStorage();
  setOwnedStorage(storage);
});
afterAll(() => setOwnedStorage(undefined));

const codes = (libraryId = LIB) => getOwnedCache(libraryId).state.index;

describe('first sync', () => {
  it('pulls everything the library owns and remembers when it finished', async () => {
    const result = await syncLibrary(LIB, () => 1_000);

    expect(result.ok).toBe(true);
    const { index, syncedAt, cursor } = getOwnedCache(LIB).state;
    expect(index.size).toBe(3);
    expect(syncedAt).toBe(1_000);
    expect(cursor).toBe('3');
    expect(index.lookup('042100005264').owned[0].locationPath).toBe('Living room › Shelf B');
  });

  it('follows pages until the server says there are no more', async () => {
    const seen: (string | null)[] = [];
    server.use(
      http.get('*/api/v1/libraries/:libraryId/ownership/snapshot', ({ request }) => {
        const url = new URL(request.url);
        seen.push(url.searchParams.get('since'));
        return HttpResponse.json(
          url.searchParams.get('since')
            ? { entries: [], nextCursor: 'c2', hasMore: false }
            : { entries: [], nextCursor: 'c1', hasMore: true },
        );
      }),
    );

    const result = await syncLibrary(LIB);

    expect(result).toMatchObject({ ok: true, pages: 2 });
    expect(seen).toEqual([null, 'c1']);
  });
});

describe('delta sync', () => {
  it('adds a new item, updates an edited one and removes a tombstoned one', async () => {
    await syncLibrary(LIB);
    const before = ownershipMock.seq;
    expect(codes().lookup('0042100005264').owned).toHaveLength(1);

    upsertOwned({
      itemId: '0000000a-0000-4000-8000-000000000000',
      deleted: false,
      releaseId: '0000000b-0000-4000-8000-000000000000',
      workId: '0000000c-0000-4000-8000-000000000000',
      title: 'Alien',
      format: 'vhs',
      platform: null,
      identifiers: ['4006381333931'],
      locationPath: 'Basement',
    });
    upsertOwned({ ...ownershipMock.changes[2].entry, locationPath: 'Office › Shelf 2' });
    deleteOwned('00000001-0000-4000-8000-000000000000');

    const asked: (string | null)[] = [];
    server.events.on('request:start', ({ request }) => asked.push(new URL(request.url).searchParams.get('since')));
    await syncLibrary(LIB);
    server.events.removeAllListeners();

    expect(asked).toEqual([String(before)]);
    expect(codes().lookup('4006381333931').owned[0].title).toBe('Alien');
    expect(codes().lookup('9780306406157').owned[0].locationPath).toBe('Office › Shelf 2');
    expect(codes().lookup('042100005264').owned).toEqual([]);
    expect(getOwnedCache(LIB).state.cursor).toBe(String(ownershipMock.seq));
  });

  it('keeps the previous copy and cursor when a page fails, then catches up on the retry', async () => {
    await syncLibrary(LIB, () => 5);
    const saved = getOwnedCache(LIB).state;
    deleteOwned('00000003-0000-4000-8000-000000000000');
    ownershipMock.failNext = 500;

    const failed = await syncLibrary(LIB, () => 9);

    expect(failed.ok).toBe(false);
    const after = getOwnedCache(LIB).state;
    expect(after.cursor).toBe(saved.cursor);
    expect(after.syncedAt).toBe(5);
    expect(after.index.size).toBe(3);

    expect((await syncLibrary(LIB, () => 9)).ok).toBe(true);
    expect(getOwnedCache(LIB).state.index.size).toBe(2);
    expect(getOwnedCache(LIB).state.syncedAt).toBe(9);
  });

  it('applies the pages that arrived when a later page fails, and resumes from the last good cursor', async () => {
    const hits: (string | null)[] = [];
    server.use(
      http.get('*/api/v1/libraries/:libraryId/ownership/snapshot', ({ request }) => {
        const since = new URL(request.url).searchParams.get('since');
        hits.push(since);
        if (!since) return HttpResponse.json({ entries: [{ ...entry(), identifiers: ['4006381333931'] }], nextCursor: 'p1', hasMore: true });
        return HttpResponse.json({ title: 'down' }, { status: 503 });
      }),
    );

    const result = await syncLibrary(LIB);

    expect(result.ok).toBe(false);
    const state = getOwnedCache(LIB).state;
    expect(state.cursor).toBe('p1');
    expect(state.syncedAt).toBeNull();
    expect(state.index.lookup('4006381333931').owned).toHaveLength(1);
  });

  it('changes nothing when saving a page fails', async () => {
    await syncLibrary(LIB, () => 5);
    const flaky: OwnedStorage = {
      getString: (k) => storage.getString(k),
      set: () => {
        throw new Error('disk full');
      },
      remove: (k) => storage.remove(k),
    };
    const cache = new OwnedCache(LIB, flaky);
    const before = cache.state;
    deleteOwned('00000003-0000-4000-8000-000000000000');

    const result = await syncOwned(cache, async () => ({ entries: [], nextCursor: 'x', hasMore: false }));

    expect(result.ok).toBe(false);
    expect(cache.state).toBe(before);
    expect(cache.state.cursor).toBe('3');
  });

  it('starts over once when the server no longer accepts the saved cursor', async () => {
    await syncLibrary(LIB);
    const cache = getOwnedCache(LIB);
    cache.commit({ ...cache.state, cursor: 'garbage' });

    const result = await syncLibrary(LIB);

    expect(result.ok).toBe(true);
    expect(cache.state.index.size).toBe(3);
    expect(cache.state.cursor).toBe('3');
  });

  it('shares one request between overlapping syncs', async () => {
    let calls = 0;
    const slow: FetchPage = async () => {
      calls += 1;
      await new Promise((r) => setTimeout(r, 10));
      return { entries: [], nextCursor: '1', hasMore: false };
    };
    const cache = getOwnedCache(LIB);

    await Promise.all([syncOwned(cache, slow), syncOwned(cache, slow)]);

    expect(calls).toBe(1);
  });

  it('saves the copy so a fresh start still answers', async () => {
    await syncLibrary(LIB);

    setOwnedStorage(storage); // a new app launch: new caches over the same storage

    expect(getOwnedCache(LIB).state.index.lookup('9780306406157').owned).toHaveLength(1);
    expect(getOwnedCache(LIB).state.cursor).toBe('3');
  });

  it('reads a viewer-role library the same way (the snapshot needs only read access)', async () => {
    // The endpoint is viewer-level; the phone has no role check of its own. Syncing must not depend on one.
    expect((await syncLibrary(LIB)).ok).toBe(true);
  });
});

describe('keeping libraries apart and wiping', () => {
  it('keeps one library per blob and does not mix them', async () => {
    await syncLibrary(LIB);
    const other = getOwnedCache(OTHER_LIB);
    other.commit({ index: other.state.index.apply([entry({ identifiers: ['5901234123457'] })]), cursor: 'o1', syncedAt: 7 });

    setOwnedStorage(storage);

    expect(getOwnedCache(LIB).state.index.lookup('5901234123457').owned).toEqual([]);
    expect(getOwnedCache(OTHER_LIB).state.index.lookup('9780306406157').owned).toEqual([]);
    expect(getOwnedCache(OTHER_LIB).state.index.size).toBe(1);
    expect(getOwnedCache(LIB).state.index.size).toBe(3);
  });

  it('forgets every library on sign-out or unpair', async () => {
    await syncLibrary(LIB);
    const other = getOwnedCache(OTHER_LIB);
    other.commit({ index: other.state.index.apply([entry({ identifiers: ['5901234123457'] })]), cursor: 'o1', syncedAt: 7 });
    const mine = getOwnedCache(LIB);

    clearOwnedData();

    expect(mine.state.index.size).toBe(0);
    expect(getOwnedCache(OTHER_LIB).state.index.size).toBe(0);
    setOwnedStorage(storage);
    expect(getOwnedCache(LIB).state.cursor).toBeNull();
    expect(getOwnedCache(OTHER_LIB).state.index.size).toBe(0);
  });
});

describe('size', () => {
  it('stays well under 1.5 MB for 5,000 items', () => {
    const cache = getOwnedCache(LIB);
    const entries = Array.from({ length: 5000 }, (_, i) =>
      entry({ title: `A reasonably long title number ${i}`, identifiers: [`40063813${String(i).padStart(5, '0')}`], locationPath: 'Living room › Shelf B', format: 'dvd' }),
    );
    cache.commit({ index: cache.state.index.apply(entries), cursor: 'x', syncedAt: 1 });

    const bytes = (storage.getString(`owned-index-v1:${LIB}`) ?? '').length;
    expect(bytes).toBeLessThan(1_500_000);
  });
});
