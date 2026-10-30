import { HttpResponse, http } from 'msw';
import type { EvalRunRequest } from '@/api/generated/models';
import { promptOf } from '@/features/scan/recognition/defaults';
import { authHeaders, configureAuth, resetAuth } from '@/lib/auth/session';
import { MemoryNativeAuth } from '@/lib/auth/native';
import { evalsMock } from '@/mocks/evals';
import { server } from '@/mocks/server';
import { setPictureFiles, trackedPictures } from '@/features/scan/pictureFiles';
import { failedRead, makeKit, uriOf } from './evalsTestKit';
import { modelItem, okRead } from '@/features/scan/recognition/fakes';

interface Result {
  photoId: string;
  engine: string;
  outcome: string;
  fellBackFrom?: string;
  items: { title: string; localId: string; resolvedBy?: string; tier?: string; provisional?: boolean; isGuess?: boolean; segmentId?: string }[];
  unreadableCount: number;
  latencyMs: number;
  firstItemMs?: number;
  thermalState: string;
  capturedCount?: number;
  capturedAt?: string;
  duplicateRescan?: { ownedHits: number; answeredOnCard: number; unsolicitedAdds: number };
}
interface Body {
  device: { model: string; osVersion: string; appVersion: string };
  configVersion: string;
  partial?: EvalRunRequest['partial'];
  baselineNote?: string;
  results: Result[];
}
const runs = () => evalsMock.runs as Body[];

beforeEach(() => {
  evalsMock.reset();
  configureAuth({ platform: 'native', native: new MemoryNativeAuth() });
});
afterEach(() => {
  resetAuth();
  setPictureFiles(undefined);
});

