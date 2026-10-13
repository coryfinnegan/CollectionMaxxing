import schema from '../../contract.schema.json';
import { FakeBarcodeSource } from '../FakeBarcodeSource';
import { FakeVisionEngine } from '../FakeVisionEngine';
import { fixtures } from '../fixtures';
import type { DetectedCode } from '../types';

type Schema = Record<string, unknown>;
const defs = schema.$defs as Record<string, Schema>;

/** Minimal validator for the subset of JSON schema used in contract.schema.json. */
function problems(value: unknown, node: Schema, path: string): string[] {
  if (node.$ref) return problems(value, defs[String(node.$ref).split('/').pop()!], path);
  if (node.enum) return (node.enum as unknown[]).includes(value) ? [] : [`${path}: not in enum`];
  if (node.type === 'number') return typeof value === 'number' ? [] : [`${path}: not a number`];
  if (node.type === 'string') return typeof value === 'string' ? [] : [`${path}: not a string`];
  if (node.type === 'object') {
    if (typeof value !== 'object' || value === null) return [`${path}: not an object`];
    const props = node.properties as Record<string, Schema>;
    const extra = Object.keys(value).filter((k) => !(k in props)).map((k) => `${path}.${k}: unexpected`);
    const missing = (node.required as string[]).filter((k) => !(k in value)).map((k) => `${path}.${k}: missing`);
    const nested = Object.entries(props).flatMap(([k, v]) => (k in value ? problems((value as Record<string, unknown>)[k], v, `${path}.${k}`) : []));
    return [...extra, ...missing, ...nested];
  }
  return [];
}

const all = [
  ['recognizeText', fixtures.recognizeText, 'TextLine', true],
  ['detectRectangles', fixtures.detectRectangles, 'Rect', true],
  ['detectBarcodes', fixtures.detectBarcodes, 'DetectedCode', true],
  ['featurePrint', fixtures.featurePrint, 'FeaturePrint', false],
] as const;

describe('fixtures', () => {
  it.each(all)('%s has at least 3 hand-written records that match the contract schema', (_name, records, def, isList) => {
    expect(records.length).toBeGreaterThanOrEqual(3);
    for (const record of records as readonly { output: unknown }[]) {
      expect(problems(record, defs.FixtureRecord, 'record')).toEqual([]);
      const outputs = isList ? (record.output as unknown[]) : [record.output];
      for (const output of outputs) expect(problems(output, defs[def], 'output')).toEqual([]);
    }
  });

  it('barcode scenes and distances have at least 3 records', () => {
    expect(fixtures.barcodeScenes.length).toBeGreaterThanOrEqual(3);
    expect(fixtures.featurePrintDistance.length).toBeGreaterThanOrEqual(3);
  });
});

describe('FakeVisionEngine', () => {
  const engine = new FakeVisionEngine();

  it('is available', async () => {
    await expect(engine.isAvailable()).resolves.toBe(true);
  });

  it('returns the fixture text for a matching image', async () => {
    const lines = await engine.recognizeText('fixture://book-cover-1');
    expect(lines.map((l) => l.text)).toEqual(['Dune', 'Frank Herbert', 'Ace Books']);
  });

  it('returns fixture rectangles and barcodes for matching images', async () => {
    await expect(engine.detectRectangles('fixture://shelf-of-three-dvds')).resolves.toHaveLength(3);
    const codes = await engine.detectBarcodes('fixture://paperback-back-1');
    expect(codes.map((c) => c.value)).toEqual(['9780441172719', '51299']);
  });

  it('tells a crop from the whole picture when printing', async () => {
    const whole = await engine.featurePrint('fixture://dvd-spine-1');
    const crop = await engine.featurePrint('fixture://dvd-spine-1', { x: 0.2, y: 0.1, width: 0.1, height: 0.6 });
    expect(whole.data).not.toEqual(crop.data);
  });

  it.each([
    ['recognizeText', (e: FakeVisionEngine) => e.recognizeText('fixture://nope')],
    ['detectRectangles', (e: FakeVisionEngine) => e.detectRectangles('fixture://nope')],
    ['detectBarcodes', (e: FakeVisionEngine) => e.detectBarcodes('fixture://nope')],
    ['featurePrint', (e: FakeVisionEngine) => e.featurePrint('fixture://nope')],
  ])('%s fails with a clear error for an unknown input', async (name, call) => {
    await expect(call(engine)).rejects.toThrow(new RegExp(`no ${name} fixture.*fixture://nope`));
  });

  it('featurePrintDistance is zero for identical prints', async () => {
    const p = await engine.featurePrint('fixture://dvd-spine-1');
    await expect(engine.featurePrintDistance(p, p)).resolves.toBe(0);
  });

  it('featurePrintDistance is symmetric', async () => {
    const a = await engine.featurePrint('fixture://dvd-spine-1');
    const b = await engine.featurePrint('fixture://book-cover-1');
    const ab = await engine.featurePrintDistance(a, b);
    const ba = await engine.featurePrintDistance(b, a);
    expect(ab).toBeGreaterThan(0);
    expect(ba).toBe(ab);
  });

  it('featurePrintDistance rejects prints it has no fixture for', async () => {
    const a = { revision: 2, data: 'AQIDBA==' };
    await expect(engine.featurePrintDistance(a, { revision: 2, data: 'AAAA' })).rejects.toThrow(/no featurePrintDistance fixture/);
  });
});

describe('FakeBarcodeSource', () => {
  it('replays a scene to the subscriber, one batch at a time', () => {
    const source = new FakeBarcodeSource();
    const batches: DetectedCode[][] = [];
    source.start((codes) => batches.push(codes));
    source.replay('two-books-then-qr');
    expect(batches.map((b) => b.length)).toEqual([1, 2, 1]);
    expect(batches[2][0].symbology).toBe('qr');
  });

  it('stops delivering after unsubscribe', () => {
    const source = new FakeBarcodeSource();
    const onCodes = jest.fn();
    const stop = source.start(onCodes);
    stop();
    source.replay('one-dvd');
    expect(onCodes).not.toHaveBeenCalled();
  });

  it('delivers hand-injected codes (the dev menu path)', () => {
    const source = new FakeBarcodeSource();
    const onCodes = jest.fn();
    source.start(onCodes);
    const code: DetectedCode = { value: '1', symbology: 'ean-8', bounds: { x: 0, y: 0, width: 1, height: 1 } };
    source.emit([code]);
    expect(onCodes).toHaveBeenCalledWith([code]);
  });

  it('fails with a clear error for an unknown scene', () => {
    expect(() => new FakeBarcodeSource().replay('nope')).toThrow(/no barcode scene fixture.*nope/);
  });
});
