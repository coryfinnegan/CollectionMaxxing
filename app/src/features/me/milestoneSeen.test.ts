// The milestone moment (#362): the first sight of a rung seeds quietly, and only
// a later climb celebrates, exactly once per rung.
import { dismissMilestone, milestoneMoment, setMilestoneSeenStorage } from './milestoneSeen';

function memoryStore() {
  const map = new Map<string, string>();
  return {
    getString: (key: string) => map.get(key),
    set: (key: string, value: string) => void map.set(key, value),
  };
}

beforeEach(() => {
  setMilestoneSeenStorage(memoryStore());
});

describe('milestoneMoment', () => {
  it('stays quiet on the first sight, however high the shelves already are', () => {
    expect(milestoneMoment('lib', 100)).toBeNull();
    expect(milestoneMoment('lib', 100)).toBeNull();
  });

  it('stays quiet below the first rung, then celebrates the climb onto it', () => {
    expect(milestoneMoment('lib', undefined)).toBeNull();
    expect(milestoneMoment('lib', 10)).toBe(10);
  });

  it('celebrates a later climb exactly once, until dismissed', () => {
    milestoneMoment('lib', 10);

    expect(milestoneMoment('lib', 50)).toBe(50);
    // Still unseen, so still celebrating.
    expect(milestoneMoment('lib', 50)).toBe(50);
    dismissMilestone('lib', 50);
    expect(milestoneMoment('lib', 50)).toBeNull();
  });

  it('stays quiet when items leave and never celebrates the way back up', () => {
    milestoneMoment('lib', 50);

    expect(milestoneMoment('lib', 10)).toBeNull();
    expect(milestoneMoment('lib', 50)).toBeNull();
  });

  it('keeps a rung per library and reseeds a ragged store quietly', () => {
    const store = memoryStore();
    store.set('milestone-seen:ragged', 'not a rung');
    setMilestoneSeenStorage(store);

    expect(milestoneMoment('ragged', 50)).toBeNull();
    expect(milestoneMoment('ragged', 50)).toBeNull();
    expect(milestoneMoment('other', 50)).toBeNull();
    expect(milestoneMoment('other', 100)).toBe(100);
  });
});
