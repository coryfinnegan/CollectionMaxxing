// Where a row was found and how the walk draws it (#833): the photo, the lit box, the band's window and the upright strip.
import type { PicSnap } from '@/features/scan/pipeline/pipelineStore';
import type { StoredSnap } from '@/features/scan/pipeline/snapStore';
import { MIN_WINDOW, bandView, isTall, placeOf, stripView } from './placement';
import { candidate } from './testData';

const SPINE = { x: 0.4, y: 0.1, width: 0.05, height: 0.8 };
const STILL = { width: 2576, height: 1932 };

const receipt = (extra: Partial<PicSnap> = {}): PicSnap => ({
  snapId: 'snap-1',
  libraryId: 'lib',
  trigger: 'shutter',
  createdAt: 10,
  thumbUri: 'file:///thumb.jpg',
  phase: 'read',
  sent: true,
  segmentId: 'seg-1',
  items: [{ localId: 'local-1', text: 'Dune', title: 'Dune', payload: { localId: 'local-1' } as never, candidateId: 'row-1' }],
  unreadableCount: 0,
  ...extra,
});
const stored = (extra: Partial<StoredSnap> = {}): StoredSnap => ({
  snapId: 'snap-1',
  createdAt: 10,
  stillUri: 'file:///still.jpg',
  ...STILL,
  items: [{ localId: 'local-1', text: 'Dune', box: SPINE, cropUri: 'file:///crop.jpg' }],
  ...extra,
});

describe('placeOf', () => {
  it('finds a phone row through its receipt and lights the phone box', () => {
    const place = placeOf(candidate({ id: 'row-1' }), { snaps: [receipt()], stored: [stored()] });
    expect(place).toEqual({ snapId: 'snap-1', takenAt: 10, still: { uri: 'file:///still.jpg', ...STILL }, box: SPINE, how: 'phone', cropUri: 'file:///crop.jpg' });
  });

  it("finds a server row through its segment and lights the server's box", () => {
    const row = candidate({ id: 'server-row', segmentId: 'seg-1', box: { x: 0.6, y: 0.2, w: 0.04, h: 0.7 } });
    const place = placeOf(row, { snaps: [receipt()], stored: [stored()] });
    expect(place?.how).toBe('server');
    expect(place?.box).toEqual({ x: 0.6, y: 0.2, width: 0.04, height: 0.7 });
  });

  it('knows the photo but not the place when the server sent no box', () => {
    const place = placeOf(candidate({ segmentId: 'seg-1' }), { snaps: [receipt()], stored: [stored()] });
    expect(place?.how).toBe('unmarked');
    expect(place?.box).toBeUndefined();
    expect(place?.still).toBeDefined();
  });

  it('has no still when the phone cleaned it up, and no place at all for a barcode', () => {
    expect(placeOf(candidate({ id: 'row-1' }), { snaps: [receipt()], stored: [] })?.still).toBeUndefined();
    expect(placeOf(candidate({ source: 'barcode' }), { snaps: [receipt()], stored: [stored()] })).toBeNull();
  });
});

describe('the band', () => {
  const band = { width: 358, height: 190 };

  it('zooms onto the lit item and about one neighbor each side, centered on it', () => {
    const view = bandView(STILL, SPINE, band);
    // 3.2 spine widths is narrower than 35% of the photo, so the window is 35% wide.
    expect(view.image.width).toBeCloseTo(band.width / MIN_WINDOW, 0);
    expect(view.box!.left + view.box!.width / 2).toBeCloseTo(band.width / 2, 0);
  });

  it('never shows past the edge of the photo', () => {
    const view = bandView(STILL, { x: 0, y: 0.1, width: 0.05, height: 0.8 }, band);
    expect(view.image.left).toBe(0);
    expect(view.box!.left).toBe(0);
  });

  it('shows the whole photo, contained, when the place is not known', () => {
    const view = bandView(STILL, undefined, band);
    expect(view.box).toBeUndefined();
    expect(view.image.height).toBe(band.height);
  });
});

describe('the strip', () => {
  const max = { width: 358, height: 96 };

  it('draws a tall spine upright, as wide as the card', () => {
    expect(isTall(SPINE, STILL)).toBe(true);
    const view = stripView(STILL, SPINE, max)!;
    expect(view.turned).toBe(true);
    expect(view.outer.width).toBeCloseTo(max.width, 0);
    // Turned, the clip is as tall as the strip is wide.
    expect(view.clip.height).toBeCloseTo(view.outer.width, 0);
  });

  it('does not turn a flat stack', () => {
    const flat = { x: 0.2, y: 0.5, width: 0.4, height: 0.08 };
    expect(isTall(flat, STILL)).toBe(false);
    const view = stripView(STILL, flat, max)!;
    expect(view.turned).toBe(false);
    expect(view.outer.width).toBeLessThanOrEqual(max.width);
    expect(view.outer.height).toBeLessThanOrEqual(max.height);
  });
});
