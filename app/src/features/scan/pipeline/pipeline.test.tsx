// The picture pipeline end to end on the scanner screen: FakeReader -> receipt -> mock API -> mock event stream -> cards.
import { act, fireEvent, screen, waitFor, within } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { CLOSE_CALL, identifyMock } from '@/mocks/identify';
import { scanMock } from '@/mocks/scan';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import type { Resizer } from '../autosnap/snapPhoto';
import { useCardsStore } from '../cards/cardsStore';
import { ScannerScreen } from '../scanner/ScannerScreen';
import { FakeScannerSource } from '../scanner/ScannerSource';
import { MemoryKeyValueStore } from '../tray/trayStore';
import { ownArtworkUrl } from './artStep';
import { createPipelineStore } from './pipelineStore';
import { SnapPipeline } from './SnapPipeline';
import { makeKit, PHOTO, readItem, readingOf, resetKit } from './testKit';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));

const resize: Resizer = async (photo, target) => ({ uri: `${photo.uri}#resized`, width: target?.width ?? photo.width, height: target?.height ?? photo.height });
const tuning = { retryMs: 20, pollMs: 20, resendMs: 40, flashMs: 50 };

const dune = readItem('Dune', { box: { x: 0.05, y: 0.1, width: 0.2, height: 0.7 } });
const mario = readItem('Super Mario 64', { kind: 'game', platform: 'Nintendo 64', box: { x: 0.3, y: 0.1, width: 0.2, height: 0.3 } });
const zelda = readItem('Zelda: Ocarina of Time', { kind: 'game', platform: 'Nintendo 64' });
const yellow = readItem(CLOSE_CALL.title, { kind: 'game', platform: 'Game Boy', box: { x: 0.55, y: 0.1, width: 0.2, height: 0.3 } });
const mutiny = readItem('Space Mutiny', { kind: 'film', format: 'vhs', box: { x: 0.8, y: 0.1, width: 0.15, height: 0.7 } });

const open = async (kit: ReturnType<typeof makeKit>, source = new FakeScannerSource()) => {
  await renderWithProviders(
    <ScannerScreen libraryId={MOCK_LIBRARY_ID} source={source} pipeline={kit.deps} tuning={tuning} autoSnapTuning={{ flashMs: 50, resize }} />,
  );
  return source;
};
const shutter = async () => {
  await fireEvent.press(screen.getByLabelText('Take picture'));
};
const showReceipt = async () => {
  await fireEvent.press(screen.getByLabelText(/Show the receipt$/));
};
const gate = () => {
  let release: () => void = () => undefined;
  const hold = new Promise<void>((resolve) => (release = resolve));
  return { hold, release };
};
const cards = () => screen.queryAllByTestId(/^card-/);
const cardTitled = (title: string) => cards().find((c) => within(c).queryByText(title));
const statusBar = () => screen.getByLabelText(/(Show|Hide) the receipt$/).props.accessibilityLabel as string;

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

describe('a snap read as three items', () => {
  it('shows three IDENTIFYING lines, then each becomes the right card as responses and events arrive', async () => {
    const kit = makeKit();
    kit.reader.script(readingOf([dune, mario, zelda]));
    const answer = gate();
    identifyMock.hold = answer.hold;
    await open(kit);

    await shutter();
    await waitFor(() => expect(identifyMock.requests).toHaveLength(1));
    await showReceipt();
    expect(screen.getAllByText('IDENTIFYING…')).toHaveLength(3);
    expect(screen.queryByTestId(/^card-/)).toBeNull(); // no card until the server has one
    expect(statusBar()).toMatch(/^3 items · 3 processing\./);

    await act(async () => answer.release());
    await waitFor(() => expect(cards()).toHaveLength(3));
    expect(within(cardTitled('Dune')!).getByText('You own 1')).toBeTruthy(); // owned
    expect(within(cardTitled('Super Mario 64')!).getByText('Super Mario 64')).toBeTruthy(); // new
    expect(within(cardTitled('Zelda: Ocarina of Time')!).getByText('Identifying…')).toBeTruthy(); // the slow one: a pending card
    expect(screen.getAllByText('IDENTIFYING…')).toHaveLength(1);

    await act(async () => identifyMock.resolvePending()); // arrives over the event stream
    await waitFor(() => expect(screen.queryByText('IDENTIFYING…')).toBeNull());
    expect(within(cardTitled('Zelda: Ocarina of Time')!).queryByText('Identifying…')).toBeNull();
    expect(cards()).toHaveLength(3);
    expect(statusBar()).toMatch(/^3 items\./);
  });

  it('puts the read text and fields in the request, with the engine and trigger, and nothing else', async () => {
    const kit = makeKit();
    kit.reader.script(readingOf([dune, mutiny]));
    await open(kit);

    await shutter();
    await waitFor(() => expect(identifyMock.requests).toHaveLength(1));
    const { body } = identifyMock.requests[0]!;
    expect(body).toMatchObject({ trigger: 'shutter', engine: 'apple_model', shotKind: 'unknown' }); // no signals on this shutter
    expect(body.fellBackFrom).toBeUndefined();
    const segmentId = kit.store.getState().snaps[0]!.segmentId;
    expect(body.items).toEqual([
      { localId: dune.localId, segmentId, text: 'Dune', title: 'Dune', kind: 'book' },
      { localId: mutiny.localId, segmentId, text: 'Space Mutiny', title: 'Space Mutiny', kind: 'film', format: 'vhs' },
    ]);
  });
});

