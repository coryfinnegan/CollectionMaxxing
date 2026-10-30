// The title of a single cover (a cover shot is one item, #504): its biggest text. A cover's longest lines are its blurbs
// ("#1 New York Times bestseller", a reviewer's quote), so length says nothing about the title; the size of the letters does.
// An author's name printed bigger than the title is the creator, not the title.
// Every number lives in defaults.ts (a test enforces it); the words below are what a cover prints that is never the title.
import type { Rect, TextLine } from '@/native/cm-vision';
import type { LocalTuning } from './defaults';
import { bottom, centerX, centerY, contains, overlapShare, right, union } from './geometry';
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

/** One word of a boilerplate line that Vision split over lines ("NATIONAL" over "BESTSELLER"): alone it is not boilerplate, yet it names nobody. */
const BOILERPLATE_FRAGMENT = /^(national|bestseller|bestselling|licensed|nintendo|memoir|novel|edition|collection)$/;

/** Whether a line of a single cover may name its creator: it is not a line a cover prints beside its title, nor one word of such a line. */
export const mayBeCreator = (text: string): boolean => !isBoilerplate(text) && !BOILERPLATE_FRAGMENT.test(normalize(text));

/** Words a title uses and a name never does ("MY FRIENDS", "Theo of Golden"). */
const FUNCTION_WORDS = new Set(['the', 'a', 'an', 'of', 'my', 'your', 'our', 'his', 'her', 'for', 'and', 'in', 'on', 'to', 'with', 'at', 'by', 'from', 'is', 'it']);

/** One word of a name: letters, with their accents and apostrophes. */
const NAME_WORD = /^[\p{L}\p{M}'’]+$/u;

/** Whether a printed line reads as a person's name: two or three words of letters only, each at least two letters, none a function word. */
export function isNameLike(text: string, tuning: LocalTuning): boolean {
  const words = text.trim().split(/\s+/);
  if (words.length < 2 || words.length > tuning.coverNameMaxWords) return false;
  return words.every((word) => NAME_WORD.test(word) && (word.match(/\p{L}/gu) ?? []).length >= 2 && !FUNCTION_WORDS.has(normalize(word)));
}

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
  /** Set when the biggest text was a name and the title is a smaller run beside it: the name, as one line. It is the creator. */
  creator?: TextLine;
}

const readingOrder = (a: TextLine, b: TextLine) => a.box.y - b.box.y || a.box.x - b.box.x;

/** Vision reads a Latin letter as a twin now and then (Ф for P, З for 3, É for F), so of two readings the one with fewer non-ASCII letters is likelier. */
const nonAsciiLetters = (text: string) => (text.match(/(?![A-Za-z])\p{L}/gu) ?? []).length;

/** The distance between two boxes, in pixels, along the axis they are further apart on; 0 when they touch or overlap. */
function gapBetween(a: Rect, b: Rect, width: number, height: number): number {
  const across = Math.max(0, Math.max(a.x, b.x) - Math.min(right(a), right(b))) * width;
  const down = Math.max(0, Math.max(a.y, b.y) - Math.min(bottom(a), bottom(b))) * height;
  return Math.max(across, down);
}

/** The empty band between two boxes, across the gap along the axis they are further apart on; nothing when they overlap on both axes. */
function bandBetween(a: Rect, b: Rect): Rect | undefined {
  const [upper, lower] = a.y <= b.y ? [a, b] : [b, a];
  if (lower.y > bottom(upper)) {
    const x = Math.min(a.x, b.x);
    return { x, y: bottom(upper), width: Math.max(right(a), right(b)) - x, height: lower.y - bottom(upper) };
  }
  const [left, farther] = a.x <= b.x ? [a, b] : [b, a];
  if (farther.x > right(left)) {
    const y = Math.min(a.y, b.y);
    return { x: right(left), y, width: farther.x - right(left), height: Math.max(bottom(a), bottom(b)) - y };
  }
  return undefined;
}

