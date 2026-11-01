import { HttpResponse, http } from 'msw';
import { promptOf } from '@/features/scan/recognition/defaults';
import { authHeaders, configureAuth, resetAuth } from '@/lib/auth/session';
import { MemoryNativeAuth } from '@/lib/auth/native';
import { evalsMock, type MockSendFailure } from '@/mocks/evals';
import { server } from '@/mocks/server';
import { setPictureFiles, trackedPictures } from '@/features/scan/pictureFiles';
import { failedRead, makeKit, uriOf } from './evalsTestKit';
import { line, modelItem, okRead, type Scene } from '@/features/scan/recognition/fakes';
import { EngineSelector } from '@/features/scan/recognition/EngineSelector';
import type { ReadItem } from '@/features/scan/recognition/types';
import { PC_NOT_DOWNLOADING, SCORING_POLL_MS, sendWaitMs } from './runner';

interface Result {
  photoId: string;
  engine: string;
  outcome: string;
  fellBackFrom?: string;
  items: {
    title: string;
    localId: string;
    resolvedBy?: string;
    tier?: string;
    provisional?: boolean;
    isGuess?: boolean;
    segmentId?: string;
    kind?: string;
    box?: { x: number; y: number; width: number; height: number };
    productCode?: string;
    unconfirmed?: boolean;
  }[];
  unreadableCount: number;
  latencyMs: number;
  firstItemMs?: number;
  thermalState: string;
  capturedCount?: number;
  capturedAt?: string;
  duplicateRescan?: { ownedHits: number; answeredOnCard: number; unsolicitedAdds: number };
  ocrLines?: { text: string; x: number; y: number; width: number; height: number }[];
  ocrPieces?: number[][];
  coverCue?: string;
  printKind?: string;
}
interface Opened {
  device: { model: string; osVersion: string; appVersion: string };
  configVersion: string;
  baselineNote?: string;
}
/** Every engine result the PC took, photo by photo, in the order they were sent. */
const results = () => evalsMock.sent.flatMap((s) => s.results as Result[]);
const opened = () => evalsMock.opened as Opened[];
const sentIds = () => evalsMock.sent.map((s) => s.photoId);

beforeEach(() => {
  evalsMock.reset();
  configureAuth({ platform: 'native', native: new MemoryNativeAuth() });
});
afterEach(() => {
  resetAuth();
  setPictureFiles(undefined);
});

/** Starts a run and, while someone is watching the screen, waits until the PC has scored it. */
async function runToEnd(kit: ReturnType<typeof makeKit>, options?: Parameters<ReturnType<typeof makeKit>['runner']['start']>[0]) {
  const unwatch = kit.runner.watch();
  try {
    await kit.runner.start(options);
    await waitFor(() => kit.runner.getState().phase === 'done');
  } finally {
    unwatch();
  }
}

