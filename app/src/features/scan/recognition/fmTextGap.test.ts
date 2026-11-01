// Spike #505: the FIXED photo set comparing the current fallback (VisionPipelineEngine: Vision OCR + TypeScript
// splitting, no model) against FM-text extraction (FmTextEngine: the same OCR text typed by the on-device model).
//
// How to read the numbers below:
// - Fallback numbers are MEASURED: real engine output over these OCR scenes on this PC.
// - Extraction numbers are CONDITIONAL: the `extract` fixtures are hand-written "correct model output", so they
//   validate the extraction pipeline (prompt → parse → map → boxes), NOT the model's own accuracy. The model's
//   accuracy is unmeasurable on this PC (no iPhone, no Foundation Models) and needs the phone run in the report.
// - Titles are fictional examples, never real collection data.
import type { WorkKind } from '@/api/generated/models';
import { DEFAULT_RECOGNITION_SETTINGS, extractionPromptOf } from './defaults';
import { FmTextEngine, ocrTextOf } from './FmTextEngine';
import { line, modelItem, okRead, RecordingVision, testIds, type Scene } from './fakes';
import { cleanLines, normalize } from './lines';
import type { ReadItem, SnapReading, Still } from './types';
import { VisionPipelineEngine } from './VisionPipelineEngine';

interface TruthItem {
  title: string;
  kind: WorkKind;
  creator?: string;
}

interface SpikeScene extends Scene {
  truth: TruthItem[];
}

const COVER_TEXT = [
  line('Dune', 0.3, 0.2, 0.4, 0.1, 1),
  line('Frank Herbert', 0.3, 0.8, 0.4, 0.06, 0.93),
  line('Ace Books', 0.4, 0.92, 0.2, 0.03, 0.71),
  line('"A masterpiece" — Review', 0.2, 0.5, 0.6, 0.05, 0.6),
  line('WINNER HUGO AWARD', 0.25, 0.6, 0.5, 0.04, 0.55),
];

const COVER_TRUTH: TruthItem[] = [{ title: 'Dune', kind: 'book', creator: 'Frank Herbert' }];

const COVER_EXTRACT = okRead([
  modelItem('Dune', 'Dune Frank Herbert Ace Books', 0, { creator: 'Frank Herbert', kind: 'book', confidence: 'high' }),
]);

