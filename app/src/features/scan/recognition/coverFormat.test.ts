import type { Rect } from '@/native/cm-vision';
import { correctFormat, coverFormat, measureShape, type FormatCue } from './coverFormat';
import { DEFAULT_COVER_FORMAT_TUNING } from './defaults';
import { line } from './fakes';
import type { ReadItem } from './types';

const STILL = { width: 3000, height: 4000 };

/** A line of text anywhere on the picture; only its words matter to the format cue. */
const said = (...texts: string[]) => texts.map((t, i) => line(t, 0.1, 0.1 + i * 0.05, 0.5, 0.04));

/** A case whose short side over long side is `ratio` in pixels, standing upright and filling `fill` of the still's height. */
function caseWith(ratio: number, fill = 0.8, still = STILL): Rect {
  const height = fill;
  const width = (ratio * height * still.height) / still.width;
  return { x: (1 - width) / 2, y: (1 - height) / 2, width, height };
}

const cueOf = (rectangles: Rect[], texts: string[] = [], still = STILL) => coverFormat({ still, rectangles, lines: said(...texts) });

describe('coverFormat: the logo on the case', () => {
  it.each([
    ['DVD', 'dvd'],
    ['DVD VIDEO', 'dvd'],
    ['dvd-video', 'dvd'],
    ['DVD VIDE0', 'dvd'],
    ['Also available on DVD', 'dvd'],
    ['DVDVIDEO', 'dvd'],
    ['Blu-ray', 'bluray'],
    ['BLU-RAY DISC', 'bluray'],
    ['Blu-ray Disc™', 'bluray'],
    ['BLURAY', 'bluray'],
    ['B1u-ray', 'bluray'],
    ['BIu-ray', 'bluray'],
    ['4K ULTRA HD', 'uhdBluray'],
    ['ULTRA HD', 'uhdBluray'],
    ['ULTRAHD', 'uhdBluray'],
    ['UHD', 'uhdBluray'],
    ['VHS', 'vhs'],
    ['VHS STEREO', 'vhs'],
    ['HI-FI', 'vhs'],
    ['VIDEOCASSETTE', 'vhs'],
    ['VIDEO CASSETTE', 'vhs'],
    ['VIDEOCASSETTF', 'vhs'],
  ])('reads "%s" as %s', (text, format) => {
    expect(cueOf([], [text])).toEqual({ format, source: 'logo' });
  });

  it('calls a 4K Ultra HD Blu-ray an Ultra HD one, not a Blu-ray', () => {
    expect(cueOf([], ['4K ULTRA HD BLU-RAY'])?.format).toBe('uhdBluray');
    expect(cueOf([], ['Blu-ray', 'ULTRA HD'])?.format).toBe('uhdBluray');
  });

  it.each([
    ['VHS inside another word', 'AVHSA'],
    ['VHS glued to a word', 'VHSX'],
    ['the film V/H/S', 'V/H/S'],
    ['DVD inside another word', 'ADVDX'],
    ['DVDs', 'DVDs'],
    ['a blurry word', 'A blurry night'],
    ['Blu alone', 'Blu'],
    ['ray alone', 'Man Ray'],
    ['4K alone, from a restoration blurb', 'New 4K digital restoration'],
    ['Ultra alone', 'Ultra Violet'],
    ['plain title words', 'THE LANTERN ROAD'],
    ['nothing at all', ''],
  ])('finds no logo in %s', (_why, text) => {
    expect(cueOf([], [text])).toBeUndefined();
  });

  it('is case-insensitive', () => {
    expect(cueOf([], ['bLu-RaY'])?.format).toBe('bluray');
  });

  it('wins over the shape: a DVD logo on a case shaped like a VHS slipcase is a DVD', () => {
    expect(cueOf([caseWith(0.55)], ['DVD VIDEO'])).toEqual({ format: 'dvd', source: 'logo' });
  });

  it('lets the shape settle a case that names two formats', () => {
    expect(cueOf([caseWith(0.6)], ['Now on DVD', 'VHS'])?.format).toBe('vhs');
    expect(cueOf([caseWith(0.71)], ['Now on DVD', 'VHS'])?.format).toBe('dvd');
  });

  it('prefers the newer format when two are named and the shape cannot settle it', () => {
    expect(cueOf([], ['Now on DVD', 'VHS'])?.format).toBe('dvd');
    expect(cueOf([], ['Blu-ray + DVD'])?.format).toBe('bluray');
  });
});

