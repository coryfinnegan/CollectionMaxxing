// Mock scan sessions. They behave like the real API (docs/04): an Idempotency-Key on barcode batches replays the
// same answer, each code gets its own result (a bad code is one `error` candidate, not a failed batch), slow
// lookups start `pending` and resolve later, every candidate carries ownership, a viewer cannot accept (403),
// and the events endpoint is a real stream that tests can drive and drop.
import { HttpResponse, http } from 'msw';
import type { CandidateDto, OwnershipDto, ReleaseDto } from '@/api/generated/models';
import { DEV_CODES } from '@/features/scan/scanner/devCodes';
import { MOCK_LIBRARY_ID, newMockId } from './data';

const problem = (status: number, title: string) =>
  HttpResponse.json({ title, status }, { status, headers: { 'Content-Type': 'application/problem+json' } });

type Known = {
  release: ReleaseDto;
  owned: { itemId: string; locationPath: string }[];
  /** Starts pending and resolves when a test (or the dev timer) calls `resolvePending`. */
  slow?: boolean;
};

const release = (n: number, title: string, format: ReleaseDto['format'], platform: string | null, year: number, creators: string[]): ReleaseDto => ({
  id: `c1000000-0000-4000-8000-${String(n).padStart(12, '0')}`,
  workId: `c2000000-0000-4000-8000-${String(n).padStart(12, '0')}`,
  title,
  format,
  platform: platform ? { id: `c3000000-0000-4000-8000-${String(n).padStart(12, '0')}`, name: platform, shortName: platform, manufacturer: null } : null,
  region: null,
  edition: null,
  publisher: null,
  releaseDate: null,
  year,
  creators,
  pageCount: null,
  runtimeMinutes: null,
  coverUrl: null,
  source: 'user',
  identifiers: [],
});

const known = (): Record<string, Known> => ({
  [DEV_CODES.duneOwned]: {
    release: release(1, 'Dune', 'paperback', null, 1990, ['Frank Herbert']),
    owned: [{ itemId: 'd0000000-0000-4000-8000-000000000001', locationPath: 'Living room › Shelf 2' }],
  },
  [DEV_CODES.mario64New]: { release: release(2, 'Super Mario 64', 'game_cartridge', 'Nintendo 64', 1996, []), owned: [] },
  [DEV_CODES.ghostbustersNew]: { release: release(3, 'Ghostbusters', 'vhs', null, 1984, []), owned: [] },
  [DEV_CODES.goldeneyeOwned]: {
    release: release(4, 'GoldenEye 007', 'game_cartridge', 'Nintendo 64', 1997, []),
    owned: [{ itemId: 'd0000000-0000-4000-8000-000000000002', locationPath: 'Game room › Shelf 1' }],
  },
  [DEV_CODES.slow]: { release: release(5, 'Zelda: Ocarina of Time', 'game_cartridge', 'Nintendo 64', 1998, []), owned: [], slow: true },
});

type MockSession = {
  id: string;
  libraryId: string;
  seq: number;
  candidates: CandidateDto[];
  /** Idempotency-Key -> the answer already given. */
  answers: Map<string, CandidateDto[]>;
  status: 'open' | 'committed' | 'abandoned';
};

type Stream = { sessionId: string; controller: ReadableStreamDefaultController<Uint8Array> };

const encoder = new TextEncoder();

