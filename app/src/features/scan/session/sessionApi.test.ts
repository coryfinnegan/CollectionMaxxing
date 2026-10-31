// The event cursor of a scan session is the candidate's `changeSeq` (it goes up on every insert and update), never `seq`
// (the creation order, which an update leaves alone). A reconnect or a poll asks only for what changed since the last one.
import { http, HttpResponse } from 'msw';
import { server } from '@/mocks/server';
import { candidate, release } from '@/features/review/testData';
import { localDateString } from '@/lib/dates';
import { setHapticDriver, type HapticDriver } from '@/lib/haptics';
import { useCardsStore } from '../cards/cardsStore';
import { useAutoCommitUndoStore } from './autoCommitUndo';
import { announce, createSessionOnce, followSession, resumeCursor, routeSessionMessage, wishlistCandidate } from './sessionApi';

const SESSION = 'b1000000-0000-4000-8000-000000000001';

const until = async (check: () => boolean) => {
  for (let i = 0; i < 400 && !check(); i++) await new Promise((r) => setTimeout(r, 5));
  expect(check()).toBe(true);
};

describe('the session cursor', () => {
  beforeEach(() => useCardsStore.getState().reset());

  it('resumes from the highest changeSeq, not the highest seq', () => {
    const created = candidate({ seq: 3, changeSeq: 3 });
    const updatedLater = candidate({ seq: 1, changeSeq: 9 }); // created first, changed last
    expect(resumeCursor([created, updatedLater])).toBe('9');
  });

  it('has no cursor for a session with nothing in it', () => {
    expect(resumeCursor([])).toBeUndefined();
  });

  it('polls with the last changeSeq it saw, so an unchanged candidate is not asked for again', async () => {
    const changed = candidate({ seq: 1, changeSeq: 7, rawCode: 'changed' });
    const asked: number[] = [];
    server.use(
      http.get('*/api/v1/scan-sessions/:id/events', () => new HttpResponse(null, { status: 503 })),
      http.get('*/api/v1/scan-sessions/:id', ({ request }) => {
        const since = Number(new URL(request.url).searchParams.get('since'));
        asked.push(since);
        return HttpResponse.json({
          id: SESSION,
          libraryId: SESSION,
          status: 'open',
          defaultLocationId: null,
          defaultStatus: null,
          createdAt: '2026-10-01T12:00:00Z',
          committedAt: null,
          candidates: [changed].filter((c) => c.changeSeq > since),
        });
      }),
    );

    // The phone had seen up to change 5 (its candidate was created at seq 1 and has changed since).
    const follower = followSession(SESSION, '5', { retryMs: 1, pollMs: 5 });
    try {
      await until(() => useCardsStore.getState().byId[changed.id]?.changeSeq === 7);
      await until(() => asked.length >= 3);
    } finally {
      follower.close();
    }

    expect(asked[0]).toBe(5);
    expect(asked.slice(1).every((since) => since === 7)).toBe(true); // 7 is the changeSeq; seq (1) would ask for it again
  });
});

