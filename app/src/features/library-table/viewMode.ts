// Table or grid: remembered in localStorage (never required to work).
import { Dimensions } from 'react-native';
import { WIDE_BREAKPOINT } from '@/theme/tokens';

export type ViewMode = 'table' | 'grid';

export const VIEW_KEY = 'cm.library.view';

const storage = (): Storage | undefined => (typeof globalThis.localStorage === 'undefined' ? undefined : globalThis.localStorage);

/** With no saved choice: the table on a wide screen, the grid on a phone (a wide table is a poor fit there). A saved choice always wins. */
export function loadViewMode(width: number = Dimensions.get('window').width): ViewMode {
  const fallback: ViewMode = width < WIDE_BREAKPOINT ? 'grid' : 'table';
  try {
    const saved = storage()?.getItem(VIEW_KEY);
    return saved === 'grid' || saved === 'table' ? saved : fallback;
  } catch {
    return fallback;
  }
}

export function saveViewMode(mode: ViewMode): void {
  try {
    storage()?.setItem(VIEW_KEY, mode);
  } catch {
    // Blocked storage: the choice lasts until the page reloads.
  }
}
