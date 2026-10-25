// A snap belongs to the library whose scanner took it: after a switch, the other library's waiting snaps are never
// uploaded into this session, shown on this receipt, counted on its Close sheet, or wiped by its Discard.
import { render, screen } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import { Text } from 'react-native';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { identifyMock } from '@/mocks/identify';
import { scanMock } from '@/mocks/scan';
import { server } from '@/mocks/server';
import { useCardsStore } from '../cards/cardsStore';
import { trackPicture, trackedPictures } from '../pictureFiles';
import { FakeScannerSource } from '../scanner/ScannerSource';
import { createTrayStore, MemoryKeyValueStore } from '../tray/trayStore';
import { clearPipelineData } from './clearPipelineData';
import { picItemOf } from './payload';
import { createPipelineStore, type PicSnap } from './pipelineStore';
import { SnapPipeline, type PipelineDeps } from './SnapPipeline';
import { usePipeline } from './usePipeline';
import { makeKit, PHOTO, readItem, readingOf, resetKit } from './testKit';

/** The library the scanner is not open on. */
const OTHER_LIBRARY_ID = 'f0000000-0000-4000-8000-000000000002';

const dune = readItem('Dune');
const mario = readItem('Super Mario 64', { kind: 'game', platform: 'Nintendo 64' });

beforeEach(() => {
  scanMock.reset();
  identifyMock.reset();
  useCardsStore.getState().reset();
});
afterEach(() => {
  resetKit();
  scanMock.reset();
});

/** Two libraries sharing one pipeline store, the way the phone holds them: trays apart, snaps together. */
function twoLibraries() {
  const kit = makeKit();
  const trayA = createTrayStore({ storage: new MemoryKeyValueStore() });
  const depsA: PipelineDeps = { ...kit.deps, libraryId: OTHER_LIBRARY_ID, tray: trayA };
  return { kit, trayA, depsA, depsB: kit.deps };
}

const openSession = (id: string, libraryId: string) => {
  scanMock.sessions.set(id, { id, libraryId, seq: 1, candidates: [], answers: new Map(), status: 'open' });
};

const waitingSnap = (snapId: string, libraryId: string, title: string, createdAt = 1): PicSnap => ({
  snapId,
  libraryId,
  trigger: 'shutter',
  createdAt,
  thumbUri: 'file:///snaps/seed.jpg',
  phase: 'read',
  sent: false,
  items: [picItemOf(readItem(title))],
  unreadableCount: 0,
  engine: 'apple_model',
});

describe('tagging a snap with its library', () => {
  it('records the library whose scanner took the picture', async () => {
    const kit = makeKit();
    kit.reader.script(readingOf([dune]));
    await new SnapPipeline(kit.deps).onSnap(await kit.snapEvent());
    expect(kit.store.getState().snaps[0]).toMatchObject({ libraryId: MOCK_LIBRARY_ID });
  });

  it('drops snaps saved before they were tagged, which cannot be attributed to a library', () => {
    const storage = new MemoryKeyValueStore();
    storage.set(
      'scan-pipeline-v1',
      JSON.stringify({
        snaps: [{ snapId: 'old', trigger: 'shutter', createdAt: 1, thumbUri: 'x', phase: 'read', sent: false, items: [], unreadableCount: 0 }],
        unreadableCount: 0,
      }),
    );
    const store = createPipelineStore({ storage });
    expect(store.getState().snaps).toEqual([]);
  });
});

