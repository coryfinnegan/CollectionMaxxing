// Everything a single cover says about the item on it, and how that corrects what an engine read. The case says the film format (its
// proportions, the logos printed on it: coverFormat.ts) and the printed text says the kind (the Criterion Collection, a console's name, a
// bestseller line: coverKind.ts). One cue, applied the same way to the picture model, the OCR-text tier and the Vision tier.
import { asFilmIn, correctFormat, coverFormat, hasFilmFormat, measureShape, type CoverFormatInput, type FormatCue, type ShapeMeasure } from './coverFormat';
import { coverKind, type CueKind } from './coverKind';
import { DEFAULT_COVER_FORMAT_TUNING } from './defaults';
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

/** What the cue search had to go on, and what it made of it. */
export interface CoverLook {
  input: CoverFormatInput;
  cue: CoverCue | undefined;
}

/**
 * How the wait for the cue ended: it was `ready` when the engine's read ended, arrived `late` (after the read, inside the cap), was given up on
 * at the cap (`timeout`), or the search itself failed (`failed`).
 */
export type CueArrival = 'ready' | 'late' | 'timeout' | 'failed';

/** The rectangles that were left out, and why: " (1 frame-hugging, 1 too small)". Empty when none was. */
function rejections(shape: ShapeMeasure): string {
  const reasons: [number, string][] = [
    [shape.unusable, 'unusable'],
    [shape.frame, 'frame-hugging'],
    [shape.small, 'too small'],
    [shape.outOfBand, 'out of band'],
  ];
  const left = reasons.filter(([count]) => count > 0);
  return left.length === 0 ? '' : ` (${left.map(([count, why]) => `${count} ${why}`).join(', ')})`;
}

/**
 * A short plain-text account of what the cue saw, for the eval record only: the scanner ignores it. It is what explains a wrong format (the case
 * was never found, was too small, hugged the frame, was shaped like no case, or the cue came too late). One line of `key=value` words, always in
 * this order, and under 200 characters:
 *
 *     cue=ready fmt=shape:bluray r=0.86 cov=0.73 rects=3 (1 frame-hugging, 1 too small) kind=film code=SNS-8C-USA
 *
 * - `cue=` how the wait ended (`CueArrival`). When it ended without a cue (`timeout`, `failed`) that is all there is.
 * - `fmt=` the format cue's source and format (`shape:bluray`, `logo:dvd`), or `none`.
 * - `r=` and `cov=` the short over long ratio and the share of the picture of the largest rectangle that is not the frame, which the shape is read
 *   from, even when it was too small or out of band. Absent when there was none.
 * - `rects=` how many rectangles Vision found, then how many were left out and why (`unusable`, `frame-hugging`, `too small`, `out of band`).
 * - `kind=` the kind the text named (`film`, `game`, `book`) or `none`; `code=` the game product code the text printed, or `none`.
 */
export function describeCue(arrival: CueArrival, look?: CoverLook): string {
  if (!look) return `cue=${arrival}`;
  const { input, cue } = look;
  const shape = measureShape(input.still, input.rectangles, input.tuning ?? DEFAULT_COVER_FORMAT_TUNING);
  return [
    `cue=${arrival}`,
    `fmt=${cue?.format ? `${cue.format.source}:${cue.format.format}` : 'none'}`,
    ...(shape.largest ? [`r=${shape.largest.ratio.toFixed(2)}`, `cov=${shape.largest.coverage.toFixed(2)}`] : []),
    `rects=${shape.seen}${rejections(shape)}`,
    `kind=${cue?.kind ?? 'none'}`,
    `code=${cue?.productCode ?? 'none'}`,
  ].join(' ');
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
