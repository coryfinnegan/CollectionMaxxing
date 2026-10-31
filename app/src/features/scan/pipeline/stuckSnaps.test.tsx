// One picture the server cannot take (it answers 500 every time) must not hold the pictures behind it, and must not be retried
// forever: it waits a little longer after each failure, is parked after a few, and the person is told. A server that is not
// there (no answer, or a gateway that says so) is not the picture's fault: those wait, as before.
import { HttpResponse, http } from 'msw';
import type { IdentifyRequest } from '@/api/generated/models';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { identifyMock } from '@/mocks/identify';
import { scanMock } from '@/mocks/scan';
import { server } from '@/mocks/server';
import { useCardsStore } from '../cards/cardsStore';
import type { ReadItem } from '../recognition/types';
import { picItemOf } from './payload';
import { SnapPipeline } from './SnapPipeline';
import { makeKit, readItem, resetKit } from './testKit';

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

const waitingSnap = (snapId: string, item: ReadItem, createdAt: number) => ({
  snapId,
  libraryId: MOCK_LIBRARY_ID,
  trigger: 'shutter' as const,
  createdAt,
  thumbUri: 'file:///snaps/x.jpg',
  phase: 'read' as const,
  sent: false,
  items: [picItemOf(item)],
  unreadableCount: 0,
  engine: 'apple_model' as const,
});

/** The server answers `status` for this snap and takes every other one. Counts how often the bad one was tried. */
function failFor(snapId: string, status: number | 'drop') {
  const tries = { count: 0 };
  server.use(
    http.post('*/api/v1/scan-sessions/:id/identify', async ({ request }) => {
      const body = (await request.clone().json()) as IdentifyRequest;
      if (body.snapId !== snapId) return undefined; // the default handler answers
      tries.count += 1;
      if (status === 'drop') return HttpResponse.error();
      return HttpResponse.json({ title: 'Server problem', status }, { status, headers: { 'Content-Type': 'application/problem+json' } });
    }),
  );
  return tries;
}

const MINUTE = 60_000;

function setup() {
  const kit = makeKit();
  let clock = 1_000_000;
  const pipeline = new SnapPipeline({ ...kit.deps, now: () => clock });
  kit.store.getState().addSnap(waitingSnap('snap-bad', dune, 1));
  kit.store.getState().addSnap(waitingSnap('snap-good', mario, 2));
  /** One send, a minute after the last one, so a snap waiting out its backoff is ready again. */
  const sendAgain = async () => {
    clock += MINUTE;
    await pipeline.flush();
  };
  return { kit, pipeline, sendAgain, advance: (ms: number) => void (clock += ms) };
}

const sentIds = (kit: ReturnType<typeof makeKit>) => kit.store.getState().snaps.filter((s) => s.sent).map((s) => s.snapId);

describe('a picture the server answers 500 for', () => {
  it('does not hold up the one behind it', async () => {
    const { kit, pipeline } = setup();
    const tries = failFor('snap-bad', 500);

    await pipeline.flush();

    expect(tries.count).toBe(1);
    expect(sentIds(kit)).toEqual(['snap-good']);
  });

  it('is parked after five failed tries: no more tries, the picture stays, and the person is told', async () => {
    const { kit, pipeline, sendAgain } = setup();
    const tries = failFor('snap-bad', 500);

    await pipeline.flush();
    for (let i = 0; i < 9; i++) await sendAgain();

    expect(tries.count).toBe(5);
    const parked = kit.store.getState().snaps.find((s) => s.snapId === 'snap-bad');
    expect(parked).toMatchObject({ phase: 'read', sent: false }); // nothing read is thrown away
    expect(kit.tray.getState().problem).toMatch(/Couldn.t send a picture/);
    expect(kit.tray.getState().online).toBe(true); // the server is there; the person is not told they are offline
  });

  it('keeps telling them while it is parked, also after the next picture goes through', async () => {
    const { kit, pipeline, sendAgain } = setup();
    failFor('snap-bad', 500);
    await pipeline.flush();
    for (let i = 0; i < 6; i++) await sendAgain();
    expect(kit.tray.getState().problem).toMatch(/Couldn.t send a picture/);

    kit.store.getState().addSnap(waitingSnap('snap-later', mario, 3));
    await sendAgain();

    expect(sentIds(kit).sort()).toEqual(['snap-good', 'snap-later']);
    expect(kit.tray.getState().problem).toMatch(/Couldn.t send a picture/);
  });

  it('waits longer after each failure instead of trying on every send', async () => {
    const { kit, pipeline, advance } = setup();
    const tries = failFor('snap-bad', 500);

    await pipeline.flush();
    await pipeline.flush(); // straight away: still waiting out the first failure
    expect(tries.count).toBe(1);

    advance(10_000);
    await pipeline.flush();
    expect(tries.count).toBe(2);
    advance(5_000); // the second wait is longer than the first
    await pipeline.flush();
    expect(tries.count).toBe(2);
    expect(sentIds(kit)).toEqual(['snap-good']);
  });

  it('is tried again from the start by the next run of the scanner', async () => {
    const { kit, pipeline, sendAgain } = setup();
    failFor('snap-bad', 500);
    await pipeline.flush();
    for (let i = 0; i < 9; i++) await sendAgain();

    server.resetHandlers(); // the server is fixed
    const reopened = new SnapPipeline({ ...kit.deps });
    await reopened.flush();

    expect(sentIds(kit).sort()).toEqual(['snap-bad', 'snap-good']);
  });
});

describe('a server that is not there', () => {
  it.each([
    ['no answer at all', 'drop' as const],
    ['a gateway that says the server is down (502)', 502],
    ['unavailable (503)', 503],
    ['a gateway timeout (504)', 504],
  ])('keeps waiting, never parks: %s', async (_name, status) => {
    const { kit, pipeline, sendAgain } = setup();
    const tries = failFor('snap-bad', status);

    await pipeline.flush();
    for (let i = 0; i < 9; i++) await sendAgain();

    expect(tries.count).toBe(10); // tried on every send
    expect(kit.tray.getState().online).toBe(false); // QUEUED, as before
    expect(kit.tray.getState().problem).toBeNull();
  });
});
