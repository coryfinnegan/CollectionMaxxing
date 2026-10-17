// No picture file outlives its use or a clear (docs/02 "Privacy"). The fake file system records every file made anywhere,
// temp and cache folders included, so a copy that leaves its source behind shows up here.
import { QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react-native';
import { clearLocalData } from '@/features/me/localData';
import { Button } from '@/ui';
import { makeQueryClient, renderWithProviders } from '@/test/render';
import { CloseScannerSheet } from '../close/CloseScannerSheet';
import { useCloseScanner } from '../close/useCloseScanner';
import { EngineSelector } from '../recognition/EngineSelector';
import { DEFAULT_RECOGNITION_SETTINGS } from '../recognition/defaults';
import { line, modelItem, okRead, RecordingVision, STILL, type Scene } from '../recognition/fakes';
import type { Still, StillCropper } from '../recognition/types';
import type { Rect } from '@/native/cm-vision';
import { MemoryKeyValueStore } from '../tray/trayStore';
import { discardAllPictures, setPictureFiles, trackedPictures, trackPicture } from '../pictureFiles';
import { clearPipelineData } from './clearPipelineData';
import { SnapStoreBridge } from './SnapStoreBridge';
import { SnapPipeline } from './SnapPipeline';
import { FakeFileSystem, makeKit, readingOf, resetKit, readItem } from './testKit';

afterEach(() => resetKit());

const dune = readItem('Dune', { box: { x: 0.05, y: 0.1, width: 0.2, height: 0.7 } });
const mario = readItem('Super Mario 64', { kind: 'game', platform: 'Nintendo 64', box: { x: 0.3, y: 0.1, width: 0.2, height: 0.3 } });

/** The two halves of a wide shot, cut into new files the way expo-image-manipulator does. */
class FsCropper implements StillCropper {
  readonly regions: Rect[] = [];
  constructor(private readonly fs: FakeFileSystem) {}
  async crop(still: Still, region: Rect): Promise<Still> {
    this.regions.push(region);
    return { uri: this.fs.create(`file:///cache/ImageManipulator/half-${this.regions.length}.jpg`), width: still.width * region.width, height: still.height * region.height };
  }
}

const SNAP = 'snap-halves';
const spine = (name: string, x: number) => line(name, x, 0.1, 0.04, 0.6);
const many = (names: string[]) => names.map((n, i) => modelItem(n, n, i));

/** A real selector reading a six-spine shelf in two halves; the whole shot is the stored still of the snap. */
function wideShelf(kit: ReturnType<typeof makeKit>) {
  const stillUri = `file:///snaps/${SNAP}-still.jpg`;
  const halves = (n: number) => `file:///cache/ImageManipulator/half-${n}.jpg`;
  const scenes: Record<string, Scene> = {
    [stillUri]: {
      read: okRead(many(['Alpha', 'Bravo', 'Cujo', 'Delta'])),
      text: [spine('Alpha', 0.05), spine('Bravo', 0.2), spine('Cujo', 0.45), spine('Delta', 0.8), spine('Echo', 0.9), spine('Foxtrot', 0.95)],
    },
    [halves(1)]: { read: okRead(many(['Alpha', 'Bravo', 'Cujo'])), text: [line('Alpha', 0.1, 0.1, 0.05, 0.6), line('Bravo', 0.4, 0.1, 0.05, 0.6), line('Cujo', 0.8, 0.1, 0.1, 0.6)] },
    [halves(2)]: {
      read: okRead(many(['Cujo', 'Delta', 'Echo', 'Foxtrot'])),
      text: [line('Cujo', 0.0, 0.1, 0.05, 0.6), line('Delta', 0.5, 0.1, 0.05, 0.6), line('Echo', 0.7, 0.1, 0.05, 0.6), line('Foxtrot', 0.85, 0.1, 0.05, 0.6)],
    },
  };
  const settings = { ...DEFAULT_RECOGNITION_SETTINGS, splitting: { ...DEFAULT_RECOGNITION_SETTINGS.splitting, halvesWhenItemsOver: 3 } };
  const cropper = new FsCropper(kit.fs);
  let n = 0;
  const selector = new EngineSelector({ vision: new RecordingVision(scenes), settings: () => settings, newId: () => `local-${++n}`, cropper });
  return { selector, cropper, recognition: { read: (still: Still, onItem: Parameters<typeof selector.read>[1]) => selector.read(still, onItem) } };
}

/** A snap of a wide shelf: shutter capture, scaled, read in halves, crops made and stored. */
async function snapAShelf(kit: ReturnType<typeof makeKit>) {
  const shelf = wideShelf(kit);
  const pipeline = new SnapPipeline({ ...kit.deps, recognition: shelf.recognition, newId: () => SNAP });
  await pipeline.onSnap(await kit.snapEvent());
  await waitFor(() => expect(kit.snaps.list()[0]?.items.length).toBeGreaterThanOrEqual(6)); // the streamed items, then the merged six
  await waitFor(() => expect(kit.snaps.list()[0]?.items.every((i) => i.cropUri)).toBe(true));
  expect(shelf.cropper.regions).toHaveLength(2);
  return pipeline;
}

describe('a snap leaves no picture file behind once it is stored', () => {
  it('deletes the camera capture, the scaled snap, both halves and every crop after copying them into the store', async () => {
    const kit = makeKit();
    await snapAShelf(kit);

    const stored = kit.snaps.list()[0]!;
    expect(kit.fs.created.length).toBe(2 + 2 + 1 + 2 * stored.items.length); // capture, scaled snap, halves, the still, each crop made and its copy
    expect(kit.fs.strays()).toEqual([]); // only the store's own copies are left
    expect(kit.fs.live().length).toBe(1 + stored.items.length);
  });

  it('shows the first line with a picture that is still there after the scaled snap was deleted', async () => {
    const kit = makeKit();
    kit.reader.script(readingOf([dune, mario]));
    await new SnapPipeline(kit.deps).onSnap(await kit.snapEvent());
    await waitFor(() => expect(kit.snaps.list()[0]?.items.every((i) => i.cropUri)).toBe(true));

    const [snap] = kit.store.getState().snaps;
    expect(kit.fs.live()).toContain(snap!.thumbUri);
    expect(snap!.items.every((i) => i.thumbUri && kit.fs.live().includes(i.thumbUri))).toBe(true);
  });

  it('keeps nothing of a picture that found nothing, or that could not be read', async () => {
    const kit = makeKit();
    kit.reader.script(readingOf([]));
    await new SnapPipeline(kit.deps).onSnap(await kit.snapEvent());
    expect(kit.fs.live()).toEqual([]);

    const failing = { read: () => Promise.reject(new Error('the model fell over')) };
    await new SnapPipeline({ ...kit.deps, recognition: failing }).onSnap(await kit.snapEvent());
    expect(kit.fs.live()).toEqual([]);
    expect(kit.snaps.list()).toEqual([]);
  });

  it('still removes the scaled snap when the store could not keep a copy, once the snap is cleared', async () => {
    const kit = makeKit();
    jest.spyOn(kit.files, 'put').mockRejectedValue(new Error('disk full'));
    kit.reader.script(readingOf([dune]));
    await new SnapPipeline(kit.deps).onSnap(await kit.snapEvent());
    expect(kit.fs.strays().length).toBeGreaterThan(0); // read from the scaled snap itself, so it has to stay for now

    await clearPipelineData();
    expect(kit.fs.live()).toEqual([]);
  });
});

describe('Discard, sign-out and unpair leave no picture file anywhere', () => {
  it('on Discard, after a snap with halves', async () => {
    const kit = makeKit();
    await snapAShelf(kit);
    // A second capture that is still being scaled when the person discards: its files are tracked and go too.
    trackPicture(kit.fs.create('file:///tmp/Camera/in-flight.jpg'));

    function Harness() {
      const close = useCloseScanner({ store: kit.tray, onLeave: jest.fn(), onReview: jest.fn() });
      return (
        <>
          <CloseScannerSheet state={close.state} unreviewed={close.unreviewed} onChoose={close.choose} onDismiss={close.dismiss} />
          <Button label="Close scanner" onPress={close.request} />
        </>
      );
    }
    await renderWithProviders(<Harness />);
    await fireEvent.press(screen.getByLabelText('Close scanner'));
    await fireEvent.press(await screen.findByLabelText('Discard'));
    await waitFor(() => expect(kit.fs.live()).toEqual([]));
    expect(trackedPictures()).toEqual([]);
  });

  it('on sign-out and on unpair, through the registered clearers', async () => {
    const kit = makeKit();
    const client = makeQueryClient();
    const view = await render(
      <QueryClientProvider client={client}>
        <SnapStoreBridge />
      </QueryClientProvider>,
    );
    for (let round = 0; round < 2; round += 1) {
      // sign-out, then unpair: both run the same clearers
      await snapAShelf(kit);
      trackPicture(kit.fs.create('file:///tmp/Camera/in-flight.jpg'));
      await clearLocalData(client);
      expect(kit.fs.live()).toEqual([]);
      expect(trackedPictures()).toEqual([]);
      kit.store.getState().clear();
    }
    view.unmount();
  });

  it('finds a file an earlier run left behind, because the register is saved', async () => {
    const storage = new MemoryKeyValueStore();
    const fs = new FakeFileSystem();
    setPictureFiles({ storage, remove: (uri) => fs.remove(uri) });
    trackPicture(fs.create('file:///cache/ImageManipulator/crop-from-before.jpg'));

    setPictureFiles({ storage, remove: (uri) => fs.remove(uri) }); // the app was killed; this is the next run
    expect(trackedPictures()).toEqual(['file:///cache/ImageManipulator/crop-from-before.jpg']);
    await discardAllPictures();
    expect(fs.live()).toEqual([]);
  });
});

describe('the halves of a wide shot', () => {
  it('are deleted when the shot has been read, also when a half fails', async () => {
    const kit = makeKit();
    const { selector, cropper } = wideShelf(kit);
    await selector.read({ ...STILL, uri: `file:///snaps/${SNAP}-still.jpg` }, () => {});
    expect(cropper.regions).toHaveLength(2);
    expect(kit.fs.live()).toEqual([]);

    const broken = new EngineSelector({
      vision: new RecordingVision({ [STILL.uri]: { read: okRead(many(['A', 'B', 'C', 'D'])), text: [] } }),
      settings: () => ({ ...DEFAULT_RECOGNITION_SETTINGS, splitting: { ...DEFAULT_RECOGNITION_SETTINGS.splitting, halvesWhenItemsOver: 3 } }),
      cropper: new FsCropper(kit.fs), // the halves have no scene, so their reads fail
    });
    await broken.read(STILL, () => {});
    expect(kit.fs.live()).toEqual([]);
  });
});
