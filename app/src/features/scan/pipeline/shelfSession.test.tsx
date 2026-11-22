// The shelf uploads belong to the scan, not to the scanner screen (#834). These tests drive the wiring around the queue: the
// signed-in app's SnapStoreBridge (rendered, with its deps injected) and clearPipelineData (sign-out, Discard).
import { waitFor } from '@testing-library/react-native';
import { AppState } from 'react-native';
import * as libraries from '@/features/onboarding/useLibraries';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { shelfMock } from '@/mocks/read-shelf';
import { scanMock } from '@/mocks/scan';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { HttpResponse, http } from 'msw';
import { useCardsStore } from '../cards/cardsStore';
import { createDiscardOutbox, setDiscardOutbox } from '../session/discards';
import { MemoryKeyValueStore } from '../tray/trayStore';
import { clearPipelineData } from './clearPipelineData';
import { restPipelineApi } from './pipelineApi';
import { FIRST_OFFLINE_WAIT_MS, stopShelfUploads } from './shelfUpgrade';
import { SnapPipeline, type PipelineDeps } from './SnapPipeline';
import { SnapStoreBridge } from './SnapStoreBridge';
import { FakeShelfFiles, makeKit, readItem, readingOf, resetKit } from './testKit';

const dune = readItem('Dune', { box: { x: 0.05, y: 0.1, width: 0.2, height: 0.7 } });
const OTHER_LIBRARY_ID = 'f0f0f0f0-0000-4000-8000-0000000000f1';

const gate = () => {
  let release: () => void = () => undefined;
  const hold = new Promise<void>((resolve) => (release = resolve));
  return { hold, release };
};

const stores: object[] = [];
let clock = 1_000_000;
const signals: AbortSignal[] = [];

/** A kit whose pipeline can send shelf photos for `libraryId`; every upload's abort signal is recorded. */
function shelfKit(libraryId = MOCK_LIBRARY_ID) {
  const kit = makeKit();
  const files = new FakeShelfFiles(kit.fs);
  const api = { ...kit.deps.api, readShelf: async (...args: Parameters<typeof restPipelineApi.readShelf>) => (signals.push(args[3]!), restPipelineApi.readShelf(...args)) };
  const deps: PipelineDeps = { ...kit.deps, libraryId, api, shelf: files.shelf, now: () => clock };
  stores.push(kit.store);
  return { ...kit, files, deps, pipeline: new SnapPipeline(deps) };
}
type Kit = ReturnType<typeof shelfKit>;

const snapShelf = async (kit: Kit) => {
  kit.reader.script(readingOf([dune]));
  await kit.pipeline.onSnap(await kit.snapEvent('shutter', 'shelf_segment'));
};
/** A read the server is holding: the upload is out and nothing has come back. */
const inFlight = async (kit: Kit) => {
  const answer = gate();
  shelfMock.hold = answer.hold;
  await snapShelf(kit);
  await waitFor(() => expect(shelfMock.requests).toHaveLength(1));
  return answer;
};
/** A read that found no connection: the snap waits in the queue, flagged, with a back-off. */
const waiting = async (kit: Kit) => {
  server.use(http.post(/read-shelf$/, () => HttpResponse.error(), { once: true }));
  await snapShelf(kit);
  await waitFor(() => expect(kit.store.getState().snaps[0]?.shelfUpload).toBe(true));
  await waitFor(() => expect(kit.files.reads).toHaveLength(1));
};
const bridge = (kit: Kit, makeDeps: (libraryId: string) => PipelineDeps = () => kit.deps) => renderWithProviders(<SnapStoreBridge makeDeps={makeDeps} />);

beforeEach(() => {
  scanMock.reset();
  shelfMock.reset();
  useCardsStore.getState().reset();
  setDiscardOutbox(createDiscardOutbox({ storage: new MemoryKeyValueStore() }));
  signals.length = 0;
  clock = 1_000_000;
});
afterEach(() => {
  for (const store of stores.splice(0)) stopShelfUploads(store);
  jest.restoreAllMocks();
  resetKit();
  scanMock.reset();
});

describe('the signed-in app keeps the uploads going', () => {
  it('sends a read that was waiting when the app came up, without the scanner opening', async () => {
    const kit = shelfKit();
    await waiting(kit);
    clock += FIRST_OFFLINE_WAIT_MS; // the back-off is over; no scanner is open to flush
    const makeDeps = jest.fn(() => kit.deps);
    await bridge(kit, makeDeps);
    await waitFor(() => expect(shelfMock.requests).toHaveLength(1));
    expect(makeDeps).toHaveBeenCalledWith(MOCK_LIBRARY_ID);
  });

  it('runs what is due when the app comes back to the front', async () => {
    const kit = shelfKit();
    let notify: (state: string) => void = () => undefined;
    jest.spyOn(AppState, 'addEventListener').mockImplementation((_type, handler) => {
      notify = handler as (state: string) => void;
      return { remove: () => undefined };
    });
    await waiting(kit);
    await bridge(kit);
    clock += FIRST_OFFLINE_WAIT_MS; // the timer did not run while the app slept
    expect(shelfMock.requests).toHaveLength(0);
    notify('active');
    await waitFor(() => expect(shelfMock.requests).toHaveLength(1));
  });

  it('leaves the active library’s read alone when the bridge is mounted', async () => {
    const kit = shelfKit();
    const answer = await inFlight(kit);
    shelfMock.rows = [];
    await bridge(kit);
    expect(signals[0]!.aborted).toBe(false);
    answer.release();
    await waitFor(() => expect(kit.store.getState().snaps.every((s) => !s.shelfUpload)).toBe(true));
    expect(shelfMock.requests).toHaveLength(1);
  });

  it('stops a read for a library that is not the active one, and resumes nothing for it', async () => {
    const kit = shelfKit();
    await inFlight(kit);
    const real = libraries.useActiveLibraryId;
    jest.spyOn(libraries, 'useActiveLibraryId').mockImplementation(() => ({ ...real(), libraryId: OTHER_LIBRARY_ID })); // the person switched libraries
    const makeDeps = jest.fn(() => kit.deps);
    await bridge(kit, makeDeps);
    await waitFor(() => expect(signals[0]!.aborted).toBe(true));
    await waitFor(() => expect(kit.store.getState().snaps.every((s) => !s.shelfUpload)).toBe(true));
    expect(makeDeps).not.toHaveBeenCalledWith(MOCK_LIBRARY_ID);
  });
});

describe('sign-out and Discard', () => {
  it('abort a read in flight when the app signs out or unpairs (everything is cleared)', async () => {
    const kit = shelfKit();
    await inFlight(kit);
    expect(signals[0]!.aborted).toBe(false);
    await clearPipelineData();
    expect(signals[0]!.aborted).toBe(true);
  });

  it('abort a read in flight when the library’s data is cleared (Discard)', async () => {
    const kit = shelfKit();
    await inFlight(kit);
    await clearPipelineData(MOCK_LIBRARY_ID);
    expect(signals[0]!.aborted).toBe(true);
  });
});
