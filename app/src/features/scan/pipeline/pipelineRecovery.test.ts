// A killed app loses nothing: the queue is storage, so a restart re-sends, re-reads, or counts what it cannot.
import { waitFor } from '@testing-library/react-native';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { identifyMock } from '@/mocks/identify';
import { scanMock } from '@/mocks/scan';
import { useCardsStore } from '../cards/cardsStore';
import { createPipelineStore } from './pipelineStore';
import { MemorySnapFiles, SnapStore } from './snapStore';
import { SnapPipeline } from './SnapPipeline';
import { MemoryKeyValueStore } from '../tray/trayStore';
import { FakeReader, makeKit, PHOTO, readingOf, readItem, resetKit } from './testKit';

const dune = readItem('Dune');

const seedSnap = (store: ReturnType<typeof createPipelineStore>, snapId: string, phase: 'reading' | 'read', localIds: string[] = []) =>
  store.getState().addSnap({
    snapId,
    libraryId: MOCK_LIBRARY_ID,
    trigger: 'auto',
    createdAt: 1,
    thumbUri: PHOTO.uri,
    phase,
    sent: false,
    items: localIds.map((localId) => ({ localId, text: localId, title: localId, payload: { localId } as never })),
    unreadableCount: 0,
  });

/** Runs the pipeline, "kills" the app, and runs it again over the same storage. */
const killAndRestart = async (setup: (kit: ReturnType<typeof makeKit>) => Promise<void>) => {
  const storage = new MemoryKeyValueStore();
  const snapStorage = new MemoryKeyValueStore();
  const files = new MemorySnapFiles();
  const kit = makeKit({ store: createPipelineStore({ storage }), snaps: new SnapStore({ files, storage: snapStorage }) });
  await setup(kit);

  const store = createPipelineStore({ storage });
  const snaps = new SnapStore({ files, storage: snapStorage });
  const reader = new FakeReader();
  const pipeline = new SnapPipeline({ ...kit.deps, recognition: reader, store, snaps });
  return { kit, store, snaps, reader, pipeline };
};

beforeEach(() => {
  scanMock.reset();
  identifyMock.reset();
  useCardsStore.getState().reset();
});
afterEach(() => {
  resetKit();
  scanMock.reset();
});

describe('an app killed mid-queue', () => {
  it('sends a read snap it never got to, still pinned', async () => {
    const { store, snaps, pipeline } = await killAndRestart(async (kit) => {
      seedSnap(kit.deps.store, 'snap-queued', 'read', ['dune-item']);
      await kit.deps.snaps.saveStill('snap-queued', PHOTO);
      kit.deps.snaps.pin('snap-queued');
    });

    expect(snaps.isPinned('snap-queued')).toBe(true); // the pin survived the kill too
    pipeline.start();
    await pipeline.flush();
    await waitFor(() => expect(identifyMock.requests).toHaveLength(1));
    expect(store.getState().snaps[0]).toMatchObject({ snapId: 'snap-queued', sent: true });
    pipeline.stop();
  });

  it('reads again a snap it was killed in the middle of, from the still it kept', async () => {
    const { store, reader, pipeline } = await killAndRestart(async (k) => {
      seedSnap(k.deps.store, 'snap-halfway', 'reading');
      await k.deps.snaps.saveStill('snap-halfway', PHOTO);
    });
    reader.script(readingOf([dune]));

    pipeline.start();
    await waitFor(() => expect(identifyMock.requests).toHaveLength(1));
    expect(store.getState().snaps[0]).toMatchObject({ snapId: 'snap-halfway', phase: 'read', sent: true });
    pipeline.stop();
  });

  it('counts a half-read snap whose still is gone too, instead of silently completing it', async () => {
    const storage = new MemoryKeyValueStore();
    const kit = makeKit({ store: createPipelineStore({ storage }) });
    seedSnap(kit.deps.store, 'snap-lost', 'reading');

    // The restart finds no still: the OS emptied the cache while the app was dead.
    const store = createPipelineStore({ storage });
    const pipeline = new SnapPipeline({ ...kit.deps, store, snaps: new SnapStore({ files: new MemorySnapFiles(), storage: new MemoryKeyValueStore() }) });
    pipeline.start();
    await waitFor(() => expect(store.getState().snaps).toHaveLength(0));
    expect(store.getState().orphanedCount).toBe(1);
    expect(identifyMock.requests).toHaveLength(0);
    pipeline.stop();
  });
});
