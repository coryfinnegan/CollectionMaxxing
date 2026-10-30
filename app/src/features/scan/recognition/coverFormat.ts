// What the case says about its format. The film catalogs do not record formats, so on a single cover the model's read
// decides one, and the model is often wrong (a Blu-ray called a DVD, a VHS clamshell called a DVD, a slipcase read as a
// book). The case itself says: its proportions (a VHS slipcase is far narrower than a Blu-ray keepcase) and the logos
// printed on it. Pure: the still's size, Vision's rectangles and Vision's text lines in, a cue or nothing out. The numbers
// (bands, coverage, wait) are `CoverFormatTuning` in defaults.ts, where a test requires every tunable value to live.
import type { ModelKind, Rect, TextLine } from '@/native/cm-vision';
import { type CoverFormatTuning, DEFAULT_COVER_FORMAT_TUNING } from './defaults';
import { normalize } from './lines';
import { mapKind } from './mapping';
import type { ReadItem } from './types';

/** The formats a film can be in; the model's own words for them (see `KIND_TABLE`). */
export type FilmFormat = Extract<ModelKind, 'vhs' | 'dvd' | 'bluray' | 'uhdBluray'>;

export interface FormatCue {
  format: FilmFormat;
  /** A logo names the format; a shape only suggests it. */
  source: 'logo' | 'shape';
}

export interface CoverFormatInput {
  /** The still's size in pixels: Vision's rectangles are 0..1 of it, so the case's real proportions need both. */
  still: { width: number; height: number };
  /** `vision.detectRectangles` on the still: normalized 0..1, origin top-left. */
  rectangles: Rect[];
  /** `readLines` on the still. */
  lines: TextLine[];
  /** The case's proportions and the cut-offs around them; `DEFAULT_COVER_FORMAT_TUNING` (defaults.ts) unless a test says otherwise. */
  tuning?: CoverFormatTuning;
}

// ---- Shape --------------------------------------------------------------------------------------------------------

const positive = (n: number) => Number.isFinite(n) && n > 0;

/**
 * The format the largest big-enough rectangle is shaped like: its short side over its long side, in pixels (Vision's
 * rectangles are 0..1 of each side, so a 0.5 by 0.5 rectangle on a 3:4 still is not a square). A ratio between the bands
 * or outside them is no cue. Ultra HD shares Blu-ray's case, so only a logo says Ultra HD.
 */
function formatFromShape(still: CoverFormatInput['still'], rectangles: Rect[], tuning: CoverFormatTuning): FilmFormat | undefined {
  if (!positive(still.width) || !positive(still.height)) return undefined;
  let largest: Rect | undefined;
  for (const r of rectangles) {
    if (positive(r.width) && positive(r.height) && (!largest || r.width * r.height > largest.width * largest.height)) largest = r;
  }
  if (!largest || largest.width * largest.height < tuning.minCaseCoverage) return undefined;
  const sides = [largest.width * still.width, largest.height * still.height];
  const ratio = Math.min(...sides) / Math.max(...sides);
  const bands: [FilmFormat, CoverFormatTuning['vhs']][] = [
    ['vhs', tuning.vhs],
    ['dvd', tuning.dvd],
    ['bluray', tuning.bluray],
  ];
  return bands.find(([, band]) => ratio >= band.min && ratio <= band.max)?.[0];
}

// ---- Logos --------------------------------------------------------------------------------------------------------

interface Logo {
  format: FilmFormat;
  /** Words as they print, space-separated. A logo OCR splits or runs together ("Blu-ray", "BLURAY") is still the same one. */
  phrases: string[];
}

/**
 * In order of precedence when a case names more than one: Ultra HD cases say "Blu-ray" too, a Blu-ray combo pack says
 * "DVD" too, and the newer format is the one the case is.
 */
const LOGOS: readonly Logo[] = [
  { format: 'uhdBluray', phrases: ['ultra hd', 'uhd'] },
  { format: 'bluray', phrases: ['blu ray'] },
  { format: 'dvd', phrases: ['dvd', 'dvd video'] },
  { format: 'vhs', phrases: ['vhs', 'hi fi', 'video cassette'] },
];

