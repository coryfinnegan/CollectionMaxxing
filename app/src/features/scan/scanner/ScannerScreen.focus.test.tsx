// The camera side of the scanner does nothing while its screen is not the one being looked at (another tab is open, Review is
// on top, or the app is in the background): no barcodes are read and no pictures are taken. When the screen comes back it
// all works again. The tab bar keeps the Scan tab mounted (docs.expo.dev "Native tabs": tab screens render eagerly), so the
// screen itself has to stop. Sending what was already taken is not camera work: it goes on, because Review is pushed on top of
// the scanner and the person waits there for the answers.
import { act, fireEvent, screen, waitFor } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import { useEffect, useState } from 'react';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { identifyMock } from '@/mocks/identify';
import { scanMock } from '@/mocks/scan';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { DEFAULT_AUTO_SNAP_CONFIG } from '../autosnap/defaults';
import { FakeFrameSignals, STEADY_TEXT_VIEW } from '../autosnap/FakeFrameSignals';
import type { Resizer } from '../autosnap/snapPhoto';
import type { SnapEvent } from '../autosnap/useAutoSnap';
import { useCardsStore } from '../cards/cardsStore';
import { picItemOf } from '../pipeline/payload';
import { makeKit, readItem, readingOf, resetKit } from '../pipeline/testKit';
import { getTrayStore, setTrayStore } from '../tray/storage';
import { createTrayStore, MemoryKeyValueStore } from '../tray/trayStore';
import { DEV_CODES } from './devCodes';
import { ScannerScreen } from './ScannerScreen';
import { FakeScannerSource } from './ScannerSource';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));

const resize: Resizer = async (photo, target) => ({ uri: `${photo.uri}#resized`, width: target?.width ?? photo.width, height: target?.height ?? photo.height });
const tuning = { retryMs: 20, pollMs: 20, resendMs: 30, flashMs: 50 };
const flush = () => act(async () => void (await Promise.resolve()));
const wait = (ms: number) => act(async () => void (await new Promise((resolve) => setTimeout(resolve, ms))));

const controls = { setActive: (_active: boolean) => undefined as void };

beforeEach(() => {
  scanMock.reset();
  identifyMock.reset();
  useCardsStore.getState().reset();
  setTrayStore(createTrayStore({ storage: new MemoryKeyValueStore(), newKey: () => 'batch', now: () => 1_000 }));
});
afterEach(() => {
  resetKit();
  scanMock.reset();
  setTrayStore(undefined);
});

/** The scanner with its screen focus in the test's hands. */
async function open(options: { pipeline?: ReturnType<typeof makeKit>['deps']; onSnap?: (event: SnapEvent) => void; initiallyActive?: boolean } = {}) {
  const signals = new FakeFrameSignals();
  const source = new FakeScannerSource(signals);
  function Harness() {
    const [active, setActive] = useState(options.initiallyActive ?? true);
    useEffect(() => {
      controls.setActive = setActive;
    }, []);
    return (
      <ScannerScreen
        libraryId={MOCK_LIBRARY_ID}
        source={source}
        active={active}
        tuning={tuning}
        pipeline={options.pipeline}
        onSnap={options.onSnap}
        autoSnapConfig={{ ...DEFAULT_AUTO_SNAP_CONFIG, hintAfterMs: 100, cooldownMs: 0 }}
        autoSnapTuning={{ flashMs: 50, hintCheckMs: 20, resize }}
      />
    );
  }
  await renderWithProviders(<Harness />);
  return { signals, source };
}
const setActive = (active: boolean) => act(async () => controls.setActive(active));
const OTHER_SHELF = { ...STEADY_TEXT_VIEW, viewHash: 'fedcba9876543210' };
const steadyShelf = (signals: FakeFrameSignals, view = STEADY_TEXT_VIEW) =>
  act(async () => {
    signals.emit(view);
    signals.emit(view);
  });

