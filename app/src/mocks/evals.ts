// Mock of the eval routes (EVAL-1a, owner only, 404 unless the PC turned evals on). Like the real API: a family member gets 403, a PC with
// evals off gets 404. `runs` keeps every body the server received so tests can assert exactly what left the phone.
import { HttpResponse, http } from 'msw';

const problem = (status: number, title: string, code?: string) =>
  HttpResponse.json({ title, status, ...(code ? { code } : {}) }, { status, headers: { 'Content-Type': 'application/problem+json' } });

/** The most one golden photo may weigh on the real server (EvalsOptions.MaxGoldenPhotoBytes). */
export const MAX_GOLDEN_PHOTO_BYTES = 40 * 1024 * 1024;

/** One photo the server received on POST /golden/{set}/{id}. */
export interface MockUpload {
  set: string;
  id: string;
  contentType: string;
  size: number;
}

export interface MockGoldenPhoto {
  id: string;
  set: string;
  hasTruth: boolean;
  bytes: number;
}

export const evalsMock = {
  /** 'enabled' is the owner on a PC with evals on; 'off' is a PC with evals off; 'forbidden' is anyone who is not the owner. */
  access: 'off' as 'enabled' | 'off' | 'forbidden',
  photos: [] as MockGoldenPhoto[],
  /** Every run body the server received. */
  runs: [] as unknown[],
  /** Set to make the next submit fail with this status. */
  failNextSubmit: null as number | null,
  /** Every golden photo upload the server received, in order. */
  uploads: [] as MockUpload[],
  /** Per photo id: make that photo's next upload fail with this status (once). 409 is "name taken", 413 "too large", 415 "not a JPEG or HEIC". */
  failUpload: {} as Record<string, number>,
  reset() {
    this.access = 'off';
    this.photos = [];
    this.runs = [];
    this.failNextSubmit = null;
    this.uploads = [];
    this.failUpload = {};
  },
};

export const goldenPhotos = (count: number): MockGoldenPhoto[] =>
  Array.from({ length: count }, (_, i) => ({
    id: `photo-${String(i + 1).padStart(3, '0')}`,
    set: i < count / 2 ? 'shelf' : 'single',
    hasTruth: true,
    bytes: 100_000 + i,
  }));

const summary = (runId: string) => ({
  runId,
  date: '2026-01-01T00:00:00Z',
  buildSha: 'dev',
  configVersion: 'built-in',
  device: { model: 'iPhone', osVersion: '26.0', appVersion: '1.0.0' },
  scoredPhotos: evalsMock.photos.length,
  skipped: [],
  engines: {},
  gate: {
    engine: 'apple_model',
    verdict: 'pass',
    measures: [{ name: 'shelf_recall', bar: '>= 90%', actual: '93%', status: 'pass' }],
    comparison: {},
  },
  regression: { status: 'ok', compared: 0, regressions: [] },
  metrics: {},
});

export const handlers = [
  http.get('*/api/admin/evals/golden', () => {
    if (evalsMock.access === 'forbidden') return problem(403, 'Forbidden');
    if (evalsMock.access === 'off') return problem(404, 'Not found');
    return HttpResponse.json(evalsMock.photos);
  }),
  // Like the real route: 404 off, 403 not the owner, 409 when the id is used in either set (letter case ignored), 413 over 40 MB, then 201.
  http.post('*/api/admin/evals/golden/:set/:id', async ({ params, request }) => {
    if (evalsMock.access !== 'enabled') return problem(evalsMock.access === 'forbidden' ? 403 : 404, 'Not found');
    const set = String(params.set);
    const id = String(params.id);
    const size = (await request.arrayBuffer()).byteLength;
    evalsMock.uploads.push({ set, id, contentType: request.headers.get('content-type') ?? '', size });
    const forced = evalsMock.failUpload[id];
    if (forced) {
      delete evalsMock.failUpload[id];
      if (forced === 409) return problem(409, 'Conflict', 'evals.id_taken');
      if (forced === 413) return problem(413, 'Payload too large', 'evals.photo_too_large');
      if (forced === 415) return problem(415, 'Unsupported media type', 'evals.unsupported_picture');
      return problem(forced, 'The photo could not be saved.');
    }
    if (set !== 'single' && set !== 'shelf') return problem(400, 'Bad request', 'evals.set_invalid');
    if (evalsMock.photos.some((p) => p.id.toLowerCase() === id.toLowerCase())) return problem(409, 'Conflict', 'evals.id_taken');
    if (size > MAX_GOLDEN_PHOTO_BYTES) return problem(413, 'Payload too large', 'evals.photo_too_large');
    const photo: MockGoldenPhoto = { id, set, hasTruth: false, bytes: size };
    evalsMock.photos.push(photo);
    return HttpResponse.json(photo, { status: 201 });
  }),
  http.post('*/api/admin/evals/runs', async ({ request }) => {
    if (evalsMock.access !== 'enabled') return problem(evalsMock.access === 'forbidden' ? 403 : 404, 'Not found');
    if (evalsMock.failNextSubmit) {
      const status = evalsMock.failNextSubmit;
      evalsMock.failNextSubmit = null;
      return problem(status, 'The eval run could not be saved.');
    }
    evalsMock.runs.push(await request.json());
    const runId = `run-${evalsMock.runs.length}`;
    return HttpResponse.json({ runId, summary: summary(runId) });
  }),
];
