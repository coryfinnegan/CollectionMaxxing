import { defaultColumns, loadColumns, saveColumns, sortArrow, sortForColumn, STORAGE_KEY, toggleColumn } from './columns';

const store = new Map<string, string>();
const fakeStorage = {
  getItem: (key: string) => store.get(key) ?? null,
  setItem: (key: string, value: string) => void store.set(key, value),
  removeItem: (key: string) => void store.delete(key),
};
const install = (storage: unknown) =>
  Object.defineProperty(globalThis, 'localStorage', { value: storage, configurable: true, writable: true });

beforeEach(() => {
  store.clear();
  install(fakeStorage);
});
afterAll(() => install(undefined));

describe('column choice', () => {
  it('starts with the contract columns and not the optional ones', () => {
    expect(defaultColumns()).toEqual(['cover', 'title', 'format', 'platform', 'year', 'location', 'status']);
  });

  it('never hides the title', () => {
    expect(toggleColumn(['cover', 'title'], 'title')).toEqual(['cover', 'title']);
  });

  it('keeps columns in table order when one is shown again', () => {
    const hidden = toggleColumn(defaultColumns(), 'year');
    expect(hidden).not.toContain('year');
    expect(toggleColumn(hidden, 'year')).toEqual(defaultColumns());
  });

  it('survives a reload', () => {
    saveColumns(['title', 'year']);
    expect(loadColumns()).toEqual(['title', 'year']);
  });

  it('falls back to the defaults when the stored value is junk', () => {
    store.set(STORAGE_KEY, '{nope');
    expect(loadColumns()).toEqual(defaultColumns());
    store.set(STORAGE_KEY, JSON.stringify(['bogus']));
    expect(loadColumns()).toEqual(defaultColumns());
  });

  it('does not crash when localStorage throws', () => {
    const blocked = () => {
      throw new Error('blocked');
    };
    install({ getItem: blocked, setItem: blocked, removeItem: blocked });
    expect(loadColumns()).toEqual(defaultColumns());
    expect(() => saveColumns(['title'])).not.toThrow();
  });

  it('does not crash when localStorage is missing', () => {
    install(undefined);
    expect(loadColumns()).toEqual(defaultColumns());
    expect(() => saveColumns(['title'])).not.toThrow();
  });

  it('maps only the columns the API can sort', () => {
    expect(sortForColumn('title')).toBe('title');
    expect(sortForColumn('year')).toBe('year');
    expect(sortForColumn('platform')).toBe('platform');
    expect(sortForColumn('added')).toBe('-addedAt');
    expect(sortForColumn('format')).toBeUndefined();
    expect(sortForColumn('location')).toBeUndefined();
    expect(sortForColumn('status')).toBeUndefined();
    expect(sortForColumn('cover')).toBeUndefined();
  });

  it('points the sort arrow the way the sort runs', () => {
    expect(sortArrow('title')).toBe('↑');
    expect(sortArrow('year')).toBe('↑');
    expect(sortArrow('platform')).toBe('↑');
    // A leading "-" is newest first: the biggest date on top.
    expect(sortArrow('-addedAt')).toBe('↓');
  });
});
