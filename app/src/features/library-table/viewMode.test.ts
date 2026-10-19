import { loadViewMode, saveViewMode, VIEW_KEY } from './viewMode';

describe('library view mode', () => {
  // The jest-expo (native) environment has no localStorage, so give the code under test a small one.
  beforeEach(() => {
    const store = new Map<string, string>();
    Object.defineProperty(globalThis, 'localStorage', {
      configurable: true,
      value: { getItem: (k: string) => store.get(k) ?? null, setItem: (k: string, v: string) => void store.set(k, v) },
    });
  });
  afterEach(() => Reflect.deleteProperty(globalThis, 'localStorage'));

  it('opens as the table on a wide screen when nothing was chosen', () => {
    expect(loadViewMode(1280)).toBe('table');
    expect(loadViewMode(768)).toBe('table');
  });

  it('opens as the grid on a phone-width screen when nothing was chosen', () => {
    expect(loadViewMode(390)).toBe('grid');
    expect(loadViewMode(767)).toBe('grid');
  });

  it('keeps a saved table choice on a phone', () => {
    saveViewMode('table');
    expect(loadViewMode(390)).toBe('table');
  });

  it('keeps a saved grid choice on a wide screen', () => {
    saveViewMode('grid');
    expect(loadViewMode(1280)).toBe('grid');
  });

  it('ignores a saved value it does not know', () => {
    globalThis.localStorage.setItem(VIEW_KEY, 'cards');
    expect(loadViewMode(390)).toBe('grid');
    expect(loadViewMode(1280)).toBe('table');
  });
});
