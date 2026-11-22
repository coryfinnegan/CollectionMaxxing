// Where a row was found and how the walk draws it (#833): the photo, the lit box, the band's window and the upright strip.
import type { PicSnap } from '@/features/scan/pipeline/pipelineStore';
import type { StoredSnap } from '@/features/scan/pipeline/snapStore';
import { BAND_HEIGHT, bandView, isTall, placeOf, stripView } from './placement';
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

  it('carries the pass that read the phone box, and none for an item stored before', () => {
    const withPass = stored({ items: [{ localId: 'local-1', text: 'Dune', box: SPINE, pass: 90 }] });
    expect(placeOf(candidate({ id: 'row-1' }), { snaps: [receipt()], stored: [withPass] })?.pass).toBe(90);
    expect(placeOf(candidate({ id: 'row-1' }), { snaps: [receipt()], stored: [stored()] })).not.toHaveProperty('pass');
    const server = candidate({ id: 'server-row', segmentId: 'seg-1', box: { x: 0.6, y: 0.2, w: 0.04, h: 0.7 } });
    expect(placeOf(server, { snaps: [receipt()], stored: [withPass] })).not.toHaveProperty('pass');
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
  const band = { width: 390, height: BAND_HEIGHT };
  /** A landscape shelf of about 30 narrow spines. */
  const WIDE = { width: 2576, height: 1449 };
  const thin = (x: number) => ({ x, y: 0.1, width: 0.028, height: 0.8 });
  const covers = (view: ReturnType<typeof bandView>) => {
    expect(view.image.left).toBeLessThanOrEqual(0);
    expect(view.image.top).toBeLessThanOrEqual(0);
    expect(view.image.left + view.image.width).toBeGreaterThanOrEqual(band.width);
    expect(view.image.top + view.image.height).toBeGreaterThanOrEqual(band.height);
  };
  /** The drawn outline: all four edges inside the band. */
  const outlined = (view: ReturnType<typeof bandView>) => {
    const b = view.box!;
    expect(b.left).toBeGreaterThanOrEqual(4);
    expect(b.top).toBeGreaterThanOrEqual(4);
    expect(b.left + b.width).toBeLessThanOrEqual(band.width - 4);
    expect(b.top + b.height).toBeLessThanOrEqual(band.height - 4);
  };

  it('A spine on a wide shelf photo is at least 48 pt wide in the band', () => {
    const view = bandView(WIDE, thin(0.48), band);
    expect(view.box!.width).toBeGreaterThanOrEqual(48);
    // No more than about three neighbors each side: the spine takes at least a seventh of the band.
    expect(view.box!.width).toBeGreaterThanOrEqual(band.width / 7 - 0.01); // rounded to the hundredth
    // Centered on the spine.
    expect(view.box!.left + view.box!.width / 2).toBeCloseTo(band.width / 2, 0);
  });

  it('The photo always fills the band: no bars beside or above it', () => {
    covers(bandView(WIDE, thin(0.48), band));
    covers(bandView(STILL, SPINE, band));
    covers(bandView({ width: 1932, height: 2576 }, { x: 0.46, y: 0.25, width: 0.08, height: 0.5 }, band));
    covers(bandView(STILL, { x: 0.2, y: 0.5, width: 0.6, height: 0.1 }, band));
  });

  it('A spine taller than the band is cropped, and its outline still shows all four edges inside the band', () => {
    const view = bandView(WIDE, thin(0.48), band);
    // The spine itself runs past the band (it is cropped) ...
    expect(view.image.height * 0.8).toBeGreaterThan(band.height);
    // ... and the outline is drawn round the part that shows.
    outlined(view);
    outlined(bandView({ width: 1932, height: 2576 }, { x: 0.46, y: 0.25, width: 0.08, height: 0.5 }, band));
  });

  it("A box at the photo's edge keeps its whole outline", () => {
    for (const box of [thin(0), thin(1 - 0.028), { x: 0, y: 0, width: 0.05, height: 0.15 }]) {
      const view = bandView(WIDE, box, band);
      outlined(view);
      expect(view.box!.width).toBeGreaterThanOrEqual(48);
    }
  });

  it('A flat stack or a cover is not zoomed past 60% of the band', () => {
    const flat = bandView(STILL, { x: 0.3, y: 0.5, width: 0.3, height: 0.08 }, band);
    expect(flat.box!.width).toBeLessThanOrEqual(band.width * 0.6 + 0.01);
    outlined(flat);
    const cover = bandView(STILL, { x: 0.35, y: 0.25, width: 0.3, height: 0.5 }, band);
    expect(cover.box!.width).toBeLessThanOrEqual(band.width * 0.6 + 0.01);
  });

  it('outlines a box as wide as the photo inside the band', () => {
    outlined(bandView(STILL, { x: 0, y: 0.4, width: 1, height: 0.1 }, band));
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

  // Vision turns the picture clockwise by the pass; the strip is turned clockwise by the same degrees, so the title reads left to right.
  it('A spine read top-to-bottom (pass 270) is drawn the right way up', () => {
    expect(stripView(STILL, SPINE, max, 270)!.turn).toBe(270);
  });

  it('A spine read bottom-to-top (pass 90) is drawn the right way up', () => {
    const view = stripView(STILL, SPINE, max, 90)!;
    expect(view.turn).toBe(90);
    expect(view.turned).toBe(true);
    expect(view.outer).toEqual(stripView(STILL, SPINE, max, 270)!.outer);
  });

  it('A spine read upside down (pass 180) is turned half a turn and keeps its own shape', () => {
    const view = stripView(STILL, SPINE, max, 180)!;
    expect(view.turn).toBe(180);
    expect(view.turned).toBe(false);
  });

  it('A spine whose title already reads left to right (pass 0) is not turned', () => {
    const view = stripView(STILL, SPINE, max, 0)!;
    expect(view.turn).toBe(0);
    expect(view.turned).toBe(false);
  });

  it('An item stored before this change is drawn as today', () => {
    const view = stripView(STILL, SPINE, max)!;
    expect(view.turn).toBe(270);
    expect(view.turned).toBe(true);
  });

  it('A flat stack is still not turned, whatever pass read it', () => {
    const flat = { x: 0.2, y: 0.5, width: 0.4, height: 0.08 };
    for (const pass of [undefined, 0, 90, 180, 270] as const) {
      const view = stripView(STILL, flat, max, pass)!;
      expect(view.turn).toBe(0);
      expect(view.turned).toBe(false);
    }
  });
});
