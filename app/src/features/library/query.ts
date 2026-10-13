// Pure logic for the library list: filter state to query params, and the plain-word labels.
import type { ItemStatus, ListItemsParams, ReleaseFormat, WorkKind } from '@/api/generated/models';

export const PAGE_SIZE = 30;

export type SortValue = '-addedAt' | 'title' | 'year' | 'platform';

export const sortOptions: { value: SortValue; label: string }[] = [
  { value: '-addedAt', label: 'Recently added' },
  { value: 'title', label: 'Title' },
  { value: 'year', label: 'Year' },
  { value: 'platform', label: 'Platform' },
];

export const sortLabel = (value: SortValue): string => sortOptions.find((o) => o.value === value)?.label ?? value;

export type LibraryFilters = {
  q: string;
  kind?: WorkKind;
  format?: ReleaseFormat;
  platform?: string;
  locationId?: string;
  sort: SortValue;
};

export type ListState = { filters: LibraryFilters; cursor?: string };

export const defaultState = (): ListState => ({ filters: { q: '', sort: '-addedAt' } });

/** Changes filters and goes back to the first page: a cursor only makes sense for the filters that produced it. */
export function changeFilter(state: ListState, patch: Partial<LibraryFilters>): ListState {
  return { filters: { ...state.filters, ...patch }, cursor: undefined };
}

/** Builds the GET /items query. `fixedStatus` is the Wishlist or Lent out view's status. */
export function toListParams(state: ListState, fixedStatus?: ItemStatus): ListItemsParams {
  const { q, kind, format, platform, locationId, sort } = state.filters;
  const params: ListItemsParams = { sort, limit: PAGE_SIZE };
  if (q.trim()) params.q = q.trim();
  if (kind) params.kind = kind;
  if (format) params.format = format;
  if (platform) params.platform = platform;
  if (locationId) params.locationId = locationId;
  if (fixedStatus) params.status = fixedStatus;
  if (state.cursor) params.cursor = state.cursor;
  return params;
}

/** True when any filter narrows the list (used to choose between "nothing here" and "nothing matches"). */
export const hasActiveFilter = (filters: LibraryFilters): boolean =>
  !!(filters.q.trim() || filters.kind || filters.format || filters.platform || filters.locationId);
