/* eslint-disable @typescript-eslint/no-require-imports */
// The iPhone remembers the chosen library across launches (MMKV) when there is no localStorage.
const mockSaved = new Map<string, string>();
jest.mock('react-native-mmkv', () => ({
  createMMKV: () => ({
    getString: (k: string) => mockSaved.get(k),
    set: (k: string, v: string) => void mockSaved.set(k, v),
    remove: (k: string) => void mockSaved.delete(k),
  }),
}));

beforeEach(() => {
  mockSaved.clear();
  jest.resetModules();
  Object.defineProperty(globalThis, 'localStorage', { value: undefined, configurable: true });
});

describe('active library on the phone', () => {
  it('remembers the chosen library for the next launch', () => {
    const first = require('./library') as typeof import('./library');
    first.useLibraryStore.getState().setActiveLibraryId('lib-2');
    jest.resetModules();
    const next = require('./library') as typeof import('./library');
    expect(next.useLibraryStore.getState().activeLibraryId).toBe('lib-2');
  });

  it('forgets it when cleared', () => {
    const lib = require('./library') as typeof import('./library');
    lib.useLibraryStore.getState().setActiveLibraryId('lib-2');
    lib.useLibraryStore.getState().setActiveLibraryId(null);
    expect(mockSaved.size).toBe(0);
  });
});