export const scanMock = {
  sessions: new Map<string, MockSession>(),
  streams: new Set<Stream>(),
  catalogue: known(),
  /** Set to `'viewer'` to make PATCH answer 403, as the real API does for a viewer. */
  role: 'owner' as 'owner' | 'editor' | 'viewer',
  /** Every barcode request the server received, so tests can assert nothing was sent twice. */
  barcodeRequests: [] as { sessionId: string; key: string | null; codes: string[] }[],

  reset() {
    for (const s of this.streams) {
      try {
        s.controller.close();
      } catch {
        // already closed
      }
    }
    this.sessions.clear();
    this.streams.clear();
    this.catalogue = known();
    this.role = 'owner';
    this.barcodeRequests = [];
  },

  /** Resolves every pending candidate (the slow lookup finished). */
  resolvePending(sessionId?: string) {
    for (const session of this.sessions.values()) {
      if (sessionId && session.id !== sessionId) continue;
      for (const c of session.candidates.filter((x) => x.resolution === 'pending')) {
        const entry = this.catalogue[c.rawCode ?? ''];
        if (!entry) continue;
        Object.assign(c, resolved(entry), { seq: ++session.seq });
        emit(session, 'candidate.updated', c);
      }
    }
  },

  /** Closes every open event stream, like a dropped connection. */
  dropStreams() {
    for (const s of this.streams) {
      try {
        s.controller.close();
      } catch {
        // already closed
      }
    }
    this.streams.clear();
  },
};

const resolved = (entry: Known): Partial<CandidateDto> => ({
  resolution: 'matched',
  score: 1,
  release: entry.release,
  alreadyOwnedCount: entry.owned.length,
  ownedAt: entry.owned.map((o) => o.locationPath),
});

function emit(session: MockSession, event: string, candidate: CandidateDto) {
  const text = `event: ${event}\nid: ${candidate.seq}\ndata: ${JSON.stringify(candidate)}\n\n`;
  for (const stream of scanMock.streams) {
    if (stream.sessionId !== session.id) continue;
    try {
      stream.controller.enqueue(encoder.encode(text));
    } catch {
      scanMock.streams.delete(stream);
    }
  }
}

const newCandidate = (session: MockSession, code: string, symbology: string | null, copies: number): CandidateDto => {
  const entry = scanMock.catalogue[code];
  const base: CandidateDto = {
    id: newMockId(),
    seq: ++session.seq,
    source: 'barcode',
    rawCode: code,
    symbology,
    visibleText: null,
    localId: null,
    box: null,
    boxSource: null,
    resolution: 'not_found',
    score: null,
    artDistance: null,
    release: null,
    inferred: null,
    alternatives: null,
    decision: 'undecided',
    copies,
    alreadyOwnedCount: 0,
    ownedAt: [],
    createdItemId: null,
  };
  if (code === DEV_CODES.unreadable) return { ...base, resolution: 'error' };
  if (!entry) return base;
  if (entry.slow) return { ...base, resolution: 'pending', alreadyOwnedCount: entry.owned.length };
  return { ...base, ...resolved(entry) };
};

const ownershipFor = (code: string): OwnershipDto => {
  const entry = scanMock.catalogue[code];
  const owned = entry?.owned ?? [];
  return {
    owned: owned.length > 0,
    count: owned.length,
    sameWorkOtherFormat: [],
    copies: owned.map((o) => ({
      itemId: o.itemId,
      releaseId: entry!.release.id,
      workId: entry!.release.workId,
      title: entry!.release.title,
      format: entry!.release.format,
      platform: entry!.release.platform?.name ?? null,
      status: 'owned',
      locationId: null,
      locationPath: o.locationPath,
    })),
  };
};

