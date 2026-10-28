// The scanner owns its session context: the bar shows what the scan inherits, the sheet changes it, and the first
// scan sends it when the session is created. Typed search is one tap from the camera.
import { act, fireEvent, screen, waitFor } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import { MOCK_LIBRARY_ID, newMockId } from '@/mocks/data';
import { scanMock } from '@/mocks/scan';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { useCardsStore } from '../cards/cardsStore';
import { rememberSessionContext, setSessionContextStorage } from '../session/sessionContext';
import { getTrayStore, setTrayStore } from '../tray/storage';
import { createTrayStore, MemoryKeyValueStore } from '../tray/trayStore';
import { DEV_CODES } from './devCodes';
import { ScannerScreen } from './ScannerScreen';
import { FakeScannerSource } from './ScannerSource';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));

const SHELF_1 = 'a0000000-0000-4000-8000-000000000002';
const SHELF_2 = 'a0000000-0000-4000-8000-000000000003';
const tuning = { retryMs: 20, pollMs: 20, resendMs: 40, flashMs: 50 };

beforeEach(() => {
  scanMock.reset();
  useCardsStore.getState().reset();
  setTrayStore(createTrayStore({ storage: new MemoryKeyValueStore(), now: () => 1_000 }));
  setSessionContextStorage(new MemoryKeyValueStore());
});

afterEach(() => {
  scanMock.reset();
  setTrayStore(undefined);
  setSessionContextStorage(undefined);
});

const scanner = async () => {
  const source = new FakeScannerSource();
  await renderWithProviders(<ScannerScreen libraryId={MOCK_LIBRARY_ID} source={source} tuning={tuning} />);
  return source;
};

/** One tap, flushed before the next (state updates inside a sheet need their own act). */
const press = async (label: string) => {
  const target = await screen.findByLabelText(label);
  await act(async () => {
    fireEvent.press(target);
  });
};

describe('scanner session context', () => {
  it('shows the remembered spot in the bar', async () => {
    rememberSessionContext(MOCK_LIBRARY_ID, { locationId: SHELF_1, status: 'wishlist', mediaHint: 'mixed', channel: 'home' });
    await scanner();

    expect(await screen.findByLabelText('Session: Game room › Shelf 1, Wishlist, Mixed, Home. Change settings')).toBeTruthy();
  });

  it('saving the sheet updates the bar and the session the next scan creates', async () => {
    const bodies: unknown[] = [];
    server.use(
      http.post('*/api/v1/libraries/:libraryId/scan-sessions', async ({ request }) => {
        bodies.push(await request.json());
        const id = newMockId();
        scanMock.sessions.set(id, { id, libraryId: MOCK_LIBRARY_ID, seq: 0, candidates: [], answers: new Map(), status: 'open' });
        return HttpResponse.json({ id }, { status: 201 });
      }),
    );
    rememberSessionContext(MOCK_LIBRARY_ID, { locationId: SHELF_1, status: 'owned', mediaHint: 'mixed', channel: 'home' });
    const source = await scanner();

    await press('Session: Game room › Shelf 1, Owned, Mixed, Home. Change settings');
    await press('Location: Game room › Shelf 1. Change');
    await press('Shelf 2');
    await press('Games');
    await press('Save');

    expect(await screen.findByLabelText('Session: Game room › Shelf 2, Owned, Games, Home. Change settings')).toBeTruthy();
    await act(async () => source.inject([{ value: DEV_CODES.duneOwned, symbology: 'ean-13' }]));
    await waitFor(() => expect(bodies).toHaveLength(1));
    expect(bodies[0]).toEqual({ defaultLocationId: SHELF_2, defaultStatus: 'owned', mediaHint: 'games', channel: 'home', autoCommitTiers: [] });
  });

  it('searches the catalog one tap from the camera', async () => {
    await scanner();

    await press('Search the catalog');
    fireEvent.changeText(await screen.findByPlaceholderText('Title, author or barcode'), 'alien');

    expect(await screen.findByRole('button', { name: 'Alien, Ridley Scott · 1979–1980 · 2 editions' })).toBeTruthy();
  });

  it('hides Close when embedded in the Scan tab', async () => {
    await renderWithProviders(<ScannerScreen libraryId={MOCK_LIBRARY_ID} source={new FakeScannerSource()} tuning={tuning} showClose={false} />);

    await screen.findByLabelText('Take picture');
    expect(screen.queryByLabelText('Close')).toBeNull();
  });

  it('opens waiting review from the Review button', async () => {
    await scanner();
    mockRouter.push.mockClear();

    await act(async () => {
      getTrayStore(MOCK_LIBRARY_ID).getState().scan({ value: '9780441172719', symbology: 'ean-13' });
    });
    await press('Review 1 item');
    expect(mockRouter.push).toHaveBeenCalledWith('/review');
  });

  it('reviews a 3-item pending session from the Review button (#451)', async () => {
    await scanner();
    mockRouter.push.mockClear();

    await act(async () => {
      const tray = getTrayStore(MOCK_LIBRARY_ID).getState();
      tray.scan({ value: '9780441172719', symbology: 'ean-13' });
      tray.scan({ value: '9780451524935', symbology: 'ean-13' });
      tray.scan({ value: '4020628758862', symbology: 'ean-13' });
    });
    expect(await screen.findByText(/^3 items/)).toBeTruthy();
    await press('Review 3 items');
    expect(mockRouter.push).toHaveBeenCalledWith('/review');
  });

  it('offers no manual add from the control row', async () => {
    await scanner();

    await screen.findByLabelText('Take picture');
    expect(screen.queryByLabelText('Add it by hand')).toBeNull();
  });

  it('merges the tray and the session counters into the Review bar status line', async () => {
    await scanner();

    expect(await screen.findByText('0 items')).toBeTruthy();
  });

  it('shows the session buckets in the status line once scans are waiting', async () => {
    await renderWithProviders(<ScannerScreen libraryId={MOCK_LIBRARY_ID} source={new FakeScannerSource()} tuning={{ ...tuning, resendMs: 3_600_000 }} />);

    await act(async () => {
      getTrayStore(MOCK_LIBRARY_ID).getState().scan({ value: '9780441172719', symbology: 'ean-13' });
    });
    expect(await screen.findByText('1 item · 1 processing · 1 to review')).toBeTruthy();
  });
});
