// The event cursor of a scan session is the candidate's `changeSeq` (it goes up on every insert and update), never `seq`
// (the creation order, which an update leaves alone). A reconnect or a poll asks only for what changed since the last one.
import { http, HttpResponse } from 'msw';
import { server } from '@/mocks/server';
import { candidate } from '@/features/review/testData';
import { useCardsStore } from '../cards/cardsStore';
import { createSessionOnce, followSession, resumeCursor } from './sessionApi';

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
