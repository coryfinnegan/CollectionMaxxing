// The scanner end to end without a camera: FakeScannerSource -> tray -> mock API -> mock event stream -> cards.
// Captures queue silently (#432): answers land in the store for Review, the status bar counts them, and nothing
// renders over the camera — so these tests read the store and the status bar, never result cards.
import { act, fireEvent, screen, waitFor } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import { type HapticDriver, setHapticDriver } from '@/lib/haptics';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { scanMock } from '@/mocks/scan';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { useCardsStore } from '../cards/cardsStore';
import { setTrayStore } from '../tray/storage';
import { createTrayStore, MemoryKeyValueStore } from '../tray/trayStore';
import { DEV_CODES, DEV_SIX_CODES } from './devCodes';
import { ScannerScreen } from './ScannerScreen';
import { FakeScannerSource } from './ScannerSource';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));

const tuning = { retryMs: 20, pollMs: 20, resendMs: 40, flashMs: 50 };
const code = (value: string) => ({ value, symbology: 'ean-13' });

let haptic: string[] = [];
let storage: MemoryKeyValueStore;

const useNewTray = () => {
  let n = 0;
  const store = createTrayStore({ storage, newKey: () => `batch-${++n}`, now: () => 1_000 });
  setTrayStore(store);
  return store;
};

const asRole = (role: 'viewer' | 'editor' | 'owner') => {
  scanMock.role = role;
  server.use(http.get('*/api/v1/me', () => HttpResponse.json({ id: 'u1', memberships: [{ libraryId: MOCK_LIBRARY_ID, role }] })));
};

const scanner = async (source = new FakeScannerSource(), devSource?: FakeScannerSource) => {
  await renderWithProviders(<ScannerScreen libraryId={MOCK_LIBRARY_ID} source={source} devSource={devSource} tuning={tuning} />);
  return source;
};

beforeEach(() => {
  scanMock.reset();
  useCardsStore.getState().reset();
  storage = new MemoryKeyValueStore();
  haptic = [];
  const driver: HapticDriver = {
    tap: () => void haptic.push('tap'),
    mediumTap: () => void haptic.push('medium'),
    longTap: () => void haptic.push('long'),
    tick: () => void haptic.push('tick'),
    success: () => void haptic.push('success'),
    wait: async () => undefined,
  };
  setHapticDriver(driver);
  useNewTray();
});

afterEach(() => {
  scanMock.reset();
  setTrayStore(undefined);
  setHapticDriver(null);
});

