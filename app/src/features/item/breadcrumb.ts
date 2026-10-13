import type { LocationDto } from '@/api/generated/models';

/** "Living room › Shelf 2" for a location id; "Not placed" when there is none. */
export function breadcrumb(locations: readonly LocationDto[], locationId: string | null | undefined): string {
  const byId = new Map(locations.map((l) => [l.id, l]));
  const names: string[] = [];
  const seen = new Set<string>();
  let current = locationId ? byId.get(locationId) : undefined;
  while (current && !seen.has(current.id)) {
    seen.add(current.id);
    names.unshift(current.name);
    current = current.parentId ? byId.get(current.parentId) : undefined;
  }
  return names.length ? names.join(' › ') : 'Not placed';
}
