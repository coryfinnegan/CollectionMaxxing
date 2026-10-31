// The scanner does nothing while its screen is not the one being looked at (another tab is open, or the app is in the
// background): no barcodes are read, no pictures taken and sent, and nothing keeps retrying. When the screen comes back
// everything works again. The tab bar keeps the Scan tab mounted (docs.expo.dev "Native tabs": tab screens render eagerly),
// so the screen itself has to stop.
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

  it('does not keep retrying what is waiting to be sent, and picks it up again when the screen comes back', async () => {
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

    await setActive(false);
    await wait(60); // lets an attempt that was already on its way finish
    const before = attempts;
    await wait(200); // several retry intervals
    expect(attempts).toBe(before);

    offline = false;
    await setActive(true);
    await waitFor(() => expect(scanMock.sessions.size).toBeGreaterThan(0));
  });

  it('does not keep retrying a picture that is waiting to be sent, and sends it when the screen comes back', async () => {
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
    expect(creates).toBe(before);

    offline = false;
    await setActive(true);
    await waitFor(() => expect(identifyMock.requests).toHaveLength(1));
  });
});