describe('scanner screen', () => {
  it('queues six injected codes silently: six answers in the store, counted in the status bar, nothing over the camera, and a buzz for each answer', async () => {
    const source = await scanner();
    await act(async () => source.inject(DEV_SIX_CODES));

    await waitFor(() => expect(Object.values(useCardsStore.getState().byId)).toHaveLength(6));
    const cards = Object.values(useCardsStore.getState().byId);
    expect(cards.map((c) => c.release?.title).filter(Boolean).sort()).toEqual(['Dune', 'Ghostbusters', 'GoldenEye 007', 'Super Mario 64']);
    expect(cards.filter((c) => c.resolution === 'not_found')).toHaveLength(1);
    expect(cards.filter((c) => c.resolution === 'pending')).toHaveLength(1);
    expect(cards.filter((c) => c.alreadyOwnedCount > 0).map((c) => c.ownedAt[0]).sort()).toEqual(['Game room › Shelf 1', 'Living room › Shelf 2']);
    // The status bar counts what Review will show; the camera shows no cards and no strip.
    expect(await screen.findByText('6 items · 1 processing', { exact: false })).toBeTruthy();
    expect(screen.queryByTestId('result-cards')).toBeNull();
    expect(screen.queryAllByTestId(/^card-/)).toHaveLength(0);
    expect(screen.queryByLabelText('Snaps in this scan')).toBeNull();

    // 6 ticks right away; then two owned (2 taps each), two new (1 tap each), one not found (long).
    await waitFor(() => expect(haptic.filter((h) => h === 'long')).toHaveLength(1));
    await waitFor(() => expect(haptic.filter((h) => h === 'tap')).toHaveLength(6));
    expect(haptic.filter((h) => h === 'tick')).toHaveLength(6);
  });

  it('shows "Nice scan!" and ticks before any network answer', async () => {
    server.use(http.post('*/api/v1/libraries/:libraryId/scan-sessions', () => HttpResponse.error()));
    const source = await scanner();
    await act(async () => source.inject([code(DEV_CODES.duneOwned)]));

    expect(screen.getByText('Nice scan!')).toBeTruthy();
    expect(haptic).toContain('tick');
  });

  it('resolves the slow lookup over the event stream into the quiet queue, with no duplicate', async () => {
    const source = await scanner();
    await act(async () => source.inject([code(DEV_CODES.slow)]));
    await waitFor(() => expect(Object.values(useCardsStore.getState().byId)).toHaveLength(1));
    expect(Object.values(useCardsStore.getState().byId)[0]!.resolution).toBe('pending');
    expect(await screen.findByText('1 item · 1 processing', { exact: false })).toBeTruthy();
    expect(screen.queryByTestId('result-cards')).toBeNull();

    await act(async () => scanMock.resolvePending());

    await waitFor(() => expect(Object.values(useCardsStore.getState().byId)[0]!.release?.title).toBe('Zelda: Ocarina of Time'));
    expect(Object.values(useCardsStore.getState().byId)).toHaveLength(1);
    await waitFor(() => expect(screen.queryByText('1 item · 1 processing', { exact: false })).toBeNull());
    expect(screen.queryByTestId('result-cards')).toBeNull();
  });

  it('resumes the stream with Last-Event-ID after a drop and does not duplicate the quiet queue', async () => {
    const lastIds: (string | null)[] = [];
    server.events.on('request:start', ({ request }) => {
      if (request.url.endsWith('/events')) lastIds.push(request.headers.get('Last-Event-ID'));
    });
    const source = await scanner();
    await act(async () => source.inject([code(DEV_CODES.slow)]));
    await waitFor(() => expect(Object.values(useCardsStore.getState().byId)).toHaveLength(1));
    await waitFor(() => expect(scanMock.streams.size).toBe(1));

    await act(async () => {
      scanMock.dropStreams();
      scanMock.resolvePending(); // happens while the client is disconnected
    });

    await waitFor(() => expect(Object.values(useCardsStore.getState().byId)[0]!.release?.title).toBe('Zelda: Ocarina of Time'));
    expect(Object.values(useCardsStore.getState().byId)).toHaveLength(1);
    expect(lastIds.length).toBeGreaterThanOrEqual(2);
    expect(lastIds[lastIds.length - 1]).not.toBeNull();
    server.events.removeAllListeners();
  });

  it('falls back to polling when the stream keeps failing', async () => {
    server.use(http.get('*/api/v1/scan-sessions/:id/events', () => new HttpResponse(null, { status: 503 })));
    const source = await scanner();
    await act(async () => source.inject([code(DEV_CODES.slow)]));
    await waitFor(() => expect(Object.values(useCardsStore.getState().byId)).toHaveLength(1));

    await act(async () => scanMock.resolvePending());

    await waitFor(() => expect(Object.values(useCardsStore.getState().byId)[0]!.release?.title).toBe('Zelda: Ocarina of Time'));
    expect(Object.values(useCardsStore.getState().byId)).toHaveLength(1);
  });

  it('rejects an unreadable decode at capture with a rescan prompt and keeps the other answer', async () => {
    const source = await scanner();
    await act(async () => source.inject([code(DEV_CODES.duneOwned), code(DEV_CODES.unreadable)]));

    expect(await screen.findByText("Couldn't read that barcode. Scan it again.")).toBeTruthy();
    await waitFor(() => expect(Object.values(useCardsStore.getState().byId)).toHaveLength(1));
    const [card] = Object.values(useCardsStore.getState().byId);
    expect(card!.release?.title).toBe('Dune');
    expect(card!.alreadyOwnedCount).toBe(1);
    expect(card!.ownedAt[0]).toBe('Living room › Shelf 2');
    // Never queued: no answer for the decode that could never resolve, and nothing over the camera.
    expect(screen.queryByText(`Code ${DEV_CODES.unreadable}`)).toBeNull();
    expect(screen.queryByTestId('result-cards')).toBeNull();
  });

  it('rejects a partial-length code at capture without queuing it', async () => {
    const source = await scanner();
    await act(async () => source.inject([code('04549690593')]));

    expect(await screen.findByText("Couldn't read that barcode. Scan it again.")).toBeTruthy();
    expect(screen.queryByTestId(/^card-/)).toBeNull();
  });

  it('shows a viewer the counts but no cards and no add', async () => {
    asRole('viewer');
    const source = await scanner();
    await act(async () => source.inject([code(DEV_CODES.duneOwned), code(DEV_CODES.mario64New)]));

    await waitFor(() => expect(Object.values(useCardsStore.getState().byId)).toHaveLength(2));
    expect(await screen.findByText(/^2 items/)).toBeTruthy();
    expect(screen.queryByTestId('result-cards')).toBeNull();
    expect(screen.queryByLabelText('Add Super Mario 64')).toBeNull();
    expect(screen.queryByText('+ Add')).toBeNull();
    expect(screen.queryByText('Wishlist')).toBeNull();
  });

  it('routes to Review from the status bar', async () => {
    const source = await scanner();
    await act(async () => source.inject([code(DEV_CODES.duneOwned)]));
    await screen.findByText(/^1 item/);
    await fireEvent.press(screen.getByLabelText('Review 1 item'));
    expect(mockRouter.push).toHaveBeenCalledWith('/review');
  });

  it('reads "N items · M processing" in the status bar', async () => {
    const source = await scanner();
    await act(async () => source.inject([code(DEV_CODES.duneOwned), code(DEV_CODES.slow)]));
    expect(await screen.findByText('2 items · 1 processing', { exact: false })).toBeTruthy();
  });

  it('runs "Scan these 6 codes" from the dev menu', async () => {
    await scanner(new FakeScannerSource(), new FakeScannerSource());
    await fireEvent.press(screen.getByLabelText('Scan these 6 codes'));
    await waitFor(() => expect(Object.values(useCardsStore.getState().byId)).toHaveLength(6));
    expect(await screen.findByText('6 items · 1 processing', { exact: false })).toBeTruthy();
    expect(screen.queryByTestId('result-cards')).toBeNull();
  });

  it('does not show the dev menu without a dev source', async () => {
    await scanner();
    expect(screen.queryByLabelText('Scan these 6 codes')).toBeNull();
  });

  it('labels every button', async () => {
    const source = await scanner(new FakeScannerSource(), new FakeScannerSource());
    await act(async () => source.inject([code(DEV_CODES.mario64New)]));
    await waitFor(() => expect(Object.values(useCardsStore.getState().byId)).toHaveLength(1));
    const buttons = screen.getAllByRole('button');
    expect(buttons.length).toBeGreaterThan(3);
    for (const button of buttons) expect(button.props.accessibilityLabel).toBeTruthy();
  });
});