describe('a scanner whose screen is not showing', () => {
  it('takes no automatic picture, and the shutter does nothing either', async () => {
    const snaps: SnapEvent[] = [];
    const { signals, source } = await open({ onSnap: (e) => snaps.push(e), initiallyActive: false });

    await steadyShelf(signals);
    await fireEvent.press(screen.getByLabelText('Take picture'));
    await flush();
    await flush();

    expect(source.captures).toBe(0);
    expect(snaps).toEqual([]);
  });

  it('reads no barcodes into the tray', async () => {
    const { source } = await open({ initiallyActive: false });

    await act(async () => source.inject([{ value: DEV_CODES.duneOwned, symbology: 'ean-13' }]));
    await flush();

    expect(getTrayStore(MOCK_LIBRARY_ID).getState().lines).toEqual([]);
  });

  it('stops taking pictures when the screen is left, and starts again when it comes back', async () => {
    const snaps: SnapEvent[] = [];
    const { signals, source } = await open({ onSnap: (e) => snaps.push(e) });
    await steadyShelf(signals);
    await waitFor(() => expect(snaps).toHaveLength(1)); // showing: one picture

    await setActive(false);
    await wait(60);
    await steadyShelf(signals);
    await wait(30);
    expect(source.captures).toBe(1); // blurred: nothing more

    await setActive(true);
    await wait(30);
    await steadyShelf(signals, OTHER_SHELF); // another view, so the cooldown after the first picture is over
    await waitFor(() => expect(snaps.length).toBeGreaterThan(1)); // back: it takes pictures again
  });

  it('stops reading barcodes when the screen is left, and reads them again when it comes back', async () => {
    const { source } = await open();
    const lines = () => getTrayStore(MOCK_LIBRARY_ID).getState().lines;

    await setActive(false);
    await act(async () => source.inject([{ value: DEV_CODES.duneOwned, symbology: 'ean-13' }]));
    expect(lines()).toEqual([]);

    await setActive(true);
    await act(async () => source.inject([{ value: DEV_CODES.duneOwned, symbology: 'ean-13' }]));
    await waitFor(() => expect(lines()).toHaveLength(1));
  });

  it('keeps retrying what is waiting to be sent while the screen is away, and sends it without the screen coming back', async () => {
    let attempts = 0;
    let offline = true;
    server.use(
      http.post('*/api/v1/libraries/:libraryId/scan-sessions', () => {
        attempts += 1;
        return offline ? HttpResponse.error() : undefined;
      }),
    );
    const { source } = await open();
    await act(async () => source.inject([{ value: DEV_CODES.duneOwned, symbology: 'ean-13' }]));
    await waitFor(() => expect(attempts).toBeGreaterThan(0));

    await setActive(false); // Review is pushed on top, and the person waits there
    await wait(60); // lets an attempt that was already on its way finish
    const before = attempts;
    await wait(200); // several retry intervals
    expect(attempts).toBeGreaterThan(before);

    offline = false;
    await waitFor(() => expect(scanMock.sessions.size).toBeGreaterThan(0));
    expect(getTrayStore(MOCK_LIBRARY_ID).getState().online).toBe(true);
  });

  it('keeps retrying a picture that is waiting to be sent while the screen is away, and sends it without the screen coming back', async () => {
    const kit = makeKit();
    kit.reader.script(readingOf([readItem('Dune')]));
    let offline = true;
    let creates = 0;
    server.use(
      http.post('*/api/v1/libraries/:libraryId/scan-sessions', () => {
        creates += 1;
        return offline ? HttpResponse.error() : undefined;
      }),
    );
    await open({ pipeline: kit.deps });
    await fireEvent.press(screen.getByLabelText('Take picture'));
    await waitFor(() => expect(creates).toBeGreaterThan(0));

    await setActive(false);
    await wait(60);
    const before = creates;
    await wait(200);
    expect(creates).toBeGreaterThan(before);

    offline = false;
    await waitFor(() => expect(identifyMock.requests).toHaveLength(1));
  });

  it('sends a picture that failed once and is waiting out its backoff while the screen is away', async () => {
    const kit = makeKit();
    let clock = 1_000_000;
    kit.store.getState().addSnap({
      snapId: 'snap-waiting',
      libraryId: MOCK_LIBRARY_ID,
      trigger: 'shutter',
      createdAt: 1,
      thumbUri: 'file:///snaps/x.jpg',
      phase: 'read',
      sent: false,
      items: [picItemOf(readItem('Dune'))],
      unreadableCount: 0,
      engine: 'apple_model',
    });
    let broken = true;
    let tries = 0;
    server.use(
      http.post('*/api/v1/scan-sessions/:id/identify', () => {
        tries += 1;
        return broken ? HttpResponse.json({ title: 'Server problem', status: 500 }, { status: 500, headers: { 'Content-Type': 'application/problem+json' } }) : undefined;
      }),
    );
    await open({ pipeline: { ...kit.deps, now: () => clock }, initiallyActive: false });
    await waitFor(() => expect(tries).toBe(1)); // the first try fails; the next waits out its backoff
    await wait(120);
    expect(tries).toBe(1);

    broken = false;
    clock += 60_000; // the wait is over; Review is still on top
    await waitFor(() => expect(identifyMock.requests).toHaveLength(1));
    expect(kit.store.getState().snaps[0]).toMatchObject({ snapId: 'snap-waiting', sent: true });
  });

  it('sends what a previous run left waiting as soon as the scanner mounts, before its screen was ever the active one', async () => {
    const kit = makeKit();
    kit.store.getState().addSnap({
      snapId: 'snap-left-behind',
      libraryId: MOCK_LIBRARY_ID,
      trigger: 'shutter',
      createdAt: 1,
      thumbUri: 'file:///snaps/x.jpg',
      phase: 'read',
      sent: false,
      items: [picItemOf(readItem('Dune'))],
      unreadableCount: 0,
      engine: 'apple_model',
    });
    getTrayStore(MOCK_LIBRARY_ID).getState().scan({ value: DEV_CODES.duneOwned, symbology: 'ean-13' });

    await open({ pipeline: kit.deps, initiallyActive: false });

    await waitFor(() => expect(identifyMock.requests).toHaveLength(1));
    await waitFor(() => expect(scanMock.barcodeRequests.map((r) => r.codes)).toEqual([[DEV_CODES.duneOwned]]));
  });
});
