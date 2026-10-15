// Small builders for the offline tests.
import type { SnapshotEntry } from './ownedIndex';

let counter = 0;
const uuid = (n: number) => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`;

export const entry = (overrides: Partial<SnapshotEntry> = {}): SnapshotEntry => {
  counter += 1;
  return {
    itemId: uuid(counter),
    deleted: false,
    releaseId: uuid(10_000 + counter),
    workId: uuid(20_000 + counter),
    title: `Title ${counter}`,
    format: 'vhs',
    platform: null,
    identifiers: [],
    locationPath: null,
    ...overrides,
  };
};

export const tombstone = (itemId: string): SnapshotEntry => ({
  itemId,
  deleted: true,
  releaseId: null,
  workId: null,
  title: null,
  format: null,
  platform: null,
  identifiers: [],
  locationPath: null,
});