describe('coverFormat: the shape of the case', () => {
  it.each([
    [0.55, 'vhs'], // slipcase
    [0.62, 'vhs'], // clamshell
    [0.64, 'vhs'],
    [0.71, 'dvd'], // keepcase
    [0.68, 'dvd'],
    [0.76, 'dvd'],
    [0.84, 'bluray'],
    [0.86, 'bluray'], // Blu-ray and UHD keepcase
    [0.9, 'bluray'],
  ])('reads a case with a %s short-to-long ratio as %s', (ratio, format) => {
    expect(cueOf([caseWith(ratio)])).toEqual({ format, source: 'shape' });
  });

  it.each([
    ['between VHS and DVD', 0.655, 0.7],
    ['between DVD and Blu-ray', 0.785, 0.7],
    ['squarer than a Blu-ray', 0.95, 0.7],
    ['square', 1, 0.7],
    ['too thin to be a case (a spine, a shelf edge)', 0.3, 0.95],
  ])('gives no cue for a rectangle %s', (_why, ratio, fill) => {
    const rectangle = caseWith(ratio, fill);
    expect(rectangle.width * rectangle.height).toBeGreaterThan(0.3); // big enough to count, so only its shape rules it out
    expect(cueOf([rectangle])).toBeUndefined();
  });

  it("measures the case in pixels, not in the picture's own 0..1", () => {
    // 0.6 x 0.6 of a 3000 x 4000 still is 1800 x 2400 pixels: a DVD keepcase, not a square.
    expect(cueOf([{ x: 0.2, y: 0.2, width: 0.6, height: 0.6 }])).toEqual({ format: 'dvd', source: 'shape' });
  });

  it('reads a case held on its side the same way', () => {
    // 2700 x 1485 pixels: a VHS slipcase turned a quarter.
    expect(cueOf([{ x: 0.05, y: 0.3, width: 0.9, height: (0.55 * 0.9 * 3000) / 4000 }])?.format).toBe('vhs');
  });

  it('measures a case on a landscape still too', () => {
    const wide = { width: 4000, height: 3000 };
    expect(coverFormat({ still: wide, rectangles: [caseWith(0.86, 0.8, wide)], lines: [] })?.format).toBe('bluray');
  });

  it('ignores a rectangle that covers too little of the picture to be the case', () => {
    expect(cueOf([caseWith(0.55, 0.4)])).toBeUndefined();
  });

  it('gives no cue when Vision found no rectangle', () => {
    expect(cueOf([])).toBeUndefined();
  });

  it('measures the largest rectangle', () => {
    expect(cueOf([caseWith(0.86, 0.5), caseWith(0.55, 0.9), caseWith(0.71, 0.6)])?.format).toBe('vhs');
  });

  it('gives no cue for an empty or broken still size', () => {
    expect(cueOf([caseWith(0.55)], [], { width: 0, height: 4000 })).toBeUndefined();
    expect(cueOf([caseWith(0.55)], [], { width: Number.NaN, height: 4000 })).toBeUndefined();
  });

  it('skips a rectangle with no size', () => {
    expect(cueOf([{ x: 0.1, y: 0.1, width: 0, height: 0.8 }])).toBeUndefined();
  });
});

