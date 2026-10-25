// Mock picture endpoints (docs/04): POST /scan-sessions/{id}/identify, POST …/candidates/{cid}:art and the artwork route.
// Like the real API: one call per snap, idempotent on `snapId` (a retry gets the same candidates and creates none), what is
// settled comes back at once and the rest arrives over the session's event stream, a close call carries its alternatives'
// `artworkUrl`s, and `:art` answers 409 once the person has decided. The body is JSON text only.
// This file also re-exports the barcode handlers, so `handlers.ts` needs one line to pick up both.
import { HttpResponse, http } from 'msw';
import type { CandidateDto, IdentifyItem, IdentifyRequest, ReleaseDto } from '@/api/generated/models';
import { newMockId } from './data';
import { handlers as barcodeHandlers, scanMock } from './scan';

const problem = (status: number, title: string, code?: string) =>
  HttpResponse.json({ title, status, ...(code ? { code } : {}) }, { status, headers: { 'Content-Type': 'application/problem+json' } });

const encoder = new TextEncoder();

const release = (n: number, title: string, format: ReleaseDto['format'], platform: string, year: number): ReleaseDto => ({
  id: `c1100000-0000-4000-8000-${String(n).padStart(12, '0')}`,
  workId: `c2100000-0000-4000-8000-${String(n).padStart(12, '0')}`,
  title,
  format,
  platform: { id: `c3100000-0000-4000-8000-${String(n).padStart(12, '0')}`, name: platform, shortName: platform, manufacturer: null },
  region: null,
  edition: null,
  publisher: null,
  releaseDate: null,
  year,
  creators: [],
  pageCount: null,
  runtimeMinutes: null,
  coverUrl: null,
  source: 'user',
  identifiers: [],
});

/** The two releases of the close call: the original and the re-release. */
export const CLOSE_CALL = {
  title: 'Pokemon Yellow',
  original: release(21, 'Pokemon Yellow', 'game_cartridge', 'Game Boy', 1999),
  rerelease: release(22, "Pokemon Yellow (Player's Choice)", 'game_cartridge', 'Game Boy', 2000),
};

type Mock = {
  /** Every identify request the server received (the parsed JSON body). */
  requests: { sessionId: string; body: IdentifyRequest }[];
  /** Every `:art` request. */
  artRequests: { sessionId: string; candidateId: string; distances: { releaseId: string; distance: number }[] }[];
  /** Every artwork download: the path and the Authorization header it carried. */
  coverRequests: { path: string; authorization: string | null }[];
  /** Set to a promise to hold every identify answer until it settles. */
  hold: Promise<void> | null;
  /** Answers identify with this status (once per call while set), e.g. 500. */
  failWith: number | null;
  /** The next N identify calls are processed by the server but the answer is lost on the way (a dropped connection). */
  dropResponses: number;
  /** Makes the artwork route answer 404. */
  coversMissing: boolean;
  answers: Map<string, CandidateDto[]>;
  /** The session each snapId was first sent to: like the real API, another session cannot use it (409 `scan.snap_in_use`). */
  answerSessions: Map<string, string>;
  reset(): void;
  /** Resolves every pending picture candidate (the slow lookup finished) and streams the update. */
  resolvePending(): void;
};

export const identifyMock: Mock = {
  requests: [],
  artRequests: [],
  coverRequests: [],
  hold: null,
  failWith: null,
  dropResponses: 0,
  coversMissing: false,
  answers: new Map(),
  answerSessions: new Map(),
  reset() {
    this.requests = [];
    this.artRequests = [];
    this.coverRequests = [];
    this.hold = null;
    this.failWith = null;
    this.dropResponses = 0;
    this.coversMissing = false;
    this.answers = new Map();
    this.answerSessions = new Map();
  },
  resolvePending() {
    for (const session of scanMock.sessions.values()) {
      for (const c of session.candidates.filter((x) => x.resolution === 'pending' && x.source !== 'barcode')) {
        const entry = lookup(c.visibleText ?? '');
        if (entry && 'release' in entry) {
          Object.assign(c, resolved(entry), { changeSeq: ++session.seq });
          emit(session.id, c);
        }
      }
    }
  },
};

const key = (text: string) => text.trim().toLowerCase();

const lookup = (text: string) => {
  const wanted = key(text);
  return Object.values(scanMock.catalog).find((e) => key(e.release.title) === wanted);
};

const resolved = (entry: NonNullable<ReturnType<typeof lookup>>): Partial<CandidateDto> => ({
  resolution: 'matched',
  score: 0.95,
  release: entry.release,
  alreadyOwnedCount: entry.owned.length,
  ownedAt: entry.owned.map((o) => o.locationPath),
  alternatives: null,
  needsArt: false,
});

function emit(sessionId: string, candidate: CandidateDto) {
  const text = `event: candidate.updated\nid: ${candidate.changeSeq}\ndata: ${JSON.stringify(candidate)}\n\n`;
  for (const stream of scanMock.streams) {
    if (stream.sessionId !== sessionId) continue;
    try {
      stream.controller.enqueue(encoder.encode(text));
    } catch {
      scanMock.streams.delete(stream);
    }
  }
}

