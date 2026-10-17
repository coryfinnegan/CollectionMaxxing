// Mock of the eval routes (EVAL-1a, owner only, 404 unless the PC turned evals on). Like the real API: a family member gets 403, a PC with
// evals off gets 404. `runs` keeps every body the server received so tests can assert exactly what left the phone.
import { HttpResponse, http } from 'msw';

const problem = (status: number, title: string) =>
  HttpResponse.json({ title, status }, { status, headers: { 'Content-Type': 'application/problem+json' } });

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
  reset() {
    this.access = 'off';
    this.photos = [];
    this.runs = [];
    this.failNextSubmit = null;
  },
};

export const goldenPhotos = (count: number): MockGoldenPhoto[] =>
  Array.from({ length: count }, (_, i) => ({
    id: `photo-${String(i + 1).padStart(3, '0')}`,
    set: i % 2 ? 'shelf' : 'single',
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
