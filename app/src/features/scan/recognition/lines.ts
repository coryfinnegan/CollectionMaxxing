// Reading text from a still: three orientations (a fourth, upside down, on a shelf with a pile of items lying flat), noise dropped, the
// same line seen twice kept once.
import type { Rect, TextLine, VisionEngine } from '@/native/cm-vision';
import { gridRegions } from '../../../../modules/cm-vision/src/regions'; // only the geometry: the native entry is loaded on the phone alone
import { overlapShare } from './geometry';
import type { LocalTuning } from './defaults';
import { hasPile } from './splitting';

export const normalize = (text: string): string =>
  text
    .normalize('NFKD')
    .replace(/[̀-ͯ]/g, '')
    .toLowerCase()
    .replace(/[^\p{L}\p{N}]+/gu, ' ')
    .trim();

export const wordsOf = (text: string): string[] => normalize(text).split(' ').filter(Boolean);

const byPosition = (a: TextLine, b: TextLine) => a.box.x - b.box.x || a.box.y - b.box.y;

export function cleanLines(lines: TextLine[], tuning: LocalTuning): TextLine[] {
  const kept: TextLine[] = [];
  const byConfidence = [...lines].filter((l) => l.confidence >= tuning.minTextConfidence && normalize(l.text) !== '');
  byConfidence.sort((a, b) => b.confidence - a.confidence);
  for (const candidate of byConfidence) {
    const same = kept.some(
      (k) => normalize(k.text) === normalize(candidate.text) && overlapShare(k.box, candidate.box) >= tuning.duplicateLineOverlap,
    );
    if (!same) kept.push(candidate);
  }
  return kept.sort(byPosition);
}

const area = (r: Rect) => r.width * r.height;

/** Whether two boxes mark one place: they overlap by `min` of their union, so a small line inside a big one is not the big one. */
function sameBox(a: Rect, b: Rect, min: number): boolean {
  const shared = overlapShare(a, b) * Math.min(area(a), area(b));
  return shared / (area(a) + area(b) - shared) >= min;
}

/** The upside-down lines joined to the rest: a box read both ways keeps the surer reading (noise is what the wrong way up reads as). */
function withUpsideDown(lines: TextLine[], flipped: TextLine[], tuning: LocalTuning): TextLine[] {
  const replaced = new Set<TextLine>();
  const added: TextLine[] = [];
  for (const f of flipped) {
    const same = lines.filter((l) => !replaced.has(l) && sameBox(l.box, f.box, tuning.sameBoxOverlap));
    if (same.every((l) => l.confidence < f.confidence)) {
      same.forEach((l) => replaced.add(l));
      added.push(f);
    }
  }
  return [...lines.filter((l) => !replaced.has(l)), ...added].sort(byPosition);
}

export interface ReadLinesOptions {
  /** A shelf shot may show items lying flat in a pile, some of them upside down. */
  shelf?: boolean;
}

/** The three-pass reads under way, by engine and picture. An entry lives only while its read is pending: this shares a read, it never caches one. */
const inFlight = new WeakMap<VisionEngine, Map<string, Promise<TextLine[]>>>();

/** One native three-pass read per engine and picture at a time: a caller that asks while one is pending awaits that same read. */
function threePassRead(vision: VisionEngine, uri: string): Promise<TextLine[]> {
  let reads = inFlight.get(vision);
  if (!reads) inFlight.set(vision, (reads = new Map()));
  const pending = reads.get(uri);
  if (pending) return pending;
  const started = vision.recognizeText(uri, { orientations: [0, 90, 270] });
  const settled = () => {
    if (reads.get(uri) === started) reads.delete(uri);
  };
  started.then(settled, settled);
  reads.set(uri, started);
  return started;
}

/**
 * Upright, then turned 90 and 270 degrees, so lines on spines are read too. A shelf with a pile of items lying flat is read once more
 * upside down, since alternate tapes in a pile lie the other way up; a shelf without one costs nothing extra.
 * Never throws: no text is a reading too.
 */
export async function readLines(vision: VisionEngine, uri: string, tuning: LocalTuning, options: ReadLinesOptions = {}): Promise<TextLine[]> {
  let lines: TextLine[];
  try {
    lines = cleanLines(await threePassRead(vision, uri), tuning);
  } catch {
    return [];
  }
  if (!options.shelf || !hasPile(lines, tuning)) return lines;
  try {
    const flipped = await vision.recognizeText(uri, { orientations: [tuning.upsideDownOrientation] });
    return withUpsideDown(lines, cleanLines(flipped, tuning), tuning);
  } catch {
    return lines; // the upside-down pass is a bonus: what the other passes read stands
  }
}

/** The shelf's tile read: the picture in 2 x 2 tiles that overlap a little, which gives small print about twice the pixels. */
export const tilesOf = (tuning: LocalTuning): Rect[] => gridRegions(2, 2, tuning.tileOverlap);

/** The tiles that any of the boxes touches. */
export function tilesTouching(boxes: Rect[], tuning: LocalTuning): Rect[] {
  return tilesOf(tuning).filter((tile) => boxes.some((b) => overlapShare(tile, b) > 0));
}

/**
 * One read of the given tiles, upright, with every box put back on the whole picture and each line saying its tile in `region` (an index into
 * `regions`). Never throws: a tile read is a bonus, and the lines of the other passes stand without it.
 */
export async function readTiles(vision: VisionEngine, uri: string, regions: Rect[], tuning: LocalTuning): Promise<TextLine[]> {
  try {
    return cleanLines(await vision.recognizeText(uri, { orientations: [0], regions }), tuning);
  } catch {
    return [];
  }
}

const isTall = (l: TextLine) => l.box.height > l.box.width;

/**
 * The lines to add to `lines` from a tile read: a tile cuts print at its seams and rereads worse than the passes do, so a tile line only
 * adds a place nothing read. It is kept when it is not tall, has two letters or more, shares no box with a line already kept, and its center
 * lies in a tall line's column, within `tileColumnReach` letter widths of the line's ends. The lines come back with the tile lines in place.
 */
export function withTiles(lines: TextLine[], tiles: TextLine[], tuning: LocalTuning): TextLine[] {
  const spines = lines.filter(isTall);
  const added = tiles.filter((t) => {
    if (isTall(t) || (t.text.match(/\p{L}/gu) ?? []).length < 2) return false;
    if (lines.some((l) => sameBox(l.box, t.box, tuning.sameBoxOverlap))) return false;
    const cx = t.box.x + t.box.width / 2;
    const cy = t.box.y + t.box.height / 2;
    return spines.some((s) => {
      const reach = tuning.tileColumnReach * s.box.width;
      return cx >= s.box.x && cx <= s.box.x + s.box.width && cy >= s.box.y - reach && cy <= s.box.y + s.box.height + reach;
    });
  });
  return added.length === 0 ? lines : [...lines, ...added].sort(byPosition);
}