describe('per-library session creation', () => {
  const LIB_A = 'aaaaaaaa-0000-4000-8000-000000000000';
  const LIB_B = 'bbbbbbbb-0000-4000-8000-000000000000';

  it('creates one session per library when two libraries ask at once', async () => {
    const asked: string[] = [];
    server.use(
      http.post('*/api/v1/libraries/:libraryId/scan-sessions', async ({ params }) => {
        asked.push(String(params.libraryId));
        await new Promise((r) => setTimeout(r, 20)); // hold the first request open while the second starts
        return HttpResponse.json({ id: `session-for-${params.libraryId}` }, { status: 201 });
      }),
    );

    const [a, b] = await Promise.all([createSessionOnce(LIB_A), createSessionOnce(LIB_B)]);

    expect(a).toBe(`session-for-${LIB_A}`);
    expect(b).toBe(`session-for-${LIB_B}`);
    expect(asked.sort()).toEqual([LIB_A, LIB_B].sort());
  });

  it('sends the session context when creating', async () => {
    const bodies: unknown[] = [];
    server.use(
      http.post('*/api/v1/libraries/:libraryId/scan-sessions', async ({ request }) => {
        bodies.push(await request.json());
        return HttpResponse.json({ id: 'session-1' }, { status: 201 });
      }),
    );

    await createSessionOnce(LIB_A, { defaultLocationId: 'loc-1', defaultStatus: 'wishlist', mediaHint: 'games', channel: 'in_store' });

    expect(bodies).toEqual([
      {
        defaultLocationId: 'loc-1',
        defaultStatus: 'wishlist',
        mediaHint: 'games',
        channel: 'in_store',
        utcOffsetMinutes: -new Date().getTimezoneOffset() || 0,
      },
    ]);
  });

  it('tells the server the phone\'s UTC offset, so an item added in the evening is dated today and not tomorrow', async () => {
    const bodies: { utcOffsetMinutes?: unknown }[] = [];
    server.use(
      http.post('*/api/v1/libraries/:libraryId/scan-sessions', async ({ request }) => {
        bodies.push((await request.json()) as { utcOffsetMinutes?: unknown });
        return HttpResponse.json({ id: 'session-1' }, { status: 201 });
      }),
    );

    await createSessionOnce(LIB_A); // the barcode path, which has no context to send

    expect(bodies).toHaveLength(1);
    expect(bodies[0]!.utcOffsetMinutes).toBe(-new Date().getTimezoneOffset() || 0);
  });

  it('shares one in-flight request when one library asks twice at once', async () => {
    let calls = 0;
    server.use(
      http.post('*/api/v1/libraries/:libraryId/scan-sessions', async () => {
        calls += 1;
        await new Promise((r) => setTimeout(r, 20));
        return HttpResponse.json({ id: 'session-1' }, { status: 201 });
      }),
    );

    const [a, b] = await Promise.all([createSessionOnce(LIB_A), createSessionOnce(LIB_A)]);

    expect(a).toBe('session-1');
    expect(b).toBe('session-1');
    expect(calls).toBe(1);
  });
});

describe('auto-add announcements', () => {
  const hapticCalls: string[] = [];
  const fakeDriver: HapticDriver = {
    tap: () => void hapticCalls.push('tap'),
    mediumTap: () => void hapticCalls.push('mediumTap'),
    longTap: () => void hapticCalls.push('longTap'),
    tick: () => void hapticCalls.push('tick'),
    success: () => void hapticCalls.push('success'),
    wait: () => Promise.resolve(),
  };

  beforeEach(() => {
    useCardsStore.getState().reset();
    useAutoCommitUndoStore.setState({ last: null });
    hapticCalls.length = 0;
    setHapticDriver(fakeDriver);
  });
  afterEach(() => setHapticDriver(null));

  it('routes candidate.auto_committed into the cards with the success haptic and Undo armed', async () => {
    const addedCandidate = candidate({ createdItemId: 'item-1' });
    routeSessionMessage(SESSION, { event: 'candidate.auto_committed', id: '0', data: JSON.stringify(addedCandidate) });

    expect(useCardsStore.getState().byId[addedCandidate.id]?.createdItemId).toBe('item-1');
    expect(useAutoCommitUndoStore.getState().last).toEqual({ sessionId: SESSION, candidateId: addedCandidate.id });
    await until(() => hapticCalls.includes('success'));
  });

  it('stays quiet when the same auto-add arrives twice', async () => {
    const addedCandidate = candidate({ createdItemId: 'item-1' });
    const message = { event: 'candidate.auto_committed', id: '0', data: JSON.stringify(addedCandidate) };
    routeSessionMessage(SESSION, message);
    await until(() => hapticCalls.includes('success'));

    routeSessionMessage(SESSION, message);
    await new Promise((r) => setTimeout(r, 20));

    expect(hapticCalls).toEqual(['success']);
  });

  it('arms Undo for an added event only when the session is known', async () => {
    announce([{ id: 'c-x', event: 'added' }]);
    expect(useAutoCommitUndoStore.getState().last).toBeNull();
    await until(() => hapticCalls.includes('success'));

    announce([{ id: 'c-y', event: 'added' }], SESSION);
    expect(useAutoCommitUndoStore.getState().last).toEqual({ sessionId: SESSION, candidateId: 'c-y' });
  });
});

describe('the Wishlist card action', () => {
  it('sends the day on the phone, so an evening add is not dated tomorrow', async () => {
    const bodies: unknown[] = [];
    server.use(
      http.post('*/api/v1/libraries/:libraryId/items', async ({ request }) => {
        bodies.push(await request.json());
        return HttpResponse.json({}, { status: 201 });
      }),
    );
    const wanted = candidate({ release: release(7, 'Hades') });

    await wishlistCandidate('aaaaaaaa-0000-4000-8000-000000000000', wanted);

    expect(bodies).toEqual([{ releaseId: wanted.release!.id, status: 'wishlist', acquiredOn: localDateString() }]);
  });
});
