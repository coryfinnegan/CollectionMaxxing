// A picture the person took on purpose (the shutter) that is dropped, because it could not be read or showed nothing, says so in
// the status line; one auto-snap took on its own stays quiet, since auto-snap already backs off after misses. A failure is
// logged either way.
import { screen, waitFor, fireEvent } from '@testing-library/react-native';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { identifyMock } from '@/mocks/identify';
import { scanMock } from '@/mocks/scan';
import { renderWithProviders } from '@/test/render';
import type { Resizer } from '../autosnap/snapPhoto';
import { useCardsStore } from '../cards/cardsStore';
import { ScannerScreen } from '../scanner/ScannerScreen';
import { FakeScannerSource } from '../scanner/ScannerSource';
import { SnapPipeline } from './SnapPipeline';
import { makeKit, PHOTO, readItem, readingOf, resetKit } from './testKit';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));

const dune = readItem('Dune', { box: { x: 0.05, y: 0.1, width: 0.2, height: 0.7 } });
const resize: Resizer = async (photo, target) => ({ uri: `${photo.uri}#resized`, width: target?.width ?? photo.width, height: target?.height ?? photo.height });
const failing = { read: () => Promise.reject(new Error('the model fell over')) };
const NOTHING_READ = /Couldn.t read that\. Move a little closer\./;
const READ_FAILED = /Couldn.t read that picture/;

beforeEach(() => {
  scanMock.reset();
  identifyMock.reset();
  useCardsStore.getState().reset();
});
afterEach(() => {
  resetKit();
  scanMock.reset();
  jest.restoreAllMocks();
});

function watched(deps: ConstructorParameters<typeof SnapPipeline>[0]) {
  const pipeline = new SnapPipeline(deps);
  const notices: (string | null)[] = [];
  pipeline.setNoticeHandler((message) => notices.push(message));
  return { pipeline, notices };
}

describe('a shutter picture that is dropped', () => {
  it('says so when it showed nothing', async () => {
    const kit = makeKit();
    kit.reader.script(readingOf([]));
    const { pipeline, notices } = watched(kit.deps);

    await pipeline.onSnap({ photo: PHOTO, trigger: 'shutter', shotKind: 'unknown' });

    expect(notices).toHaveLength(1);
    expect(notices[0]).toMatch(NOTHING_READ);
  });

  it('says so when it could not be read, and logs why', async () => {
    const kit = makeKit();
    const warn = jest.spyOn(console, 'warn').mockImplementation(() => undefined);
    const { pipeline, notices } = watched({ ...kit.deps, recognition: failing });

    await pipeline.onSnap({ photo: PHOTO, trigger: 'shutter', shotKind: 'unknown' });

    expect(notices).toHaveLength(1);
    expect(notices[0]).toMatch(READ_FAILED);
    expect(warn).toHaveBeenCalledWith(expect.stringContaining('the model fell over'));
    expect(kit.store.getState().snaps).toEqual([]); // still dropped, never a crash
  });

  it('says so for a cover snapped for a Review row that showed nothing, and the row keeps waiting', async () => {
    const kit = makeKit();
    kit.reader.script(readingOf([]));
    const { pipeline, notices } = watched(kit.deps);
    const onDone = jest.fn();

    await pipeline.onSnap({ photo: PHOTO, trigger: 'shutter', shotKind: 'single_cover' }, { bound: { candidateId: 'row-1', onDone } });

    expect(notices[0]).toMatch(NOTHING_READ);
    expect(onDone).not.toHaveBeenCalled();
  });

  it('clears the line again once a picture is read', async () => {
    const kit = makeKit();
    kit.reader.script(readingOf([]), readingOf([dune]));
    const { pipeline, notices } = watched(kit.deps);

    await pipeline.onSnap({ photo: PHOTO, trigger: 'shutter', shotKind: 'unknown' });
    await pipeline.onSnap({ photo: PHOTO, trigger: 'shutter', shotKind: 'unknown' });

    expect(notices.at(-1)).toBeNull();
  });
});

describe('a picture auto-snap or an import dropped', () => {
  it.each(['auto', 'import'] as const)('says nothing for %s when it showed nothing', async (trigger) => {
    const kit = makeKit();
    kit.reader.script(readingOf([]));
    const { pipeline, notices } = watched(kit.deps);

    await pipeline.onSnap({ photo: PHOTO, trigger, shotKind: 'unknown' });

    expect(notices.filter(Boolean)).toEqual([]);
  });

  it('says nothing for auto when it could not be read, but still logs it', async () => {
    const kit = makeKit();
    const warn = jest.spyOn(console, 'warn').mockImplementation(() => undefined);
    const { pipeline, notices } = watched({ ...kit.deps, recognition: failing });

    await pipeline.onSnap({ photo: PHOTO, trigger: 'auto', shotKind: 'unknown' });

    expect(notices.filter(Boolean)).toEqual([]);
    expect(warn).toHaveBeenCalledWith(expect.stringContaining('the model fell over'));
  });
});

describe('on the scanner screen', () => {
  const open = async (kit: ReturnType<typeof makeKit>, noticeMs?: number) =>
    renderWithProviders(
      <ScannerScreen
        libraryId={MOCK_LIBRARY_ID}
        source={new FakeScannerSource()}
        pipeline={kit.deps}
        tuning={{ retryMs: 20, pollMs: 20, resendMs: 40, flashMs: 50, noticeMs }}
        autoSnapTuning={{ flashMs: 50, resize }}
      />,
    );

  it('shows the line under the camera for a shutter picture that showed nothing, then lets it go', async () => {
    const kit = makeKit();
    kit.reader.script(readingOf([]));
    await open(kit, 150);

    await fireEvent.press(screen.getByLabelText('Take picture'));

    expect(await screen.findByText(NOTHING_READ)).toBeTruthy();
    await waitFor(() => expect(screen.queryByText(NOTHING_READ)).toBeNull());
  });
});