describe('switching libraries with a snap still waiting', () => {
  it('never uploads the waiting snap into the other library\'s session', async () => {
    const { kit, trayA, depsA, depsB } = twoLibraries();
    const sessionA = 'a0000000-0000-4000-8000-0000000000a1';
    openSession(sessionA, OTHER_LIBRARY_ID);
    trayA.getState().setSessionId(sessionA);
    server.use(http.post('*/api/v1/scan-sessions/:id/identify', () => HttpResponse.error())); // the PC is off

    kit.reader.script(readingOf([dune]));
    await new SnapPipeline(depsA).onSnap(await kit.snapEvent());
    expect(kit.store.getState().snaps[0]).toMatchObject({ sent: false });

    server.resetHandlers(); // back online, and the person has switched libraries
    kit.reader.script(readingOf([mario]));
    await new SnapPipeline(depsB).onSnap(await kit.snapEvent());

    expect(identifyMock.requests).toHaveLength(1); // only this library's own snap
    const [waiting, sent] = kit.store.getState().snaps;
    expect(identifyMock.requests[0]!.sessionId).toBe(kit.tray.getState().sessionId);
    expect(identifyMock.requests[0]!.body.snapId).toBe(sent!.snapId);
    expect(waiting).toMatchObject({ sent: false });

    await new SnapPipeline(depsA).flush(); // switching back sends it to its own session
    expect(identifyMock.requests).toHaveLength(2);
    expect(identifyMock.requests[1]).toMatchObject({ sessionId: sessionA });
    expect(kit.store.getState().snaps.every((s) => s.sent)).toBe(true);
  });

  it('does not re-read the other library\'s snap that the app was killed in the middle of', async () => {
    const { kit, depsB } = twoLibraries();
    await kit.snaps.saveStill('snap-a', PHOTO);
    kit.store.getState().addSnap({ ...waitingSnap('snap-a', OTHER_LIBRARY_ID, 'Dune'), phase: 'reading', items: [] });
    kit.reader.script(readingOf([dune]));

    const pipeline = new SnapPipeline(depsB);
    pipeline.start();
    await new Promise((resolve) => setTimeout(resolve, 50)); // a resume would have started by now
    pipeline.stop();

    expect(kit.reader.stills).toEqual([]);
    expect(kit.store.getState().snaps[0]).toMatchObject({ snapId: 'snap-a', phase: 'reading' });
  });

  it('sweeping this scan leaves the other library\'s snaps alone', () => {
    const { kit, depsB } = twoLibraries();
    const sessionA = 'a0000000-0000-4000-8000-0000000000a1';
    openSession(sessionA, OTHER_LIBRARY_ID);
    kit.store.getState().addSnap({ ...waitingSnap('snap-a', OTHER_LIBRARY_ID, 'Dune'), sent: true, sessionId: sessionA });
    kit.store.getState().addSnap({ ...waitingSnap('snap-b-old', MOCK_LIBRARY_ID, 'Super Mario 64', 2), sent: true, sessionId: 'b0000000-0000-4000-8000-0000000000b1' });
    kit.store.getState().addSnap(waitingSnap('snap-b-new', MOCK_LIBRARY_ID, 'Zelda', 3));
    kit.tray.getState().setSessionId('b0000000-0000-4000-8000-0000000000b2');

    new SnapPipeline(depsB).sweep();

    expect(kit.store.getState().snaps.map((s) => s.snapId).sort()).toEqual(['snap-a', 'snap-b-new']);
  });

  it('shows only this library\'s snaps on the receipt', async () => {
    const { kit, depsB } = twoLibraries();
    kit.store.getState().addSnap(waitingSnap('snap-a', OTHER_LIBRARY_ID, 'Dune'));
    kit.store.getState().addSnap(waitingSnap('snap-b', MOCK_LIBRARY_ID, 'Super Mario 64', 2));

    function Lines({ deps }: { deps: PipelineDeps }) {
      const { lines } = usePipeline({ deps, source: new FakeScannerSource(), resendMs: 3_600_000 });
      return <>{lines.map((l) => <Text key={l.key}>{l.title}</Text>)}</>;
    }
    await render(<Lines deps={depsB} />);

    expect(await screen.findByText('Super Mario 64')).toBeTruthy();
    expect(screen.queryByText('Dune')).toBeNull();
  });
});

describe('clearing one library\'s pipeline data', () => {
  it('keeps the other library\'s snaps, stills and files', async () => {
    const kit = makeKit();
    kit.store.getState().addSnap(waitingSnap('snap-a', OTHER_LIBRARY_ID, 'Dune'));
    kit.store.getState().addSnap(waitingSnap('snap-b', MOCK_LIBRARY_ID, 'Super Mario 64', 2));
    await kit.snaps.saveStill('snap-a', PHOTO);
    await kit.snaps.saveStill('snap-b', PHOTO);
    const fileA = trackPicture(kit.fs.create('file:///cache/ImageManipulator/crop-a.jpg'), 'snap-a');
    trackPicture(kit.fs.create('file:///cache/ImageManipulator/crop-b.jpg'), 'snap-b');
    trackPicture(kit.fs.create('file:///tmp/Camera/in-flight.jpg')); // being scaled when the person discards

    await clearPipelineData(MOCK_LIBRARY_ID);

    expect(kit.store.getState().snaps.map((s) => s.snapId)).toEqual(['snap-a']);
    expect(kit.snaps.list().map((s) => s.snapId)).toEqual(['snap-a']);
    expect(trackedPictures()).toEqual([fileA]);
    expect(kit.fs.live()).toHaveLength(2); // the kept still and the kept file
    expect(kit.fs.live()).toContain(fileA);
  });
});
