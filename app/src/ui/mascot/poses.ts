// The mascot pose library (#365): ten poses over the one shared dragon. A pose is
// only layer visibility, an eyelid state, a puff count and small shifts; all
// drawing lives in layers.tsx, so no pose can redraw the dragon.
export const poseIds = [
  'idle',
  'reading',
  'sleeping',
  'celebrating',
  'searching',
  'emptyHoard',
  'oops',
  'milestone',
  'fastReading',
  'refreshPeek',
] as const;

export type MascotPose = (typeof poseIds)[number];

export type LidState = 'open' | 'squint' | 'closed';

export type LayerId = 'hoard' | 'tail' | 'body' | 'wing' | 'head' | 'horns' | 'book' | 'page' | 'shield';

/** A small move of one layer: translate, then rotate about (cx, cy) when given. */
export type LayerShift = { x?: number; y?: number; rotate?: number; cx?: number; cy?: number };

/** The loop a pose plays while motion is allowed; Reduce Motion always holds the static pose. */
export type PoseMotion = 'hop' | 'bob' | 'flutter';

export type PoseConfig = {
  layers: Record<LayerId, boolean>;
  lid: LidState;
  /** Smoke puffs trailing from the nose, 0 to 3. */
  puffs: 0 | 1 | 2 | 3;
  /** Shifts for the whole dragon plus any single layer; horns ride the head shift and add their own. */
  shifts: Partial<Record<'dragon' | 'head' | 'horns' | 'wing' | 'book' | 'page' | 'shield', LayerShift>>;
  motion?: PoseMotion;
};

const all: Record<LayerId, boolean> = {
  hoard: true,
  tail: true,
  body: true,
  wing: true,
  head: true,
  horns: true,
  book: true,
  page: false,
  shield: false,
};

const still: Omit<PoseConfig, 'layers' | 'lid' | 'shifts'> & { shifts: PoseConfig['shifts'] } = { puffs: 0, shifts: {} };

export const poses: Record<MascotPose, PoseConfig> = {
  /** Perched on the hoard, book in claw, eyes open. The base every other pose shifts. */
  idle: { layers: { ...all }, lid: 'open', ...still },
  /** Head bowed to the lifted book. */
  reading: {
    layers: { ...all },
    lid: 'open',
    puffs: 0,
    shifts: { head: { y: 10, rotate: 10, cx: 150, cy: 90 }, book: { y: -10, rotate: -6, cx: 179, cy: 133 } },
  },
  /** Eyes shut, book lowered, smoke trailing from the nose. Still: it is asleep. */
  sleeping: {
    layers: { ...all },
    lid: 'closed',
    puffs: 3,
    shifts: { head: { y: 10, rotate: -12, cx: 150, cy: 90 }, book: { y: 12 } },
  },
  /** A hop with the head up. */
  celebrating: { layers: { ...all }, lid: 'open', puffs: 0, shifts: { head: { y: -8 } }, motion: 'hop' },
  /** Squinting into the distance, wing half raised, book lowered. */
  searching: {
    layers: { ...all },
    lid: 'squint',
    puffs: 0,
    shifts: { head: { x: 12, rotate: 6, cx: 150, cy: 90 }, wing: { rotate: -20, cx: 95, cy: 110 }, book: { y: 14 } },
  },
  /** No pile: the dragon sits on the ground, looking around. */
  emptyHoard: {
    layers: { ...all, hoard: false },
    lid: 'open',
    puffs: 0,
    shifts: { dragon: { y: 28 }, head: { x: -6, rotate: -8, cx: 150, cy: 90 } },
  },
  /** Head tilted, one puff slipping from the nose. */
  oops: {
    layers: { ...all },
    lid: 'open',
    puffs: 1,
    shifts: { head: { rotate: 5, cx: 150, cy: 90 } },
  },
  /** The shield presented in place of the book. */
  milestone: {
    layers: { ...all, book: false, shield: true },
    lid: 'open',
    puffs: 0,
    shifts: { head: { y: -4 }, shield: { y: -8 } },
  },
  /** Loading: book up, a loose page flying, fluttering. */
  fastReading: {
    layers: { ...all, page: true },
    lid: 'open',
    puffs: 0,
    shifts: { book: { y: -6 }, page: { x: 14, y: -18, rotate: 24, cx: 200, cy: 80 } },
    motion: 'flutter',
  },
  /** Peeking over the pile: head, horns and wing only, bobbing. */
  refreshPeek: {
    layers: { hoard: true, tail: false, body: false, wing: true, head: true, horns: true, book: false, page: false, shield: false },
    lid: 'open',
    puffs: 0,
    shifts: { head: { y: 78 }, wing: { x: -4, y: 68 } },
    motion: 'bob',
  },
};
