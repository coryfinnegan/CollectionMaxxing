// Auto-snap and the shutter on the scanner screen, with no camera: FakeScannerSource + FakeFrameSignals.
import { act, fireEvent, screen, waitFor } from '@testing-library/react-native';
import { AccessibilityInfo, Animated } from 'react-native';
import { setHapticDriver, type HapticDriver } from '@/lib/haptics';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { renderWithProviders } from '@/test/render';
import { DEFAULT_AUTO_SNAP_CONFIG } from '../autosnap/defaults';
import { FakeFrameSignals, STEADY_TEXT_VIEW } from '../autosnap/FakeFrameSignals';
import { fitLongEdge, prepareSnapPhoto, type Resizer } from '../autosnap/snapPhoto';
import type { SnapEvent } from '../autosnap/useAutoSnap';
import { useCardsStore } from '../cards/cardsStore';
import { setTrayStore } from '../tray/storage';
import { createTrayStore, MemoryKeyValueStore } from '../tray/trayStore';
import { ScannerScreen } from './ScannerScreen';
import { FakeScannerSource } from './ScannerSource';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));

const resize: Resizer = async (photo, target) => ({ uri: `${photo.uri}#resized`, width: target?.width ?? photo.width, height: target?.height ?? photo.height });
const tuning = { retryMs: 20, pollMs: 20, resendMs: 40, flashMs: 50 };

let ticks: number;
const flush = () => act(async () => void (await Promise.resolve()));

// The flash and the hint run on the real clock (setTimeout, setInterval, Date). A test that looks at them right after a press
// or a render loses that race on a loaded machine (the act can take longer than the flash lasts, or than the quiet spell
// before the hint), so these tests stop the clock: nothing changes until the test (or waitFor, which advances fake timers
// itself) moves it. By default Date stays real, for a test that measures a wall-clock budget; `date: true` stops it too, for a
// test that reads Date to decide something. afterEach restores everything.
const holdTheClock = ({ date = false }: { date?: boolean } = {}) =>
  jest.useFakeTimers(date ? {} : { doNotFake: ['Date', 'performance'] });

const setup = async ({ reduceMotion = false }: { reduceMotion?: boolean } = {}) => {
  jest.spyOn(AccessibilityInfo, 'isReduceMotionEnabled').mockResolvedValue(reduceMotion);
  const signals = new FakeFrameSignals();
  const source = new FakeScannerSource(signals);
  const snaps: SnapEvent[] = [];
  await renderWithProviders(
    <ScannerScreen
      libraryId={MOCK_LIBRARY_ID}
      source={source}
      tuning={tuning}
      onSnap={(e) => snaps.push(e)}
      autoSnapConfig={{ ...DEFAULT_AUTO_SNAP_CONFIG, hintAfterMs: 150 }}
      autoSnapTuning={{ flashMs: 300, hintCheckMs: 30, resize }}
    />,
  );
  return { signals, source, snaps };
};

beforeEach(() => {
  ticks = 0;
  const driver: HapticDriver = {
    tap: () => undefined,
    mediumTap: () => undefined,
    longTap: () => undefined,
    tick: () => void (ticks += 1),
    success: () => undefined,
    wait: async () => undefined,
  };
  setHapticDriver(driver);
  useCardsStore.getState().reset();
  setTrayStore(createTrayStore({ storage: new MemoryKeyValueStore(), newKey: () => 'batch', now: () => 1_000 }));
});

afterEach(() => {
  jest.useRealTimers();
  jest.restoreAllMocks();
  setTrayStore(undefined);
  setHapticDriver(null);
});