/** Letters OCR trades for digits and capital I, so "VIDE0", "B1u-ray" and "BIu-ray" still read. Applied to both sides. */
const fold = (text: string): string => normalize(text).replace(/[01i]/g, (c) => (c === '0' ? 'o' : 'l'));

function oneEditApart(a: string, b: string): boolean {
  if (Math.abs(a.length - b.length) > 1) return false;
  let i = 0;
  while (i < a.length && i < b.length && a[i] === b[i]) i++;
  if (a.length === b.length) return a.slice(i + 1) === b.slice(i + 1);
  const [longer, shorter] = a.length > b.length ? [a, b] : [b, a];
  return longer.slice(i + 1) === shorter.slice(i);
}

/** The same word, or (for a long logo word only, see `fuzzyLogoMinLength`) one character off. */
const sameWord = (seen: string, logo: string, tuning: CoverFormatTuning) =>
  seen === logo || (logo.length >= tuning.fuzzyLogoMinLength && oneEditApart(seen, logo));

/**
 * Whole words only: "VHS" in "AVHSA" or "DVD" in "DVDs" is not a logo, and the film "V/H/S" is not a tape. A logo of
 * several words may arrive in one more piece than it has ("Blu-ray" as "Blu ray", "Blu-ray" as "BLURAY", "B lu ray").
 */
function mentions(words: string[], logo: Logo, tuning: CoverFormatTuning): boolean {
  return logo.phrases.some((phrase) => {
    const parts = fold(phrase).split(' ');
    const squashed = parts.join('');
    for (let start = 0; start < words.length; start++) {
      for (let count = 1; count <= parts.length + 1 && start + count <= words.length; count++) {
        if (sameWord(words.slice(start, start + count).join(''), squashed, tuning)) return true;
      }
    }
    return false;
  });
}

/** The formats the lines name, in order of precedence. A mention anywhere counts, a blurb included. */
function formatsFromLogos(lines: TextLine[], tuning: CoverFormatTuning): FilmFormat[] {
  const spoken = lines.map((l) => fold(l.text).split(' ').filter(Boolean));
  return LOGOS.filter((logo) => spoken.some((words) => mentions(words, logo, tuning))).map((logo) => logo.format);
}

// ---- Together -----------------------------------------------------------------------------------------------------

/** Ultra HD and Blu-ray share a case, so for agreeing with the shape they are one family. */
const family = (format: FilmFormat) => (format === 'uhdBluray' ? 'bluray' : format);

/**
 * The format the case says, or nothing. A logo wins over the shape. When a case names two formats the shape settles it if
 * it agrees with one of them; otherwise the first in `LOGOS` stands.
 */
export function coverFormat({ still, rectangles, lines, tuning = DEFAULT_COVER_FORMAT_TUNING }: CoverFormatInput): FormatCue | undefined {
  const shape = formatFromShape(still, rectangles, tuning);
  const logos = formatsFromLogos(lines, tuning);
  if (logos.length > 0) {
    const agreeing = shape === undefined ? undefined : logos.find((logo) => family(logo) === family(shape));
    return { format: agreeing ?? logos[0], source: 'logo' };
  }
  return shape === undefined ? undefined : { format: shape, source: 'shape' };
}

// ---- Correcting a read --------------------------------------------------------------------------------------------

const FILM_RELEASE_FORMATS = new Set((['vhs', 'dvd', 'bluray', 'uhdBluray'] as const).flatMap((f) => mapKind(f).format ?? []));
const hasFilmFormat = (item: ReadItem) => item.kind === 'film' && item.format !== undefined && FILM_RELEASE_FORMATS.has(item.format);

/**
 * The item with the format the case says.
 * - A shape cue only switches one film format for another. Books are the same shape as DVDs and VHS tapes, so the shape
 *   never makes a book, a game or an "other" into a film.
 * - A logo is explicit: it also turns a book or an "other" into a film in that format.
 * - A game is never touched.
 * An item that already has the format comes back as the same object.
 */
export function correctFormat(item: ReadItem, cue: FormatCue): ReadItem {
  if (item.kind === 'game') return item;
  if (cue.source === 'shape' && !hasFilmFormat(item)) return item;
  const target = mapKind(cue.format);
  if (item.kind === target.kind && item.format === target.format) return item;
  return { ...item, kind: target.kind, format: target.format };
}
