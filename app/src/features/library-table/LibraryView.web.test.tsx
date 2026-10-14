import { configure, fireEvent, screen } from '@testing-library/react-native';
import { resetItems } from '@/mocks/items';
import { renderWithProviders } from '@/test/render';
import { LibraryView } from '@/features/library/LibraryView.web';

configure({ asyncUtilTimeout: 8000 });
jest.setTimeout(30_000);

jest.mock('expo-router', () => ({ useRouter: () => ({ push: jest.fn(), back: jest.fn(), replace: jest.fn() }) }));

const store = new Map<string, string>();
Object.defineProperty(globalThis, 'localStorage', {
  value: {
    getItem: (key: string) => store.get(key) ?? null,
    setItem: (key: string, value: string) => void store.set(key, value),
    removeItem: (key: string) => void store.delete(key),
  },
  configurable: true,
  writable: true,
});

beforeEach(() => {
  resetItems();
  store.clear();
});

describe('web LibraryView', () => {
  it('opens on the table and switches to the grid and back', async () => {
    await renderWithProviders(<LibraryView title="Library" />);
    expect(await screen.findByLabelText('Library table')).toBeTruthy();

    await fireEvent.press(screen.getByLabelText('Grid view'));
    expect(await screen.findByTestId('library-grid')).toBeTruthy();
    expect(screen.queryByLabelText('Library table')).toBeNull();
    expect(store.get('cm.library.view')).toBe('grid');

    await fireEvent.press(screen.getByLabelText('Table view'));
    expect(await screen.findByLabelText('Library table')).toBeTruthy();
  });

  it('has no camera or scanning controls', async () => {
    await renderWithProviders(<LibraryView title="Library" />);
    await screen.findByLabelText('Library table');
    expect(screen.queryByLabelText(/scan|camera/i)).toBeNull();
    expect(screen.queryByText(/scan|camera/i)).toBeNull();
  });
});