describe('scanner auto-snap', () => {
  it('takes one picture of a steady text-bearing view, scaled, with the auto trigger', async () => {
    const { signals, source, snaps } = await setup();
    await act(async () => {
      signals.emit(STEADY_TEXT_VIEW);
      signals.emit(STEADY_TEXT_VIEW);
    });
    await flush();
    await flush();
    await flush();
    expect(source.captures).toBe(1);
    expect(snaps).toHaveLength(1);
    expect(snaps[0]!.trigger).toBe('auto');
    expect(snaps[0]!.shotKind).toBe('shelf_segment'); // six text rows
    expect(snaps[0]!.photo.uri).toContain('#resized');
    expect(Math.max(snaps[0]!.photo.width, snaps[0]!.photo.height)).toBeLessThanOrEqual(2576);
  });

  it('does not snap while a barcode is being read', async () => {
    const { signals, source, snaps } = await setup();
    await act(async () => signals.emit({ ...STEADY_TEXT_VIEW, barcodesInView: 1 }));
    await flush();
    expect(source.captures).toBe(0);
    expect(snaps).toHaveLength(0);
  });

  it('takes a picture on the shutter even with a barcode in view, with the shutter trigger', async () => {
    const { signals, snaps } = await setup();
    await act(async () => signals.emit({ ...STEADY_TEXT_VIEW, barcodesInView: 1 }));
    await fireEvent.press(screen.getByLabelText('Take picture'));
    await flush();
    await flush();
    await flush();
    expect(snaps.map((s) => s.trigger)).toEqual(['shutter']);
    expect(snaps.map((s) => s.shotKind)).toEqual(['barcode']); // in view wins, even on the shutter
  });

  it('shows "Nice scan!" and buzzes at once on the shutter, before the picture arrives', async () => {
    holdTheClock();
    const { source } = await setup();
    let release: () => void = () => undefined;
    source.capturePhoto = () => new Promise((resolve) => (release = () => resolve({ uri: 'file:///x.jpg', width: 100, height: 100, orientation: 1 })));
    await fireEvent.press(screen.getByLabelText('Take picture'));
    expect(screen.getByText('Nice scan!')).toBeTruthy();
    expect(ticks).toBe(1);
    release();
    await flush();
  });

  it('shows "Nice scan!" within 100 ms and takes it away by itself', async () => {
    holdTheClock(); // timers only; Date stays real, so the 100 ms budget below is still measured on the wall clock
    await setup();
    const started = Date.now();
    await fireEvent.press(screen.getByLabelText('Take picture'));
    expect(screen.getByText('Nice scan!')).toBeTruthy();
    expect(Date.now() - started).toBeLessThanOrEqual(100);
    await waitFor(() => expect(screen.queryByText('Nice scan!')).toBeNull());
  });

  it('does not animate the flash with Reduce Motion, and does with it off', async () => {
    holdTheClock();
    const timing = jest.spyOn(Animated, 'timing');
    await setup({ reduceMotion: true });
    await flush();
    await fireEvent.press(screen.getByLabelText('Take picture'));
    expect(screen.getByText('Nice scan!')).toBeTruthy();
    expect(timing).not.toHaveBeenCalled();
  });

  it('animates the flash when Reduce Motion is off', async () => {
    const timing = jest.spyOn(Animated, 'timing');
    await setup();
    await flush();
    await fireEvent.press(screen.getByLabelText('Take picture'));
    expect(timing).toHaveBeenCalled();
  });

  it('suggests the flash after a quiet spell, turns it on when tapped, and clears on a snap', async () => {
    holdTheClock({ date: true }); // the quiet spell (hintAfterMs 150) is read off Date by an interval: only the test spends it
    const { source } = await setup();
    expect(screen.queryByText('Try the flash or move closer')).toBeNull();
    await act(async () => void jest.advanceTimersByTime(149)); // not yet
    expect(screen.queryByText('Try the flash or move closer')).toBeNull();
    await act(async () => void jest.advanceTimersByTime(1 + 30)); // quiet for 150 ms, and the 30 ms check has run
    expect(screen.getByText('Try the flash or move closer')).toBeTruthy();
    await fireEvent.press(screen.getByText('Try the flash or move closer'));
    expect(source.flash).toBe(true);
    await fireEvent.press(screen.getByLabelText('Take picture'));
    expect(screen.queryByText('Try the flash or move closer')).toBeNull();
  });
});

describe('snap pictures', () => {
  it('scale to a long edge of 2576 px and leave smaller ones alone', () => {
    expect(fitLongEdge(4032, 3024)).toEqual({ width: 2576, height: 1932 });
    expect(fitLongEdge(3024, 4032)).toEqual({ width: 1932, height: 2576 });
    expect(fitLongEdge(2000, 1500)).toBeNull();
  });

  it('go through the resizer with the fitted size', async () => {
    const photo = await prepareSnapPhoto({ uri: 'file:///a.jpg', width: 4032, height: 3024, orientation: 1 }, resize);
    expect(photo).toEqual({ uri: 'file:///a.jpg#resized', width: 2576, height: 1932 });
  });
});