describe('coverFormat: a rectangle that hugs the frame is the crop, not the case', () => {
  // On a 3000 x 4000 still, a rectangle as wide as it is tall in 0..1 is 3:4 (0.75), which is the DVD band. A rectangle that fills the frame
  // says how the photo was cropped (or the photo's own border), never what case is in it.
  const hugging = { x: 0, y: 0, width: 1, height: 1 };

  it('gives no shape cue for a rectangle that fills the frame', () => {
    expect(cueOf([hugging])).toBeUndefined();
  });

  it('gives no shape cue for a rectangle a hair inside every edge', () => {
    expect(cueOf([{ x: 0.01, y: 0.01, width: 0.98, height: 0.98 }])).toBeUndefined();
  });

  it('ignores a rectangle that is within the margin of three edges, however it is shaped', () => {
    // 0.9467 x 1 of a 3000 x 4000 still is 0.71 in pixels: a DVD keepcase, if it were not pushed against the left, top and bottom edges.
    const threeEdges = { x: 0, y: 0, width: 0.9467, height: 1 };
    expect(cueOf([threeEdges])).toBeUndefined();
    expect(cueOf([{ x: 0.019, y: 0.019, width: 0.9467, height: 0.962 }])).toBeUndefined();
  });

  it('still counts a rectangle that touches only two edges', () => {
    expect(cueOf([{ x: 0, y: 0, width: 0.852, height: 0.9 }])).toEqual({ format: 'dvd', source: 'shape' });
  });

  it('still counts a case at the photo\'s side, touching one edge', () => {
    expect(cueOf([{ x: 0, y: 0.1, width: 0.757, height: 0.8 }])).toEqual({ format: 'dvd', source: 'shape' });
  });

  it('counts a rectangle just outside the margin as inside the picture', () => {
    expect(cueOf([{ x: 0.03, y: 0.03, width: 0.9467 * 0.94, height: 0.94 }])).toEqual({ format: 'dvd', source: 'shape' });
  });

  it('measures a smaller real case when the frame-hugging rectangle is ignored', () => {
    expect(cueOf([hugging, caseWith(0.55, 0.8)])).toEqual({ format: 'vhs', source: 'shape' });
  });

  it('takes the margin and the number of edges from the tuning', () => {
    const tuning = { ...DEFAULT_COVER_FORMAT_TUNING, frameEdgeMargin: 0.1, frameHuggingSides: 2 };
    const near = { x: 0.05, y: 0.05, width: 0.852, height: 0.9 }; // within 0.1 of the left and top edges
    expect(coverFormat({ still: STILL, rectangles: [near], lines: [], tuning })).toBeUndefined();
    expect(coverFormat({ still: STILL, rectangles: [near], lines: [] })).toEqual({ format: 'dvd', source: 'shape' });
  });

  it('does not touch a logo', () => {
    expect(cueOf([hugging], ['Blu-ray Disc'])).toEqual({ format: 'bluray', source: 'logo' });
    expect(cueOf([hugging], ['DVD VIDEO'])).toEqual({ format: 'dvd', source: 'logo' });
  });

  it('no longer lets the ignored shape settle a case that names two formats', () => {
    const vhsShaped = { x: 0, y: 0, width: 0.8, height: 1 }; // 0.6 in pixels, against the left, top and bottom edges
    expect(cueOf([vhsShaped], ['Now on DVD', 'VHS'])).toEqual({ format: 'dvd', source: 'logo' });
    expect(cueOf([caseWith(0.6)], ['Now on DVD', 'VHS'])?.format).toBe('vhs');
  });
});

describe('correctFormat', () => {
  const read = (kind: ReadItem['kind'], format?: ReadItem['format'], extra: Partial<ReadItem> = {}): ReadItem => ({
    localId: 'id-1',
    text: 'CASABLANCA',
    title: 'Casablanca',
    kind,
    format,
    order: 0,
    confidence: 'high',
    ...extra,
  });
  const shape = (format: FormatCue['format']): FormatCue => ({ format, source: 'shape' });
  const logo = (format: FormatCue['format']): FormatCue => ({ format, source: 'logo' });

  it.each([
    ['vhs', 'dvd'],
    ['dvd', 'vhs'],
    ['dvd', 'bluray'],
    ['bluray', 'dvd'],
    ['uhd_bluray', 'dvd'],
  ] as const)('a shape cue turns a %s film into the cued format %s', (was, cued) => {
    expect(correctFormat(read('film', was), shape(cued))).toEqual(read('film', cued));
  });

  it('turns a film into the Ultra HD format the table names', () => {
    expect(correctFormat(read('film', 'dvd'), logo('uhdBluray'))).toEqual(read('film', 'uhd_bluray'));
  });

  it.each(['book', 'other', 'game'] as const)('a shape cue never turns a %s into a film', (kind) => {
    const item = read(kind);
    expect(correctFormat(item, shape('dvd'))).toBe(item);
  });

  it.each(['book', 'other'] as const)('a logo cue turns a %s into a film in that format', (kind) => {
    expect(correctFormat(read(kind, undefined, { creator: 'Curtiz' }), logo('bluray'))).toEqual(read('film', 'bluray', { creator: 'Curtiz' }));
  });

  it('a logo cue never touches a game', () => {
    const item = read('game', undefined, { platform: 'Nintendo 64' });
    expect(correctFormat(item, logo('dvd'))).toBe(item);
  });

  it('hands back the same item when it already has the format', () => {
    const item = read('film', 'dvd');
    expect(correctFormat(item, shape('dvd'))).toBe(item);
    expect(correctFormat(item, logo('dvd'))).toBe(item);
  });

  it('keeps every other field, and does not change the item it was given', () => {
    const item = read('film', 'dvd', { creator: 'Curtiz', edition: 'Criterion', box: { x: 0.1, y: 0.1, width: 0.5, height: 0.5 } });
    const before = structuredClone(item);
    expect(correctFormat(item, shape('vhs'))).toEqual({ ...before, format: 'vhs' });
    expect(item).toEqual(before);
  });
});

