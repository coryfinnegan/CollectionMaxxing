// The castle decoration density (#357): the full theme for a bare shelf, the
// frame for a handful of items, covers and the header once the shelf fills.
// The count is the loaded items; the grid stays the fast path at any count.
export type CastleDensity = 'full' | 'frame' | 'covers';

/** Full at 0-1 items, frame-only at 2-12, covers and the header beyond that. */
export function castleDensityForCount(count: number): CastleDensity {
  if (count <= 1) return 'full';
  if (count <= 12) return 'frame';
  return 'covers';
}
