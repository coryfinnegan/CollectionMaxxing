// A repeat snap that matches a stored print shows its card at once from the local match, offline, and the server's answer replaces it.
import { HttpResponse, http } from 'msw';
import { identifyMock } from '@/mocks/identify';
import { scanMock } from '@/mocks/scan';
import { server } from '@/mocks/server';
import { useCardsStore, withLocalCards } from '../cards/cardsStore';
import { pictureLines } from '../pipeline/lines';
import { SnapPipeline } from '../pipeline/SnapPipeline';
import { makeKit, readItem, readingOf, recordRequests, resetKit } from '../pipeline/testKit';
import { createLocalMatch } from './matcher';
import { setFingerprintStorage } from './store';
import { FakeVision, freshStore, printRow } from './testKit';

beforeEach(() => {
  scanMock.reset();
  identifyMock.reset();
  useCardsStore.getState().reset();
});
afterEach(() => {
  resetKit();
  setFingerprintStorage(undefined);
});

const BOX = { x: 0.05, y: 0.1, width: 0.2, height: 0.7 };
const goOffline = () =>
  server.use(
    http.post('*/api/v1/scan-sessions/:id/identify', () => HttpResponse.error()),
    http.post('*/api/v1/libraries/:id/scan-sessions', () => HttpResponse.error()),
  );

async function repeatSnap(stored: boolean) {
  const store = freshStore();
  if (stored) store.put(printRow(3, { print: '0', synced: true }));
  const kit = makeKit({ localMatch: createLocalMatch({ store, vision: new FakeVision(), maxDistance: () => 0.5 }) });
  kit.reader.script(readingOf([readItem('Dune', { box: BOX })]));
  const pipeline = new SnapPipeline(kit.deps);
  return { kit, pipeline };
}

describe('offline', () => {
  it('draws a card from the local match with no request made, and the snap stays Queued', async () => {
    const { kit, pipeline } = await repeatSnap(true);
    goOffline();
    const requests = recordRequests();

    await pipeline.onSnap(await kit.snapEvent());
    await pipeline.flush();

    const cards = Object.values(useCardsStore.getState().local);
    expect(cards).toHaveLength(1);
    expect(cards[0]!.release?.id).toBe(printRow(3).releaseId); // the release it matched
    expect(cards[0]!.release?.title).toBe('Dune');
    expect(cards[0]!.resolution).toBe('matched');
    expect(kit.store.getState().snaps[0]!.sent).toBeFalsy();
    const lines = pictureLines(kit.store.getState().snaps, withLocalCards(useCardsStore.getState()), false);
    expect(lines.map((l) => l.status)).toEqual(['queued']);
    const seen = await requests.all();
    requests.stop();
    expect(seen.some((r) => /feature-prints|artwork|catalog/.test(r.url))).toBe(false);
  });

  it('the local card is not a server candidate: nothing can add it or commit it', async () => {
    const { kit, pipeline } = await repeatSnap(true);
    goOffline();

    await pipeline.onSnap(await kit.snapEvent());

    expect(useCardsStore.getState().byId).toEqual({});
  });

  it('no match offline draws no card, as before', async () => {
    const { kit, pipeline } = await repeatSnap(false);
    goOffline();

    await pipeline.onSnap(await kit.snapEvent());

    expect(useCardsStore.getState().local).toEqual({});
    expect(withLocalCards(useCardsStore.getState())).toEqual([]);
  });
});

describe('reconnect', () => {
  it("the server's answer replaces the local card's details, with no duplicate card", async () => {
    const { kit, pipeline } = await repeatSnap(true);
    goOffline();
    await pipeline.onSnap(await kit.snapEvent());
    expect(Object.values(useCardsStore.getState().local)).toHaveLength(1);

    server.resetHandlers(); // the network is back
    await pipeline.flush();

    const state = useCardsStore.getState();
    expect(state.local).toEqual({});
    const shown = withLocalCards(state);
    expect(shown).toHaveLength(1);
    expect(shown[0]!.id).not.toMatch(/^local:/);
    expect(shown[0]!.localId).toBe(kit.store.getState().snaps[0]!.items[0]!.localId);
    expect(kit.store.getState().snaps[0]!.sent).toBe(true);
  });

  it('swiping the item away takes its local card with it', async () => {
    const { kit, pipeline } = await repeatSnap(true);
    goOffline();
    await pipeline.onSnap(await kit.snapEvent());
    const snap = kit.store.getState().snaps[0]!;

    pipeline.removeItem(snap.snapId, snap.items[0]!.localId);

    expect(useCardsStore.getState().local).toEqual({});
  });
});
