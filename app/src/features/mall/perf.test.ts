// Mode data-path budgets (#357): dealing 500 items onto shelves and anchors
// stays far under a frame. The lists themselves window through FlatList, the
// same virtualization the grid ships, so this pins the per-item mapping cost.
import type { ItemSummaryDto, WorkKind } from '@/api/generated/models';
import { toShelves } from '@/features/library/CastleShelf';
import { anchorSlices, itemsForRoom } from './anchors';
import { mallRooms } from './rooms';

const KINDS: WorkKind[] = ['film', 'game', 'book', 'tv', 'other'];

function makeItem(n: number): ItemSummaryDto {
  const id = `f0000000-0000-4000-8000-${String(n).padStart(12, '0')}`;
  return {
    id,
    releaseId: id,
    workId: id,
    title: `Perf item ${n}`,
    kind: KINDS[n % KINDS.length],
    format: 'dvd',
    platform: null,
    year: 2000,
    status: 'owned',
    condition: null,
    locationId: null,
    coverUrl: null,
    createdAt: '2026-09-01T12:00:00Z',
    editionVerified: true,
  };
}

const library = Array.from({ length: 500 }, (_, n) => makeItem(n + 1));

/** A quarter second: generous on any machine, still far under any interaction budget. */
const BUDGET_MS = 250;

function timed<T>(label: string, run: () => T): T {
  const start = performance.now();
  const result = run();
  const ms = performance.now() - start;
  // eslint-disable-next-line no-console
  console.log(`${label}: ${ms.toFixed(1)}ms`);
  expect(ms).toBeLessThan(BUDGET_MS);
  return result;
}

describe('mode data-path budgets', () => {
  it('deals 500 items onto castle shelves inside the budget', () => {
    const shelves = timed('castle shelves', () => toShelves(library, 12));

    expect(shelves).toHaveLength(42);
    expect(shelves.flatMap((shelf) => shelf.items)).toHaveLength(500);
  });

  it('maps 500 items into every room inside the budget', () => {
    for (const room of mallRooms) {
      const slices = timed(`mall ${room}`, () => anchorSlices(room, library));
      const shown = slices.flatMap((slice) => slice.items).length;
      expect(shown).toBeLessThanOrEqual(slices.length * 8);
      expect(itemsForRoom(room, library).length).toBeGreaterThan(0);
    }
  });
});
