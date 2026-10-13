import type { LocationDto } from '@/api/generated/models';

export type LocationRowData = { location: LocationDto; depth: number };

/** Orders locations as a depth-first tree (siblings by sortOrder, then name); orphans are shown at the top level. */
export function flattenTree(locations: readonly LocationDto[]): LocationRowData[] {
  const ids = new Set(locations.map((l) => l.id));
  const children = new Map<string | null, LocationDto[]>();
  for (const location of locations) {
    const key = location.parentId && ids.has(location.parentId) ? location.parentId : null;
    const siblings = children.get(key) ?? [];
    siblings.push(location);
    children.set(key, siblings);
  }
  const rows: LocationRowData[] = [];
  const visit = (parent: string | null, depth: number) => {
    const siblings = [...(children.get(parent) ?? [])].sort(
      (a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name),
    );
    for (const location of siblings) {
      rows.push({ location, depth });
      visit(location.id, depth + 1);
    }
  };
  visit(null, 0);
  return rows;
}
