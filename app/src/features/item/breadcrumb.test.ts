import type { LocationDto } from '@/api/generated/models';
import { breadcrumb } from './breadcrumb';

const loc = (id: string, name: string, parentId: string | null): LocationDto => ({ id, name, parentId, sortOrder: 0 });
const locations = [loc('a', 'Living room', null), loc('b', 'Shelf 2', 'a'), loc('c', 'Box', 'b')];

describe('breadcrumb', () => {
  it('joins the chain from the room down', () => {
    expect(breadcrumb(locations, 'c')).toBe('Living room › Shelf 2 › Box');
  });

  it('is "Not placed" without a location, and for an unknown one', () => {
    expect(breadcrumb(locations, null)).toBe('Not placed');
    expect(breadcrumb(locations, 'zzz')).toBe('Not placed');
  });

  it('survives a parent loop', () => {
    const loop = [loc('a', 'A', 'b'), loc('b', 'B', 'a')];
    expect(breadcrumb(loop, 'a')).toBe('B › A');
  });
});
