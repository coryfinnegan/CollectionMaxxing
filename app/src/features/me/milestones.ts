// The Great Hall's milestone arms (#363): coats of arms at fixed collection sizes,
// for the whole shelves and per medium. Pure counts in, earned arms and the road
// to the next one out.
export const MILESTONE_LADDER = [10, 50, 100, 250, 500, 1000] as const;

/** Every rung the count has reached, lowest first. */
export function earnedMilestones(total: number): number[] {
  return MILESTONE_LADDER.filter((rung) => total >= rung);
}

/** The next rung and how many more items it takes, or null past the top rung. */
export function nextMilestone(total: number): { at: number; toGo: number } | null {
  const at = MILESTONE_LADDER.find((rung) => total < rung);
  return at === undefined ? null : { at, toGo: at - total };
}
