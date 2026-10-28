import { act, renderHook } from '@testing-library/react-native';
import { MemoryKeyValueStore } from '@/features/scan/tray/trayStore';
import {
  clearRecentSearches,
  getRecentStorage,
  loadRecentSearches,
  MAX_RECENT_SEARCHES,
  saveRecentSearch,
  setRecentStorage,
  useRecentSearches,
} from './recent';

beforeEach(() => setRecentStorage(new MemoryKeyValueStore()));
afterAll(() => setRecentStorage(undefined));

describe('recent searches', () => {
  it('loads nothing when nothing was saved', () => {
    expect(loadRecentSearches(new MemoryKeyValueStore())).toEqual([]);
  });

  it('saves a search and loads it back', () => {
    const storage = new MemoryKeyValueStore();
    expect(saveRecentSearch(storage, 'alien')).toEqual(['alien']);
    expect(loadRecentSearches(storage)).toEqual(['alien']);
  });

  it('trims whitespace and ignores a blank search', () => {
    const storage = new MemoryKeyValueStore();
    expect(saveRecentSearch(storage, '  alien  ')).toEqual(['alien']);
    expect(saveRecentSearch(storage, '   ')).toEqual(['alien']);
  });

  it('moves a repeat to the front instead of duplicating it', () => {
    const storage = new MemoryKeyValueStore();
    saveRecentSearch(storage, 'alien');
    saveRecentSearch(storage, 'dune');
    expect(saveRecentSearch(storage, 'Alien')).toEqual(['Alien', 'dune']);
  });

  it('keeps at most eight, dropping the oldest', () => {
    const storage = new MemoryKeyValueStore();
    for (let n = 1; n <= MAX_RECENT_SEARCHES + 2; n += 1) saveRecentSearch(storage, `search ${n}`);
    const loaded = loadRecentSearches(storage);
    expect(loaded).toHaveLength(MAX_RECENT_SEARCHES);
    expect(loaded[0]).toBe(`search ${MAX_RECENT_SEARCHES + 2}`);
    expect(loaded).not.toContain('search 1');
    expect(loaded).not.toContain('search 2');
  });

  it('clears everything', () => {
    const storage = new MemoryKeyValueStore();
    saveRecentSearch(storage, 'alien');
    clearRecentSearches(storage);
    expect(loadRecentSearches(storage)).toEqual([]);
  });

  it('reads a damaged blob as empty', () => {
    const storage = new MemoryKeyValueStore();
    storage.set('search-recent-v1', '{not json');
    expect(loadRecentSearches(storage)).toEqual([]);
  });

  it('drops non-string entries', () => {
    const storage = new MemoryKeyValueStore();
    storage.set('search-recent-v1', JSON.stringify(['alien', 42, null, 'dune']));
    expect(loadRecentSearches(storage)).toEqual(['alien', 'dune']);
  });

  it('falls back to memory storage outside the phone', () => {
    setRecentStorage(undefined);
    saveRecentSearch(getRecentStorage(), 'alien');
    expect(loadRecentSearches(getRecentStorage())).toEqual(['alien']);
  });
});

describe('useRecentSearches', () => {
  it('saves and clears through the shared storage', async () => {
    const { result } = await renderHook(() => useRecentSearches());
    expect(result.current.recent).toEqual([]);
    await act(() => result.current.save('alien'));
    expect(result.current.recent).toEqual(['alien']);
    expect(loadRecentSearches(getRecentStorage())).toEqual(['alien']);
    await act(() => result.current.clear());
    expect(result.current.recent).toEqual([]);
    expect(loadRecentSearches(getRecentStorage())).toEqual([]);
  });
});
