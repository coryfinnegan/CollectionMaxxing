// The library design choice (#357): Current until picked, kept on this device.
import { setDesignStorage, useLibraryDesign } from './design';

function memoryStore() {
  const map = new Map<string, string>();
  return {
    getString: (key: string) => map.get(key),
    set: (key: string, value: string) => void map.set(key, value),
  };
}

beforeEach(() => {
  setDesignStorage(memoryStore());
  useLibraryDesign.setState({ choice: 'current' });
});

afterEach(() => {
  setDesignStorage(undefined);
});

describe('the library design choice', () => {
  it('starts on Current', () => {
    expect(useLibraryDesign.getState().choice).toBe('current');
    useLibraryDesign.getState().reload();
    expect(useLibraryDesign.getState().choice).toBe('current');
  });

  it('keeps Castle and Mall once picked', () => {
    useLibraryDesign.getState().set('castle');
    expect(useLibraryDesign.getState().choice).toBe('castle');

    useLibraryDesign.setState({ choice: 'current' });
    useLibraryDesign.getState().reload();
    expect(useLibraryDesign.getState().choice).toBe('castle');

    useLibraryDesign.getState().set('mall');
    useLibraryDesign.setState({ choice: 'current' });
    useLibraryDesign.getState().reload();
    expect(useLibraryDesign.getState().choice).toBe('mall');
  });

  it('falls back to Current for anything it does not recognize', () => {
    const store = memoryStore();
    store.set('design', 'brutalist');
    setDesignStorage(store);

    useLibraryDesign.getState().reload();

    expect(useLibraryDesign.getState().choice).toBe('current');
  });
});
