import { HttpResponse, http } from 'msw';
import { promptOf } from '@/features/scan/recognition/defaults';
import { configureAuth, resetAuth } from '@/lib/auth/session';
import { MemoryTokenStore } from '@/lib/auth/tokenStore';
import { evalsMock } from '@/mocks/evals';
import { server } from '@/mocks/server';
import { setPictureFiles, trackedPictures } from '@/features/scan/pictureFiles';
import { failedRead, makeKit, uriOf } from './evalsTestKit';

interface Result {
  photoId: string;
  engine: string;
  outcome: string;
  fellBackFrom?: string;
  items: { title: string; localId: string }[];
  unreadableCount: number;
  latencyMs: number;
  firstItemMs?: number;
  thermalState: string;
}
interface Body {
  device: { model: string; osVersion: string; appVersion: string };
  configVersion: string;
  partial?: boolean;
  results: Result[];
}
const runs = () => evalsMock.runs as Body[];

beforeEach(() => {
  evalsMock.reset();
  configureAuth({ platform: 'native', tokenStore: new MemoryTokenStore({ refreshToken: 'mock-refresh', serverUrl: 'http://api.test' }) });
});
afterEach(() => {
  resetAuth();
  setPictureFiles(undefined);
});

describe('the eval runner', () => {
  it('reads each photo with both engines, in order, and sends one run with every declared field', async () => {
    const kit = makeKit({ photos: 4 });
    await kit.runner.start();

    expect(runs()).toHaveLength(1);
    const { results, device, configVersion } = runs()[0];
    expect(results).toHaveLength(8);
    expect(results.map((r) => `${r.photoId}:${r.engine}`)).toEqual(
      kit.ids.flatMap((id) => [`${id}:apple_model`, `${id}:vision_pipeline`]),
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
    const [first, , second] = runs()[0].results;
    expect(first).toMatchObject({ engine: 'apple_model', outcome: 'refused', fellBackFrom: 'declined', items: [] });
    expect(second).toMatchObject({ engine: 'apple_model', outcome: 'error', fellBackFrom: 'context', items: [] });
    expect(runs()[0].results[1]).toMatchObject({ engine: 'vision_pipeline', outcome: 'ok' });
  });

  it('keeps the screen awake for the run and lets go at the end', async () => {
    const kit = makeKit({ photos: 2 });
    await kit.runner.start();
    expect(kit.awake).toEqual({ on: 1, off: 1 });
  });

  it('records a serious thermal state, tells the owner to keep the phone cool and carries on', async () => {
    const kit = makeKit({ photos: 2 });
    jest.spyOn(kit.vision, 'deviceStatus').mockResolvedValue({ thermalState: 'serious', osVersion: '26.0' });
    await kit.runner.start();
    expect(kit.runner.getState().keepCool).toBe(true);
    expect(runs()[0].results).toHaveLength(4);
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
    expect(runs()[0].results.map((r) => r.photoId)).toEqual(['photo-001', 'photo-001']);
    expect(kit.runner.getState()).toMatchObject({ phase: 'done', partial: true });

    await kit.runner.resume();

    expect(runs()).toHaveLength(2);
    expect(runs()[1].partial).toBeFalsy();
    expect(runs()[1].results).toHaveLength(8);
    expect(kit.downloaded).toEqual(['photo-001', 'photo-002', 'photo-002', 'photo-003', 'photo-004']);
  });

  it('pauses between steps and carries on when resumed', async () => {
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
    expect(runs()[0].results).toHaveLength(6);
    expect(kit.downloaded).toEqual(['photo-001', 'photo-002', 'photo-003']);
  });

  it('keeps the results when the upload fails and sends them when the owner retries', async () => {
    const kit = makeKit({ photos: 2 });
    evalsMock.failNextSubmit = 500;
    await kit.runner.start();
    expect(kit.runner.getState().phase).toBe('upload_failed');
    expect(kit.runner.getState().resultCount).toBe(4);
    expect(evalsMock.runs).toHaveLength(0);

    await kit.runner.retryUpload();
    expect(kit.runner.getState().phase).toBe('done');
    expect(runs()).toHaveLength(1);
    expect(runs()[0].results).toHaveLength(4);
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

  it('deletes the photo even when reading it throws, and carries on with the next one', async () => {
    const kit = makeKit({ photos: 2 });
    jest.spyOn(kit.vision, 'recognizeText').mockRejectedValue(new Error('boom'));
    await kit.runner.start();
    expect(kit.deleted).toEqual(kit.ids.map(uriOf));
    expect(trackedPictures()).toEqual([]);
    expect(kit.runner.getState().phase).toBe('done');
    expect(runs()[0].results.some((r) => r.outcome === 'error')).toBe(true);
  });

  it('skips a photo it cannot download, says so, and sends the rest', async () => {
    const kit = makeKit({ photos: 3, failDownload: ['photo-002'] });
    await kit.runner.start();
    expect(runs()[0].results.map((r) => r.photoId)).toEqual(['photo-001', 'photo-001', 'photo-003', 'photo-003']);
    expect(kit.runner.getState().skipped).toEqual(['photo-002']);
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