/**
 * The cover's title out of its Vision lines, or nothing when no line is left to be one (the caller then picks as it always did).
 *
 * The title is the line with the biggest letters, measured as the smaller side of its box in pixels, so a sideways line counts by its width.
 * Lines set about as big and standing next to it in reading order join it, so a title broken over two lines comes out whole; a line the
 * cover prints beside its title ("A NOVEL") standing in the gap between two lines keeps them apart.
 * Boilerplate and slanted lines never count, and a line Vision saw twice counts once, as its surer reading (of two as sure, the one with
 * fewer non-ASCII letters).
 *
 * An author's name is often printed bigger than the title. When the biggest run reads as a name and a smaller run that does not read as one
 * is at least `coverTitleBesideNameShare` as high, that smaller run is the title and the name comes back as `creator`.
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
    .sort((a, b) => b.l.confidence - a.l.confidence || nonAsciiLetters(a.l.text) - nonAsciiLetters(b.l.text) || a.order - b.order)
    .map(({ l }) => l);
  const ordered: TextLine[] = [];
  for (const l of surest) if (!ordered.some((kept) => repeats(kept, l))) ordered.push(l);
  if (ordered.length === 0) return undefined;
  ordered.sort(readingOrder);

  // A boilerplate line standing in the gap between two lines (the "A NOVEL" between an author and a title) says they are not one title.
  const beside = lines.filter((l) => isBoilerplate(l.text));
  const apart = (a: TextLine, b: TextLine) => {
    const band = bandBetween(a.box, b.box);
    return band !== undefined && beside.some((l) => contains(band, centerX(l.box), centerY(l.box)));
  };

  const taken = new Set<TextLine>();
  const biggestOf = (pool: TextLine[]) => pool.reduce((best, l) => (letters(l) > letters(best) ? l : best));
  // The seed and the lines about as big that stand next to it in reading order. The lines of an earlier run are never taken again.
  const runOf = (seed: TextLine): TextLine[] => {
    const peak = letters(seed);
    const joins = (next: TextLine, from: TextLine) =>
      !taken.has(next) &&
      letters(next) >= peak * tuning.coverTitleSimilarHeight &&
      gapBetween(next.box, from.box, width, height) <= peak * tuning.coverTitleMaxGap &&
      !apart(next, from);
    let first = ordered.indexOf(seed);
    let last = first;
    while (first > 0 && joins(ordered[first - 1], ordered[first])) first -= 1;
    while (last < ordered.length - 1 && joins(ordered[last + 1], ordered[last])) last += 1;
    const run = ordered.slice(first, last + 1);
    run.forEach((l) => taken.add(l));
    return run;
  };
  const asLine = (run: TextLine[]): TextLine =>
    run.length === 1 ? run[0] : { text: run.map((l) => l.text).join(' '), confidence: Math.min(...run.map((l) => l.confidence)), box: union(run.map((l) => l.box)) };
  const spelledBy = (run: TextLine[]) => lines.filter((l) => run.some((title) => title === l || repeats(title, l)));

  const seed = biggestOf(ordered);
  const biggest = runOf(seed);
  const biggestLine = asLine(biggest);
  if (isNameLike(biggestLine.text, tuning)) {
    // The biggest text is a name: the title may be the biggest run beside it that is not one. Each name-like run passed over is set aside.
    const floor = letters(seed) * tuning.coverTitleBesideNameShare;
    for (let rest = ordered.filter((l) => !taken.has(l)); rest.length > 0 && letters(biggestOf(rest)) >= floor; rest = ordered.filter((l) => !taken.has(l))) {
      const run = runOf(biggestOf(rest));
      if (!isNameLike(asLine(run).text, tuning)) return { line: asLine(run), used: spelledBy(run), creator: biggestLine };
    }
  }
  return { line: biggestLine, used: spelledBy(biggest) };
}
