// Table columns: which exist, which the API can sort by, and the visible set persisted in localStorage.
import type { SortValue } from '@/features/library/query';

export type ColumnId = 'cover' | 'title' | 'format' | 'platform' | 'year' | 'location' | 'status' | 'added';

export const columnDefs: { id: ColumnId; label: string; sort?: SortValue; width: number }[] = [
  { id: 'cover', label: 'Cover', width: 64 },
  { id: 'title', label: 'Title', sort: 'title', width: 280 },
  { id: 'format', label: 'Format', width: 130 },
  { id: 'platform', label: 'Platform', sort: 'platform', width: 150 },
  { id: 'year', label: 'Year', sort: 'year', width: 80 },
  { id: 'location', label: 'Location', width: 190 },
  { id: 'status', label: 'Status', width: 130 },
  { id: 'added', label: 'Added', sort: '-addedAt', width: 120 },
];

const allIds = columnDefs.map((c) => c.id);

export const STORAGE_KEY = 'cm.library.columns';

export const defaultColumns = (): ColumnId[] => allIds.filter((id) => id !== 'added');

/** The API's sort value for a column, or undefined when the API can't sort by it (those headers aren't sort buttons). */
export const sortForColumn = (id: ColumnId): SortValue | undefined => columnDefs.find((c) => c.id === id)?.sort;

/** Shows or hides one column. The title always stays; the result keeps table order. */
export function toggleColumn(current: readonly ColumnId[], id: ColumnId): ColumnId[] {
  if (id === 'title') return [...current];
  const next = current.includes(id) ? current.filter((c) => c !== id) : [...current, id];
  return allIds.filter((c) => next.includes(c));
}

const storage = (): Storage | undefined => (typeof globalThis.localStorage === 'undefined' ? undefined : globalThis.localStorage);

export function loadColumns(): ColumnId[] {
  try {
    const raw = storage()?.getItem(STORAGE_KEY);
    if (!raw) return defaultColumns();
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) return defaultColumns();
    const valid = allIds.filter((id) => parsed.includes(id));
    return valid.includes('title') ? valid : defaultColumns();
  } catch {
    return defaultColumns();
  }
}

export function saveColumns(columns: readonly ColumnId[]): void {
  try {
    storage()?.setItem(STORAGE_KEY, JSON.stringify(columns));
  } catch {
    // Private windows and blocked storage: the choice just doesn't persist.
  }
}