describe('what the cards say', () => {
  it('shows Fix it, starting from the read text, when the server does not know the item', async () => {
    const kit = makeKit();
    kit.reader.script(readingOf([mutiny]));
    await open(kit);

    await shutter();
    expect(await screen.findByText('Not in the catalog. Fix it to search by title.')).toBeTruthy();
    await fireEvent.press(await screen.findByLabelText('Fix it: Space Mutiny'));
    expect(screen.getByDisplayValue('Space Mutiny')).toBeTruthy();
  });

  it('searches again from Fix it with the corrected title, and the card updates', async () => {
    const kit = makeKit();
    kit.reader.script(readingOf([readItem('Goldeneye 7')]));
    await open(kit);

    await shutter();
    await fireEvent.press(await screen.findByLabelText('Fix it: Goldeneye 7'));
    await fireEvent.changeText(screen.getByDisplayValue('Goldeneye 7'), 'GoldenEye 007');
    await fireEvent.press(screen.getByLabelText('Search'));

    expect(await screen.findByText('You own 1')).toBeTruthy();
    expect(screen.getByText('GoldenEye 007')).toBeTruthy();
    expect(screen.queryByText('Fix it')).toBeNull(); // the sheet closed and the card is matched
  });

  it('leads with "You own" for an item the library already has', async () => {
    const kit = makeKit();
    kit.reader.script(readingOf([dune]));
    await open(kit);

    await shutter();
    const card = await screen.findByTestId(/^card-/);
    expect(within(card).getByText('Dune')).toBeTruthy();
    expect(within(card).getByText('You own 1')).toBeTruthy();
    expect(within(card).queryByText('+ Add')).toBeNull();
  });
});

