// The Great Hall's milestone arms (#363): earned at fixed collection sizes, with the road to the next rung.
import { earnedMilestones, MILESTONE_LADDER, nextMilestone } from './milestones';

describe('milestones', () => {
  it('raises arms at 10, 50, 100, 250, 500 and 1000 items', () => {
    expect(MILESTONE_LADDER).toEqual([10, 50, 100, 250, 500, 1000]);
  });

  it.each([
    [0, []],
    [9, []],
    [10, [10]],
    [35, [10]],
    [100, [10, 50, 100]],
    [2500, [10, 50, 100, 250, 500, 1000]],
  ])('earns %s at %s items', (total: number, earned: number[]) => {
    expect(earnedMilestones(total)).toEqual(earned);
  });

  it.each([
    [0, { at: 10, toGo: 10 }],
    [9, { at: 10, toGo: 1 }],
    [35, { at: 50, toGo: 15 }],
    [100, { at: 250, toGo: 150 }],
    [1000, null],
    [2500, null],
  ])('points %s items at %s next', (total: number, next: { at: number; toGo: number } | null) => {
    expect(nextMilestone(total)).toEqual(next);
  });
});
