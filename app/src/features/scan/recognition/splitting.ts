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

/**
 * Whether a tall line is the shorter line beside one of the column's lines: the first line of a two-line title, set a little off the
 * second (#655), shares too little of its width with the column to join it, yet it sits within the band of the line it is set beside,
 * overlapping it in x. A line about as tall as that one is the next spine's lettering, whose box overlaps the same way.
 */
function besideTaller(l: TextLine, column: Column, tuning: LocalTuning): boolean {
  return column.lines.some(
    (k) =>
      Math.min(right(k.box), right(l.box)) - Math.max(k.box.x, l.box.x) >= l.box.width * tuning.spineShortLineOverlap &&
      yOverlapShare(k.box, l.box) >= tuning.spineRowOverlap &&
      l.box.height <= k.box.height * tuning.spineShortLineHeight,
  );
}

/**
 * Lines left to right (the tall ones for spines, the flat ones for a pile): a line joins the last column when it shares enough of its own
 * width with it. A tall line that is the shorter line beside one of the column's lines joins it too, without widening the column, so the
 * lines that overlap the short line only (the next spine's author below it) do not follow it in.
 */
function columnsOf(columnLines: TextLine[], tuning: LocalTuning): Column[] {
  const columns: Column[] = [];
  for (const l of [...columnLines].sort((a, b) => a.box.x - b.box.x)) {
    const last = columns[columns.length - 1];
    const shared = last ? Math.min(right(last.box), right(l.box)) - Math.max(last.box.x, l.box.x) : 0;
    if (last && shared >= l.box.width * tuning.spineColumnOverlap) {
      last.lines.push(l);
      last.box = union([last.box, l.box]);
    } else if (last && isTall(l) && besideTaller(l, last, tuning)) {
      last.lines.push(l);
    } else {
      columns.push({ box: l.box, lines: [l] });
    }
  }
  return columns;
}

/** Whether the line's box is about one letter wide: a leaning line's box is far wider than its lettering. */
const readsUpright = (l: TextLine, tuning: LocalTuning) => l.box.height / Math.max(1, l.text.trim().length) >= l.box.width * tuning.twoRowUpright;

/**
 * Whether two tall lines are the two rows of one title printed down a spine: set in one size of lettering, each upright, running along each
 * other for most of the longer, and overlapping across by a part of a letter (at least `titleRowTouch`, at most `twoRowMaxTouch` of the
 * narrower line's width). The next spine's lettering is told apart by overlapping further, by leaning, or by running along for less.
 */
function twoRows(a: TextLine, b: TextLine, tuning: LocalTuning): boolean {
  const [narrower, wider] = [Math.min(a.box.width, b.box.width), Math.max(a.box.width, b.box.width)];
  if (!(narrower >= wider * tuning.twoRowLetters)) return false;
  const across = Math.min(right(a.box), right(b.box)) - Math.max(a.box.x, b.box.x);
  if (across < narrower * tuning.titleRowTouch || across > narrower * tuning.twoRowMaxTouch) return false;
  if (!readsUpright(a, tuning) || !readsUpright(b, tuning)) return false;
  const along = Math.min(bottom(a.box), bottom(b.box)) - Math.max(a.box.y, b.box.y);
  return along > 0 && along >= Math.max(a.box.height, b.box.height) * tuning.titleRowOverlap;
}