describe('the eval runner', () => {
  describe('reading and sending, a photo at a time', () => {
    it('reads each photo with all three engines, in order, and sends it to the PC with every declared field', async () => {
      const kit = makeKit({ photos: 4 });
      await kit.runner.start();

      expect(sentIds()).toEqual(kit.ids);
      expect(results()).toHaveLength(12);
      expect(results().map((r) => `${r.photoId}:${r.engine}`)).toEqual(
        kit.ids.flatMap((id) => [`${id}:apple_model`, `${id}:fm_text`, `${id}:vision_pipeline`]),
      );
      for (const r of results()) {
        expect(r.outcome).toBe('ok');
        expect(r.items.length).toBeGreaterThan(0);
        expect(r.unreadableCount).toEqual(expect.any(Number));
        expect(r.latencyMs).toEqual(expect.any(Number));
        expect(r.firstItemMs).toEqual(expect.any(Number));
        expect(r.thermalState).toBe('nominal');
      }
      expect(opened()).toHaveLength(1);
      expect(opened()[0].device).toEqual({ model: 'iPhone16,1', osVersion: '26.0', appVersion: '1.2.3' });
      expect(opened()[0].configVersion).toBe('v1');
    });

    it('sends a photo before it reads the next one, so nothing waits to go up at the end', async () => {
      const sentBeforeEach: number[] = [];
      let last = '';
      const kit = makeKit({
        photos: 4,
        onRead: (uri) => {
          if (uri !== last) {
            last = uri;
            sentBeforeEach.push(evalsMock.sent.length);
          }
        },
      });
      await kit.runner.start();
      expect(sentBeforeEach).toEqual([0, 1, 2, 3]);
    });

    it('opens the run on the PC with the baseline note, and leaves it out when there is none', async () => {
      const kit = makeKit({ photos: 1 });
      await runToEnd(kit, { baselineNote: 'T1 bar moved 0.90 to 0.92 for the acceptance re-run.' });
      expect(opened()[0].baselineNote).toBe('T1 bar moved 0.90 to 0.92 for the acceptance re-run.');

      await runToEnd(kit);
      expect(opened()[1]).not.toHaveProperty('baselineNote');

      await runToEnd(kit, { baselineNote: '   ' });
      expect(opened()[2]).not.toHaveProperty('baselineNote');
    });

    it('records why the model fell back, and does not count the fallback reading as the model\'s', async () => {
      const kit = makeKit({ photos: 2, scenes: { 'photo-001': { read: failedRead('declined') }, 'photo-002': { read: failedRead('contextExceeded') } } });
      await kit.runner.start();
      const [first, middle, , second] = results();
      expect(first).toMatchObject({ engine: 'apple_model', outcome: 'refused', fellBackFrom: 'declined', items: [] });
      expect(second).toMatchObject({ engine: 'apple_model', outcome: 'error', fellBackFrom: 'context', items: [] });
      expect(middle).toMatchObject({ engine: 'fm_text', outcome: 'ok' });
      expect(results()[2]).toMatchObject({ engine: 'vision_pipeline', outcome: 'ok' });
    });

    it('sends a model timeout as outcome timeout with fellBackFrom timeout, not as an error or an outage', async () => {
      const kit = makeKit({ photos: 1, scenes: { 'photo-001': { read: failedRead('timeout') } } });
      await kit.runner.start();
      const [model, , vision] = results();
      expect(model).toMatchObject({ engine: 'apple_model', outcome: 'timeout', fellBackFrom: 'timeout', items: [] });
      expect(model.latencyMs).toBeGreaterThanOrEqual(0);
      expect(vision).toMatchObject({ engine: 'vision_pipeline', outcome: 'ok' });
    });

    it('sends an extraction refusal and timeout as their own outcomes, not as errors', async () => {
      const kit = makeKit({ photos: 2, scenes: { 'photo-001': { extract: failedRead('declined') }, 'photo-002': { extract: failedRead('timeout') } } });
      await kit.runner.start();
      const fm = results().filter((r) => r.engine === 'fm_text');
      expect(fm).toHaveLength(2);
      expect(fm[0]).toMatchObject({ photoId: 'photo-001', outcome: 'refused', fellBackFrom: 'declined', items: [] });
      expect(fm[1]).toMatchObject({ photoId: 'photo-002', outcome: 'timeout', fellBackFrom: 'timeout', items: [] });
    });

    it('sends the model\'s own reading as its row, not the scanner\'s model-plus-Vision combination', async () => {
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
      const rows = results();
      expect(rows.find((r) => r.engine === 'apple_model')?.items.map((i) => i.title)).toEqual(['Title of photo-001', 'Other of photo-001']);
      expect(rows.find((r) => r.engine === 'vision_pipeline')?.items.map((i) => i.title)).toEqual(['Title of photo-001', 'Other of photo-001', 'Third of photo-001']);
      for (const item of rows.find((r) => r.engine === 'apple_model')?.items ?? []) expect(item.resolvedBy).toBe('model');
    });

    it('tells every engine what kind of shot a photo is, so a single reads like the scanner reads a cover and a shelf like a shelf', async () => {
      // photo-001 and photo-002 are in the shelf set, photo-003 and photo-004 in the single set, and the runner reads the shelves first.
      const kit = makeKit({ photos: 4 });
      await kit.runner.start();
      const asked = (calls: { options: { maxItems?: number } }[]) => calls.map((c) => c.options.maxItems);
      // A cover is one item to both model tiers (the kit's config allows 17 on a shelf).
      expect(asked(kit.vision.readCalls)).toEqual([17, 17, 1, 1]);
      expect(asked(kit.vision.extractCalls)).toEqual([17, 17, 1, 1]);
      // A cover is the whole frame to the Vision engine; a shelf is still split per spine (the kit's two lines are two spines).
      const vision = results().filter((r) => r.engine === 'vision_pipeline');
      expect(vision.map((r) => [r.photoId, r.items.length])).toEqual([
        ['photo-001', 2],
        ['photo-002', 2],
        ['photo-003', 1],
        ['photo-004', 1],
      ]);
    });

    it('corrects a single cover in every engine\'s row the way the scanner does, and a shelf in none', async () => {
      // photo-001 is a shelf and photo-002 a single. Both model tiers read the Criterion film as a book; the Vision engine says "other".
      const criterion = {
        read: okRead([modelItem('RUSHMORE', 'RUSHMORE', 0, { kind: 'book' })]),
        extract: okRead([modelItem('RUSHMORE', 'RUSHMORE', 0, { kind: 'book' })]),
        text: [line('RUSHMORE', 0.2, 0.2, 0.5, 0.1), line('THE CRITERION COLLECTION', 0.1, 0.8, 0.3, 0.04)],
      };
      const kit = makeKit({ photos: 2, scenes: { 'photo-001': criterion, 'photo-002': criterion } });
      await kit.runner.start();
      const kinds = (photoId: string) =>
        Object.fromEntries(
          results().filter((r) => r.photoId === photoId).map((r) => [r.engine, (r.items as { kind?: string }[]).map((i) => i.kind)]),
        );
      expect(kinds('photo-002')).toEqual({ apple_model: ['film'], fm_text: ['film'], vision_pipeline: ['film'] });
      expect(kinds('photo-001')).toEqual({ apple_model: ['book'], fm_text: ['book'], vision_pipeline: ['other'] });
    });

    describe('what the shelf printed', () => {
      const printOf = (photoId: string, engine: string) => results().find((r) => r.photoId === photoId && r.engine === engine)?.printKind;

      it('sends the kind a film shelf printed on the row that read the print', async () => {
        const tapes = { text: [line('STAR TREK GENERATIONS', 0.2, 0.2, 0.05, 0.6), line('92 Minutes Color', 0.3, 0.2, 0.05, 0.6)] };
        const kit = makeKit({ photos: 2, scenes: { 'photo-001': tapes } });

        await kit.runner.start();

        // The Vision tier reads the shelf's text, so the print is its row's. The model is read alone here, which the scanner never does
        // on a shelf: it has no print reading of its own, and the PC's combined row takes Vision's items where both read a spine.
        expect(printOf('photo-001', 'vision_pipeline')).toBe('film');
        expect(printOf('photo-001', 'apple_model')).toBeUndefined();
      });

      it('leaves it out of a shelf whose print says nothing of its kind', async () => {
        const kit = makeKit({ photos: 2, scenes: { 'photo-001': { text: [line('THE SECRET HISTORY', 0.2, 0.2, 0.05, 0.6)] } } });

        await kit.runner.start();

        for (const row of results().filter((r) => r.photoId === 'photo-001')) expect(row).not.toHaveProperty('printKind');
      });

      it('leaves it out of a single cover, whose own print speaks for its one item', async () => {
        const kit = makeKit({ photos: 2, scenes: { 'photo-002': { text: [line('WIDESCREEN', 0.2, 0.2, 0.5, 0.1)] } } });

        await kit.runner.start();

        for (const row of results().filter((r) => r.photoId === 'photo-002')) expect(row).not.toHaveProperty('printKind');
      });
    });

    describe('what the cover cue saw', () => {
      // photo-001 is a shelf and photo-002 a single (the runner reads the shelves first). A Blu-ray keepcase, 0.86 wide for its height, 80% of a 3000 x 4000 picture tall.
      const blurayWidth = (0.86 * 0.8 * 4000) / 3000;
      const blurayCase = { x: (1 - blurayWidth) / 2, y: 0.1, width: blurayWidth, height: 0.8 };
      const cueOf = (photoId: string, engine: string) => results().find((r) => r.photoId === photoId && r.engine === engine)?.coverCue;

      it('sends it with every engine\'s row of a single cover: the shape it found, its ratio and that it came in time', async () => {
        const kit = makeKit({ photos: 2, scenes: { 'photo-002': { rectangles: [blurayCase] } } });
        await kit.runner.start();
        // "late" is the Vision tier, which can finish its read a moment before the cue's own search; both are inside the wait.
        for (const engine of ['apple_model', 'fm_text', 'vision_pipeline']) {
          expect([engine, cueOf('photo-002', engine)]).toEqual([engine, expect.stringMatching(/^cue=(ready|late) fmt=shape:bluray r=0\.86 cov=0\.\d\d rects=1 /)]);
        }
      });

      it('says when the cover had no rectangle to measure', async () => {
        const kit = makeKit({ photos: 2 });
        await kit.runner.start();
        expect(cueOf('photo-002', 'vision_pipeline')).toContain('rects=0');
      });

      it('leaves it out of a shelf\'s rows, which the cue does not look at', async () => {
        const kit = makeKit({ photos: 2, scenes: { 'photo-001': { rectangles: [blurayCase] } } });
        await kit.runner.start();
        for (const row of results().filter((r) => r.photoId === 'photo-001')) expect(row).not.toHaveProperty('coverCue');
      });

      it('leaves it out of a row whose model gave no reading, which used no cue', async () => {
        const kit = makeKit({ photos: 2, scenes: { 'photo-002': { read: failedRead('declined') } } });
        await kit.runner.start();
        const refused = results().find((r) => r.photoId === 'photo-002' && r.engine === 'apple_model');
        expect(refused?.outcome).toBe('refused');
        expect(refused).not.toHaveProperty('coverCue');
        expect(cueOf('photo-002', 'vision_pipeline')).toEqual(expect.stringContaining('cue='));
      });
    });

    it('sends the OCR lines the Vision engine split and the pieces it made, on its row only', async () => {
      const kit = makeKit({ photos: 1 });
      await kit.runner.start();
      const rows = results();
      const vision = rows.find((r) => r.engine === 'vision_pipeline');
      expect(vision?.ocrLines).toEqual([
        { text: 'Title of photo-001', x: 0.1, y: 0.1, width: 0.1, height: 0.5 },
        { text: 'Other of photo-001', x: 0.5, y: 0.1, width: 0.1, height: 0.5 },
      ]);
      expect(vision?.ocrPieces).toEqual([[0], [1]]);
      for (const row of rows.filter((r) => r.engine !== 'vision_pipeline')) {
        expect(row.ocrLines).toBeUndefined();
        expect(row.ocrPieces).toBeUndefined();
      }
    });

    it('points each piece at the lines it was made from, a flat stack included', async () => {
      const stack = { text: [
        line('Spine one', 0.1, 0.05, 0.05, 0.5),
        line('Spine two', 0.3, 0.05, 0.05, 0.5),
        line('Flat book', 0.2, 0.8, 0.4, 0.05),
      ] };
      const kit = makeKit({ photos: 1, scenes: { 'photo-001': stack } });
      await kit.runner.start();
      const vision = results().find((r) => r.engine === 'vision_pipeline');
      expect(vision?.ocrLines?.map((l) => l.text)).toEqual(['Spine one', 'Flat book', 'Spine two']);
      expect(vision?.ocrPieces).toEqual([[0], [2], [1]]);
      expect(vision?.items.map((i) => i.title)).toEqual(['Spine one', 'Spine two', 'Flat book']);
    });

    it('keeps the OCR record inside what the server accepts: boxes 0 to 1, text up to 200 characters, 1000 lines', async () => {
      const many = Array.from({ length: 1010 }, (_, i) => line(`Line ${i}`, i / 1200, 0.1, 0.0005, 0.02));
      many.push(line('x'.repeat(300), -0.0004, -0.0002, 1.0003, 1.0001));
      const kit = makeKit({ photos: 1, scenes: { 'photo-001': { text: many } } });
      await kit.runner.start();
      const vision = results().find((r) => r.engine === 'vision_pipeline');
      const lines = vision?.ocrLines ?? [];
      expect(lines).toHaveLength(1000);
      expect(lines.every((l) => l.text.length <= 200)).toBe(true);
      expect(lines.every((l) => [l.x, l.y, l.width, l.height].every((v) => v >= 0 && v <= 1))).toBe(true);
      const indexes = (vision?.ocrPieces ?? []).flat();
      expect(indexes.length).toBeGreaterThan(0);
      expect(indexes.every((index) => index >= 0 && index < lines.length)).toBe(true);
      expect(kit.runner.getState().phase).toBe('scoring');
    });

    it('marks extraction rows as settled by the model', async () => {
      const kit = makeKit({ photos: 1 });
      await kit.runner.start();
      const fm = results().find((r) => r.engine === 'fm_text');
      expect(fm?.items.length).toBeGreaterThan(0);
      for (const item of fm?.items ?? []) expect(item.resolvedBy).toBe('model');
    });

    it('records a serious thermal state, tells the owner to keep the phone cool and keeps going', async () => {
      const kit = makeKit({ photos: 2 });
      jest.spyOn(kit.vision, 'deviceStatus').mockResolvedValue({ thermalState: 'serious', osVersion: '26.0' });
      await kit.runner.start();
      expect(kit.runner.getState().keepCool).toBe(true);
      expect(results()).toHaveLength(6);
      expect(results().every((r) => r.thermalState === 'serious')).toBe(true);
    });

    it('gives the engines the prompt and limits from the config store, and a newer version to the next run', async () => {
      const kit = makeKit({ photos: 1, versions: ['v1', 'v2'] });
      await runToEnd(kit);
      const first = kit.vision.readCalls[0].options;
      expect(first.prompt).toBe(promptOf(kit.configFor('v1')));
      expect(first.timeoutMs).toBe(1234);
      expect(first.maxItems).toBe(17);
      expect(opened()[0].configVersion).toBe('v1');

      await runToEnd(kit);
      const next = kit.vision.readCalls[1].options;
      expect(next.prompt).toBe(promptOf(kit.configFor('v2')));
      expect(next.timeoutMs).toBe(1235);
      expect(opened()[1].configVersion).toBe('v2');
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
      expect(kit.runner.getState().phase).toBe('scoring');
      expect(results().filter((r) => r.engine === 'apple_model').every((r) => r.outcome === 'error')).toBe(true);
    });

    it('skips a photo it cannot download, says so, and sends the rest', async () => {
      const kit = makeKit({ photos: 3, failDownload: ['photo-002'] });
      await kit.runner.start();
      expect(sentIds()).toEqual(['photo-001', 'photo-003']);
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
      const model = results().filter((r) => r.engine === 'apple_model');
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
      for (const row of results().filter((r) => r.engine === 'vision_pipeline')) {
        expect(row.items.length).toBeGreaterThan(0);
        for (const item of row.items) {
          expect(item).toMatchObject({ provisional: true, isGuess: true });
          expect(item).not.toHaveProperty('tier');
        }
      }
      // the extraction tier is a model read, so its high-confidence rows claim T1 like the Apple's
      for (const row of results().filter((r) => r.engine === 'fm_text')) {
        expect(row.items.length).toBeGreaterThan(0);
        for (const item of row.items) {
          expect(item).toMatchObject({ tier: 'T1', provisional: false });
          expect(item).not.toHaveProperty('isGuess');
        }
      }
    });

    describe('a read only the picture model made', () => {
      const readOf = (title: string, extra: Partial<ReadItem> = {}): ReadItem => ({
        localId: `id-${title}`,
        text: title,
        title,
        kind: 'book',
        order: 0,
        confidence: 'high',
        ...extra,
      });

      it('is sent with its mark, and a read without the mark is sent without it', async () => {
        const items = [readOf('Ubik', { unconfirmed: true }), readOf('Dune', { order: 1 })];
        const spy = jest.spyOn(EngineSelector.prototype, 'read').mockImplementation(async (_still, onItem) => {
          items.forEach(onItem);
          return { items, unreadableCount: 0, engine: 'apple_model', timings: { totalMs: 1, visionMs: 0, modelMs: 1 } };
        });
        try {
          const kit = makeKit({ photos: 1 });
          await kit.runner.start();
          const model = results().find((r) => r.engine === 'apple_model');
          expect(model?.items.map((i) => [i.title, i.unconfirmed])).toEqual([['Ubik', true], ['Dune', undefined]]);
          expect(model?.items[1]).not.toHaveProperty('unconfirmed');
        } finally {
          spy.mockRestore();
        }
      });

      it('is not marked in an ordinary run: the model row is read alone and no engine row has a read nothing confirmed', async () => {
        const kit = makeKit({ photos: 4 });
        await kit.runner.start();
        for (const row of results()) {
          for (const item of row.items) expect(item).not.toHaveProperty('unconfirmed');
        }
      });
    });

    it('stamps one segment id on each read and reconciles capturedCount with it', async () => {
      const kit = makeKit({ photos: 3 });
      await kit.runner.start();
      const segments = new Set<string>();
      for (const row of results()) {
        expect(row.items.length).toBeGreaterThan(0);
        const ids = new Set(row.items.map((i) => i.segmentId));
        expect(ids.size).toBe(1); // the read's items share its segment
        const [segment] = [...ids];
        expect(typeof segment).toBe('string');
        expect(segment?.length).toBeGreaterThan(0);
        if (segment) segments.add(segment);
        expect(row.capturedCount).toBe(row.unreadableCount + 1);
      }
      expect(segments.size).toBe(results().length); // each read mints its own
    });

    it('stamps capturedAt when each read starts', async () => {
      const kit = makeKit({ photos: 2 });
      const before = Date.now();
      await kit.runner.start();
      const after = Date.now();
      for (const row of results()) {
        const stamped = Date.parse(row.capturedAt ?? '');
        expect(Number.isNaN(stamped)).toBe(false);
        expect(stamped).toBeGreaterThanOrEqual(before);
        expect(stamped).toBeLessThanOrEqual(after);
      }
    });

    it('sends no duplicate evidence: the runner never touches the library', async () => {
      const kit = makeKit({ photos: 2 });
      await kit.runner.start();
      for (const row of results()) {
        expect(row).not.toHaveProperty('duplicateRescan');
      }
    });

    it('runs only one run at a time', async () => {
      const kit = makeKit({ photos: 2 });
      const first = kit.runner.start();
      const second = kit.runner.start();
      await Promise.all([first, second]);
      expect(evalsMock.opened).toHaveLength(1);
      expect(sentIds()).toEqual(kit.ids);
      expect(kit.vision.maxConcurrentReads).toBe(1);
    });
  });

  describe('picking a run up where it stopped', () => {
    it('shows the run the PC holds with the photos it already has, before anything is read', async () => {
      const kit = makeKit({ photos: 4, held: ['photo-001', 'photo-003'] });
      await kit.runner.refresh();

      expect(kit.runner.getState()).toMatchObject({ phase: 'resumable', total: 4, photosDone: 2, pending: 0 });
      expect(kit.downloaded).toEqual([]);
    });

    it('reads only the photos the PC does not have, and never reads one it has again', async () => {
      const kit = makeKit({ photos: 4, held: ['photo-001', 'photo-003'] });
      await kit.runner.refresh();
      await kit.runner.continueRun();

      expect(kit.downloaded).toEqual(['photo-002', 'photo-004']);
      expect(sentIds()).toEqual(['photo-002', 'photo-004']);
      expect(evalsMock.opened).toHaveLength(0); // the run was not opened again
      expect(evalsMock.finishes).toHaveLength(1);
      expect(kit.runner.getState().phase).toBe('scoring');
    });

    it('finishes at once when the PC already has every photo', async () => {
      const kit = makeKit({ photos: 2, held: ['photo-001', 'photo-002'] });
      await kit.runner.refresh();
      await kit.runner.continueRun();

      expect(kit.downloaded).toEqual([]);
      expect(evalsMock.finishes).toEqual([{}]);
    });

    it('finds nothing to continue when the PC holds no run', async () => {
      const kit = makeKit({ photos: 2 });
      await kit.runner.refresh();
      expect(kit.runner.getState().phase).toBe('idle');
      await kit.runner.continueRun();
      expect(kit.downloaded).toEqual([]);
    });

    it('goes quietly to the start when the PC cannot be asked, or the evals are off', async () => {
      const kit = makeKit({ photos: 2 });
      evalsMock.access = 'off';
      await kit.runner.refresh();
      expect(kit.runner.getState().phase).toBe('idle');
    });

    it('offers to continue when a run was already open on the PC at Start, instead of opening another', async () => {
      const kit = makeKit({ photos: 3, held: ['photo-001'] });
      await kit.runner.start();

      expect(kit.runner.getState()).toMatchObject({ phase: 'resumable', photosDone: 1, total: 3 });
      expect(kit.runner.getState().message).toMatch(/already holding a run/);
      expect(kit.downloaded).toEqual([]);
      expect(evalsMock.run?.runId).toBe('run-held');
    });

    it('ends in an error with Check again, not stuck on opening, when a run is already open and the PC cannot then be asked about it', async () => {
      const kit = makeKit({ photos: 3, held: ['photo-001'] });
      server.use(http.get('*/api/admin/evals/runs/open', () => HttpResponse.error()));

      await kit.runner.start();

      expect(kit.runner.getState().phase).toBe('error');
      expect(kit.runner.getState().message).toMatch(/already holding a run/);
      server.resetHandlers();
      await kit.runner.recheck();
      expect(kit.runner.getState()).toMatchObject({ phase: 'resumable', photosDone: 1, total: 3 });
    });

    it('shows a run the PC is scoring, and its summary when it is done', async () => {
      const kit = makeKit({ photos: 2, held: ['photo-001', 'photo-002'] });
      evalsMock.run!.state = 'finishing';
      evalsMock.scoredAfterPolls = 3;
      const unwatch = kit.runner.watch();
      await kit.runner.refresh();
      expect(kit.runner.getState().phase).toBe('scoring');

      await waitFor(() => kit.runner.getState().phase === 'done');
      unwatch();

      expect(kit.runner.getState().summary?.gate.verdict).toBe('pass');
      expect(kit.sleeps).toEqual([SCORING_POLL_MS, SCORING_POLL_MS]);
    });

    it('starts over by throwing the run on the PC away and the readings kept here', async () => {
      const kit = makeKit({ photos: 3, held: ['photo-001'] });
      await kit.runner.refresh();

      await kit.runner.startOver();

      expect(evalsMock.discards).toBe(1);
      expect(evalsMock.run).toBeUndefined();
      expect(kit.runner.getState().phase).toBe('idle');
      expect(kit.outbox.getString('eval-outbox-v1')).toBeUndefined();
    });

    it('says so when it cannot reach the PC to start over, and keeps the run', async () => {
      const kit = makeKit({ photos: 3, held: ['photo-001'] });
      await kit.runner.refresh();
      server.use(http.delete('*/api/admin/evals/runs/open', () => HttpResponse.error()));

      await kit.runner.startOver();

      expect(kit.runner.getState().phase).toBe('error');
      expect(evalsMock.run).toBeDefined();
    });

    it('does not mix up readings kept for another run', async () => {
      const kit = makeKit({ photos: 2, held: ['photo-001'] });
      kit.outbox.set('eval-outbox-v1', JSON.stringify({ runId: 'run-other', photos: [{ photoId: 'photo-002', results: [{ photoId: 'photo-002' }] }] }));

      await kit.runner.refresh();

      expect(kit.runner.getState()).toMatchObject({ phase: 'resumable', photosDone: 1, pending: 0 });
    });
  });

  describe('when the PC does not take a photo', () => {
    it('keeps the readings, waits a little longer each time, and sends them before reading the next photo', async () => {
      const downloadedAtSend: number[] = [];
      const kit: ReturnType<typeof makeKit> = makeKit({ photos: 2, onSend: () => void downloadedAtSend.push(kit.downloaded.length) });
      evalsMock.failSends = ['network', 'network', 500];

      await kit.runner.start();

      expect(kit.sleeps).toEqual([sendWaitMs(1), sendWaitMs(2), sendWaitMs(3)]);
      expect(kit.sleeps).toEqual([1000, 2000, 4000]);
      expect(evalsMock.attempts).toEqual(['photo-001', 'photo-001', 'photo-001', 'photo-001', 'photo-002']);
      expect(downloadedAtSend).toEqual([1, 1, 1, 1, 2]); // photo 2 is not even downloaded until photo 1 is on the PC
      expect(sentIds()).toEqual(kit.ids);
      expect(results().filter((r) => r.photoId === 'photo-001')).toHaveLength(3);
    });

    it('says it is waiting for the PC, with the readings kept on the phone, until it answers', async () => {
      const seen: { phase: string; pending: number; kept: boolean }[] = [];
      const kit = makeKit({
        photos: 1,
        onSleep: () => {
          seen.push({ phase: kit.runner.getState().phase, pending: kit.runner.getState().pending, kept: kit.outbox.getString('eval-outbox-v1') !== undefined });
        },
      });
      evalsMock.failSends = ['network'];

      await kit.runner.start();

      expect(seen).toEqual([{ phase: 'waiting', pending: 1, kept: true }]);
      expect(kit.outbox.getString('eval-outbox-v1')).toBeUndefined(); // gone once the PC has it
      expect(kit.runner.getState().pending).toBe(0);
    });

    it('caps the wait at thirty seconds', () => {
      expect([1, 2, 3, 4, 5, 6, 7, 20].map(sendWaitMs)).toEqual([1000, 2000, 4000, 8000, 16000, 30000, 30000, 30000]);
    });

    it('leaves a photo out when the PC refuses it for good, and goes on with the rest', async () => {
      const kit = makeKit({ photos: 3 });
      evalsMock.refuse['photo-002'] = 'evals.thermal_required';

      await kit.runner.start();

      expect(evalsMock.attempts).toEqual(['photo-001', 'photo-002', 'photo-003']); // once: a final answer is not tried again
      expect(kit.sleeps).toEqual([]);
      expect(sentIds()).toEqual(['photo-001', 'photo-003']);
      expect(kit.runner.getState().refused).toEqual(['photo-002']);
      expect(kit.runner.getState().phase).toBe('scoring');
      expect(evalsMock.finishes).toHaveLength(1);
    });

    it('does not read a photo again that the PC refused when the run is picked up', async () => {
      const kit = makeKit({ photos: 3, held: ['photo-001'] });
      evalsMock.run!.refused.push({ photoId: 'photo-002', reason: 'evals.thermal_required' });

      await kit.runner.refresh();
      await kit.runner.continueRun();

      expect(kit.downloaded).toEqual(['photo-003']);
      expect(kit.runner.getState().refused).toEqual(['photo-002']);
    });

    it('leaves a photo out when the PC says the photo is too big, as it does for a photo that fails its own checks', async () => {
      const kit = makeKit({ photos: 3 });
      evalsMock.failSends = [{ status: 413, code: 'evals.body_too_large' }];

      await kit.runner.start();

      expect(kit.runner.getState().refused).toEqual(['photo-001']);
      expect(sentIds()).toEqual(['photo-002', 'photo-003']);
      expect(kit.outbox.getString('eval-outbox-v1')).toBeUndefined();
      expect(kit.runner.getState().phase).toBe('scoring');
    });

    describe('when the PC is restarting', () => {
      it.each([502, 503, 504])('waits for a PC that answers %s for as long as it takes, and keeps the photo until it is taken', async (status) => {
        const kit = makeKit({ photos: 2 });
        evalsMock.failSends = Array.from({ length: 10 }, () => status);

        await kit.runner.start();

        expect(kit.sleeps).toHaveLength(10);
        expect(evalsMock.attempts.filter((id) => id === 'photo-001')).toHaveLength(11);
        expect(sentIds()).toEqual(['photo-001', 'photo-002']);
        expect(kit.runner.getState()).toMatchObject({ phase: 'scoring', refused: [], pending: 0 });
      });

      it('counts only plain 500s in a row against a photo: an answer that says the PC is not there starts the count again', async () => {
        const kit = makeKit({ photos: 1 });
        evalsMock.failSends = [500, 500, 500, 500, 502, 500, 500, 500, 500];

        await kit.runner.start();

        expect(sentIds()).toEqual(['photo-001']);
        expect(kit.runner.getState()).toMatchObject({ phase: 'scoring', refused: [] });
      });

      it('waits for the PC to take the finish when the proxy answers 502 meanwhile', async () => {
        const kit = makeKit({ photos: 1 });
        evalsMock.failNextFinish = 502;

        await kit.runner.start();

        expect(evalsMock.finishes).toHaveLength(2);
        expect(kit.runner.getState().phase).toBe('scoring');
      });

      it('waits out a PC that is not answering while a photo downloads, and skips nothing', async () => {
        const kit = makeKit({ photos: 3, downloadFailures: ['network', 502, 'network'] });

        await kit.runner.start();

        expect(kit.sleeps).toEqual([1000, 2000, 4000]);
        expect(kit.runner.getState().skipped).toEqual([]);
        expect(kit.downloaded).toEqual(kit.ids);
        expect(sentIds()).toEqual(kit.ids);
        expect(evalsMock.finishes).toEqual([{}]);
        expect(evalsMock.run?.partial).toBe(false);
      });

      it('says it is waiting for the PC while a download waits', async () => {
        const seen: string[] = [];
        const kit = makeKit({ photos: 1, downloadFailures: ['network'], onSleep: () => void seen.push(kit.runner.getState().phase) });

        await kit.runner.start();

        expect(seen).toEqual(['waiting']);
        expect(kit.runner.getState().phase).toBe('scoring');
      });

      it('skips a photo only when the PC gives a final answer about that photo, such as 404, and reads the others', async () => {
        const kit = makeKit({ photos: 3, downloadFailures: [404] });

        await kit.runner.start();

        expect(kit.runner.getState().skipped).toEqual(['photo-001']);
        expect(sentIds()).toEqual(['photo-002', 'photo-003']);
        expect(kit.sleeps).toEqual([]);
      });

      it('stops with the readings kept when a download keeps answering with a server error, rather than skipping photo after photo', async () => {
        const kit = makeKit({ photos: 3, downloadFailures: [500, 500, 500, 500, 500] });

        await kit.runner.start();

        expect(kit.runner.getState()).toMatchObject({ phase: 'error', message: PC_NOT_DOWNLOADING, skipped: [] });
        expect(kit.downloaded).toEqual([]);
        expect(evalsMock.finishes).toEqual([]);
      });

      it.each([401, 403])('stops with the readings kept when a download is answered %s, which says nothing about the photo', async (status) => {
        const kit = makeKit({ photos: 3, downloadFailures: [status, status] });

        await kit.runner.start();

        expect(kit.runner.getState()).toMatchObject({ phase: 'error', message: PC_NOT_DOWNLOADING, skipped: [] });
        expect(evalsMock.finishes).toEqual([]);
      });

      it('reads the photos it skipped again when the run is continued', async () => {
        const kit = makeKit({ photos: 3, downloadFailures: [404] });
        evalsMock.failSends = [{ status: 409, code: 'evals.results_not_writable' }];
        await kit.runner.start();
        expect(kit.runner.getState().skipped).toEqual(['photo-001']);

        await kit.runner.recheck();
        expect(kit.runner.getState().skipped).toEqual([]);
        await kit.runner.continueRun();

        expect(sentIds()).toEqual(['photo-002', 'photo-001', 'photo-003']);
        expect(kit.runner.getState().skipped).toEqual([]);
        expect(evalsMock.run?.partial).toBe(false);
      });

      it('clears the skipped photos as soon as the run is continued, not only when it is rechecked first', async () => {
        const kit = makeKit({ photos: 3, downloadFailures: [404] });
        evalsMock.failSends = [{ status: 409, code: 'evals.results_not_writable' }];
        await kit.runner.start();
        expect(kit.runner.getState().skipped).toEqual(['photo-001']);

        await kit.runner.refresh(); // picks the still-open run back up without touching skipped itself
        expect(kit.runner.getState()).toMatchObject({ phase: 'resumable', skipped: ['photo-001'] });

        const continuing = kit.runner.continueRun();
        expect(kit.runner.getState().skipped).toEqual([]);
        await continuing;
      });

      it('stops waiting for a download at once, and goes idle when nothing was read', async () => {
        const kit = makeKit({ photos: 3, downloadFailures: ['network', 'network', 'network'], onSleep: () => kit.runner.stop() });

        await kit.runner.start();

        expect(kit.sleeps).toEqual([1000]); // the wait was cut short after the first try, not retried with no limit
        expect(kit.downloaded).toEqual([]);
        expect(evalsMock.finishes).toEqual([]);
        expect(kit.runner.getState()).toMatchObject({ phase: 'idle', message: 'Nothing was read, so there was nothing to score.' });
      });

      it('stops waiting for a download at once, and goes on to finish with what is already read', async () => {
        // 0 is falsy: the first download (photo-001) succeeds, the second (photo-002) never answers until it is stopped.
        const kit = makeKit({ photos: 3, downloadFailures: [0, 'network', 'network', 'network'], onSleep: () => kit.runner.stop() });

        await kit.runner.start();

        expect(kit.sleeps).toEqual([1000]);
        expect(kit.downloaded).toEqual(['photo-001']);
        expect(sentIds()).toEqual(['photo-001']);
        expect(evalsMock.finishes).toEqual([{ partial: true }]);
        expect(kit.runner.getState()).toMatchObject({ phase: 'scoring', partial: true });
      });
    });

    describe('when the PC cannot take anything right now', () => {
      const notice = "Couldn't send to your PC. What's read is kept on this phone; check again to pick up.";
      // A 401 is asked again once with a fresh token by the auth layer before it reaches the runner, so the PC refuses twice.
      const cases: [string, MockSendFailure[]][] = [
        ['a 401 (signed out)', [401, 401]],
        ['a 404 (the evals are switched off)', [404]],
        ['a 409 evals.results_not_writable (the results folder cannot be written)', [{ status: 409, code: 'evals.results_not_writable' }]],
        ['a 403 (not the owner)', [403]],
      ];

      it.each(cases)('stops the run and keeps every reading when a send gets %s, refusing nothing', async (_name, failures) => {
        const kit = makeKit({ photos: 3 });
        evalsMock.failSends = failures;

        await kit.runner.start();

        const state = kit.runner.getState();
        expect(state).toMatchObject({ phase: 'error', message: notice, refused: [], pending: 1 });
        expect(kit.downloaded).toEqual(['photo-001']); // it stopped reading
        expect(evalsMock.sent).toEqual([]);
        expect(evalsMock.finishes).toEqual([]);
        expect(kit.outbox.getString('eval-outbox-v1')).toContain('photo-001');
        expect(kit.sleeps).toEqual([]); // a final answer is not waited out
      });

      it('sends the kept readings first when the owner checks again and continues, and reads nothing twice', async () => {
        const kit = makeKit({ photos: 3 });
        evalsMock.failSends = [{ status: 409, code: 'evals.results_not_writable' }];
        await kit.runner.start();

        await kit.runner.recheck();
        expect(kit.runner.getState()).toMatchObject({ phase: 'resumable', total: 3, photosDone: 1, pending: 1 });
        await kit.runner.continueRun();

        expect(sentIds()).toEqual(['photo-001', 'photo-002', 'photo-003']); // the kept photo goes first
        expect(kit.downloaded).toEqual(['photo-001', 'photo-002', 'photo-003']);
        expect(evalsMock.finishes).toEqual([{}]);
        expect(kit.runner.getState().refused).toEqual([]);
      });

      it('keeps the readings and the message when the PC cannot be reached to check again, and picks up once it can', async () => {
        const kit = makeKit({ photos: 2 });
        evalsMock.failSends = [404];
        await kit.runner.start();
        server.use(http.get('*/api/admin/evals/runs/open', () => HttpResponse.error()));

        await kit.runner.recheck();

        expect(kit.runner.getState()).toMatchObject({ phase: 'error', message: notice, pending: 1 });
        expect(kit.outbox.getString('eval-outbox-v1')).toContain('photo-001');
        server.resetHandlers();
        await kit.runner.recheck();
        await kit.runner.continueRun();
        expect(sentIds()).toEqual(['photo-001', 'photo-002']);
      });

      it('keeps the readings when the PC keeps answering one photo with a server error, rather than throwing the photo away', async () => {
        const kit = makeKit({ photos: 2 });
        evalsMock.failSends = [500, 500, 500, 500, 500];

        await kit.runner.start();

        expect(kit.sleeps).toEqual([1000, 2000, 4000, 8000]);
        expect(evalsMock.attempts).toEqual(['photo-001', 'photo-001', 'photo-001', 'photo-001', 'photo-001']);
        expect(kit.runner.getState()).toMatchObject({ phase: 'error', message: notice, refused: [], pending: 1 });
        expect(kit.outbox.getString('eval-outbox-v1')).toContain('photo-001');

        await kit.runner.recheck();
        await kit.runner.continueRun();
        expect(sentIds()).toEqual(['photo-001', 'photo-002']);
      });
    });

    it('waits for a PC that does not answer at all for as long as it takes, and never gives the photo up', async () => {
      const kit = makeKit({ photos: 1 });
      evalsMock.failSends = Array.from({ length: 20 }, () => 'network' as const);

      await kit.runner.start();

      expect(kit.sleeps).toHaveLength(20);
      expect(sentIds()).toEqual(['photo-001']);
      expect(kit.runner.getState().refused).toEqual([]);
    });

    it('does not ask the PC to finish while a photo is still waiting to go up', async () => {
      const finishesWhileWaiting: number[] = [];
      const kit = makeKit({ photos: 2, onSleep: () => void finishesWhileWaiting.push(evalsMock.finishes.length) });
      evalsMock.failSends = ['network', 'network'];

      await kit.runner.start();

      expect(finishesWhileWaiting).toEqual([0, 0]);
      expect(evalsMock.finishes).toHaveLength(1);
    });

    it('keeps a photo that was read when the app is killed, and sends it first when it is opened again, without reading it again', async () => {
      // The first app waits for the PC and is then killed: its wait never ends.
      const kit = makeKit({ photos: 3, onSleep: () => new Promise<void>(() => undefined) });
      evalsMock.failSends = ['network'];
      void kit.runner.start();
      await waitFor(() => kit.runner.getState().phase === 'waiting');
      expect(kit.outbox.getString('eval-outbox-v1')).toContain('photo-001');

      const relaunched = kit.relaunch();
      await relaunched.refresh();
      expect(relaunched.getState()).toMatchObject({ phase: 'resumable', total: 3, photosDone: 1, pending: 1 });
      await relaunched.continueRun();

      expect(sentIds()).toEqual(['photo-001', 'photo-002', 'photo-003']);
      expect(kit.downloaded).toEqual(['photo-001', 'photo-002', 'photo-003']); // photo 1 was not read twice
      expect(evalsMock.finishes).toHaveLength(1);
    });

    it('says the run is not open any more when the PC no longer has it, and keeps what it read', async () => {
      const kit = makeKit({ photos: 2, onSend: () => { evalsMock.run = undefined; } });

      await kit.runner.start();

      expect(kit.runner.getState().phase).toBe('error');
      expect(kit.runner.getState().message).toMatch(/isn't open any more/);
      expect(evalsMock.finishes).toEqual([]);
    });

    it('waits for the PC to take the finish too, and says so', async () => {
      const kit = makeKit({ photos: 1 });
      evalsMock.failNextFinish = 503;

      await kit.runner.start();

      expect(evalsMock.finishes).toHaveLength(2);
      expect(kit.sleeps).toEqual([1000]);
      expect(kit.runner.getState().phase).toBe('scoring');
    });
  });

  describe('leaving the app', () => {
    /** Sends the app to the background while the first photo's first engine is reading, as if the owner switched apps. */
    const leaveDuringFirstRead = (kit: () => ReturnType<typeof makeKit>, failRead = false) => {
      let left = false;
      return async () => {
        if (left) return;
        left = true;
        kit().app.go('background');
        if (failRead) throw new Error('the read was cut short');
      };
    };

    it('pauses when the app goes to the background, re-reads the photo that was being read when it comes back, and records no error', async () => {
      const kit: ReturnType<typeof makeKit> = makeKit({ photos: 2, onRead: leaveDuringFirstRead(() => kit, true) });

      const finished = kit.runner.start();
      await waitFor(() => kit.runner.getState().phase === 'paused');
      expect(kit.runner.getState().pause).toBe('background');
      expect(evalsMock.sent).toEqual([]); // the cut-short read was not sent
      kit.app.go('active');
      await finished;

      expect(kit.downloaded).toEqual(['photo-001', 'photo-001', 'photo-002']); // read again from the start
      expect(sentIds()).toEqual(['photo-001', 'photo-002']);
      expect(results().map((r) => r.outcome)).toEqual(Array(6).fill('ok')); // no engine error or timeout from being cut short
    });

    it('records nothing for a read that finished fine but spanned the background, because its timing is not the engine\'s', async () => {
      const kit: ReturnType<typeof makeKit> = makeKit({ photos: 1, onRead: leaveDuringFirstRead(() => kit, false) });

      const finished = kit.runner.start();
      await waitFor(() => kit.runner.getState().phase === 'paused');
      kit.app.go('active');
      await finished;

      expect(kit.downloaded).toEqual(['photo-001', 'photo-001']);
      expect(evalsMock.sent).toHaveLength(1);
      expect(results()).toHaveLength(3);
    });

    it('does not pick up while the app is still in the background', async () => {
      const kit: ReturnType<typeof makeKit> = makeKit({ photos: 2, onRead: leaveDuringFirstRead(() => kit) });

      const finished = kit.runner.start();
      await waitFor(() => kit.runner.getState().phase === 'paused');
      await new Promise((resolve) => setTimeout(resolve, 30));

      expect(kit.runner.getState().phase).toBe('paused');
      expect(kit.downloaded).toEqual(['photo-001']);
      kit.app.go('active');
      await finished;
      expect(sentIds()).toEqual(kit.ids);
    });

    it('stays paused when the owner had paused before leaving, and goes on when they resume', async () => {
      let paused = false;
      const kit: ReturnType<typeof makeKit> = makeKit({
        photos: 2,
        onRead: () => {
          if (!paused) {
            paused = true;
            kit.runner.pause();
          }
        },
      });

      const finished = kit.runner.start();
      await waitFor(() => kit.runner.getState().phase === 'paused');
      kit.app.go('background');
      kit.app.go('active');
      await new Promise((resolve) => setTimeout(resolve, 30));
      expect(kit.runner.getState()).toMatchObject({ phase: 'paused', pause: 'user' });

      kit.runner.resume();
      await finished;
      expect(sentIds()).toEqual(kit.ids);
    });

    it('does not send anything to the PC while the app is in the background, and sends it when it is back', async () => {
      let attemptsWhenAway = -1;
      const kit: ReturnType<typeof makeKit> = makeKit({
        photos: 1,
        onSleep: () => {
          kit.app.go('background');
          attemptsWhenAway = evalsMock.attempts.length;
        },
      });
      evalsMock.failSends = ['network'];

      const finished = kit.runner.start();
      await waitFor(() => kit.app.state === 'background');
      await new Promise((resolve) => setTimeout(resolve, 30));
      expect(attemptsWhenAway).toBe(1);
      expect(evalsMock.attempts).toHaveLength(1);
      kit.app.go('active');
      await finished;

      expect(sentIds()).toEqual(['photo-001']);
      expect(evalsMock.attempts).toHaveLength(2);
    });

    it('asks the PC again when the app comes back, in case it finished scoring while away', async () => {
      const kit = makeKit({ photos: 2, held: ['photo-001', 'photo-002'] });
      evalsMock.run!.state = 'finishing';
      evalsMock.scoredAfterPolls = 100;
      await kit.runner.refresh();
      expect(kit.runner.getState().phase).toBe('scoring');
      evalsMock.run!.state = 'done';

      kit.app.go('background');
      kit.app.go('active');

      await waitFor(() => kit.runner.getState().phase === 'done');
    });
  });

  describe('keeping the phone awake', () => {
    it('holds the screen awake while it reads and lets go once the run is with the PC', async () => {
      const kit = makeKit({ photos: 2 });
      await kit.runner.start();
      expect(kit.awake).toEqual({ on: 1, off: 1, held: false });
    });

    it('holds it while each photo is read', async () => {
      const heldAtRead: boolean[] = [];
      const kit: ReturnType<typeof makeKit> = makeKit({ photos: 2, onRead: () => void heldAtRead.push(kit.awake.held) });
      await kit.runner.start();
      expect(heldAtRead).toEqual([true, true]);
    });

    it('does not hold it while paused, and holds it again on resume', async () => {
      let paused = false;
      const kit: ReturnType<typeof makeKit> = makeKit({
        photos: 2,
        onRead: () => {
          if (!paused) {
            paused = true;
            kit.runner.pause();
          }
        },
      });
      const finished = kit.runner.start();
      await waitFor(() => kit.runner.getState().phase === 'paused');
      await waitFor(() => !kit.awake.held);
      expect(kit.awake).toEqual({ on: 1, off: 1, held: false });

      kit.runner.resume();
      await finished;
      expect(kit.awake).toEqual({ on: 2, off: 2, held: false });
    });

    it('does not hold it while the app is in the background', async () => {
      let left = false;
      const kit: ReturnType<typeof makeKit> = makeKit({
        photos: 1,
        onRead: () => {
          if (!left) {
            left = true;
            kit.app.go('background');
          }
        },
      });
      const finished = kit.runner.start();
      await waitFor(() => kit.runner.getState().phase === 'paused');
      await waitFor(() => !kit.awake.held);

      kit.app.go('active');
      await finished;
      expect(kit.awake).toEqual({ on: 2, off: 2, held: false });
    });

    it('does not hold it while it waits for the PC, and holds it again when the PC answers', async () => {
      const heldWhileWaiting: boolean[] = [];
      const kit: ReturnType<typeof makeKit> = makeKit({
        photos: 1,
        onSleep: async () => {
          await new Promise((resolve) => setTimeout(resolve, 0)); // the screen is let go of a moment after the runner says it is waiting
          heldWhileWaiting.push(kit.awake.held);
        },
      });
      evalsMock.failSends = ['network'];

      await kit.runner.start();

      expect(heldWhileWaiting).toEqual([false]);
      expect(kit.awake.on).toBe(2);
      expect(kit.awake.held).toBe(false);
    });

    it('does not hold it while the PC scores', async () => {
      const kit = makeKit({ photos: 1 });
      await kit.runner.start();
      expect(kit.runner.getState().phase).toBe('scoring');
      expect(kit.awake.held).toBe(false);
    });
  });

  describe('finishing', () => {
    it('asks the PC to finish once, after the last photo is on it, and says the PC is scoring', async () => {
      const kit = makeKit({ photos: 3 });
      await kit.runner.start();

      expect(evalsMock.finishes).toEqual([{}]);
      expect(evalsMock.sentAtFinish).toEqual([3]);
      expect(kit.runner.getState().phase).toBe('scoring');
    });

    it('asks the gentle way: it does not poll when nobody is watching, and does when someone is', async () => {
      const kit = makeKit({ photos: 1 });
      await kit.runner.start();
      expect(evalsMock.asked).toBe(0);
      expect(kit.sleeps).toEqual([]);

      const unwatch = kit.runner.watch();
      await waitFor(() => kit.runner.getState().phase === 'done');
      unwatch();
      expect(evalsMock.asked).toBe(1);
      expect(kit.sleeps).toEqual([SCORING_POLL_MS]);
    });

    it('stops asking when the screen closes', async () => {
      const gate = deferred();
      const kit = makeKit({ photos: 1, onSleep: () => gate.promise });
      evalsMock.scoredAfterPolls = 100;
      await kit.runner.start();
      const unwatch = kit.runner.watch();
      await waitFor(() => kit.sleeps.length === 1);
      unwatch();
      gate.resolve();
      await new Promise((resolve) => setTimeout(resolve, 30));

      expect(evalsMock.asked).toBe(0);
      expect(kit.sleeps).toEqual([SCORING_POLL_MS]);
    });

    it('keeps why the PC cannot score the run, so the screen can say so, and clears it when it can', async () => {
      const kit = makeKit({ photos: 2, held: ['photo-001', 'photo-002'] });
      evalsMock.run!.state = 'finishing';
      evalsMock.run!.failure = 'evals.results_not_writable';
      evalsMock.scoredAfterPolls = 100;

      await kit.runner.refresh();
      expect(kit.runner.getState()).toMatchObject({ phase: 'scoring', failure: 'evals.results_not_writable' });

      evalsMock.run!.failure = null;
      await kit.runner.refresh();
      expect(kit.runner.getState().failure).toBeUndefined();
    });

    it('shows the gate summary once the PC has scored', async () => {
      const kit = makeKit({ photos: 2 });
      evalsMock.scoredAfterPolls = 3;
      await runToEnd(kit);

      expect(kit.runner.getState().summary?.gate.verdict).toBe('pass');
      expect(kit.sleeps).toEqual([SCORING_POLL_MS, SCORING_POLL_MS, SCORING_POLL_MS]);
    });
  });

  describe('stopping early', () => {
    it('scores what the PC has: the readings of the photos read so far are already there, and the run is flagged partial', async () => {
      let stopped = false;
      const kit: ReturnType<typeof makeKit> = makeKit({
        photos: 4,
        onRead: () => {
          if (!stopped && kit.downloaded.length === 2) {
            stopped = true;
            kit.runner.stop();
          }
        },
      });
      await kit.runner.start();

      expect(sentIds()).toEqual(['photo-001']); // the photo that was being read when it stopped is not half-sent
      expect(evalsMock.finishes).toEqual([{ partial: true }]);
      expect(evalsMock.run?.partial).toBe(true);
      expect(kit.runner.getState()).toMatchObject({ phase: 'scoring', partial: true });
    });

    it('remembers that the owner stopped it, and that a run that was not stopped was not', async () => {
      let stopped = false;
      const kit: ReturnType<typeof makeKit> = makeKit({
        photos: 3,
        onRead: () => {
          if (!stopped && kit.downloaded.length === 2) {
            stopped = true;
            kit.runner.stop();
          }
        },
      });
      await kit.runner.start();
      expect(kit.runner.getState().stopped).toBe(true);

      const whole = makeKit({ photos: 1 });
      await whole.runner.start();
      expect(whole.runner.getState().stopped).toBe(false);
    });

    it('stops while paused', async () => {
      let paused = false;
      const kit: ReturnType<typeof makeKit> = makeKit({
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

      kit.runner.stop();
      await finished;

      expect(sentIds()).toEqual(['photo-001']);
      expect(evalsMock.finishes).toEqual([{ partial: true }]);
    });

    it('leaves nothing behind on the PC when nothing was read', async () => {
      const kit: ReturnType<typeof makeKit> = makeKit({
        photos: 2,
        onRead: () => {
          if (kit.runner.getState().phase === 'running') kit.runner.stop();
        },
      });

      await kit.runner.start();

      expect(evalsMock.finishes).toEqual([]);
      expect(evalsMock.run).toBeUndefined();
      expect(kit.runner.getState()).toMatchObject({ phase: 'idle', message: 'Nothing was read, so there was nothing to score.' });
    });

    it('still sends what it has read before it finishes', async () => {
      let stopped = false;
      const kit: ReturnType<typeof makeKit> = makeKit({
        photos: 3,
        onSleep: () => {
          if (!stopped) {
            stopped = true;
            kit.runner.stop();
          }
        },
      });
      evalsMock.failSends = ['network'];

      await kit.runner.start();

      expect(sentIds()).toEqual(['photo-001']);
      expect(evalsMock.finishes).toEqual([{ partial: true }]);
      expect(kit.downloaded).toEqual(['photo-001']);
    });
  });

  describe('pausing', () => {
    it('pauses between steps and continues when resumed', async () => {
      let paused = false;
      const kit: ReturnType<typeof makeKit> = makeKit({
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
      expect(kit.runner.getState().pause).toBe('user');
      expect(sentIds()).toEqual(['photo-001']);
      kit.runner.resume();
      await finished;
      expect(sentIds()).toEqual(kit.ids);
      expect(results()).toHaveLength(9);
      expect(kit.downloaded).toEqual(['photo-001', 'photo-002', 'photo-003']); // the paused photo was not read twice
    });
  });

  describe('locked: what leaves the phone and what the runner may touch', () => {
    it('sends only text, fields, timings and ids: no bytes, data URI or file URI, and the ids are the PC\'s', async () => {
      const kit = makeKit({ photos: 4 });
      await kit.runner.start();
      const text = JSON.stringify([evalsMock.sent, evalsMock.opened]);
      expect(text).not.toMatch(/data:/i);
      expect(text).not.toMatch(/file:\/\//i);
      expect(text).not.toMatch(/\/cache\//i);
      expect(text).not.toMatch(/\.(jpe?g|heic|png)/i);
      expect(text).not.toMatch(/[A-Za-z0-9+/]{200,}/); // no base64 blob
      expect(results().map((r) => r.photoId).filter((id, i, all) => all.indexOf(id) === i)).toEqual(evalsMock.photos.map((p) => p.id));
      expect(Object.keys(opened()[0]).sort()).toEqual(['configVersion', 'device']);
      expect(evalsMock.sent.every((s) => Object.keys(s).sort().join() === 'photoId,results')).toBe(true);
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
      const allowed = /^(POST \/api\/admin\/evals\/runs\/open|PUT \/api\/admin\/evals\/runs\/open\/photos\/[A-Za-z0-9_-]+|POST \/api\/admin\/evals\/runs\/open\/finish|GET \/api\/v1\/recognition\/config)$/;
      expect(requests.length).toBeGreaterThan(0);
      expect(requests.filter((r) => !allowed.test(r))).toEqual([]);
      expect(requests.filter((r) => r.startsWith('POST'))).toEqual(['POST /api/admin/evals/runs/open', 'POST /api/admin/evals/runs/open/finish']);
      expect(requests.filter((r) => r.startsWith('PUT'))).toHaveLength(3);
    });
  });

  describe('where each read sits, and the product code it printed', () => {
    const rows = (engine: string) => results().filter((r) => r.engine === engine);
    /** A box as the engines compute it (a box's far edge minus its near one), so to the last digit but a rounding error. */
    const boxesOf = (items: Result['items']) => items.map((i) => (i.box ? [i.box.x, i.box.y, i.box.width, i.box.height] : undefined));
    const expectBoxes = (items: Result['items'], expected: ([number, number, number, number] | undefined)[]) => {
      const boxes = boxesOf(items);
      expect(boxes.map((b) => b === undefined)).toEqual(expected.map((b) => b === undefined));
      boxes.forEach((b, i) => b?.forEach((value, k) => expect(value).toBeCloseTo(expected[i]?.[k] ?? Number.NaN, 9)));
    };

    it('sends the box of every read that has one, so reads of one release in different places stay apart', async () => {
      const kit = makeKit({ photos: 2 });
      await kit.runner.start();
      for (const row of results()) {
        for (const item of row.items) {
          expect(item.box).toBeDefined();
          const { x, y, width, height } = item.box ?? { x: -1, y: -1, width: 0, height: 0 };
          expect([x, y].every((v) => v >= 0 && v <= 1)).toBe(true);
          expect([width, height].every((v) => v > 0 && v <= 1)).toBe(true);
        }
      }
      // the scene's two lines: the model's items sit on them
      expectBoxes(rows('apple_model')[0].items, [
        [0.1, 0.1, 0.1, 0.5],
        [0.5, 0.1, 0.1, 0.5],
      ]);
    });

    it('pulls a box a hair outside the picture into it, and leaves out a box with no area', async () => {
      const kit = makeKit({
        photos: 2,
        scenes: {
          'photo-001': {
            text: [line('Title of photo-001', -0.004, 0.1, 0.1, 0.5), line('Other of photo-001', 0.5, -0.01, 0.1, 1.02)],
          },
          'photo-002': {
            text: [line('Title of photo-002', 0.1, 0.1, 0, 0.5), line('Other of photo-002', 0.5, 0.1, 0.1, 0.5)],
          },
        },
      });
      await kit.runner.start();
      const [first, second] = rows('apple_model');
      expectBoxes(first.items, [
        [0, 0.1, 0.1, 0.5],
        [0.5, 0, 0.1, 1],
      ]);
      expect(second.items.map((i) => i.title)).toEqual(['Title of photo-002', 'Other of photo-002']);
      expectBoxes(second.items, [undefined, [0.5, 0.1, 0.1, 0.5]]);
      expect(second.items[0]).not.toHaveProperty('box');
    });

    const cartridge: Partial<Scene> = {
      read: okRead([modelItem('Super Mario World', 'SUPER MARIO WORLD', 0, { kind: 'other' })]),
      extract: okRead([modelItem('Super Mario World', 'SUPER MARIO WORLD', 0, { kind: 'other' })]),
      text: [line('SUPER MARIO WORLD', 0.2, 0.2, 0.5, 0.1), line('SNS-8C-USA', 0.1, 0.85, 0.3, 0.04)],
    };

    it('sends a single cover\'s product code from all three engines, as a game', async () => {
      const kit = makeKit({ photos: 2, scenes: { 'photo-002': cartridge } });
      await kit.runner.start();
      for (const engine of ['apple_model', 'fm_text', 'vision_pipeline']) {
        const [, single] = rows(engine);
        expect([engine, single.photoId, single.items.map((i) => [i.kind, i.productCode])]).toEqual([engine, 'photo-002', [['game', 'SNS-8C-USA']]]);
      }
    });

    it('sends a shelf spine\'s product code from the Vision engine, on that spine only', async () => {
      const kit = makeKit({
        photos: 2,
        scenes: {
          'photo-001': {
            text: [line('GRAN TURISMO 3', 0.1, 0.1, 0.05, 0.6), line('SLUS-20034', 0.11, 0.72, 0.04, 0.03), line('EMMA', 0.5, 0.1, 0.05, 0.6)],
            rectangles: [
              { x: 0.08, y: 0.05, width: 0.09, height: 0.7 },
              { x: 0.48, y: 0.05, width: 0.09, height: 0.7 },
            ],
          },
        },
      });
      await kit.runner.start();
      const [shelf] = rows('vision_pipeline');
      expect(shelf.photoId).toBe('photo-001');
      expect(shelf.items.map((i) => i.productCode)).toEqual(['SLUS-20034', undefined]);
    });

    it('leaves the product code out of every read that printed none', async () => {
      const kit = makeKit({ photos: 4 });
      await kit.runner.start();
      for (const row of results()) {
        for (const item of row.items) expect(item).not.toHaveProperty('productCode');
      }
    });
  });
});

/** A promise the test settles itself, to hold a step of the run open. */
function deferred() {
  let resolve!: () => void;
  const promise = new Promise<void>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}

async function waitFor(condition: () => boolean) {
  for (let i = 0; i < 400 && !condition(); i += 1) await new Promise((resolve) => setTimeout(resolve, 5));
  expect(condition()).toBe(true);
}
