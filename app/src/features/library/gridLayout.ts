// How many covers fit across the library grid. The count follows the grid's own width, not the window's: on the
// web the sidebar takes 220 px, so the window is wider than the room the grid has.
import { spacing } from '@/theme/tokens';

/** The narrowest a cover tile may get once there is room for more than three columns. */
const MIN_TILE = 140;
const MIN_COLUMNS = 3;
const MAX_COLUMNS = 6;

/** Columns for a grid `width` px wide (the list's own width, padding included). Always 3 to 6. */
export function gridColumns(width: number): number {
  const fit = Math.floor((width - spacing.lg * 2 + spacing.md) / (MIN_TILE + spacing.md));
  return Math.min(MAX_COLUMNS, Math.max(MIN_COLUMNS, fit));
}

/** The width of one tile when `columns` share a grid `width` px wide, after its padding and the gaps between tiles. */
export const tileWidthFor = (width: number, columns: number): number =>
  Math.floor((width - spacing.lg * 2 - spacing.md * (columns - 1)) / columns);
