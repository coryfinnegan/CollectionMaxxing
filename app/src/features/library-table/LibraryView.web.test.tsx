import { configure, fireEvent, screen } from '@testing-library/react-native';
import { Dimensions } from 'react-native';
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

/** The width the view reads for its first pick (react-native's mock window is 750 wide, which is a phone here). */
const windowWidth = (width: number) =>
  jest.spyOn(Dimensions, 'get').mockReturnValue({ width, height: 900, scale: 1, fontScale: 1 });

beforeEach(() => {
  resetItems();
  store.clear();
  windowWidth(1280);
});

afterEach(() => jest.restoreAllMocks());

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

  it('opens on the grid on a phone-width screen when nothing was chosen', async () => {
    windowWidth(390);
    await renderWithProviders(<LibraryView title="Library" />);
    expect(await screen.findByTestId('library-grid')).toBeTruthy();
    expect(screen.queryByLabelText('Library table')).toBeNull();
  });

  it('keeps a saved table choice on a phone-width screen', async () => {
    windowWidth(390);
    store.set('cm.library.view', 'table');
    await renderWithProviders(<LibraryView title="Library" />);
    expect(await screen.findByLabelText('Library table')).toBeTruthy();
  });

  it('has no camera or scanning controls', async () => {
    await renderWithProviders(<LibraryView title="Library" />);
    await screen.findByLabelText('Library table');
    expect(screen.queryByLabelText(/scan|camera/i)).toBeNull();
    expect(screen.queryByText(/scan|camera/i)).toBeNull();
  });
});
