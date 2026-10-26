// The scanner end to end without a camera: FakeScannerSource -> tray -> mock API -> mock event stream -> cards.
import { act, fireEvent, screen, waitFor } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import { type HapticDriver, setHapticDriver } from '@/lib/haptics';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { scanMock } from '@/mocks/scan';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { NOT_FOUND_COPY } from '../cards/copy';
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
  it('turns six injected codes into six cards, says what you own, and buzzes for each answer', async () => {
    const source = await scanner();
    await act(async () => source.inject(DEV_SIX_CODES));

    expect(await screen.findByText('Dune')).toBeTruthy();
    expect(await screen.findByText('Super Mario 64')).toBeTruthy();
    expect(await screen.findByText('Ghostbusters')).toBeTruthy();
    expect(await screen.findByText('GoldenEye 007')).toBeTruthy();
    expect(await screen.findByText(NOT_FOUND_COPY)).toBeTruthy();
    expect(await screen.findByText('Looking up…')).toBeTruthy();
    expect(screen.getByText('Already own — Paperback, Living room › Shelf 2')).toBeTruthy();
    expect(screen.getByText('Already own — Nintendo 64, Game room › Shelf 1')).toBeTruthy();
    expect(screen.getAllByTestId(/^card-/)).toHaveLength(6);

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

  it('shows the slow lookup resolving over the event stream, with no duplicate card', async () => {
    const source = await scanner();
    await act(async () => source.inject([code(DEV_CODES.slow)]));
    expect(await screen.findByText('Looking up…')).toBeTruthy();

    await act(async () => scanMock.resolvePending());

    expect(await screen.findByText('Zelda: Ocarina of Time')).toBeTruthy();
    expect(screen.getAllByTestId(/^card-/)).toHaveLength(1);
  });

  it('resumes the stream with Last-Event-ID after a drop and does not duplicate cards', async () => {
    const lastIds: (string | null)[] = [];
    server.events.on('request:start', ({ request }) => {
      if (request.url.endsWith('/events')) lastIds.push(request.headers.get('Last-Event-ID'));
    });
    const source = await scanner();
    await act(async () => source.inject([code(DEV_CODES.slow)]));
    await screen.findByText('Looking up…');
    await waitFor(() => expect(scanMock.streams.size).toBe(1));

    await act(async () => {
      scanMock.dropStreams();
      scanMock.resolvePending(); // happens while the client is disconnected
    });

    expect(await screen.findByText('Zelda: Ocarina of Time')).toBeTruthy();
    expect(screen.getAllByTestId(/^card-/)).toHaveLength(1);
    expect(lastIds.length).toBeGreaterThanOrEqual(2);
    expect(lastIds[lastIds.length - 1]).not.toBeNull();
    server.events.removeAllListeners();
  });

  it('falls back to polling when the stream keeps failing', async () => {
    server.use(http.get('*/api/v1/scan-sessions/:id/events', () => new HttpResponse(null, { status: 503 })));
    const source = await scanner();
    await act(async () => source.inject([code(DEV_CODES.slow)]));
    await screen.findByText('Looking up…');

    await act(async () => scanMock.resolvePending());

    expect(await screen.findByText('Zelda: Ocarina of Time')).toBeTruthy();
    expect(screen.getAllByTestId(/^card-/)).toHaveLength(1);
  });

  it('rejects an unreadable decode at capture with a rescan prompt and keeps the other cards', async () => {
    const source = await scanner();
    await act(async () => source.inject([code(DEV_CODES.duneOwned), code(DEV_CODES.unreadable)]));

    expect(await screen.findByText("Couldn't read that barcode. Scan it again.")).toBeTruthy();
    expect(await screen.findByText('Dune')).toBeTruthy();
    expect(screen.getByText('Already own — Paperback, Living room › Shelf 2')).toBeTruthy();
    // Never queued: no error card for the decode that could never resolve.
    expect(screen.queryByText(`Code ${DEV_CODES.unreadable}`)).toBeNull();
    expect(screen.getAllByTestId(/^card-/)).toHaveLength(1);
  });

  it('rejects a partial-length code at capture without queuing it', async () => {
    const source = await scanner();
    await act(async () => source.inject([code('04549690593')]));

    expect(await screen.findByText("Couldn't read that barcode. Scan it again.")).toBeTruthy();
    expect(screen.queryByTestId(/^card-/)).toBeNull();
  });

  it('offers + Add to an editor and adds only that item', async () => {
    asRole('editor');
    const source = await scanner();
    await act(async () => source.inject([code(DEV_CODES.mario64New), code(DEV_CODES.ghostbustersNew)]));

    await fireEvent.press(await screen.findByLabelText('Add Super Mario 64'));

    expect(await screen.findByText('Added to this scan')).toBeTruthy();
    const [mario, ghostbusters] = [...scanMock.sessions.values()][0]!.candidates;
    expect(mario!.decision).toBe('accepted');
    expect(ghostbusters!.decision).toBe('undecided');
  });

  it('shows a viewer the cards and ownership but no + Add', async () => {
    asRole('viewer');
    const source = await scanner();
    await act(async () => source.inject([code(DEV_CODES.duneOwned), code(DEV_CODES.mario64New)]));

    expect(await screen.findByText('Super Mario 64')).toBeTruthy();
    expect(screen.getByText('Already own — Paperback, Living room › Shelf 2')).toBeTruthy();
    expect(screen.queryByLabelText('Add Super Mario 64')).toBeNull();
    expect(screen.queryByText('+ Add')).toBeNull();
    expect(screen.queryByText('Wishlist')).toBeNull();
  });

  it('opens the result detail when a card is tapped', async () => {
    const source = await scanner();
    await act(async () => source.inject([code(DEV_CODES.duneOwned)]));
    await fireEvent.press(await screen.findByLabelText(/^Open Dune/));
    expect(mockRouter.push).toHaveBeenCalledWith({ pathname: '/result/[id]', params: { id: expect.any(String) } });
  });

  it('routes to Review from the status bar', async () => {
    const source = await scanner();
    await act(async () => source.inject([code(DEV_CODES.duneOwned)]));
    await screen.findByText('Dune');
    await fireEvent.press(screen.getByLabelText('Review'));
    expect(mockRouter.push).toHaveBeenCalledWith('/review');
  });

  it('reads "N items · M processing" in the status bar', async () => {
    const source = await scanner();
    await act(async () => source.inject([code(DEV_CODES.duneOwned), code(DEV_CODES.slow)]));
    expect(await screen.findByText('2 items · 1 processing')).toBeTruthy();
  });

  it('runs "Scan these 6 codes" from the dev menu', async () => {
    await scanner(new FakeScannerSource(), new FakeScannerSource());
    await fireEvent.press(screen.getByLabelText('Scan these 6 codes'));
    expect(await screen.findByText('Dune')).toBeTruthy();
    expect(screen.getAllByTestId(/^card-/)).toHaveLength(6);
  });

  it('does not show the dev menu without a dev source', async () => {
    await scanner();
    expect(screen.queryByLabelText('Scan these 6 codes')).toBeNull();
  });

  it('labels every button', async () => {
    const source = await scanner(new FakeScannerSource(), new FakeScannerSource());
    await act(async () => source.inject([code(DEV_CODES.mario64New)]));
    await screen.findByText('Super Mario 64');
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

  it('shows QUEUED offline, keeps it across a restart, and sends it once when back online', async () => {
    offline();
    const source = await scanner();
    await act(async () => source.inject([code(DEV_CODES.mario64New), code(DEV_CODES.duneOwned)]));

    await fireEvent.press(await screen.findByLabelText(/Show the receipt/));
    expect((await screen.findAllByText('QUEUED')).length).toBe(2);
    expect(screen.getByText(/Your scans are saved and will finish when you're back online/)).toBeTruthy();
    expect(scanMock.barcodeRequests).toHaveLength(0);

    // The app is killed: a new tray reads the same storage.
    await screen.unmount();
    useCardsStore.getState().reset();
    const restarted = useNewTray();
    expect(restarted.getState().lines).toHaveLength(2);

    server.resetHandlers(); // the connection is back
    await scanner();
    await waitFor(() => expect(screen.getAllByTestId(/^card-/)).toHaveLength(2));

    // Even after more retry rounds, the server saw the codes exactly once.
    await act(async () => {
      await new Promise((r) => setTimeout(r, 150));
    });
    const sentCodes = scanMock.barcodeRequests.flatMap((r) => r.codes).sort();
    expect(sentCodes).toEqual([DEV_CODES.duneOwned, DEV_CODES.mario64New].sort());
    expect(new Set(scanMock.barcodeRequests.map((r) => r.key)).size).toBe(scanMock.barcodeRequests.length);
    expect(scanMock.sessions.size).toBe(1);
  });

  it('removes a line with the swipe action and adds a copy with +1', async () => {
    offline();
    const source = await scanner();
    await act(async () => source.inject([code(DEV_CODES.mario64New), code(DEV_CODES.duneOwned)]));
    await fireEvent.press(await screen.findByLabelText(/Show the receipt/));

    await fireEvent.press(screen.getByLabelText(`Add another copy of ${DEV_CODES.mario64New}`));
    expect(await screen.findByText(/^3 items/)).toBeTruthy();

    const row = screen.getByLabelText(new RegExp(`^${DEV_CODES.duneOwned}, 1 copy`));
    await act(async () => row.props.onAccessibilityAction({ nativeEvent: { actionName: 'delete' } }));
    expect(await screen.findByText(/^2 items/)).toBeTruthy();
    expect(screen.queryByLabelText(new RegExp(`^${DEV_CODES.duneOwned}`))).toBeNull();
  });
});
