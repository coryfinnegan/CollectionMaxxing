// Splitting what Vision saw into items, with no model: rectangles first, then piles of items lying flat (one item per band of text) and spine
// columns (found from the tall lines), else the whole frame. Both also split a rectangle that holds several items, and read the lines that
// sit outside every rectangle (#509).
import type { Rect, TextLine } from '@/native/cm-vision';
import type { LocalTuning, RecognitionConfig } from './defaults';
import { bottom, centerX, centerY, contains, overlapShare, right, union, yOverlapShare } from './geometry';

export interface Piece {
  lines: TextLine[];
  box: Rect;
  /** Set for an item lying flat in a stack, whose title may run over several lines. Absent: the title is the best single line. */
  title?: string;
}

export interface Split {
  pieces: Piece[];
  /** Rectangles or columns that held no text. */
  unreadableCount: number;
}

const readingOrder = (a: TextLine, b: TextLine) => a.box.y - b.box.y || a.box.x - b.box.x;

function piece(lines: TextLine[], box?: Rect): Piece {
  const ordered = [...lines].sort(readingOrder);
  return { lines: ordered, box: box ?? union(ordered.map((l) => l.box)) };
}

/** Rectangles that largely overlap depict one surface (a cover's nested quads); disjoint spine quads pass through. */
function mergeSurfaces(rectangles: Rect[], minOverlap: number): Rect[] {
  const groups: Rect[][] = [];
  for (const rect of [...rectangles].sort((a, b) => a.x - b.x || a.y - b.y)) {
    const group = groups.find((g) => g.some((r) => overlapShare(r, rect) >= minOverlap));
    if (group) group.push(rect);
    else groups.push([rect]);
  }
  return groups.map(union);
}

/** One item per rectangle, or one per spine when the text inside forms spine columns in one row. `outside` holds the lines in no rectangle. */
function byRectangles(lines: TextLine[], rectangles: Rect[], tuning: LocalTuning, minWidth: number): Split & { outside: TextLine[] } {
  const sorted = mergeSurfaces(rectangles, tuning.rectangleMergeOverlap);
  const pieces: Piece[] = [];
  const taken = new Set<TextLine>();
  let unreadableCount = 0;
  for (const rect of sorted) {
    const inside = lines.filter((l) => contains(rect, centerX(l.box), centerY(l.box)));
    inside.forEach((l) => taken.add(l));
    if (inside.length === 0) unreadableCount += 1;
    else pieces.push(...segment(inside, tuning, minWidth, rect));
  }
  return { pieces, unreadableCount, outside: lines.filter((l) => !taken.has(l)) };
}

const isTall = (l: TextLine) => bottom(l.box) - l.box.y > l.box.width;

interface Column {
  box: Rect;
  lines: TextLine[];
}

/** Lines left to right (the tall ones for spines, the flat ones for a pile): a line joins the last column when it shares enough of its own width with it. */
function columnsOf(columnLines: TextLine[], tuning: LocalTuning): Column[] {
  const columns: Column[] = [];
  for (const l of [...columnLines].sort((a, b) => a.box.x - b.box.x)) {
    const last = columns[columns.length - 1];
    const shared = last ? Math.min(right(last.box), right(l.box)) - Math.max(last.box.x, l.box.x) : 0;
    if (last && shared >= l.box.width * tuning.spineColumnOverlap) {
      last.lines.push(l);
      last.box = union([last.box, l.box]);
    } else {
      columns.push({ box: l.box, lines: [l] });
    }
  }
  return columns;
}

/**
 * Spines stand in one row: most neighbors share their vertical band, so one short book beside tall ones does not undo the split.
 * Scattered cover lines share almost none, so they are not split (#504).
 */
function inOneRow(columns: Column[], tuning: LocalTuning): boolean {
  const pairs = columns.length - 1;
  const sharing = columns.filter((c, i) => i > 0 && yOverlapShare(columns[i - 1].box, c.box) >= tuning.spineRowOverlap).length;
  return sharing / pairs >= tuning.spineRowShare;
}

const isFlat = (l: TextLine, tuning: LocalTuning) => l.box.width >= l.box.height * tuning.flatLineAspect;

interface Band {
  top: number;
  bottom: number;
  /** The tallest lettering in the band. */
  letters: number;
  lines: TextLine[];
}

/**
 * One column of flat lines, cut into bands from the biggest lettering down: a line joins the band it overlaps or touches (within `bandMaxGap`
 * of its own height): the small print on a tape, a repeat reading of the same text, the second line of a title. Any other line starts a
 * band. Only lettering about as big as the band's own stretches it, so small print never bridges two bands.
 */
function bandsOf(flat: TextLine[], tuning: LocalTuning): Band[] {
  const bands: Band[] = [];
  for (const l of [...flat].sort((a, b) => b.box.height - a.box.height)) {
    const top = l.box.y;
    const low = bottom(l.box);
    const shared = (b: Band) => Math.min(b.bottom, low) - Math.max(b.top, top);
    const home = bands.filter((b) => shared(b) >= -l.box.height * tuning.bandMaxGap).sort((a, b) => shared(b) - shared(a))[0];
    if (!home) {
      bands.push({ top, bottom: low, letters: l.box.height, lines: [l] });
      continue;
    }
    home.lines.push(l);
    if (l.box.height >= home.letters * tuning.bandSameLetters) {
      home.top = Math.min(home.top, top);
      home.bottom = Math.max(home.bottom, low);
    }
  }
  return bands.sort((a, b) => a.top - b.top);
}