describe('measureShape: what the shape search saw', () => {
  const measure = (rectangles: Rect[], still = STILL) => measureShape(still, rectangles, DEFAULT_COVER_FORMAT_TUNING);
  const frame: Rect = { x: 0, y: 0, width: 1, height: 1 };

  it('has seen nothing when Vision found no rectangle', () => {
    expect(measure([])).toEqual({ seen: 0, unusable: 0, frame: 0, small: 0, outOfBand: 0 });
  });

  it('measures the largest rectangle that is not the frame, and the format it is shaped like', () => {
    const seen = measure([caseWith(0.86)]);
    expect(seen.seen).toBe(1);
    expect(seen.largest?.ratio).toBeCloseTo(0.86, 5);
    expect(seen.largest?.coverage).toBeCloseTo(0.8 * ((0.86 * 0.8 * 4000) / 3000), 5);
    expect(seen.format).toBe('bluray');
    expect([seen.unusable, seen.frame, seen.small, seen.outOfBand]).toEqual([0, 0, 0, 0]);
  });

  it('counts a rectangle against the frame, and measures the case beside it instead', () => {
    const seen = measure([frame, caseWith(0.71)]);
    expect(seen).toMatchObject({ seen: 2, frame: 1, format: 'dvd' });
    expect(seen.largest?.ratio).toBeCloseTo(0.71, 5);
  });

  it('has no measurement when the only rectangle is the frame', () => {
    expect(measure([frame])).toEqual({ seen: 1, unusable: 0, frame: 1, small: 0, outOfBand: 0 });
  });

  it('counts the rectangles too small to be the case, and keeps the largest one measured', () => {
    const seen = measure([caseWith(0.86, 0.4), caseWith(0.55, 0.3)]);
    expect(seen).toMatchObject({ seen: 2, small: 2, outOfBand: 0 });
    expect(seen.format).toBeUndefined();
    expect(seen.largest?.coverage).toBeLessThan(DEFAULT_COVER_FORMAT_TUNING.minCaseCoverage);
  });

  it('counts a case whose ratio is in no band as out of band', () => {
    const seen = measure([caseWith(0.785)]);
    expect(seen).toMatchObject({ seen: 1, outOfBand: 1 });
    expect(seen.format).toBeUndefined();
    expect(seen.largest?.ratio).toBeCloseTo(0.785, 5);
  });

  it('counts a rectangle with no size as unusable', () => {
    expect(measure([{ x: 0.1, y: 0.1, width: 0, height: 0.5 }])).toEqual({ seen: 1, unusable: 1, frame: 0, small: 0, outOfBand: 0 });
  });

  it('can use none of them when the still has no size', () => {
    expect(measure([caseWith(0.86)], { width: 0, height: 0 })).toEqual({ seen: 1, unusable: 1, frame: 0, small: 0, outOfBand: 0 });
  });

  it('agrees with the cue about which format the shape gives', () => {
    for (const ratio of [0.55, 0.62, 0.71, 0.76, 0.785, 0.86, 0.95]) {
      expect(measure([caseWith(ratio)]).format).toBe(cueOf([caseWith(ratio)])?.format);
    }
  });
});
