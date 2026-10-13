import type { LocationDto } from '@/api/generated/models';
import { flattenTree } from './tree';

const loc = (id: string, name: string, parentId: string | null = null, sortOrder = 0): LocationDto => ({
  id,
  name,
  parentId,
  sortOrder,
});

describe('flattenTree', () => {
  it('lists children directly under their parent with increasing depth', () => {
    const rows = flattenTree([loc('c', 'Shelf 1', 'a'), loc('a', 'Game room'), loc('b', 'Attic', null, 1)]);
    expect(rows.map((r) => [r.location.name, r.depth])).toEqual([
      ['Game room', 0],
      ['Shelf 1', 1],
      ['Attic', 0],
    ]);
  });

  it('orders siblings by sort order, then by name', () => {
    const rows = flattenTree([loc('1', 'B', null, 0), loc('2', 'A', null, 0), loc('3', 'Z', null, -1)]);
    expect(rows.map((r) => r.location.name)).toEqual(['Z', 'A', 'B']);
  });

  it('shows a location whose parent is missing at the top level', () => {
    const rows = flattenTree([loc('1', 'Lost', 'gone')]);
    expect(rows).toEqual([{ location: loc('1', 'Lost', 'gone'), depth: 0 }]);
  });
});
