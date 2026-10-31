// Mock of the eval routes (EVAL-1a, owner only, 404 unless the PC turned evals on). Like the real API: a family member gets 403, a PC with
// evals off gets 404. The run is a small fake PC: it opens a run, stores each photo the phone sends, and scores the run after a few polls once it
// is finished. `sent` keeps every photo it took, `attempts` every photo it was asked to take, so tests can assert exactly what left the phone.
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

/** The run the fake PC holds. */
export interface MockRun {
  runId: string;
  state: 'open' | 'finishing' | 'done';
  partial: boolean;
  photos: { id: string; set: string }[];
  /** What it stored for each photo it took. */
  stored: Record<string, unknown[]>;
  refused: { photoId: string; reason: string }[];
  device: unknown;
  configVersion: string;
  baselineNote?: string;
  /** How many times the run was asked about since it was finished. */
  pollsSinceFinish: number;
  /** Why the PC could not score the run yet (a problem code), while it keeps trying. */
  failure?: string | null;
}

/** What a send can be made to do instead of working: an HTTP status (with the problem code the PC gives it, if any), or 'network' for no answer at all. */
export type MockSendFailure = number | 'network' | { status: number; code: string };

export const evalsMock = {
  /** 'enabled' is the owner on a PC with evals on; 'off' is a PC with evals off; 'forbidden' is anyone who is not the owner. */
  access: 'off' as 'enabled' | 'off' | 'forbidden',
  photos: [] as MockGoldenPhoto[],
  /** The run the fake PC holds. */
  run: undefined as MockRun | undefined,
  /** Every body POST /runs/open received. */
  opened: [] as unknown[],
  /** Every photo the PC took, in order: its id and the readings sent. */
  sent: [] as { photoId: string; results: unknown[] }[],
  /** The id of every photo the phone tried to send, in order, the ones that failed included. */
  attempts: [] as string[],
  /** The next sends fail like this, one entry each, before the PC takes anything. */
  failSends: [] as MockSendFailure[],
  /** Photos the PC refuses for good, with this problem code. */
  refuse: {} as Record<string, string>,
  /** Every body POST /runs/open/finish received. */
  finishes: [] as { partial?: boolean }[],
  /** How many photos the PC held each time it was asked to finish. */
  sentAtFinish: [] as number[],
  /** How many times the run was thrown away. */
  discards: 0,
  /** How many times the run was asked about. */
  asked: 0,
  /** How many times the run is asked about after it is finished before the PC has scored it. */
  scoredAfterPolls: 1,
  /** Set to make the next finish fail with this status. */
  failNextFinish: null as number | null,
  /** Every golden photo upload the server received, in order. */
  uploads: [] as MockUpload[],
  /** Per photo id: make that photo's next upload fail with this status (once). 409 is "name taken", 413 "too large", 415 "not a JPEG or HEIC". */
  failUpload: {} as Record<string, number>,
  reset() {
    this.access = 'off';
    this.photos = [];
    this.run = undefined;
    this.opened = [];
    this.sent = [];
    this.attempts = [];
    this.failSends = [];
    this.refuse = {};
    this.finishes = [];
    this.sentAtFinish = [];
    this.discards = 0;
    this.asked = 0;
    this.scoredAfterPolls = 1;
    this.failNextFinish = null;
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

/** Puts a run on the fake PC as if the phone had opened it earlier, with these photos already stored. Returns it. */
export function holdRun(stored: string[] = [], state: MockRun['state'] = 'open'): MockRun {
  const run: MockRun = {
    runId: 'run-held',
    state,
    partial: false,
    photos: [...evalsMock.photos].sort(byOrder).map((p) => ({ id: p.id, set: p.set })),
    stored: Object.fromEntries(stored.map((id) => [id, [{ photoId: id }]])),
    refused: [],
    device: { model: 'iPhone', osVersion: '26.0', appVersion: '1.0.0' },
    configVersion: 'v1',
    pollsSinceFinish: 0,
  };
  evalsMock.run = run;
  return run;
}

const byOrder = (a: MockGoldenPhoto, b: MockGoldenPhoto) => (a.set === b.set ? a.id.localeCompare(b.id) : a.set.localeCompare(b.set));

const summary = (runId: string, partial: boolean) => ({
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
    verdict: partial ? 'incomplete' : 'pass',
    measures: [{ name: 'shelf_recall', bar: '>= 90%', actual: '93%', status: 'pass' }],
    comparison: {},
  },
  regression: { status: partial ? 'partial_run' : 'ok', compared: 0, regressions: [] },
  metrics: {},
  partial,
});

const statusOf = (run: MockRun) => {
  const received = run.photos.filter((p) => p.id in run.stored).map((p) => p.id);
  return {
    runId: run.runId,
    state: run.state,
    partial: run.partial,
    openedAt: '2026-01-01T00:00:00Z',
    configVersion: run.configVersion,
    device: run.device,
    baselineNote: run.baselineNote ?? null,
    photos: run.photos,
    received,
    grounded: run.state === 'open' ? 0 : received.length,
    refused: run.refused,
    failure: run.failure ?? null,
    summary: run.state === 'done' ? summary(run.runId, run.partial) : null,
  };
};

const deny = () => (evalsMock.access === 'enabled' ? undefined : problem(evalsMock.access === 'forbidden' ? 403 : 404, 'Not found'));

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

  // The resumable run. Like the real routes: the run is opened on the PC, each photo is stored at once, finishing returns at once and the PC scores later.
  http.get('*/api/admin/evals/runs/open', () => {
    const denied = deny();
    if (denied) return denied;
    evalsMock.asked += 1;
    const run = evalsMock.run;
    if (!run) return problem(404, 'Not found', 'evals.no_open_run');
    if (run.state === 'finishing') {
      run.pollsSinceFinish += 1;
      if (run.pollsSinceFinish >= evalsMock.scoredAfterPolls) run.state = 'done';
    }
    return HttpResponse.json(statusOf(run));
  }),
  http.post('*/api/admin/evals/runs/open', async ({ request }) => {
    const denied = deny();
    if (denied) return denied;
    if (evalsMock.run && evalsMock.run.state !== 'done') return problem(409, 'Conflict', 'evals.run_open');
    const body = (await request.json()) as { device?: unknown; configVersion?: string; baselineNote?: string };
    evalsMock.opened.push(body);
    if (evalsMock.photos.length === 0) return problem(400, 'Bad request', 'evals.no_photos');
    const run = holdRun();
    run.runId = `run-${evalsMock.opened.length}`;
    run.device = body.device;
    run.configVersion = body.configVersion ?? '';
    run.baselineNote = body.baselineNote;
    return HttpResponse.json(statusOf(run), { status: 201 });
  }),
  http.delete('*/api/admin/evals/runs/open', () => {
    const denied = deny();
    if (denied) return denied;
    if (!evalsMock.run) return problem(404, 'Not found', 'evals.no_open_run');
    evalsMock.run = undefined;
    evalsMock.discards += 1;
    return new HttpResponse(null, { status: 204 });
  }),
  http.put('*/api/admin/evals/runs/open/photos/:photoId', async ({ params, request }) => {
    const denied = deny();
    if (denied) return denied;
    const photoId = String(params.photoId);
    evalsMock.attempts.push(photoId);
    const failure = evalsMock.failSends.shift();
    if (failure === 'network') return HttpResponse.error();
    if (typeof failure === 'object') return problem(failure.status, 'The photo could not be saved.', failure.code);
    if (failure) return problem(failure, 'The photo could not be saved.');
    const run = evalsMock.run;
    if (!run) return problem(404, 'Not found', 'evals.no_open_run');
    if (run.state !== 'open') return problem(409, 'Conflict', 'evals.run_not_open');
    if (!run.photos.some((p) => p.id === photoId)) return problem(400, 'Bad request', 'evals.photo_not_in_run');
    const body = (await request.json()) as { results?: unknown[] };
    if (evalsMock.refuse[photoId]) {
      run.refused.push({ photoId, reason: evalsMock.refuse[photoId] });
      return problem(400, 'Bad request', evalsMock.refuse[photoId]);
    }
    run.stored[photoId] = body.results ?? [];
    evalsMock.sent.push({ photoId, results: body.results ?? [] });
    return HttpResponse.json({ photoId, received: Object.keys(run.stored).length, total: run.photos.length });
  }),
  http.post('*/api/admin/evals/runs/open/finish', async ({ request }) => {
    const denied = deny();
    if (denied) return denied;
    const text = await request.text();
    const body = (text ? JSON.parse(text) : {}) as { partial?: boolean };
    evalsMock.finishes.push(body);
    evalsMock.sentAtFinish.push(evalsMock.sent.length);
    if (evalsMock.failNextFinish) {
      const status = evalsMock.failNextFinish;
      evalsMock.failNextFinish = null;
      return problem(status, 'The run could not be finished.');
    }
    const run = evalsMock.run;
    if (!run) return problem(404, 'Not found', 'evals.no_open_run');
    if (run.state === 'open') {
      if (Object.keys(run.stored).length === 0) return problem(400, 'Bad request', 'evals.results_required');
      run.state = 'finishing';
      run.pollsSinceFinish = 0;
      run.partial = body.partial === true || Object.keys(run.stored).length + run.refused.length < run.photos.length;
    }
    return HttpResponse.json(statusOf(run), { status: 202 });
  }),
];
