// Everything a single cover says about the item on it, and how that corrects what an engine read. The case says the film format (its
// proportions, the logos printed on it: coverFormat.ts) and the printed text says the kind (the Criterion Collection, a console's name, a
// bestseller line: coverKind.ts). One cue, applied the same way to the picture model, the OCR-text tier and the Vision tier.
import { asFilmIn, correctFormat, coverFormat, hasFilmFormat, type CoverFormatInput, type FormatCue } from './coverFormat';
import { coverKind, type CueKind } from './coverKind';
import { productCodeOf } from './productCode';
import type { ReadItem } from './types';

export interface CoverCue {
  format?: FormatCue;
  kind?: CueKind;
  /** The game product code the cover's text prints (ADR-031); it goes only on a game read, or one that has no kind yet. */
  productCode?: string;
}

/** The cue the still gives, or nothing. The same input as the format cue: the still's size, Vision's rectangles and Vision's text lines. */
export function coverCue(input: CoverFormatInput): CoverCue | undefined {
  const format = coverFormat(input);
  const kind = coverKind(input.lines, input.tuning);
  const productCode = productCodeOf(input.lines);
  return format || kind || productCode
    ? { ...(format ? { format } : {}), ...(kind ? { kind } : {}), ...(productCode ? { productCode } : {}) }
    : undefined;
}

/** The item as `kind`, with no film format: a format belongs to a film, and nothing in the cue says one for a game or a book. */
function asKind(item: ReadItem, kind: ReadItem['kind']): ReadItem {
  const next: ReadItem = { ...item, kind };
  delete next.format;
  return next;
}

/**
 * The item with the kind the cover's text names. A game read is never changed to anything else. A bestseller line only settles a read that
 * has no kind yet (the Vision tier's "other"): the picture model and the text tier read books and films themselves, and a film cover can
 * quote a bestseller.
 */
function correctKind(item: ReadItem, kind: CueKind): ReadItem {
  if (item.kind === 'game') return item;
  switch (kind) {
    case 'game':
      return asKind(item, 'game');
    case 'film':
      return item.kind === 'film' ? item : asKind(item, 'film');
    case 'book':
      return item.kind === 'other' ? asKind(item, 'book') : item;
  }
}

/**
 * The item with the product code the cover prints, once everything else has decided what the item is. Only a game takes the code, or an
 * "other" (a read with no kind: the Vision tier's), which the code makes a game. A book or a film is never changed because of a
 * code-like string, and never given the code: the server turns any read that has one into the game the code names.
 */
function withProductCode(item: ReadItem, productCode: string): ReadItem {
  if (item.kind !== 'game' && item.kind !== 'other') return item;
  const game = item.kind === 'game' ? item : asKind(item, 'game');
  return game.productCode === productCode ? game : { ...game, productCode };
}

/**
 * The item as the cover says it is.
 * - A format logo is explicit and beats every kind cue (the rules for it are `correctFormat`'s).
 * - Otherwise the kind the text names is applied first, then the shape of the case: it only switches between film formats, so it never
 *   makes a book, a game or an "other" into a film. A film that the text alone made (the Criterion Collection says no format) takes the
 *   shape's.
 * - Last, a product code goes on what is then a game or has no kind (`withProductCode`).
 * An item the cue changes nothing about comes back as the same object.
 */
export function correctRead(item: ReadItem, cue: CoverCue): ReadItem {
  const corrected = correctKindAndFormat(item, cue);
  return cue.productCode ? withProductCode(corrected, cue.productCode) : corrected;
}

function correctKindAndFormat(item: ReadItem, cue: CoverCue): ReadItem {
  const { format, kind } = cue;
  if (format?.source === 'logo') return correctFormat(item, format);
  const byKind = kind ? correctKind(item, kind) : item;
  if (format?.source !== 'shape' || byKind.kind === 'game') return byKind;
  const filmNamedByText = kind === 'film' && byKind.kind === 'film';
  return hasFilmFormat(byKind) || filmNamedByText ? asFilmIn(byKind, format.format) : byKind;
}
