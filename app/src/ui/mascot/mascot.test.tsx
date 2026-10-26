// The dragon mascot pack (#365): ten poses share one layered vector dragon, so a
// pose is only visibility plus small shifts, never a redraw. The mascot stays
// decorative to screen readers unless the pose conveys state, and Reduce Motion
// always gets the static pose.
import { render, screen } from '@testing-library/react-native';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { setMascotStorage, useDragonAppearance } from './appearances';
import { Mascot } from './Mascot';
import { poseIds, poses, type MascotPose } from './poses';

jest.mock('@/lib/a11y/useReduceMotion', () => ({ useReduceMotion: jest.fn() }));
const reduceMotion = jest.mocked(useReduceMotion);

function memoryStore() {
  const map = new Map<string, string>();
  return {
    getString: (key: string) => map.get(key),
    set: (key: string, value: string) => void map.set(key, value),
  };
}

const coreLayers = ['hoard', 'tail', 'body', 'wing', 'head', 'horns', 'book'] as const;

function visibleLayers(): string[] {
  return [...coreLayers, 'page', 'puffs', 'shield'].filter((layer) => screen.queryByTestId(`mascot-layer-${layer}`));
}

function puffCount(): number {
  return screen.queryAllByTestId(/mascot-puff-\d+/).length;
}

function lidState(): string | null {
  for (const lid of ['open', 'squint', 'closed'] as const) {
    if (screen.queryByTestId(`mascot-lid-${lid}`)) return lid;
  }
  return null;
}

describe('mascot pose library', () => {
  beforeEach(() => {
    reduceMotion.mockReset().mockReturnValue(false);
  });

  it('ships the ten poses from the issue, each with a config', () => {
    expect(poseIds).toEqual([
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
    ]);
    expect(Object.keys(poses).sort()).toEqual([...poseIds].sort());
  });

  it.each(poseIds)('renders the %s pose', async (pose: MascotPose) => {
    await render(<Mascot pose={pose} testID="mascot" />);
    expect(screen.getByTestId('mascot')).toBeTruthy();
  });

  it('perches the idle dragon on its hoard with open eyes', async () => {
    await render(<Mascot pose="idle" testID="mascot" />);
    expect(visibleLayers()).toEqual([...coreLayers]);
    expect(lidState()).toBe('open');
    expect(puffCount()).toBe(0);
  });

  it('reads with the book up and the head bowed', async () => {
    await render(<Mascot pose="reading" testID="mascot" />);
    expect(visibleLayers()).toEqual([...coreLayers]);
    expect(lidState()).toBe('open');
  });

  it('sleeps with closed eyes and trailing smoke', async () => {
    await render(<Mascot pose="sleeping" testID="mascot" />);
    expect(visibleLayers()).toEqual([...coreLayers, 'puffs']);
    expect(lidState()).toBe('closed');
    expect(puffCount()).toBe(3);
  });

  it('searches with a squint, still on its hoard', async () => {
    await render(<Mascot pose="searching" testID="mascot" />);
    expect(visibleLayers()).toEqual([...coreLayers]);
    expect(lidState()).toBe('squint');
    expect(puffCount()).toBe(0);
  });

  it('shows no pile in the empty-hoard pose', async () => {
    await render(<Mascot pose="emptyHoard" testID="mascot" />);
    expect(visibleLayers()).toEqual(['tail', 'body', 'wing', 'head', 'horns', 'book']);
    expect(lidState()).toBe('open');
  });

  it('lets a single nose puff slip in the oops pose', async () => {
    await render(<Mascot pose="oops" testID="mascot" />);
    expect(visibleLayers()).toEqual([...coreLayers, 'puffs']);
    expect(puffCount()).toBe(1);
  });

  it('presents the shield instead of the book for a milestone', async () => {
    await render(<Mascot pose="milestone" testID="mascot" />);
    expect(visibleLayers()).toEqual(['hoard', 'tail', 'body', 'wing', 'head', 'horns', 'shield']);
  });

  it('flies a loose page while fast-reading', async () => {
    await render(<Mascot pose="fastReading" testID="mascot" />);
    expect(visibleLayers()).toEqual([...coreLayers, 'page']);
  });

  it('peeks over the pile with just head, horns and wing showing', async () => {
    await render(<Mascot pose="refreshPeek" testID="mascot" />);
    expect(visibleLayers()).toEqual(['hoard', 'wing', 'head', 'horns']);
    expect(lidState()).toBe('open');
  });
});

