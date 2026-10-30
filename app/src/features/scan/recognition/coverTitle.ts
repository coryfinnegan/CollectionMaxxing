// The title of a single cover (a cover shot is one item, #504): its biggest text. A cover's longest lines are its blurbs
// ("#1 New York Times bestseller", a reviewer's quote), so length says nothing about the title; the size of the letters does.
// Every number lives in defaults.ts (a test enforces it); the words below are what a cover prints that is never the title.
import type { Rect, TextLine } from '@/native/cm-vision';
import type { LocalTuning } from './defaults';
import { bottom, overlapShare, right, union } from './geometry';
import { normalize } from './lines';

/** Praise and its attribution, as printed: a quoted line, or a name after a dash ("-NPR", "—Joel Siegel"). Not « or »: Vision draws those from art. */
const PRAISE = /^["“„]|["”]$|^[-–—]\s*\p{L}/u;

/** What else a cover prints beside its title, matched on `normalize`d text (lower case, no accents or punctuation). */
const BESIDE_THE_TITLE: readonly RegExp[] = [
  /\bbest ?sell|\bnew york times\b/, // bestseller lines
  /^an? (novel|memoir|thriller)$/, // what the book is
  /\bauthor of\b|\bnovel by\b|\bwinner of\b|\bmotion picture\b/, // credentials
  /\b(dvd|vhs|dolby|blu ?ray|hi ?fi)\b/, // format and sound logos
  /^(digital|stereo|surround|sound|widescreen|disc|video)( (digital|stereo|surround|sound|widescreen|disc|video))*$/, // the same kind, only when the line is nothing else: "Digital Fortress" stays
  /\bcriterion\b/, // the Criterion Collection
  /\blicensed\b|\b(made|assembled) in\b|\bin (japan|mexico|usa|china|korea|taiwan)\b/, // a cartridge label
  /playstation|xbox|nintendo|\bsega\b|game ?boy|gamecube/, // the console's own brand
];

/** A line a cover prints beside its title and never as it: a bestseller line, praise, a format logo, a licence. */
export const isBoilerplate = (text: string): boolean => PRAISE.test(text.trim()) || BESIDE_THE_TITLE.some((pattern) => pattern.test(normalize(text)));

/** The picture's size in pixels, which turns a box's shares into letter heights. */
export interface PictureSize {
  width: number;
  height: number;
}

export interface CoverTitle {
  /** The title as one line: the text of its lines in reading order, boxed together. A title of one line is that line itself. */
  line: TextLine;
  /** Every line that spells the title, a second reading of one included. None of these is the creator. */
  used: TextLine[];
}

const readingOrder = (a: TextLine, b: TextLine) => a.box.y - b.box.y || a.box.x - b.box.x;

/** Vision reads a Latin letter as its Cyrillic or Greek twin now and then (Ф for P, З for 3). */
const foreignLetters = (text: string) => (text.match(/[Ͱ-ԯ]/g) ?? []).length;

/** The distance between two boxes, in pixels, along the axis they are further apart on; 0 when they touch or overlap. */
function gapBetween(a: Rect, b: Rect, width: number, height: number): number {
  const across = Math.max(0, Math.max(a.x, b.x) - Math.min(right(a), right(b))) * width;
  const down = Math.max(0, Math.max(a.y, b.y) - Math.min(bottom(a), bottom(b))) * height;
  return Math.max(across, down);
}

/**
 * The cover's title out of its Vision lines, or nothing when no line is left to be one (the caller then picks as it always did).
 *
 * The title is the line with the biggest letters, measured as the smaller side of its box in pixels, so a sideways line counts by its width.
 * Lines set about as big and standing next to it in reading order join it, so a title broken over two lines comes out whole.
 * Boilerplate and slanted lines never count, and a line Vision saw twice counts once, as its surer reading.
 */
export function coverTitleOf(lines: TextLine[], picture: PictureSize, tuning: LocalTuning): CoverTitle | undefined {
  // Without a size the shares themselves stand in for pixels.
  const width = picture.width > 0 ? picture.width : 1;
  const height = picture.height > 0 ? picture.height : 1;
  const letters = (l: TextLine) => Math.min(l.box.width * width, l.box.height * height);
  const isSlanted = (l: TextLine) => {
    const sides = [l.box.width * width, l.box.height * height];
    const characters = l.text.replace(/\s/g, '').length;
    return Math.max(...sides) < Math.min(...sides) * characters * tuning.coverTitleLineAspect;
  };
  const repeats = (a: TextLine, b: TextLine) => overlapShare(a.box, b.box) >= tuning.duplicateLineOverlap;

  const surest = lines
    .filter((l) => !isBoilerplate(l.text) && !isSlanted(l))
    .map((l, order) => ({ l, order }))
    .sort((a, b) => b.l.confidence - a.l.confidence || foreignLetters(a.l.text) - foreignLetters(b.l.text) || a.order - b.order)
    .map(({ l }) => l);
  const ordered: TextLine[] = [];
  for (const l of surest) if (!ordered.some((kept) => repeats(kept, l))) ordered.push(l);
  if (ordered.length === 0) return undefined;
  ordered.sort(readingOrder);

  const biggest = ordered.reduce((best, l) => (letters(l) > letters(best) ? l : best));
  const peak = letters(biggest);
  const joins = (next: TextLine, from: TextLine) =>
    letters(next) >= peak * tuning.coverTitleSimilarHeight && gapBetween(next.box, from.box, width, height) <= peak * tuning.coverTitleMaxGap;
  let first = ordered.indexOf(biggest);
  let last = first;
  while (first > 0 && joins(ordered[first - 1], ordered[first])) first -= 1;
  while (last < ordered.length - 1 && joins(ordered[last + 1], ordered[last])) last += 1;
  const run = ordered.slice(first, last + 1);

  return {
    line:
      run.length === 1
        ? run[0]
        : { text: run.map((l) => l.text).join(' '), confidence: Math.min(...run.map((l) => l.confidence)), box: union(run.map((l) => l.box)) },
    used: lines.filter((l) => run.some((title) => title === l || repeats(title, l))),
  };
}