describe('the eval runner', () => {
  it('reads each photo with all three engines, in order, and sends one run with every declared field', async () => {
    const kit = makeKit({ photos: 4 });
    await kit.runner.start();

    expect(runs()).toHaveLength(1);
    const { results, device, configVersion } = runs()[0];
    expect(results).toHaveLength(12);
    expect(results.map((r) => `${r.photoId}:${r.engine}`)).toEqual(
      kit.ids.flatMap((id) => [`${id}:apple_model`, `${id}:fm_text`, `${id}:vision_pipeline`]),
    );
    for (const r of results) {
      expect(r.outcome).toBe('ok');
      expect(r.items.length).toBeGreaterThan(0);
      expect(r.unreadableCount).toEqual(expect.any(Number));
      expect(r.latencyMs).toEqual(expect.any(Number));
      expect(r.firstItemMs).toEqual(expect.any(Number));
      expect(r.thermalState).toBe('nominal');
    }
    expect(device).toEqual({ model: 'iPhone16,1', osVersion: '26.0', appVersion: '1.2.3' });
    expect(configVersion).toBe('v1');
    expect(kit.runner.getState().phase).toBe('done');
    expect(kit.runner.getState().summary?.gate.verdict).toBe('pass');
  });

  it('records why the model fell back, and does not count the fallback reading as the model\'s', async () => {
    const kit = makeKit({ photos: 2, scenes: { 'photo-001': { read: failedRead('declined') }, 'photo-002': { read: failedRead('contextExceeded') } } });
    await kit.runner.start();
    const [first, middle, , second] = runs()[0].results;
    expect(first).toMatchObject({ engine: 'apple_model', outcome: 'refused', fellBackFrom: 'declined', items: [] });
    expect(second).toMatchObject({ engine: 'apple_model', outcome: 'error', fellBackFrom: 'context', items: [] });
    expect(middle).toMatchObject({ engine: 'fm_text', outcome: 'ok' });
    expect(runs()[0].results[2]).toMatchObject({ engine: 'vision_pipeline', outcome: 'ok' });
  });

  it('sends a model timeout as outcome timeout with fellBackFrom timeout, not as an error or an outage', async () => {
    const kit = makeKit({ photos: 1, scenes: { 'photo-001': { read: failedRead('timeout') } } });
    await kit.runner.start();
    const [model, , vision] = runs()[0].results;
    expect(model).toMatchObject({ engine: 'apple_model', outcome: 'timeout', fellBackFrom: 'timeout', items: [] });
    expect(model.latencyMs).toBeGreaterThanOrEqual(0);
    expect(vision).toMatchObject({ engine: 'vision_pipeline', outcome: 'ok' });
  });

  it('sends an extraction refusal and timeout as their own outcomes, not as errors', async () => {
    const kit = makeKit({ photos: 2, scenes: { 'photo-001': { extract: failedRead('declined') }, 'photo-002': { extract: failedRead('timeout') } } });
    await kit.runner.start();
    const fm = runs()[0].results.filter((r) => r.engine === 'fm_text');
    expect(fm).toHaveLength(2);
    expect(fm[0]).toMatchObject({ photoId: 'photo-001', outcome: 'refused', fellBackFrom: 'declined', items: [] });
    expect(fm[1]).toMatchObject({ photoId: 'photo-002', outcome: 'timeout', fellBackFrom: 'timeout', items: [] });
  });

  it('uploads the model\'s own reading as its row, not the scanner\'s model-plus-Vision combination', async () => {
    // Vision finds a third spine the model missed. The scanner would add it to the model's items; the eval keeps the two engines apart
    // and the PC builds the combination, so the model's row must hold the model's items only.
    const third = { text: [
      { text: 'Title of photo-001', confidence: 0.9, box: { x: 0.1, y: 0.1, width: 0.05, height: 0.6 } },
      { text: 'Other of photo-001', confidence: 0.9, box: { x: 0.4, y: 0.1, width: 0.05, height: 0.6 } },
      { text: 'Third of photo-001', confidence: 0.9, box: { x: 0.7, y: 0.1, width: 0.05, height: 0.6 } },
    ], rectangles: [
      { x: 0.08, y: 0.05, width: 0.09, height: 0.7 },
      { x: 0.38, y: 0.05, width: 0.09, height: 0.7 },
      { x: 0.68, y: 0.05, width: 0.09, height: 0.7 },
    ] };
    const kit = makeKit({ photos: 1, scenes: { 'photo-001': third } });
    await kit.runner.start();
    const rows = runs()[0].results;
    expect(rows.find((r) => r.engine === 'apple_model')?.items.map((i) => i.title)).toEqual(['Title of photo-001', 'Other of photo-001']);
    expect(rows.find((r) => r.engine === 'vision_pipeline')?.items.map((i) => i.title)).toEqual(['Title of photo-001', 'Other of photo-001', 'Third of photo-001']);
    for (const item of rows.find((r) => r.engine === 'apple_model')?.items ?? []) expect(item.resolvedBy).toBe('model');
  });

  it('marks extraction rows as settled by the model', async () => {
    const kit = makeKit({ photos: 1 });
    await kit.runner.start();
    const fm = runs()[0].results.find((r) => r.engine === 'fm_text');
    expect(fm?.items.length).toBeGreaterThan(0);
    for (const item of fm?.items ?? []) expect(item.resolvedBy).toBe('model');
  });

  it('keeps the screen awake for the run and lets go at the end', async () => {
    const kit = makeKit({ photos: 2 });
    await kit.runner.start();
    expect(kit.awake).toEqual({ on: 1, off: 1 });
  });

  it('records a serious thermal state, tells the owner to keep the phone cool and keeps going', async () => {
    const kit = makeKit({ photos: 2 });
    jest.spyOn(kit.vision, 'deviceStatus').mockResolvedValue({ thermalState: 'serious', osVersion: '26.0' });
    await kit.runner.start();
    expect(kit.runner.getState().keepCool).toBe(true);
    expect(runs()[0].results).toHaveLength(6);
    expect(runs()[0].results.every((r) => r.thermalState === 'serious')).toBe(true);
  });

  it('cancel uploads what it has, flagged partial, and resume continues after the last photo', async () => {
    let cancelled = false;
    const kit = makeKit({
      photos: 4,
      onRead: () => {
        if (!cancelled && kit.downloaded.length === 2) {
          cancelled = true;
          kit.runner.cancel();
        }
      },
    });
    await kit.runner.start();

    expect(runs()).toHaveLength(1);
    expect(runs()[0].partial).toBe(true);
    expect(runs()[0].results.map((r) => r.photoId)).toEqual(['photo-001', 'photo-001', 'photo-001']);
    expect(kit.runner.getState()).toMatchObject({ phase: 'done', partial: true });

    await kit.runner.resume();

    expect(runs()).toHaveLength(2);
    expect(runs()[1].partial).toBeFalsy();
    expect(runs()[1].results).toHaveLength(12);
    expect(kit.downloaded).toEqual(['photo-001', 'photo-002', 'photo-002', 'photo-003', 'photo-004']);
  });

  it('pauses between steps and continues when resumed', async () => {
    let paused = false;
    const kit = makeKit({
      photos: 3,
      onRead: () => {
        if (!paused && kit.downloaded.length === 2) {
          paused = true;
          kit.runner.pause();
        }
      },
    });
    const finished = kit.runner.start();
    await waitFor(() => kit.runner.getState().phase === 'paused');
    expect(evalsMock.runs).toHaveLength(0);
    void kit.runner.resume();
    await finished;
    expect(runs()).toHaveLength(1);
    expect(runs()[0].results).toHaveLength(9);
    expect(kit.downloaded).toEqual(['photo-001', 'photo-002', 'photo-003']);
  });

  it('keeps the results when the upload fails and sends them when the owner retries', async () => {
    const kit = makeKit({ photos: 2 });
    evalsMock.failNextSubmit = 500;
    await kit.runner.start();
    expect(kit.runner.getState().phase).toBe('upload_failed');
    expect(kit.runner.getState().resultCount).toBe(6);
    expect(evalsMock.runs).toHaveLength(0);

    await kit.runner.retryUpload();
    expect(kit.runner.getState().phase).toBe('done');
    expect(runs()).toHaveLength(1);
    expect(runs()[0].results).toHaveLength(6);
  });

  it('runs only one run at a time', async () => {
    const kit = makeKit({ photos: 2 });
    const first = kit.runner.start();
    const second = kit.runner.start();
    await Promise.all([first, second]);
    expect(runs()).toHaveLength(1);
    expect(kit.vision.maxConcurrentReads).toBe(1);
  });

  it('gives the engines the prompt and limits from the config store, and a newer version to the next run', async () => {
    const kit = makeKit({ photos: 1, versions: ['v1', 'v2'] });
    await kit.runner.start();
    const first = kit.vision.readCalls[0].options;
    expect(first.prompt).toBe(promptOf(kit.configFor('v1')));
    expect(first.timeoutMs).toBe(1234);
    expect(first.maxItems).toBe(17);
    expect(runs()[0].configVersion).toBe('v1');

    await kit.runner.start();
    const next = kit.vision.readCalls[1].options;
    expect(next.prompt).toBe(promptOf(kit.configFor('v2')));
    expect(next.timeoutMs).toBe(1235);
    expect(runs()[1].configVersion).toBe('v2');
  });

  it('deletes each downloaded photo right after it is read', async () => {
    const kit = makeKit({ photos: 3 });
    const seen: string[][] = [];
    const afterEach = kit.runner.subscribe(() => seen.push([...kit.deleted]));
    await kit.runner.start();
    afterEach();
    expect(kit.deleted).toEqual(kit.ids.map(uriOf));
    expect(trackedPictures()).toEqual([]);
    // each file was gone before the next one was downloaded
    expect(seen.some((deleted) => deleted.length === 1)).toBe(true);
  });

  it('deletes the photo even when reading it throws, and continues with the next one', async () => {
    const kit = makeKit({ photos: 2, selectorThrows: true });
    await kit.runner.start();
    expect(kit.deleted).toEqual(kit.ids.map(uriOf));
    expect(trackedPictures()).toEqual([]);
    expect(kit.runner.getState().phase).toBe('done');
    expect(runs()[0].results.filter((r) => r.engine === 'apple_model').every((r) => r.outcome === 'error')).toBe(true);
  });

  it('skips a photo it cannot download, says so, and sends the rest', async () => {
    const kit = makeKit({ photos: 3, failDownload: ['photo-002'] });
    await kit.runner.start();
    expect(runs()[0].results.map((r) => r.photoId)).toEqual(['photo-001', 'photo-001', 'photo-001', 'photo-003', 'photo-003', 'photo-003']);
    expect(kit.runner.getState().skipped).toEqual(['photo-002']);
  });

  it('claims T1 for high-confidence reads and keeps the rest as guesses for a human', async () => {
    const kit = makeKit({
      photos: 2,
      scenes: {
        'photo-001': {
          read: okRead([
            modelItem('Sure thing', 'Sure thing', 0, { confidence: 'high' }),
            modelItem('Maybe thing', 'Maybe thing', 1, { confidence: 'medium' }),
            modelItem('Wild guess', 'Wild guess', 2, { confidence: 'low' }),
          ]),
        },
      },
    });
    await kit.runner.start();
    const model = runs()[0].results.filter((r) => r.engine === 'apple_model');
    const first = model.find((r) => r.photoId === 'photo-001');
    expect(first).toBeDefined();
    expect(first?.items).toMatchObject([
      { title: 'Sure thing', tier: 'T1', provisional: false },
      { title: 'Maybe thing', provisional: true, isGuess: true },
      { title: 'Wild guess', provisional: true, isGuess: true },
    ]);
    expect(first?.items[0]).not.toHaveProperty('isGuess');
    expect(first?.items[1]).not.toHaveProperty('tier');
    expect(first?.items[2]).not.toHaveProperty('tier');
    // the Vision text engine only guesses, so every one of its rows is kept, never auto-added
    for (const row of runs()[0].results.filter((r) => r.engine === 'vision_pipeline')) {
      expect(row.items.length).toBeGreaterThan(0);
      for (const item of row.items) {
        expect(item).toMatchObject({ provisional: true, isGuess: true });
        expect(item).not.toHaveProperty('tier');
      }
    }
    // the extraction tier is a model read, so its high-confidence rows claim T1 like the Apple's
    for (const row of runs()[0].results.filter((r) => r.engine === 'fm_text')) {
      expect(row.items.length).toBeGreaterThan(0);
      for (const item of row.items) {
        expect(item).toMatchObject({ tier: 'T1', provisional: false });
        expect(item).not.toHaveProperty('isGuess');
      }
    }
  });

  it('stamps one segment id on each read and reconciles capturedCount with it', async () => {
    const kit = makeKit({ photos: 3 });
    await kit.runner.start();
    const segments = new Set<string>();
    for (const row of runs()[0].results) {
      expect(row.items.length).toBeGreaterThan(0);
      const ids = new Set(row.items.map((i) => i.segmentId));
      expect(ids.size).toBe(1); // the read's items share its segment
      const [segment] = [...ids];
      expect(typeof segment).toBe('string');
      expect(segment?.length).toBeGreaterThan(0);
      if (segment) segments.add(segment);
      expect(row.capturedCount).toBe(row.unreadableCount + 1);
    }
    expect(segments.size).toBe(runs()[0].results.length); // each read mints its own
  });

  it('stamps capturedAt when each read starts', async () => {
    const kit = makeKit({ photos: 2 });
    const before = Date.now();
    await kit.runner.start();
    const after = Date.now();
    for (const row of runs()[0].results) {
      const stamped = Date.parse(row.capturedAt ?? '');
      expect(Number.isNaN(stamped)).toBe(false);
      expect(stamped).toBeGreaterThanOrEqual(before);
      expect(stamped).toBeLessThanOrEqual(after);
    }
  });

  it('sends the baseline note when the run carries one, and leaves it out otherwise', async () => {
    const kit = makeKit({ photos: 1 });
    await kit.runner.start({ baselineNote: 'T1 bar moved 0.90 to 0.92 for the acceptance re-run.' });
    expect(runs()[0].baselineNote).toBe('T1 bar moved 0.90 to 0.92 for the acceptance re-run.');

    const plain = makeKit({ photos: 1 });
    await plain.runner.start();
    expect(plain.runner.getState().phase).toBe('done');
    expect(runs()[1]).not.toHaveProperty('baselineNote');

    const blank = makeKit({ photos: 1 });
    await blank.runner.start({ baselineNote: '   ' });
    expect(runs()[2]).not.toHaveProperty('baselineNote');
  });

  it('keeps the baseline note when the upload is retried', async () => {
    const kit = makeKit({ photos: 1 });
    evalsMock.failNextSubmit = 500;
    await kit.runner.start({ baselineNote: 'Threshold move, re-run.' });
    expect(kit.runner.getState().phase).toBe('upload_failed');
    await kit.runner.retryUpload();
    expect(runs()).toHaveLength(1);
    expect(runs()[0].baselineNote).toBe('Threshold move, re-run.');
  });

  it('sends no duplicate evidence: the runner never touches the library', async () => {
    const kit = makeKit({ photos: 2 });
    await kit.runner.start();
    for (const row of runs()[0].results) {
      expect(row).not.toHaveProperty('duplicateRescan');
    }
  });

  describe('locked: what leaves the phone and what the runner may touch', () => {
    it('sends only text, fields, timings and ids: no bytes, data URI or file URI, and the ids are the PC\'s', async () => {
      const kit = makeKit({ photos: 4 });
      await kit.runner.start();
      const text = JSON.stringify(evalsMock.runs);
      expect(text).not.toMatch(/data:/i);
      expect(text).not.toMatch(/file:\/\//i);
      expect(text).not.toMatch(/\/cache\//i);
      expect(text).not.toMatch(/\.(jpe?g|heic|png)/i);
      expect(text).not.toMatch(/[A-Za-z0-9+/]{200,}/); // no base64 blob
      expect(runs()[0].results.map((r) => r.photoId).filter((id, i, all) => all.indexOf(id) === i)).toEqual(evalsMock.photos.map((p) => p.id));
      expect(Object.keys(runs()[0]).sort()).toEqual(['configVersion', 'device', 'results']);
    });

    it('creates no scan session, item or candidate and sends no request other than the eval routes and the config fetch', async () => {
      const kit = makeKit({ photos: 3 });
      server.use(http.get('*/api/v1/recognition/config', () => HttpResponse.json(kit.configFor('v1'))));
      await authHeaders('GET'); // the sign-in refresh is the auth layer's, not the runner's: do it before listening
      const requests: string[] = [];
      const listener = ({ request }: { request: Request }) => requests.push(`${request.method} ${new URL(request.url).pathname}`);
      server.events.on('request:start', listener);
      try {
        await kit.runner.start();
      } finally {
        server.events.removeListener('request:start', listener);
      }
      const allowed = /^(GET \/api\/admin\/evals\/golden(\/[A-Za-z0-9_-]+\/image)?|POST \/api\/admin\/evals\/runs|GET \/api\/v1\/recognition\/config)$/;
      expect(requests.length).toBeGreaterThan(0);
      expect(requests.filter((r) => !allowed.test(r))).toEqual([]);
      expect(requests.filter((r) => r.startsWith('POST'))).toEqual(['POST /api/admin/evals/runs']);
    });
  });
});

async function waitFor(condition: () => boolean) {
  for (let i = 0; i < 400 && !condition(); i += 1) await new Promise((resolve) => setTimeout(resolve, 5));
  expect(condition()).toBe(true);
}