/** A band's title is its big lettering from the top down, one reading per place (the surer of two readings of the same box). */
function bandTitle(lines: TextLine[], tuning: LocalTuning): string {
  const tallest = Math.max(...lines.map((l) => l.box.height));
  const big = lines.filter((l) => l.box.height >= tallest * tuning.bandTitleLetters).sort((a, b) => b.confidence - a.confidence);
  const distinct = big.filter((l, i) => !big.slice(0, i).some((k) => overlapShare(k.box, l.box) >= tuning.duplicateLineOverlap));
  return distinct.sort(readingOrder).map((l) => l.text).join(' ');
}

/**
 * A column of flat lines is a pile of spines when it is as wide as a spine and no wider than half the picture (a cover fills the frame), has
 * enough bands, and its bands sit close: the median gap between two bands is under `stackMaxGap` of their letters. A name running down a
 * cover is narrower, and a cover's title, author and tagline sit far apart.
 */
function isPile(column: Column, bands: Band[], tuning: LocalTuning): boolean {
  if (column.box.width < tuning.stackMinWidth || column.box.width > tuning.stackMaxWidth || bands.length < tuning.stackMinBands) return false;
  const gaps = bands.slice(1).map((b, i) => (b.top - bands[i].bottom) / Math.max(b.letters, bands[i].letters));
  return gaps.sort((a, b) => a - b)[Math.floor(gaps.length / 2)] <= tuning.stackMaxGap;
}

/** Piles of items lying flat: one piece per band. `rest` holds every line that is in no pile. */
function byPiles(lines: TextLine[], tuning: LocalTuning): { pieces: Piece[]; rest: TextLine[] } {
  const pieces: Piece[] = [];
  const rest = new Set(lines);
  for (const column of columnsOf(lines.filter((l) => isFlat(l, tuning)), tuning)) {
    const bands = bandsOf(column.lines, tuning);
    const biggest = Math.max(...bands.map((b) => b.letters));
    const real = bands.filter((b) => b.letters >= biggest * tuning.bandMinLetters);
    if (!isPile(column, real, tuning)) continue;
    for (const b of real) {
      pieces.push({ ...piece(b.lines), title: bandTitle(b.lines, tuning) });
      b.lines.forEach((l) => rest.delete(l));
    }
  }
  return { pieces, rest: [...rest] };
}

/** Whether the lines hold a pile of items lying flat (the cue to read the picture upside down too). */
export function hasPile(lines: TextLine[], tuning: LocalTuning): boolean {
  return byPiles(lines, tuning).pieces.length > 0;
}

/**
 * Upright spines and piles of items lying flat, side by side: piles are cut first, so a flat stack below a row of spines does not
 * lend its lines to the spine above. What is left is split by spine columns, else stays one piece (with `box`, when nothing was cut).
 */
function segment(lines: TextLine[], tuning: LocalTuning, minWidth: number, box?: Rect): Piece[] {
  const piles = byPiles(lines, tuning);
  const cut = piles.pieces.length > 0;
  const upright = bySpineColumns(piles.rest, tuning, minWidth)?.pieces ?? (piles.rest.length > 0 ? [piece(piles.rest, cut ? undefined : box)] : []);
  return [...upright, ...piles.pieces];
}

/**
 * Spine columns come from the tall lines alone, so books lying flat in the frame do not hide the spines. A short line whose center
 * sits across a column inside the row's band joins that column; every other line (a flat stack below the row) becomes one more piece.
 * A tall line in a column too narrow to be a spine is treated like a short line, so no line is dropped.
 */
function bySpineColumns(lines: TextLine[], tuning: LocalTuning, minWidth: number): Split | undefined {
  const tallColumns = columnsOf(lines.filter(isTall), tuning);
  const columns = tallColumns.filter((c) => c.box.width >= minWidth);
  if (columns.length < tuning.minSpineColumns || !inOneRow(columns, tuning)) return undefined;
  const band = union(columns.map((c) => c.box));
  const narrow = tallColumns.filter((c) => !columns.includes(c)).flatMap((c) => c.lines);
  const rest: TextLine[] = [];
  for (const l of [...lines.filter((l) => !isTall(l)), ...narrow]) {
    const x = centerX(l.box);
    const across = centerY(l.box) >= band.y && centerY(l.box) <= bottom(band) ? columns.filter((c) => x >= c.box.x && x <= right(c.box)) : [];
    if (across.length === 0) rest.push(l);
    else across.reduce((a, b) => (Math.abs(x - centerX(b.box)) < Math.abs(x - centerX(a.box)) ? b : a)).lines.push(l);
  }
  const pieces = columns.map((c) => piece(c.lines));
  if (rest.length > 0) pieces.push(piece(rest));
  return { pieces, unreadableCount: 0 };
}

/** One item for the whole frame: the cover path, never segmented. Empty when there is no text. */
export function wholeFrame(lines: TextLine[]): Split {
  if (lines.length === 0) return { pieces: [], unreadableCount: 0 };
  return { pieces: [piece(lines)], unreadableCount: 0 };
}

export function splitIntoPieces(
  lines: TextLine[],
  rectangles: Rect[],
  splitting: RecognitionConfig['splitting'],
  tuning: LocalTuning,
): Split {
  if (lines.length === 0) return { pieces: [], unreadableCount: 0 };
  const withoutRectangles = (rest: TextLine[]): Split => ({ pieces: segment(rest, tuning, splitting.minSpineColumnWidth), unreadableCount: 0 });
  if (rectangles.length > 0) {
    const { outside, ...split } = byRectangles(lines, rectangles, tuning, splitting.minSpineColumnWidth);
    // Vision may box only some of the books: the lines outside every rectangle still become items.
    if (split.pieces.length > 0) return { ...split, pieces: [...split.pieces, ...withoutRectangles(outside).pieces] };
  }
  return withoutRectangles(lines);
}
