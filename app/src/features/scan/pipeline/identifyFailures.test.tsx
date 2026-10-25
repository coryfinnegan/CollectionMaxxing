// What happens to a read that the server does not accept: told to slow down (429), a session that has gone (404) and the
// snapId the old session still holds (409), and a refusal that will not change (400, 413). A read is never lost silently.
import { HttpResponse, http } from 'msw';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { identifyMock } from '@/mocks/identify';
import { scanMock } from '@/mocks/scan';
import { server } from '@/mocks/server';
import type { IdentifyRequest } from '@/api/generated/models';
import { useCardsStore } from '../cards/cardsStore';
import type { ReadItem } from '../recognition/types';
import { picItemOf } from './payload';
import { SnapPipeline } from './SnapPipeline';
import { makeKit, readItem, readingOf, resetKit } from './testKit';

const dune = readItem('Dune', { box: { x: 0.05, y: 0.1, width: 0.2, height: 0.7 } });
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

const refuseOnce = (status: number, headers: Record<string, string> = {}, extra: Record<string, unknown> = {}) =>
  server.use(
    http.post(
      '*/api/v1/scan-sessions/:id/identify',
      () => HttpResponse.json({ title: 'No', status, ...extra }, { status, headers: { 'Content-Type': 'application/problem+json', ...headers } }),
      { once: true },
    ),
  );

const unsentSnap = (snapId: string, item: ReadItem, createdAt: number) => ({
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

describe('a 429 from the server', () => {
  it.each([
    ['honours Retry-After', { 'Retry-After': '7' }, 7_000],
    ['waits a few seconds when the server did not say how long', {}, 5_000],
  ])('%s: the snap is kept, nothing more is sent until then, and it is sent unchanged afterwards', async (_name, headers, waitMs) => {
    const kit = makeKit();
    let clock = 1_000_000;
    const pipeline = new SnapPipeline({ ...kit.deps, now: () => clock });
    kit.reader.script(readingOf([dune]));
    refuseOnce(429, headers);

    await pipeline.onSnap(await kit.snapEvent());
    const [snap] = kit.store.getState().snaps;
    expect(snap).toMatchObject({ phase: 'read', sent: false }); // kept, not removed
    expect(kit.tray.getState().problem).toMatch(/slow down|moment/i); // says what is happening
    expect(kit.tray.getState().online).toBe(true); // not QUEUED: the server is there

    clock += waitMs - 1;
    await pipeline.flush();
    expect(identifyMock.requests).toHaveLength(0); // still waiting

    clock += 1;
    await pipeline.flush();
    expect(identifyMock.requests).toHaveLength(1);
    expect(identifyMock.requests[0]!.body.snapId).toBe(snap!.snapId);
    expect(kit.store.getState().snaps[0]!.sent).toBe(true);
    expect(kit.tray.getState().problem).toBeNull();
  });
});

describe('a session that has gone', () => {
  it('sends the waiting snap to a new session under a new snapId, so the old session\'s row cannot refuse it', async () => {
    const kit = makeKit();
    const pipeline = new SnapPipeline(kit.deps);
    kit.reader.script(readingOf([dune, mario]));
    identifyMock.dropResponses = 1; // the old session got the snap; the answer never came back

    await pipeline.onSnap(await kit.snapEvent());
    expect(kit.store.getState().snaps[0]).toMatchObject({ sent: false });
    expect(identifyMock.requests).toHaveLength(1);
    const oldSession = kit.tray.getState().sessionId!;
    scanMock.sessions.delete(oldSession); // the session is gone (expired, or the server was reset)

    await pipeline.flush();
    expect(kit.store.getState().snaps[0]).toMatchObject({ sent: true });
    expect(kit.store.getState().snaps[0]!.sessionId).not.toBe(oldSession);
    const [first, ...later] = identifyMock.requests.map((r) => r.body.snapId);
    expect(later.length).toBeGreaterThan(0);
    expect(later.every((id) => id !== first)).toBe(true); // a fresh snapId for the new session
    expect(Object.keys(useCardsStore.getState().byId)).toHaveLength(2);
    expect(kit.tray.getState().problem).toBeNull();
  });

  it('also takes a new snapId when the server says this one is in use, and carries on', async () => {
    const kit = makeKit();
    const pipeline = new SnapPipeline(kit.deps);
    kit.reader.script(readingOf([dune]));
    const seen: string[] = [];
    server.use(
      http.post(
        '*/api/v1/scan-sessions/:id/identify',
        async ({ request }) => {
          seen.push(String(((await request.clone().json()) as IdentifyRequest).snapId));
          return HttpResponse.json(
            { title: 'That snapId was already used.', status: 409, code: 'scan.snap_in_use' },
            { status: 409, headers: { 'Content-Type': 'application/problem+json' } },
          );
        },
        { once: true },
      ),
    );

    await pipeline.onSnap(await kit.snapEvent());
    expect(kit.store.getState().snaps[0]).toMatchObject({ sent: true });
    expect(identifyMock.requests).toHaveLength(1);
    expect(identifyMock.requests[0]!.body.snapId).not.toBe(seen[0]);
  });
});

describe('a refusal that will not change', () => {
  it.each([
    [400, /Couldn.t look up/],
    [413, /too many items/],
    [409, /Couldn.t look up/],
  ])('%s: the person is told, and the snap and everything kept for it are gone', async (status, message) => {
    const kit = makeKit();
    const pipeline = new SnapPipeline(kit.deps);
    kit.reader.script(readingOf([dune]));
    refuseOnce(status);

    await pipeline.onSnap(await kit.snapEvent());

    expect(kit.tray.getState().problem).toMatch(message);
    expect(kit.store.getState().snaps).toEqual([]);
    expect(kit.snaps.list()).toEqual([]);
    expect(kit.fs.live()).toEqual([]);
  });

  it('keeps telling them when the next snap in the same send goes through', async () => {
    const kit = makeKit();
    kit.store.getState().addSnap(unsentSnap('snap-big', dune, 1));
    kit.store.getState().addSnap(unsentSnap('snap-fine', mario, 2));
    server.use(
      http.post(
        '*/api/v1/scan-sessions/:id/identify',
        async ({ request }) => {
          const body = (await request.clone().json()) as IdentifyRequest;
          if (body.snapId !== 'snap-big') return undefined; // the default handler answers
          return HttpResponse.json({ title: 'Too big', status: 413 }, { status: 413, headers: { 'Content-Type': 'application/problem+json' } });
        },
      ),
    );

    await new SnapPipeline(kit.deps).flush();

    expect(kit.store.getState().snaps.map((s) => [s.snapId, s.sent])).toEqual([['snap-fine', true]]);
    expect(kit.tray.getState().problem).toMatch(/too many items/);
  });
});
