import schema from '../../contract.schema.json';
import { FakeBarcodeSource } from '../FakeBarcodeSource';
import { FakeVisionEngine } from '../FakeVisionEngine';
import { fixtures } from '../fixtures';
import type { DetectedCode, ModelCapabilities, ModelItem } from '../types';

type Schema = Record<string, unknown>;
const defs = schema.$defs as Record<string, Schema>;

/** Minimal validator for the subset of JSON schema used in contract.schema.json. */
function problems(value: unknown, node: Schema, path: string): string[] {
  if (node.$ref) return problems(value, defs[String(node.$ref).split('/').pop()!], path);
  if (node.enum) return (node.enum as unknown[]).includes(value) ? [] : [`${path}: not in enum`];
  if (Array.isArray(node.type)) {
    const kinds = node.type as string[];
    const actual = value === null ? 'null' : Number.isInteger(value) ? 'integer' : typeof value;
    return kinds.includes(actual) ? [] : [`${path}: not one of ${kinds}`];
  }
  if (node.type === 'boolean') return typeof value === 'boolean' ? [] : [`${path}: not a boolean`];
  if (node.type === 'integer') return Number.isInteger(value) ? [] : [`${path}: not an integer`];
  if (node.type === 'array') {
    if (!Array.isArray(value)) return [`${path}: not an array`];
    return value.flatMap((v, i) => problems(v, node.items as Schema, `${path}[${i}]`));
  }
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
  ['readItems', fixtures.readItems, 'ReadItemsResult', false],
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

describe('model fixtures', () => {
  it('are hand-written, cover the five scenes and every non-ok outcome, and hold no personal data', () => {
    expect(fixtures.readItems.every((r) => r.engine === 'hand-written')).toBe(true);
    const uris = fixtures.readItems.map((r) => r.input.imageUri);
    for (const scene of ['book-cover-1', 'n64-cart-1', 'vhs-sleeve-1', 'shelf-of-three-spines', 'refused-photo']) {
      expect(uris).toContain(`fixture://${scene}`);
    }
    const outcomes = new Set(fixtures.readItems.map((r) => r.output.outcome));
    expect([...outcomes].sort()).toEqual(['contextExceeded', 'declined', 'ok', 'timeout', 'unavailable']);
    const shelf = fixtures.readItems.find((r) => r.input.imageUri.endsWith('shelf-of-three-spines'))!;
    expect(shelf.output.items).toHaveLength(3);
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

describe('FakeVisionEngine model reading', () => {
  const options = { prompt: 'read the items', timeoutMs: 5000 };

  it('streams each item to onItem in order before it resolves', async () => {
    const seen: ModelItem[] = [];
    let resolved = false;
    const pending = new FakeVisionEngine().readItems('fixture://shelf-of-three-spines', options, (i) => {
      expect(resolved).toBe(false);
      seen.push(i);
    });
    const result = await pending;
    resolved = true;
    expect(seen.map((i) => i.order)).toEqual([0, 1, 2]);
    expect(result.items).toEqual(seen);
    expect(result).toMatchObject({ outcome: 'ok', unreadableCount: 1 });
  });

  it('streams in order even when the recorded items are out of order', async () => {
    const [record] = fixtures.readItems.filter((r) => r.input.imageUri.endsWith('shelf-of-three-spines'));
    const shuffled = { ...record, output: { ...record.output, items: [...record.output.items].reverse() } };
    const engine = new FakeVisionEngine({ ...fixtures, readItems: [shuffled] });
    const orders: number[] = [];
    await engine.readItems('fixture://shelf-of-three-spines', options, (i) => orders.push(i.order));
    expect(orders).toEqual([0, 1, 2]);
  });

  it.each([
    ['refused-photo', 'declined'],
    ['slow-shelf', 'timeout'],
    ['huge-shelf', 'contextExceeded'],
    ['no-model', 'unavailable'],
  ])('replays %s as the %s outcome without throwing or streaming', async (scene, outcome) => {
    const onItem = jest.fn();
    const result = await new FakeVisionEngine().readItems(`fixture://${scene}`, options, onItem);
    expect(result.outcome).toBe(outcome);
    expect(result.items).toEqual([]);
    expect(onItem).not.toHaveBeenCalled();
  });

  it('works without an onItem callback', async () => {
    await expect(new FakeVisionEngine().readItems('fixture://book-cover-1', options)).resolves.toMatchObject({ outcome: 'ok' });
  });

  it('fails loudly for a picture it has no fixture for', async () => {
    await expect(new FakeVisionEngine().readItems('fixture://nope', options)).rejects.toThrow(/no readItems fixture.*fixture:\/\/nope/);
  });

  it('reports a capable model by default and is configurable per test', async () => {
    const engine = new FakeVisionEngine();
    await expect(engine.modelCapabilities()).resolves.toMatchObject({ available: true, vision: true, guidedGeneration: true });
    const off: ModelCapabilities = { available: false, vision: false, guidedGeneration: false, contextSize: null, reason: 'appleIntelligenceOff' };
    engine.setCapabilities(off);
    await expect(engine.modelCapabilities()).resolves.toEqual(off);
    const notReady: ModelCapabilities = { ...off, reason: 'modelNotReady' };
    await expect(new FakeVisionEngine(fixtures, { capabilities: notReady }).modelCapabilities()).resolves.toEqual(notReady);
  });

  it('reports device status, configurable', async () => {
    const engine = new FakeVisionEngine();
    await expect(engine.deviceStatus()).resolves.toMatchObject({ thermalState: 'nominal' });
    engine.setDeviceStatus({ thermalState: 'serious', osVersion: '26.1' });
    await expect(engine.deviceStatus()).resolves.toEqual({ thermalState: 'serious', osVersion: '26.1' });
  });

  it('recognizeText defaults to orientation 0', async () => {
    const lines = await new FakeVisionEngine().recognizeText('fixture://book-cover-1');
    expect(lines.map((l) => l.text)).toEqual(['Dune', 'Frank Herbert', 'Ace Books']);
  });

  it('recognizeText returns the lines of each orientation, in order, without de-duplicating', async () => {
    const lines = await new FakeVisionEngine().recognizeText('fixture://book-cover-1', { orientations: [0, 90, 270] });
    expect(lines.map((l) => l.text)).toEqual(['Dune', 'Frank Herbert', 'Ace Books', 'ECUD', 'Dune']);
  });

  it('recognizeText fails loudly for an orientation with no fixture', async () => {
    await expect(new FakeVisionEngine().recognizeText('fixture://dvd-spine-1', { orientations: [90] })).rejects.toThrow(/no recognizeText fixture/);
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