/** Neighboring columns whose lines include the two rows of one title are one column, so that the title is read whole and is wide enough to be a spine. */
function joinTwoRows(columns: Column[], tuning: LocalTuning): Column[] {
  const joined: Column[] = [];
  for (const column of columns) {
    const last = joined[joined.length - 1];
    if (last && last.lines.some((a) => column.lines.some((b) => twoRows(a, b, tuning)))) {
      last.lines.push(...column.lines);
      last.box = union([last.box, column.box]);
    } else {
      joined.push({ box: column.box, lines: [...column.lines] });
    }
  }
  return joined;
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

/** Words no title ends on: a title that stops on one is the first line of a longer title. */
const JOINING_WORDS = new Set(['of', 'the', 'and', 'a', 'an', 'to', 'in', 'for', 'with', 'on', 'at', 'by', 'from', 'or', '&']);

const endsOnJoiningWord = (title: string) => JOINING_WORDS.has(title.toLowerCase().split(/[^\p{L}\p{N}&]+/u).filter(Boolean).pop() ?? '');

/**
 * A hardcover lying flat prints a small "A COURT OF" a little above its big subtitle, further than `bandMaxGap`, so the two come out as bands
 * of their own. A band whose title ends on a joining word is joined to the band right below it when it lies within `bandJoinGap` of the
 * smaller band's lettering from it. Looking from the bottom up lets a title of three bands join all three.
 */
function joinBands(bands: Band[], tuning: LocalTuning): Band[] {
  const joined = [...bands];
  for (let i = joined.length - 2; i >= 0; i--) {
    const [first, next] = [joined[i], joined[i + 1]];
    const gap = next.top - first.bottom;
    if (endsOnJoiningWord(bandTitle(first.lines, tuning)) && gap <= Math.min(first.letters, next.letters) * tuning.bandJoinGap) {
      joined.splice(i, 2, { ...next, top: first.top, letters: Math.max(first.letters, next.letters), lines: [...first.lines, ...next.lines] });
    }
  }
  return joined;
}

/**
 * A column of flat lines is a pile of spines when it is as wide as a spine and no wider than `stackMaxWidth` (a cover fills the frame),
 * has enough bands, and its bands sit close: the median gap between two bands is under `stackMaxGap` of their letters (`stackSureMaxGap`
 * for a pile of five or more, whose hardcovers sit a little further apart than tapes). A name running down a cover is narrower, and a
 * cover's title, author and tagline sit far apart. A small stack of three or four (#618) is split too, but band count, width and spacing
 * alone do not tell it from a cover's three or four lines, so it must also have one size of lettering (`stackSmallLetters`, a little less
 * for a stack of four: `stackFourLetters`): tapes are set alike, a cover's title, author and tagline are not. It may stand a little wider
 * than `stackMaxWidth`.
 */
function isPile(column: Column, bands: Band[], tuning: LocalTuning): boolean {
  if (column.box.width < tuning.stackMinWidth || bands.length < tuning.stackMinBands) return false;
  const small = bands.length < tuning.stackSureBands;
  if (column.box.width > (small ? tuning.stackSmallMaxWidth : tuning.stackMaxWidth)) return false;
  const floor = bands.length === tuning.stackSureBands - 1 ? tuning.stackFourLetters : tuning.stackSmallLetters;
  if (small && Math.min(...bands.map((b) => b.letters)) < Math.max(...bands.map((b) => b.letters)) * floor) return false;
  const gaps = bands.slice(1).map((b, i) => (b.top - bands[i].bottom) / Math.max(b.letters, bands[i].letters));
  return gaps.sort((a, b) => a - b)[Math.floor(gaps.length / 2)] <= (small ? tuning.stackMaxGap : tuning.stackSureMaxGap);
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
    for (const b of joinBands(real, tuning)) {
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
 * The lines cut into shelf rows. A frame can hold two shelves of one bookcase, and a spine column joins tall lines on x alone, so a lower
 * spine would join the upper spine at its x and the two would be read as one. A cut goes in the middle of any stretch of at least
 * `shelfRowGap` letters (the median tall line's width, since tall lines read sideways) that no tall line covers, when `shelfRowMinLines`
 * tall lines lie on each side of it. Every line, tall or not, goes to the row its center sits in. Returns the lines as they are when
 * nothing is cut.
 */
function shelfRows(lines: TextLine[], tuning: LocalTuning): TextLine[][] {
  const tall = lines.filter(isTall).sort((a, b) => a.box.y - b.box.y);
  if (tall.length < 2 * tuning.shelfRowMinLines) return [lines];
  const letters = tall.map((l) => l.box.width).sort((a, b) => a - b)[Math.floor(tall.length / 2)];
  const cuts: number[] = [];
  let reach = bottom(tall[0].box);
  for (const [i, l] of tall.entries()) {
    if (i >= tuning.shelfRowMinLines && tall.length - i >= tuning.shelfRowMinLines && l.box.y - reach >= letters * tuning.shelfRowGap) {
      cuts.push((reach + l.box.y) / 2);
    }
    reach = Math.max(reach, bottom(l.box));
  }
  if (cuts.length === 0) return [lines];
  const rows: TextLine[][] = Array.from({ length: cuts.length + 1 }, () => []);
  for (const l of lines) rows[cuts.filter((c) => centerY(l.box) > c).length].push(l);
  return rows.filter((row) => row.length > 0);
}

/**
 * Upright spines and piles of items lying flat, side by side: shelf rows are cut apart first, each segmented alone. Within a row, piles
 * are cut first, so a flat stack below a row of spines does not lend its lines to the spine above. What is left is split by spine
 * columns, else stays one piece (with `box`, when nothing was cut).
 */
function segment(lines: TextLine[], tuning: LocalTuning, minWidth: number, box?: Rect): Piece[] {
  const rows = shelfRows(lines, tuning);
  if (rows.length > 1) return rows.flatMap((row) => segment(row, tuning, minWidth));
  const piles = byPiles(lines, tuning);
  const cut = piles.pieces.length > 0;
  const upright = bySpineColumns(piles.rest, tuning, minWidth)?.pieces ?? (piles.rest.length > 0 ? [piece(piles.rest, cut ? undefined : box)] : []);
  return [...upright, ...piles.pieces];
}

/**
 * An item lying flat across the tops of the spines: a flat line as tall as half a spine's lettering and at least `lyingLineWidth` of its
 * column wide, whose center is beyond the column's reach. The column's width stands in for a spine's lettering, which reads sideways.
 */
function liesAcross(l: TextLine, column: Column, letters: number, tuning: LocalTuning): boolean {
  if (!isFlat(l, tuning) || l.box.height < letters * tuning.lyingLineLetters || l.box.width < column.box.width * tuning.lyingLineWidth) return false;
  const reach = column.box.width * tuning.lyingLineReach;
  return centerY(l.box) < column.box.y - reach || centerY(l.box) > bottom(column.box) + reach;
}

/**
 * Spine columns come from the tall lines alone, so books lying flat in the frame do not hide the spines. A short line whose center
 * sits across a column inside the row's band joins that column, unless it lies across the spines (a tape on top of them, `liesAcross`):
 * those lines are cut into columns of their own, one piece each, and take in the lines left over that sit inside it. Every other line
 * (a flat stack below the row) becomes one more piece. A tall line in a column too narrow to be a spine is treated like a short line, so
 * no line is dropped.
 */
function bySpineColumns(lines: TextLine[], tuning: LocalTuning, minWidth: number): Split | undefined {
  const tallColumns = joinTwoRows(columnsOf(lines.filter(isTall), tuning), tuning);
  const columns = tallColumns.filter((c) => c.box.width >= minWidth);
  if (columns.length < tuning.minSpineColumns || !inOneRow(columns, tuning)) return undefined;
  const band = union(columns.map((c) => c.box));
  const narrow = tallColumns.filter((c) => !columns.includes(c)).flatMap((c) => c.lines);
  const letters = columns.map((c) => c.box.width).sort((a, b) => a - b)[Math.floor(columns.length / 2)];
  const rest: TextLine[] = [];
  const lying: TextLine[] = [];
  for (const l of [...lines.filter((l) => !isTall(l)), ...narrow]) {
    const x = centerX(l.box);
    const across = centerY(l.box) >= band.y && centerY(l.box) <= bottom(band) ? columns.filter((c) => x >= c.box.x && x <= right(c.box)) : [];
    if (across.length === 0) {
      rest.push(l);
      continue;
    }
    const home = across.reduce((a, b) => (Math.abs(x - centerX(b.box)) < Math.abs(x - centerX(a.box)) ? b : a));
    if (liesAcross(l, home, letters, tuning)) lying.push(l);
    else home.lines.push(l);
  }
  const pieces = columns.map((c) => piece(c.lines));
  for (const c of columnsOf(lying, tuning)) {
    const inside = rest.filter((l) => contains(c.box, centerX(l.box), centerY(l.box)));
    pieces.push(piece([...c.lines, ...inside]));
    for (const l of inside) rest.splice(rest.indexOf(l), 1);
  }
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