const alternative = (r: ReleaseDto, score: number, artworkPath: string) => ({
  releaseId: r.id,
  title: r.title,
  format: r.format,
  platform: r.platform?.name ?? null,
  year: r.year,
  score,
  creators: [],
  coverUrl: null,
  alreadyOwnedCount: 0,
  artworkUrl: artworkPath,
});

function candidateFor(seq: number, item: IdentifyItem): CandidateDto {
  const base: CandidateDto = {
    id: newMockId(),
    seq,
    changeSeq: seq,
    source: 'picture_apple_model',
    rawCode: null,
    symbology: null,
    visibleText: item.text ?? item.title ?? null,
    localId: item.localId ?? null,
    box: null,
    boxSource: null,
    resolution: 'not_found',
    score: null,
    artDistance: null,
    needsArt: false,
    release: null,
    inferred: null,
    alternatives: null,
    decision: 'undecided',
    copies: 1,
    alreadyOwnedCount: 0,
    ownedAt: [],
    createdItemId: null,
    edits: null,
    userEdited: false,
    pendingReason: null,
    retryAfter: null,
  };
  const title = item.title ?? item.text ?? '';
  if (key(title) === key(CLOSE_CALL.title)) {
    return {
      ...base,
      resolution: 'ambiguous',
      score: 0.7,
      needsArt: true,
      alternatives: [
        alternative(CLOSE_CALL.original, 0.7, '/media/covers/aaaaaaaa-0000-4000-8000-000000000001'),
        alternative(CLOSE_CALL.rerelease, 0.68, '/media/covers/aaaaaaaa-0000-4000-8000-000000000002'),
      ],
    };
  }
  const entry = lookup(title);
  if (!entry) return base;
  if (entry.slow) return { ...base, resolution: 'pending' };
  return { ...base, ...resolved(entry) };
}

export const handlers = [
  http.post('*/api/v1/scan-sessions/:id/identify', async ({ params, request }) => {
    const session = scanMock.sessions.get(String(params.id));
    if (!session) return problem(404, 'Not found');
    const body = (await request.json()) as IdentifyRequest;
    identifyMock.requests.push({ sessionId: session.id, body });
    if (identifyMock.hold) await identifyMock.hold;
    if (identifyMock.failWith) return problem(identifyMock.failWith, 'Server problem');
    if (!body.snapId || !body.trigger || !body.engine) return problem(400, 'Send a snapId, a trigger and an engine.');
    if (!body.items || body.items.length === 0) return problem(400, 'Send at least one item.');
    const firstSession = identifyMock.answerSessions.get(body.snapId);
    if (firstSession && firstSession !== session.id) return problem(409, 'That snapId was already used. Send a new snapId for a new picture.', 'scan.snap_in_use');
    const replay = identifyMock.answers.get(body.snapId);
    let candidates = replay;
    if (!candidates) {
      candidates = body.items.map((item) => candidateFor(++session.seq, item));
      session.candidates.push(...candidates);
      identifyMock.answers.set(body.snapId, candidates);
      identifyMock.answerSessions.set(body.snapId, session.id);
    }
    if (identifyMock.dropResponses > 0) {
      identifyMock.dropResponses -= 1;
      return HttpResponse.error();
    }
    return HttpResponse.json({ candidates });
  }),

  http.post(/\/api\/v1\/scan-sessions\/([^/]+)\/candidates\/([^/]+):art$/, async ({ request }) => {
    const match = /\/scan-sessions\/([^/]+)\/candidates\/([^/:]+):art$/.exec(new URL(request.url).pathname);
    const session = scanMock.sessions.get(match?.[1] ?? '');
    const candidate = session?.candidates.find((c) => c.id === match?.[2]);
    if (!session || !candidate) return problem(404, 'Not found');
    const body = (await request.json()) as { distances?: { releaseId: string; distance: number }[] };
    const distances = body.distances ?? [];
    identifyMock.artRequests.push({ sessionId: session.id, candidateId: candidate.id, distances });
    if (candidate.decision !== 'undecided' || !candidate.needsArt) return problem(409, 'This item has been decided already.');
    const best = [...distances].sort((a, b) => a.distance - b.distance)[0];
    const pick = [CLOSE_CALL.original, CLOSE_CALL.rerelease].find((r) => r.id === best?.releaseId);
    candidate.needsArt = false;
    candidate.artDistance = best?.distance ?? null;
    if (pick && best && best.distance < 0.4) Object.assign(candidate, { resolution: 'matched', score: 0.95, release: pick, alternatives: null });
    candidate.changeSeq = ++session.seq;
    emit(session.id, candidate);
    return HttpResponse.json(candidate);
  }),

  http.get('*/media/covers/:assetId', ({ params, request }) => {
    identifyMock.coverRequests.push({ path: `/media/covers/${String(params.assetId)}`, authorization: request.headers.get('Authorization') });
    if (identifyMock.coversMissing) return problem(404, 'Not found');
    return new HttpResponse(new Uint8Array([0xff, 0xd8, 0xff, 0xd9]), { headers: { 'Content-Type': 'image/jpeg' } });
  }),

  ...barcodeHandlers,
];