describe('offline and the PC being off', () => {
  it('reads anyway, shows QUEUED, sends nothing, and sends one identify per snap with the same snapId when it is back', async () => {
    const kit = makeKit();
    kit.reader.script(readingOf([dune, mario]));
    server.use(http.post('*/api/v1/libraries/:libraryId/scan-sessions', () => HttpResponse.error()));
    await open(kit);

    await shutter();
    await showReceipt();
    expect(await screen.findAllByText('QUEUED')).toHaveLength(2);
    expect(kit.reader.stills).toHaveLength(1); // read on the phone
    expect(identifyMock.requests).toEqual([]); // nothing sent
    expect(screen.getByText(/Couldn.t reach your server/)).toBeTruthy();

    server.resetHandlers(); // back online
    await waitFor(() => expect(identifyMock.requests).toHaveLength(1));
    expect(await screen.findByText('You own 1')).toBeTruthy();
    expect(identifyMock.requests[0]!.body.snapId).toBe(kit.store.getState().snaps[0]!.snapId);
    await waitFor(() => expect(screen.queryByText('QUEUED')).toBeNull());
    expect(identifyMock.requests).toHaveLength(1);
  });

  it('retries a lost answer with the same snapId and creates each candidate once', async () => {
    const kit = makeKit();
    kit.reader.script(readingOf([dune, mario]));
    identifyMock.dropResponses = 1; // the server got it; the answer never came back
    await open(kit);

    await shutter();
    await waitFor(() => expect(identifyMock.requests.length).toBeGreaterThanOrEqual(2));
    expect(new Set(identifyMock.requests.map((r) => r.body.snapId)).size).toBe(1);
    expect(await screen.findByText('Super Mario 64')).toBeTruthy();
    expect([...scanMock.sessions.values()].flatMap((s) => s.candidates)).toHaveLength(2);
    expect(screen.getAllByTestId(/^card-/)).toHaveLength(2);
  });

  it('keeps what was read across a restart and sends it from the new run', async () => {
    const storage = new MemoryKeyValueStore();
    const kit = makeKit({ store: createPipelineStore({ storage }) });
    kit.reader.script(readingOf([dune, mario]));
    server.use(http.post('*/api/v1/libraries/:libraryId/scan-sessions', () => HttpResponse.error()));
    await new SnapPipeline(kit.deps).onSnap({ photo: PHOTO, trigger: 'auto', shotKind: 'shelf_segment' });

    const restarted = createPipelineStore({ storage }); // the app was killed; this is the next run
    const [snap] = restarted.getState().snaps;
    expect(snap).toMatchObject({ trigger: 'auto', phase: 'read', sent: false });
    expect(snap!.items.map((i) => i.title)).toEqual(['Dune', 'Super Mario 64']);

    server.resetHandlers();
    await new SnapPipeline({ ...kit.deps, store: restarted }).flush();
    expect(identifyMock.requests.map((r) => r.body.snapId)).toEqual([snap!.snapId]);
    expect(restarted.getState().snaps[0]!.sent).toBe(true);
  });

  it('reads again a snap the app was killed in the middle of, from the still it kept', async () => {
    const kit = makeKit();
    const stored = await kit.snaps.saveStill('snap-halfway', PHOTO);
    kit.store.getState().addSnap({ snapId: 'snap-halfway', libraryId: MOCK_LIBRARY_ID, trigger: 'auto', createdAt: 1, thumbUri: PHOTO.uri, phase: 'reading', sent: false, items: [], unreadableCount: 0 });
    kit.store.getState().addSnap({ snapId: 'snap-lost', libraryId: MOCK_LIBRARY_ID, trigger: 'auto', createdAt: 2, thumbUri: PHOTO.uri, phase: 'reading', sent: false, items: [], unreadableCount: 0 });
    kit.reader.script(readingOf([dune]));

    const pipeline = new SnapPipeline(kit.deps);
    pipeline.start();
    await waitFor(() => expect(identifyMock.requests).toHaveLength(1));
    pipeline.stop();
    expect(kit.reader.stills[0]!.uri).toBe(stored!.stillUri);
    expect(kit.store.getState().snaps.map((s) => s.snapId)).toEqual(['snap-halfway']); // the other had no still to read again
  });
});