const SET: Record<string, SpikeScene> = {
  // A clean book cover: one item in truth, several text lines on the page.
  'spike://cover-clean': { text: [...COVER_TEXT], truth: COVER_TRUTH, extract: COVER_EXTRACT },
  // The #504 SHAPE (not its photo): the same cover, but six detected rectangles shard the OCR into pseudo-items.
  'spike://cover-rectangles': {
    text: [...COVER_TEXT, line('A NOVEL', 0.06, 0.055, 0.08, 0.03, 0.5)],
    rectangles: [
      { x: 0.25, y: 0.15, width: 0.5, height: 0.2 },
      { x: 0.15, y: 0.47, width: 0.7, height: 0.12 },
      { x: 0.2, y: 0.56, width: 0.6, height: 0.1 },
      { x: 0.25, y: 0.76, width: 0.5, height: 0.12 },
      { x: 0.35, y: 0.9, width: 0.3, height: 0.08 },
      { x: 0.05, y: 0.05, width: 0.1, height: 0.05 },
    ],
    truth: COVER_TRUTH,
    extract: COVER_EXTRACT,
  },
  // Three spines on a shelf, read as columns.
  'spike://shelf-three': {
    text: [
      line('DUNE', 0.05, 0.1, 0.08, 0.5, 0.9),
      line('HERBERT', 0.05, 0.65, 0.08, 0.25, 0.8),
      line('THE MATRIX', 0.25, 0.1, 0.08, 0.5, 0.95),
      line('DVD', 0.25, 0.65, 0.08, 0.15, 0.85),
      line('MARIO 64', 0.45, 0.1, 0.08, 0.5, 0.9),
      line('N64', 0.45, 0.65, 0.08, 0.15, 0.75),
    ],
    truth: [
      { title: 'Dune', kind: 'book', creator: 'Frank Herbert' },
      { title: 'The Matrix', kind: 'film' },
      { title: 'Mario 64', kind: 'game' },
    ],
    extract: okRead([
      modelItem('Dune', 'DUNE HERBERT', 0, { creator: 'Frank Herbert', kind: 'book', confidence: 'medium' }),
      modelItem('The Matrix', 'THE MATRIX DVD', 1, { kind: 'dvd', confidence: 'high' }),
      modelItem('Mario 64', 'MARIO 64 N64', 2, { kind: 'game', platform: 'Nintendo 64', confidence: 'medium' }),
    ]),
  },
  // One DVD spine plus its studio line.
  'spike://spine-single': {
    text: [line('THE MATRIX', 0.2, 0.1, 0.1, 0.6, 0.98), line('WARNER HOME VIDEO', 0.2, 0.75, 0.1, 0.2, 0.85)],
    truth: [{ title: 'The Matrix', kind: 'film' }],
    extract: okRead([modelItem('The Matrix', 'THE MATRIX WARNER HOME VIDEO', 0, { kind: 'vhs', confidence: 'medium' })]),
  },
  // A loose game cartridge label: text names the platform, but only a picture shows loose-vs-boxed.
  'spike://cart-loose': {
    text: [
      line('SUPER MARIO 64', 0.2, 0.15, 0.6, 0.12, 0.92),
      line('Nintendo', 0.35, 0.35, 0.3, 0.08, 0.88),
      line("Player's Choice", 0.3, 0.7, 0.4, 0.06, 0.6),
    ],
    truth: [{ title: 'Super Mario 64', kind: 'game' }],
    extract: okRead([
      modelItem('Super Mario 64', "SUPER MARIO 64 Nintendo Player's Choice", 0, {
        kind: 'game',
        platform: 'Nintendo 64',
        editionMarks: "Player's Choice",
        confidence: 'high',
      }),
    ]),
  },
  // Nothing to read.
  'spike://blank': { text: [], truth: [] },
};

const IDS = Object.keys(SET);
const stillOf = (uri: string): Still => ({ uri, width: 3000, height: 4000 });

async function readAll(
  make: (vision: RecordingVision) => { read(still: Still, onItem: (item: ReadItem) => void): Promise<SnapReading> },
): Promise<Record<string, SnapReading>> {
  const vision = new RecordingVision(SET);
  const engine = make(vision);
  const out: Record<string, SnapReading> = {};
  for (const uri of IDS) out[uri] = await engine.read(stillOf(uri), () => {});
  return out;
}

interface SceneScore {
  read: number;
  truth: number;
  titleHits: number;
  kindHits: number;
  creatorHits: number;
  creatorScored: number;
  high: number;
  boxed: number;
  unreadable: number;
}

/** Greedy title match in reading order, each truth item claimed once (the EvalScorer shape, harness-local). */
function score(reading: SnapReading, truth: TruthItem[]): SceneScore {
  const claimed = new Set<number>();
  let titleHits = 0;
  let kindHits = 0;
  let creatorHits = 0;
  let creatorScored = 0;
  for (const item of reading.items) {
    const hit = truth.findIndex((t, i) => !claimed.has(i) && normalize(t.title) === normalize(item.title));
    if (hit === -1) continue;
    claimed.add(hit);
    titleHits += 1;
    if (truth[hit].kind === item.kind) kindHits += 1;
    creatorScored += 1;
    // A correctly absent creator counts: not guessing is the right call when truth names none.
    if ((truth[hit].creator ?? undefined) === (item.creator ?? undefined)) creatorHits += 1;
  }
  return {
    read: reading.items.length,
    truth: truth.length,
    titleHits,
    kindHits,
    creatorHits,
    creatorScored,
    high: reading.items.filter((i) => i.confidence === 'high').length,
    boxed: reading.items.filter((i) => i.box !== undefined).length,
    unreadable: reading.unreadableCount,
  };
}

const total = (scores: SceneScore[]): SceneScore =>
  scores.reduce(
    (a, b) => ({
      read: a.read + b.read,
      truth: a.truth + b.truth,
      titleHits: a.titleHits + b.titleHits,
      kindHits: a.kindHits + b.kindHits,
      creatorHits: a.creatorHits + b.creatorHits,
      creatorScored: a.creatorScored + b.creatorScored,
      high: a.high + b.high,
      boxed: a.boxed + b.boxed,
      unreadable: a.unreadable + b.unreadable,
    }),
    { read: 0, truth: 0, titleHits: 0, kindHits: 0, creatorHits: 0, creatorScored: 0, high: 0, boxed: 0, unreadable: 0 },
  );

