// Mock of POST /scan-sessions/{id}/snaps/{snapId}:read-shelf (#809, ADR-033): the shelf photo for the server's own reader. Like the real
// API: multipart with one JPEG part named `file` (4 MB at most), 404 for a snap `identify` has not answered for, the same snap sent twice
// is answered with the same rows (read once), and the answer is the rows the server added followed by the phone's rows it skipped.
// The handler records what arrived; tests script the rows and the refusals.
import { HttpResponse, http } from 'msw';
import type { CandidateDto } from '@/api/generated/models';
import { identifyMock } from './identify';
import { scanMock } from './scan';

const MAX_BYTES = 4 * 1024 * 1024;

/** A refusal to answer next: its status, the problem code, and a Retry-After in seconds. */
export interface ShelfRefusal {
  status: number;
  code?: string;
  retryAfter?: number;
}

export interface ShelfRequest {
  sessionId: string;
  snapId: string;
  contentType: string;
  /** Bytes of the file part. */
  size: number;
  /** The names of every part in the form: only `file` is expected. */
  parts: string[];
}

export const shelfMock = {
  /** Every request the server received. */
  requests: [] as ShelfRequest[],
  /** The rows the next read answers with (added rows first, then skipped phone rows). */
  rows: [] as CandidateDto[],
  /** Refusals, used one per request in order, before any read happens. */
  refusals: [] as ShelfRefusal[],
  /** Holds every answer until this settles. */
  hold: null as Promise<void> | null,
  /** Answers already given, by snap: the same snap is read once. */
  answers: new Map<string, CandidateDto[]>(),
  reset() {
    this.requests = [];
    this.rows = [];
    this.refusals = [];
    this.hold = null;
    this.answers = new Map();
  },
};

const problem = (status: number, title: string, code?: string, retryAfter?: number) =>
  HttpResponse.json(
    { title, status, ...(code ? { code } : {}) },
    {
      status,
      headers: { 'Content-Type': 'application/problem+json', ...(retryAfter === undefined ? {} : { 'Retry-After': String(retryAfter) }) },
    },
  );

type UploadedFile = { type: string; size: number };

export const handlers = [
  http.post(/\/api\/v1\/scan-sessions\/([^/]+)\/snaps\/([^/]+):read-shelf$/, async ({ request }) => {
    const match = /\/scan-sessions\/([^/]+)\/snaps\/([^/:]+):read-shelf$/.exec(new URL(request.url).pathname);
    const sessionId = match?.[1] ?? '';
    const snapId = match?.[2] ?? '';
    const form = await request.formData().catch(() => null);
    // The React Native types do not describe the form; the mock server sees a web FormData.
    const names = form ? [...(form as unknown as { keys(): Iterable<string> }).keys()] : [];
    const file = (form?.getAll('file') as unknown[] | undefined)?.find((v): v is UploadedFile => typeof v !== 'string');
    shelfMock.requests.push({
      sessionId,
      snapId,
      contentType: file?.type ?? '',
      size: file?.size ?? 0,
      parts: names,
    });
    if (shelfMock.hold) await shelfMock.hold;
    const refusal = shelfMock.refusals.shift();
    if (refusal) return problem(refusal.status, 'Refused.', refusal.code, refusal.retryAfter);
    const session = scanMock.sessions.get(sessionId);
    if (!session) return problem(404, 'Not found');
    if (identifyMock.answerSessions.get(snapId) !== sessionId) return problem(404, 'Not found'); // identify has not answered for it
    if (!file) return problem(400, 'Send the picture as a multipart part named "file".');
    if (file.type !== 'image/jpeg') return problem(415, 'Only JPEG pictures are accepted.');
    if (file.size > MAX_BYTES) return problem(413, 'That picture is too large.');
    const answered = shelfMock.answers.get(snapId);
    if (answered) return HttpResponse.json({ candidates: answered });
    const candidates = shelfMock.rows;
    for (const row of candidates) {
      if (!session.candidates.some((c) => c.id === row.id)) session.candidates.push(row);
    }
    shelfMock.answers.set(snapId, candidates);
    return HttpResponse.json({ candidates });
  }),
];