describe('shared layers, no redraws', () => {
  beforeEach(() => {
    reduceMotion.mockReset().mockReturnValue(false);
  });

  /** The drawn shape of a layer: element types plus their data props, without renderer internals (fibers, owners, refs). */
  function fingerprint(node: unknown): unknown {
    if (Array.isArray(node)) return node.map(fingerprint);
    if (node && typeof node === 'object') {
      if ('type' in node && 'props' in node) {
        const element = node as { type: unknown; props: unknown };
        const type =
          typeof element.type === 'string'
            ? element.type
            : ((element.type as { displayName?: string; name?: string }).displayName ?? (element.type as { name?: string }).name ?? 'component');
        return { type, props: fingerprint(element.props) };
      }
      const record = node as Record<string, unknown>;
      return Object.fromEntries(
        Object.entries(record)
          .filter(([key]) => !key.startsWith('_'))
          .map(([key, value]) => [key, fingerprint(value)]),
      );
    }
    return node;
  }

  /** Both poses stay mounted while their layers compare; unmounting between renders overlaps act() scopes. */
  async function expectSameLayer(first: MascotPose, second: MascotPose, layer: string): Promise<void> {
    const one = await render(<Mascot pose={first} testID="mascot-one" />);
    const two = await render(<Mascot pose={second} testID="mascot-two" />);
    expect(fingerprint(one.getByTestId(`mascot-layer-${layer}`).props)).toEqual(fingerprint(two.getByTestId(`mascot-layer-${layer}`).props));
  }

  it('draws the same tail in idle and celebrating', async () => {
    await expectSameLayer('celebrating', 'idle', 'tail');
  });

  it('draws the same hoard in idle and searching', async () => {
    await expectSameLayer('searching', 'idle', 'hoard');
  });

  it('draws the same horns in reading and sleeping', async () => {
    await expectSameLayer('sleeping', 'reading', 'horns');
  });

  it('keeps pose configs free of geometry: visibility and shifts only', () => {
    // Path data would show up as drawing commands; configs may only name layers, lids, counts and numbers.
    expect(JSON.stringify(poses)).not.toMatch(/[MC][\d\s,.-]/);
  });
});

describe('mascot motion', () => {
  beforeEach(() => {
    reduceMotion.mockReset().mockReturnValue(false);
    setMascotStorage(memoryStore());
    useDragonAppearance.setState({ appearance: 'full' });
  });

  it.each(['celebrating', 'fastReading', 'refreshPeek'] as const)('loops the %s pose while motion is allowed', async (pose: MascotPose) => {
    await render(<Mascot pose={pose} testID="mascot" />);
    expect(screen.getByTestId('mascot-motion')).toBeTruthy();
  });

  it.each(['idle', 'reading', 'sleeping', 'searching', 'emptyHoard', 'oops', 'milestone'] as const)(
    'holds the %s pose still: no loop even with motion allowed',
    async (pose: MascotPose) => {
      await render(<Mascot pose={pose} testID="mascot" />);
      expect(screen.queryByTestId('mascot-motion')).toBeNull();
    },
  );

  it('holds every pose still when Reduce Motion is on', async () => {
    reduceMotion.mockReturnValue(true);
    for (const pose of poseIds) {
      const rendered = await render(<Mascot pose={pose} testID={`mascot-${pose}`} />);
      expect(rendered.queryByTestId(`mascot-${pose}-motion`)).toBeNull();
    }
  });

  it('holds every pose still on Minimal, even with motion allowed', async () => {
    useDragonAppearance.setState({ appearance: 'minimal' });
    for (const pose of poseIds) {
      const rendered = await render(<Mascot pose={pose} testID={`mascot-${pose}`} />);
      expect(rendered.queryByTestId(`mascot-${pose}-motion`)).toBeNull();
    }
  });
});

describe('mascot accessibility', () => {
  beforeEach(() => {
    reduceMotion.mockReset().mockReturnValue(false);
  });

  it('stays decorative by default', async () => {
    await render(<Mascot pose="idle" testID="mascot" />);
    expect(screen.getByTestId('mascot').props.accessible).toBe(false);
  });

  it('announces the pose when it conveys state', async () => {
    await render(<Mascot pose="sleeping" testID="mascot" accessibilityLabel="Loading your shelves" />);
    const mascot = screen.getByTestId('mascot');
    expect(mascot.props.accessibilityRole).toBe('image');
    expect(mascot.props.accessibilityLabel).toBe('Loading your shelves');
  });
});
