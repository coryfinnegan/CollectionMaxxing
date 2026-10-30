// Reading text from a still: three orientations (a fourth, upside down, on a shelf with a pile of items lying flat), noise dropped, the
// same line seen twice kept once.
import type { Rect, TextLine, VisionEngine } from '@/native/cm-vision';
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

/**
 * Upright, then turned 90 and 270 degrees, so lines on spines are read too. A shelf with a pile of items lying flat is read once more
 * upside down, since alternate tapes in a pile lie the other way up; a shelf without one costs nothing extra.
 * Never throws: no text is a reading too.
 */
export async function readLines(vision: VisionEngine, uri: string, tuning: LocalTuning, options: ReadLinesOptions = {}): Promise<TextLine[]> {
  let lines: TextLine[];
  try {
    lines = cleanLines(await vision.recognizeText(uri, { orientations: [0, 90, 270] }), tuning);
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
