// Reading part of a picture. Swift reads each region as its own request and returns its boxes in that region's own frame (normalised 0..1,
// origin top-left, measured inside the region); the pure functions here put them back on the whole picture, so the Swift stays thin.
import type { Rect, TextLine } from './types';

/** A box measured inside `region` (both normalised, origin top-left), as the same box on the whole picture. */
export function boxInPicture(box: Rect, region: Rect): Rect {
  return {
    x: region.x + box.x * region.width,
    y: region.y + box.y * region.height,
    width: box.width * region.width,
    height: box.height * region.height,
  };
}

/**
 * Lines read by `regions` (each line's `region` is its index there), with every box put on the whole picture. A line that no region
 * read (it has no `region`) is returned as it is; a line that names a region that was not asked for cannot be placed, so it is left out.
 * The lines given are not changed.
 */
export function linesInPicture(lines: TextLine[], regions: Rect[]): TextLine[] {
  return lines.flatMap((line) => {
    if (line.region === undefined) return [line];
    const region = regions[line.region];
    return region ? [{ ...line, box: boxInPicture(line.box, region) }] : [];
  });
}

/**
 * `columns` x `rows` equal tiles, top row first and left to right in a row, each reaching `overlap` of the picture's width (or height) into its
 * neighbour: half of it on each side that has a neighbour, so two neighbours share a strip `overlap` wide and the edge tiles stay inside the picture.
 */
export function gridRegions(columns: number, rows: number, overlap: number): Rect[] {
  const span = (index: number, count: number) => {
    const start = Math.max(0, index / count - overlap / 2);
    const end = Math.min(1, (index + 1) / count + overlap / 2);
    return { start, size: end - start };
  };
  const tiles: Rect[] = [];
  for (let row = 0; row < rows; row += 1) {
    for (let column = 0; column < columns; column += 1) {
      const across = span(column, columns);
      const down = span(row, rows);
      tiles.push({ x: across.start, y: down.start, width: across.size, height: down.size });
    }
  }
  return tiles;
}