export const handlers = [
  http.post('*/api/v1/libraries/:libraryId/scan-sessions', ({ params }) => {
    if (params.libraryId !== MOCK_LIBRARY_ID) return problem(404, 'Not found');
    const session: MockSession = { id: newMockId(), libraryId: MOCK_LIBRARY_ID, seq: 0, candidates: [], answers: new Map(), status: 'open' };
    scanMock.sessions.set(session.id, session);
    return HttpResponse.json({ id: session.id }, { status: 201 });
  }),

  http.get('*/api/v1/scan-sessions/:id', ({ params, request }) => {
    const session = scanMock.sessions.get(String(params.id));
    if (!session) return problem(404, 'Not found');
    const since = Number(new URL(request.url).searchParams.get('since') ?? 0);
    return HttpResponse.json({
      id: session.id,
      libraryId: session.libraryId,
      status: session.status,
      defaultLocationId: null,
      defaultStatus: null,
      createdAt: '2026-10-01T12:00:00Z',
      committedAt: null,
      candidates: session.candidates.filter((c) => c.seq > since),
    });
  }),

  http.delete('*/api/v1/scan-sessions/:id', ({ params }) => {
    const session = scanMock.sessions.get(String(params.id));
    if (!session) return problem(404, 'Not found');
    if (scanMock.role === 'viewer') return problem(403, 'Forbidden');
    session.status = 'abandoned';
    return new HttpResponse(null, { status: 204 });
  }),

  http.get('*/api/v1/scan-sessions/:id/events', ({ params, request }) => {
    const session = scanMock.sessions.get(String(params.id));
    if (!session) return problem(404, 'Not found');
    const last = Number(request.headers.get('Last-Event-ID') ?? 0);
    let stream: Stream | undefined;
    const body = new ReadableStream<Uint8Array>({
      start(controller) {
        // Replay what the client missed, then stay open for new events.
        for (const c of session.candidates.filter((x) => x.seq > last)) {
          controller.enqueue(encoder.encode(`event: candidate.added\nid: ${c.seq}\ndata: ${JSON.stringify(c)}\n\n`));
        }
        stream = { sessionId: session.id, controller };
        scanMock.streams.add(stream);
      },
      cancel() {
        if (stream) scanMock.streams.delete(stream);
      },
    });
    return new HttpResponse(body, { headers: { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-cache' } });
  }),

  http.post('*/api/v1/scan-sessions/:id/barcodes', async ({ params, request }) => {
    const session = scanMock.sessions.get(String(params.id));
    if (!session) return problem(404, 'Not found');
    const body = (await request.json()) as { codes?: { value?: string | null; symbology?: string | null; copies?: number | null }[] };
    const codes = body.codes ?? [];
    const key = request.headers.get('Idempotency-Key');
    scanMock.barcodeRequests.push({ sessionId: session.id, key, codes: codes.map((c) => c.value ?? '') });
    if (codes.length > 50) return problem(422, 'At most 50 codes per request.');
    const replay = key ? session.answers.get(key) : undefined;
    if (replay) return HttpResponse.json({ candidates: replay });

    const answer: CandidateDto[] = [];
    for (const code of codes) {
      const value = code.value?.trim();
      if (!value) continue;
      const copies = code.copies ?? 1;
      const existing = session.candidates.find((c) => c.rawCode === value);
      if (existing) {
        existing.copies += copies;
        existing.seq = ++session.seq;
        emit(session, 'candidate.updated', existing);
        answer.push(existing);
        continue;
      }
      const candidate = newCandidate(session, value, code.symbology ?? null, copies);
      session.candidates.push(candidate);
      emit(session, 'candidate.added', candidate);
      answer.push(candidate);
    }
    if (key) session.answers.set(key, answer);
    return HttpResponse.json({ candidates: answer });
  }),

  http.patch('*/api/v1/scan-sessions/:id/candidates/:cid', async ({ params, request }) => {
    if (scanMock.role === 'viewer') return problem(403, 'Forbidden');
    const session = scanMock.sessions.get(String(params.id));
    const candidate = session?.candidates.find((c) => c.id === params.cid);
    if (!session || !candidate) return problem(404, 'Not found');
    const body = (await request.json()) as { decision?: CandidateDto['decision'] | null; copies?: number | null };
    if (body.decision) candidate.decision = body.decision;
    if (body.copies) candidate.copies = body.copies;
    candidate.seq = ++session.seq;
    emit(session, 'candidate.updated', candidate);
    return HttpResponse.json(candidate);
  }),

  http.get('*/api/v1/libraries/:libraryId/ownership', ({ request }) => {
    const code = new URL(request.url).searchParams.get('code');
    if (!code) return problem(400, 'Give a code, a release or a work.');
    return HttpResponse.json(ownershipFor(code));
  }),
];
