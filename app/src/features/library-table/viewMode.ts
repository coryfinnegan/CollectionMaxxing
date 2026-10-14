// Table or grid: remembered in localStorage (never required to work).
export type ViewMode = 'table' | 'grid';

export const VIEW_KEY = 'cm.library.view';

const storage = (): Storage | undefined => (typeof globalThis.localStorage === 'undefined' ? undefined : globalThis.localStorage);

export function loadViewMode(): ViewMode {
  try {
    return storage()?.getItem(VIEW_KEY) === 'grid' ? 'grid' : 'table';
  } catch {
    return 'table';
  }
}

export function saveViewMode(mode: ViewMode): void {
  try {
    storage()?.setItem(VIEW_KEY, mode);
  } catch {
    // Blocked storage: the choice lasts until the page reloads.
  }
}