describe('spike #505 fixed set', () => {
  it('is fixed: six scenes, seven truth items', () => {
    expect(IDS).toEqual([
      'spike://cover-clean',
      'spike://cover-rectangles',
      'spike://shelf-three',
      'spike://spine-single',
      'spike://cart-loose',
      'spike://blank',
    ]);
    expect(IDS.map((id) => SET[id].truth.length)).toEqual([1, 1, 3, 1, 1, 0]);
  });

  it('fallback: a clean cover reads as one low-confidence guess, now of the biggest lettering', async () => {
    const readings = await readAll((vision) => new VisionPipelineEngine({ vision, settings: () => DEFAULT_RECOGNITION_SETTINGS, newId: testIds }));
    const reading = readings['spike://cover-clean'];
    expect(reading.items).toHaveLength(1);
    expect(reading.unreadableCount).toBe(0);
    const [item] = reading.items;
    // The shelf rule takes the biggest lettering on the piece, so the review quote no longer outruns the title (#617).
    expect(item.title).toBe('Dune');
    expect(item.creator).toBe('Frank Herbert');
    expect(item.kind).toBe('other');
    expect(item.confidence).toBe('low');
  });

  it('fallback: rectangles shard a cover into pseudo-items (the #504 shape)', async () => {
    const readings = await readAll((vision) => new VisionPipelineEngine({ vision, settings: () => DEFAULT_RECOGNITION_SETTINGS, newId: testIds }));
    const reading = readings['spike://cover-rectangles'];
    expect(reading.items.map((i) => i.title)).toEqual([
      'A NOVEL',
      '"A masterpiece" — Review',
      'WINNER HUGO AWARD',
      'Dune',
      'Frank Herbert',
      'Ace Books',
    ]);
    expect(reading.unreadableCount).toBe(0);
    expect(reading.items.every((i) => i.kind === 'other' && i.confidence === 'low')).toBe(true);
  });

  it('fallback: spines split into columns but titles and creators are heuristic guesses', async () => {
    const readings = await readAll((vision) => new VisionPipelineEngine({ vision, settings: () => DEFAULT_RECOGNITION_SETTINGS, newId: testIds }));
    const shelf = readings['spike://shelf-three'];
    expect(shelf.items.map((i) => i.title)).toEqual(['HERBERT', 'THE MATRIX', 'MARIO 64']);
    expect(shelf.items.map((i) => i.creator)).toEqual(['DUNE', 'DVD', undefined]);
    const spine = readings['spike://spine-single'];
    expect(spine.items.map((i) => i.title)).toEqual(['WARNER HOME VIDEO']);
    expect(spine.items.map((i) => i.creator)).toEqual(['THE MATRIX']);
    const cart = readings['spike://cart-loose'];
    expect(cart.items.map((i) => i.title)).toEqual(['SUPER MARIO 64']);
    expect(cart.items.map((i) => i.creator)).toEqual(['Nintendo']);
    const blank = readings['spike://blank'];
    expect(blank.items).toEqual([]);
    expect(blank.unreadableCount).toBe(0);
  });

  it('extraction: typed fields survive the pipeline end to end (conditional on the model output)', async () => {
    const readings = await readAll((vision) => new FmTextEngine({ vision, settings: () => DEFAULT_RECOGNITION_SETTINGS, newId: testIds }));
    expect(readings['spike://cover-clean'].items).toHaveLength(1);
    expect(readings['spike://cover-rectangles'].items).toHaveLength(1);
    expect(readings['spike://shelf-three'].items).toHaveLength(3);
    expect(readings['spike://spine-single'].items).toHaveLength(1);
    expect(readings['spike://cart-loose'].items).toHaveLength(1);
    expect(readings['spike://blank'].items).toHaveLength(0);
    const shelf = readings['spike://shelf-three'].items;
    expect(shelf.map((i) => [i.title, i.kind, i.confidence])).toEqual([
      ['Dune', 'book', 'medium'],
      ['The Matrix', 'film', 'high'],
      ['Mario 64', 'game', 'medium'],
    ]);
    expect(shelf[0].creator).toBe('Frank Herbert');
    expect(shelf[1].format).toBe('dvd');
    expect(shelf[2].platform).toBe('Nintendo 64');
    expect(shelf.every((i) => i.box !== undefined)).toBe(true);
    // Text alone cannot see the cartridge: completeness stays absent where the picture reader would fill it.
    expect(readings['spike://cart-loose'].items[0].completeness).toBeUndefined();
    expect(readings['spike://cart-loose'].items[0].edition).toBe("Player's Choice");
  });

  it('measured gap: fallback vs extraction across the fixed set', async () => {
    const fallback = await readAll(
      (vision) => new VisionPipelineEngine({ vision, settings: () => DEFAULT_RECOGNITION_SETTINGS, newId: testIds }),
    );
    const extraction = await readAll(
      (vision) => new FmTextEngine({ vision, settings: () => DEFAULT_RECOGNITION_SETTINGS, newId: testIds }),
    );
    const rows = IDS.map((id) => ({ id, fallback: score(fallback[id], SET[id].truth), extraction: score(extraction[id], SET[id].truth) }));
    const f = total(rows.map((r) => r.fallback));
    const e = total(rows.map((r) => r.extraction));

    // eslint-disable-next-line no-console
    console.log(
      ['scene | engine | read/truth | titles | kinds | creators | high-conf | boxed | unreadable']
        .concat(
          rows.flatMap((r) => [
            `${r.id} | fallback | ${r.fallback.read}/${r.fallback.truth} | ${r.fallback.titleHits} | ${r.fallback.kindHits} | ${r.fallback.creatorHits}/${r.fallback.creatorScored} | ${r.fallback.high} | ${r.fallback.boxed} | ${r.fallback.unreadable}`,
            `${r.id} | extraction* | ${r.extraction.read}/${r.extraction.truth} | ${r.extraction.titleHits} | ${r.extraction.kindHits} | ${r.extraction.creatorHits}/${r.extraction.creatorScored} | ${r.extraction.high} | ${r.extraction.boxed} | ${r.extraction.unreadable}`,
          ]),
        )
        .concat([
          `TOTAL | fallback | ${f.read}/${f.truth} | ${f.titleHits} | ${f.kindHits} | ${f.creatorHits}/${f.creatorScored} | ${f.high} | ${f.boxed} | ${f.unreadable}`,
          `TOTAL | extraction* | ${e.read}/${e.truth} | ${e.titleHits} | ${e.kindHits} | ${e.creatorHits}/${e.creatorScored} | ${e.high} | ${e.boxed} | ${e.unreadable}`,
          '* conditional on hand-written correct model output; the model itself is unmeasured on this PC.',
        ])
        .join('\n'),
    );

    // The fallback's titleHits rose from 4 to 5 and its creatorHits from 1 to 2 with the spine-title rule (#617).
    expect([f.read, f.truth, f.titleHits, f.kindHits, f.high, f.unreadable]).toEqual([12, 7, 5, 0, 0, 0]);
    expect([e.read, e.truth, e.titleHits, e.kindHits, e.high, e.unreadable]).toEqual([7, 7, 7, 7, 4, 0]);
    expect([f.creatorHits, f.creatorScored]).toEqual([2, 5]);
    expect([e.creatorHits, e.creatorScored]).toEqual([7, 7]);
  });

  it('the extraction input fits the 4K context with room to spare', () => {
    const promptChars = extractionPromptOf(DEFAULT_RECOGNITION_SETTINGS).length;
    const ocrChars = IDS.map((id) => ocrTextOf(cleanLines(SET[id].text ?? [], DEFAULT_RECOGNITION_SETTINGS.local)).length);
    const worst = Math.max(...ocrChars);
    // eslint-disable-next-line no-console
    console.log(`extraction input: prompt ${promptChars} chars, largest scene OCR ${worst} chars, total ${promptChars + worst} chars vs a 4096-token budget`);
    expect(promptChars + worst).toBeLessThan(4096);
  });
});
