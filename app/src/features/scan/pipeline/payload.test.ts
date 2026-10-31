// The /identify payload sends the reading's engine to the server as named, and each read's position and product code with it.
import type { ReadItem } from '../recognition/types';
import { identifyItemOf, identifyRequestOf, picItemOf, toScanEngine } from './payload';
import type { PicSnap } from './pipelineStore';

describe('toScanEngine', () => {
  it.each(['apple_model', 'fm_text', 'vision_pipeline'] as const)('passes %s through to the server', (engine) => {
    expect(toScanEngine(engine)).toBe(engine);
  });
});

const read = (title: string, extra: Partial<ReadItem> = {}): ReadItem => ({
  localId: `id-${title}`,
  text: title,
  title,
  kind: 'film',
  order: 0,
  confidence: 'low',
  ...extra,
});

const snapOf = (...items: ReadItem[]): PicSnap => ({
  snapId: 'snap-1',
  libraryId: 'library-1',
  trigger: 'auto',
  shotKind: 'shelf_segment',
  segmentId: 'segment-1',
  createdAt: 1,
  thumbUri: 'thumb',
  phase: 'read',
  sent: false,
  items: items.map(picItemOf),
  unreadableCount: 0,
  engine: 'vision_pipeline',
});

describe('where a read sits', () => {
  it('goes with the read, so five spines titled HIGHLANDER stay five items', () => {
    const spine = (x: number) => read('HIGHLANDER', { localId: `spine-${x}`, box: { x, y: 0.1, width: 0.05, height: 0.7 } });
    const request = identifyRequestOf(snapOf(spine(0.1), spine(0.3), spine(0.5), spine(0.7), spine(0.9)));
    expect(request.items?.map((i) => i.box)).toEqual([
      { x: 0.1, y: 0.1, width: 0.05, height: 0.7 },
      { x: 0.3, y: 0.1, width: 0.05, height: 0.7 },
      { x: 0.5, y: 0.1, width: 0.05, height: 0.7 },
      { x: 0.7, y: 0.1, width: 0.05, height: 0.7 },
      { x: 0.9, y: 0.1, width: 0.05, height: 0.7 },
    ]);
  });

  it('is stored with the item the pipeline keeps, which is exactly what is sent', () => {
    const item = read('Alien', { box: { x: 0.2, y: 0.3, width: 0.1, height: 0.4 } });
    expect(picItemOf(item).payload.box).toEqual({ x: 0.2, y: 0.3, width: 0.1, height: 0.4 });
    expect(identifyRequestOf(snapOf(item)).items?.[0]).toEqual({ ...picItemOf(item).payload, segmentId: 'segment-1' });
  });

  it('is pulled into 0..1 when Vision\'s box overshoots the picture a hair', () => {
    const item = read('Alien', { box: { x: -0.004, y: 0.3, width: 1.006, height: 0.4 } });
    expect(identifyItemOf(item).box).toEqual({ x: 0, y: 0.3, width: 1, height: 0.4 });
  });

  it.each([
    ['there is none', undefined],
    ['it has no width', { x: 0.1, y: 0.1, width: 0, height: 0.5 }],
    ['it has no height', { x: 0.1, y: 0.1, width: 0.5, height: 0 }],
    ['a value is not a number', { x: Number.NaN, y: 0.1, width: 0.5, height: 0.5 }],
    ['a value is infinite', { x: 0.1, y: 0.1, width: Number.POSITIVE_INFINITY, height: 0.5 }],
  ])('is left out when %s', (_why, box) => {
    const item = read('Alien', box ? { box } : {});
    expect(identifyItemOf(item)).not.toHaveProperty('box');
    expect(identifyRequestOf(snapOf(item)).items?.[0]).not.toHaveProperty('box');
  });

  it('keeps the segment id the request stamps on each item, beside the box', () => {
    const item = read('Alien', { box: { x: 0.2, y: 0.3, width: 0.1, height: 0.4 } });
    expect(identifyRequestOf(snapOf(item)).items?.[0]).toMatchObject({ segmentId: 'segment-1', box: { x: 0.2 } });
  });
});

describe('a game\'s product code', () => {
  it('goes with the read when it has one', () => {
    const item = read('Super Mario World', { kind: 'game', productCode: 'SNS-8C-USA' });
    expect(identifyItemOf(item).productCode).toBe('SNS-8C-USA');
    expect(identifyRequestOf(snapOf(item)).items?.[0]).toMatchObject({ kind: 'game', productCode: 'SNS-8C-USA' });
  });

  it('is not in the request at all when the read has none', () => {
    const item = read('Alien');
    expect(identifyItemOf(item)).not.toHaveProperty('productCode');
    expect(identifyRequestOf(snapOf(item)).items?.[0]).not.toHaveProperty('productCode');
  });

  it('is sent beside the box', () => {
    const item = read('Gran Turismo 3', { kind: 'other', productCode: 'SLUS-20034', box: { x: 0.1, y: 0.1, width: 0.05, height: 0.7 } });
    expect(identifyRequestOf(snapOf(item)).items?.[0]).toMatchObject({ productCode: 'SLUS-20034', box: { x: 0.1, y: 0.1, width: 0.05, height: 0.7 } });
  });
});

describe('a read only the picture model made', () => {
  it('goes with the read, so the server keeps it for review instead of adding it', () => {
    const item = read('Modern Comfort Food', { kind: 'book', unconfirmed: true });
    expect(identifyItemOf(item).unconfirmed).toBe(true);
    expect(identifyRequestOf(snapOf(item)).items?.[0]).toMatchObject({ title: 'Modern Comfort Food', unconfirmed: true });
    expect(picItemOf(item).payload.unconfirmed).toBe(true);
  });

  it('is not in the request at all for a read nothing doubts', () => {
    const item = read('Alien');
    expect(identifyItemOf(item)).not.toHaveProperty('unconfirmed');
    expect(identifyRequestOf(snapOf(item)).items?.[0]).not.toHaveProperty('unconfirmed');
  });

  it('is marked on its own item only, in a snap with other reads', () => {
    const request = identifyRequestOf(snapOf(read('Alien'), read('Ubik', { unconfirmed: true }), read('Dune')));
    expect(request.items?.map((i) => i.unconfirmed)).toEqual([undefined, true, undefined]);
  });
});