describe('a close call', () => {
  const settle = async () => {
    await open(kit);
    await shutter();
    expect(await screen.findByText('Pick one')).toBeTruthy();
  };
  let kit: ReturnType<typeof makeKit>;
  beforeEach(() => {
    kit = makeKit();
    kit.reader.script(readingOf([yellow]));
    kit.art.distances.set('0001', 0.1);
    kit.art.distances.set('0002', 0.8);
  });

  it('downloads the alternatives from this server, compares them with the crop, sends the distances once and updates the card', async () => {
    await open(kit);
    await shutter();

    await waitFor(() => expect(identifyMock.artRequests).toHaveLength(1));
    expect(kit.art.downloads.map((d) => d.url)).toEqual([
      'http://api.test/media/covers/aaaaaaaa-0000-4000-8000-000000000001',
      'http://api.test/media/covers/aaaaaaaa-0000-4000-8000-000000000002',
    ]);
    expect(identifyMock.coverRequests).toHaveLength(2);
    expect(kit.art.printed[0]!.uri).toContain('/snaps/'); // the item's own crop, kept on the phone
    expect(identifyMock.artRequests[0]!.distances).toEqual([
      { releaseId: CLOSE_CALL.original.id, distance: 0.1 },
      { releaseId: CLOSE_CALL.rerelease.id, distance: 0.8 },
    ]);
    await waitFor(() => expect(screen.queryByText('Pick one')).toBeNull());
    expect(screen.getByText('Pokemon Yellow')).toBeTruthy();
    expect(kit.art.discarded).toHaveLength(2); // the downloaded thumbnails do not pile up
    expect(identifyMock.artRequests).toHaveLength(1);
  });

  it('leaves the card as Pick one when a download fails, and says nothing is wrong', async () => {
    kit.art.failDownloads = true;
    await settle();
    await act(async () => new Promise((resolve) => setTimeout(resolve, 150)));
    expect(identifyMock.artRequests).toEqual([]);
    expect(screen.getByText('Pick one')).toBeTruthy();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('leaves the card as it is when the server says the person already decided (409)', async () => {
    server.use(http.post(/\/candidates\/[^/]+:art$/, () => HttpResponse.json({ title: 'Decided', status: 409 }, { status: 409, headers: { 'Content-Type': 'application/problem+json' } })));
    await settle();
    await waitFor(() => expect(kit.art.downloads).toHaveLength(2));
    await act(async () => new Promise((resolve) => setTimeout(resolve, 100)));
    expect(screen.getByText('Pick one')).toBeTruthy();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('never downloads from an address that is not this server\'s own route', async () => {
    expect(ownArtworkUrl('/media/covers/abc')).toBe('http://api.test/media/covers/abc');
    expect(ownArtworkUrl('https://images.example.com/cover.jpg')).toBeUndefined();
    expect(ownArtworkUrl('//images.example.com/cover.jpg')).toBeUndefined();
    expect(ownArtworkUrl('cover.jpg')).toBeUndefined();
  });
});

describe('a snap the model declined', () => {
  it.each([
    ['declined', 'declined'],
    ['unavailable', 'unavailable'],
    ['contextExceeded', 'context'],
    ['timeout', 'timeout'],
  ] as const)('sends engine vision_pipeline and fellBackFrom %s as %s', async (from, sent) => {
    const kit = makeKit();
    kit.reader.script(readingOf([dune], { engine: 'vision_pipeline', fellBackFrom: from }));
    await new SnapPipeline(kit.deps).onSnap({ photo: PHOTO, trigger: 'auto', shotKind: 'single_cover' });
    expect(identifyMock.requests[0]!.body).toMatchObject({ engine: 'vision_pipeline', fellBackFrom: sent, trigger: 'auto' });
  });

  it('replaces the streamed items with the final reading when the fallback read them differently', async () => {
    const kit = makeKit();
    const streamed = readItem('Dun');
    const reader = {
      async read(_still: unknown, onItem: (i: typeof streamed) => void) {
        onItem(streamed);
        return readingOf([dune], { engine: 'vision_pipeline', fellBackFrom: 'declined' });
      },
    };
    await new SnapPipeline({ ...kit.deps, recognition: reader }).onSnap({ photo: PHOTO, trigger: 'auto', shotKind: 'single_cover' });
    expect(identifyMock.requests[0]!.body.items?.map((i) => i.title)).toEqual(['Dune']);
  });
});

describe('the processing count and Get closer', () => {
  it('counts what is being read or identified and clears when it is done', async () => {
    const kit = makeKit();
    kit.reader.script(readingOf([dune, zelda]));
    const reading = gate();
    kit.reader.hold = reading.hold;
    await open(kit);

    await shutter();
    await waitFor(() => expect(statusBar()).toMatch(/^2 items · 2 processing\./)); // streamed, still being read
    await act(async () => reading.release());
    await screen.findByText('You own 1');
    await waitFor(() => expect(statusBar()).toMatch(/^2 items · 1 processing\./)); // Zelda is still being looked up
    await act(async () => identifyMock.resolvePending());
    await waitFor(() => expect(statusBar()).toMatch(/^2 items\./));
  });

  it('shows "Get closer for these N" with the unreadable count, without blocking the camera', async () => {
    const kit = makeKit();
    kit.reader.script(readingOf([dune], { unreadableCount: 3 }));
    const answer = gate();
    identifyMock.hold = answer.hold;
    await open(kit);

    await shutter();
    expect(await screen.findByText('Get closer for these 3')).toBeTruthy();
    // Still waiting on the server, and the shutter works: the next snap is read at once. It found everything, so the chip goes.
    kit.reader.script(readingOf([mario]));
    await shutter();
    await waitFor(() => expect(kit.reader.stills).toHaveLength(2));
    await waitFor(() => expect(screen.queryByText(/Get closer for these/)).toBeNull());
    await act(async () => answer.release());
  });

  it('goes away when tapped', async () => {
    const kit = makeKit();
    kit.reader.script(readingOf([dune], { unreadableCount: 2 }));
    await open(kit);

    await shutter();
    await fireEvent.press(await screen.findByText('Get closer for these 2'));
    expect(screen.queryByText(/Get closer for these/)).toBeNull();
  });

  it('forgets a picture that found nothing, and counts it as a miss for auto-snap', async () => {
    const kit = makeKit();
    kit.reader.script(readingOf([]));
    const outcomes: boolean[] = [];
    const pipeline = new SnapPipeline(kit.deps);
    pipeline.setOutcomeHandler((found) => outcomes.push(found));
    await pipeline.onSnap({ photo: PHOTO, trigger: 'auto', shotKind: 'unknown' });
    expect(outcomes).toEqual([false]);
    expect(kit.store.getState().snaps).toEqual([]);
    expect(kit.snaps.list()).toEqual([]);
    expect(identifyMock.requests).toEqual([]);
  });
});

describe('the receipt', () => {
  it('shows a line with a thumbnail from the first moment, before anything is read', async () => {
    const kit = makeKit();
    const reading = gate();
    kit.reader.hold = reading.hold;
    kit.reader.script(readingOf([dune]));
    await open(kit);

    await shutter();
    await showReceipt();
    await waitFor(() => expect(screen.getAllByText('IDENTIFYING…').length).toBeGreaterThan(0));
    expect(screen.getByLabelText(/^Dune, identifying…/)).toBeTruthy();
    await act(async () => reading.release());
  });

  it('removes a picture item on a swipe action: gone from the receipt and the cards, and rejected so Review will not add it', async () => {
    const kit = makeKit();
    kit.reader.script(readingOf([dune, mario]));
    await open(kit);

    await shutter();
    await waitFor(() => expect(cards()).toHaveLength(2));
    await showReceipt();
    const row = screen.getByLabelText('Super Mario 64'); // settled, so no status in its label
    await act(async () => row.props.onAccessibilityAction({ nativeEvent: { actionName: 'delete' } }));

    await waitFor(() => expect(screen.queryByText('Super Mario 64')).toBeNull());
    expect(cards()).toHaveLength(1);
    const rejected = [...scanMock.sessions.values()].flatMap((s) => s.candidates).filter((c) => c.decision === 'rejected');
    expect(rejected.map((c) => c.visibleText)).toEqual(['Super Mario 64']);
  });

  it('leaves the receipt when the scan it was sent to is over', async () => {
    const kit = makeKit();
    kit.reader.script(readingOf([dune]));
    await open(kit);

    await shutter();
    await screen.findByText('You own 1');
    expect(kit.store.getState().snaps).toHaveLength(1);
    await act(async () => kit.tray.getState().setSessionId(null)); // committed or discarded
    await waitFor(() => expect(kit.store.getState().snaps).toEqual([]));
  });
});

describe('the dev menu', () => {
  it('replays a recorded fixture snap end to end without a camera', async () => {
    const kit = makeKit();
    const source = new FakeScannerSource();
    const devSource = new FakeScannerSource();
    await renderWithProviders(
      <ScannerScreen libraryId={MOCK_LIBRARY_ID} source={source} devSource={devSource} pipeline={kit.deps} tuning={tuning} autoSnapTuning={{ flashMs: 50, resize }} />,
    );

    await fireEvent.press(screen.getByLabelText('Snap a fixture shelf'));
    expect(await screen.findByText('Super Mario 64')).toBeTruthy();
    expect(await screen.findByText('Space Mutiny')).toBeTruthy();
    expect(await screen.findByText('Get closer for these 2')).toBeTruthy();
    expect(screen.getAllByTestId(/^card-/).length).toBeGreaterThanOrEqual(4);
    expect(source.captures).toBe(0); // no camera
    expect(kit.reader.stills).toEqual([]); // and no model: the recording was played
  });
});

describe('shot classification', () => {
  it('sends the framing kind and one segment id shared by the snap\'s items, echoed on the cards', async () => {
    const kit = makeKit();
    kit.reader.script(readingOf([dune, mario]));
    await new SnapPipeline(kit.deps).onSnap({ photo: PHOTO, trigger: 'auto', shotKind: 'shelf_segment' });

    await waitFor(() => expect(identifyMock.requests).toHaveLength(1));
    const body = identifyMock.requests[0]!.body;
    expect(body.shotKind).toBe('shelf_segment');
    const segments = body.items?.map((i) => i.segmentId) ?? [];
    expect(segments).toHaveLength(2);
    expect(new Set(segments).size).toBe(1); // one segment links the snap's items
    expect(segments[0]).toBe(kit.store.getState().snaps[0]!.segmentId);
    await waitFor(() => expect(Object.values(useCardsStore.getState().byId)).toHaveLength(2));
    expect(Object.values(useCardsStore.getState().byId).map((c) => c.shotKind)).toEqual(['shelf_segment', 'shelf_segment']);
  });
});
