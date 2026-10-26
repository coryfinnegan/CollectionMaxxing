// The counters bar follows the session: snapshots and `session.counters` events seed it, polls refresh it.
import { http, HttpResponse } from 'msw';
import { server } from '@/mocks/server';
import { candidate } from '@/features/review/testData';
import { MemoryKeyValueStore } from '../tray/trayStore';
import { followSession, routeSessionMessage } from './sessionApi';
import { createSessionCountersStore, getSessionCountersStore, setSessionCountersStore } from './sessionCounters';

const SESSION = 'b1000000-0000-4000-8000-000000000001';
const COUNTERS = { captured: 4, added: 1, toReview: 1, owned: 1, skipped: 1 };

const until = async (check: () => boolean) => {
  for (let i = 0; i < 400 && !check(); i++) await new Promise((r) => setTimeout(r, 5));
  expect(check()).toBe(true);
};

beforeEach(() => setSessionCountersStore(createSessionCountersStore({ storage: new MemoryKeyValueStore() })));
afterEach(() => setSessionCountersStore(undefined));

describe('session.counters events', () => {
  it('seeds the bar from the event body', () => {
    routeSessionMessage(SESSION, { event: 'session.counters', id: '7', data: JSON.stringify({ counters: COUNTERS, changeSeq: 7 }) });

    expect(getSessionCountersStore().getState().snapshots[SESSION]).toEqual({ counters: COUNTERS, changeSeq: 7 });
  });

  it('falls back to the event id when the body carries no change counter', () => {
    routeSessionMessage(SESSION, { event: 'session.counters', id: '9', data: JSON.stringify({ counters: COUNTERS }) });

    expect(getSessionCountersStore().getState().snapshots[SESSION]).toEqual({ counters: COUNTERS, changeSeq: 9 });
  });

  it('skips a malformed event and keeps the last snapshot', () => {
    routeSessionMessage(SESSION, { event: 'session.counters', id: '7', data: JSON.stringify({ counters: COUNTERS, changeSeq: 7 }) });
    routeSessionMessage(SESSION, { event: 'session.counters', id: '8', data: '{not json' });

    expect(getSessionCountersStore().getState().snapshots[SESSION]).toEqual({ counters: COUNTERS, changeSeq: 7 });
  });
});

describe('counters under polling', () => {
  it('seeds the bar from the polled snapshot’s whole-session counters', async () => {
    const changed = candidate({ seq: 1, changeSeq: 7, rawCode: 'changed' });
    server.use(
      http.get('*/api/v1/scan-sessions/:id/events', () => new HttpResponse(null, { status: 503 })),
      http.get('*/api/v1/scan-sessions/:id', () =>
        HttpResponse.json({
          id: SESSION,
          libraryId: SESSION,
          status: 'open',
          defaultLocationId: null,
          defaultStatus: null,
          createdAt: '2026-10-01T12:00:00Z',
          committedAt: null,
          candidates: [changed],
          counters: COUNTERS,
          segments: [],
        }),
      ),
    );

    const follower = followSession(SESSION, undefined, { retryMs: 1, pollMs: 5 });
    try {
      await until(() => getSessionCountersStore().getState().snapshots[SESSION]?.changeSeq === 7);
    } finally {
      follower.close();
    }

    expect(getSessionCountersStore().getState().snapshots[SESSION]?.counters).toEqual(COUNTERS);
  });
});