describe('scanner offline', () => {
  const offline = () =>
    server.use(
      http.post('*/api/v1/libraries/:libraryId/scan-sessions', () => HttpResponse.error()),
      http.post('*/api/v1/scan-sessions/:id/barcodes', () => HttpResponse.error()),
    );

  it('queues offline, keeps it across a restart, and sends it once when back online', async () => {
    offline();
    const source = await scanner();
    await act(async () => source.inject([code(DEV_CODES.mario64New), code(DEV_CODES.duneOwned)]));

    expect(await screen.findByText('2 items · 2 processing', { exact: false })).toBeTruthy();
    expect(screen.getByLabelText('Offline')).toBeTruthy();
    expect(screen.getByText(/Your scans are saved and will finish when you're back online/)).toBeTruthy();
    expect(scanMock.barcodeRequests).toHaveLength(0);

    // The app is killed: a new tray reads the same storage.
    await screen.unmount();
    useCardsStore.getState().reset();
    const restarted = useNewTray();
    expect(restarted.getState().lines).toHaveLength(2);

    server.resetHandlers(); // the connection is back
    await scanner();
    await waitFor(() => expect(Object.values(useCardsStore.getState().byId)).toHaveLength(2));
    expect(screen.queryByTestId('result-cards')).toBeNull();

    // Even after more retry rounds, the server saw the codes exactly once.
    await act(async () => {
      await new Promise((r) => setTimeout(r, 150));
    });
    const sentCodes = scanMock.barcodeRequests.flatMap((r) => r.codes).sort();
    expect(sentCodes).toEqual([DEV_CODES.duneOwned, DEV_CODES.mario64New].sort());
    expect(new Set(scanMock.barcodeRequests.map((r) => r.key)).size).toBe(scanMock.barcodeRequests.length);
    expect(scanMock.sessions.size).toBe(1);
  });

  // +1 and swipe-to-remove left the scanner with the receipt (#451): copies and removal now happen in Review.
});
