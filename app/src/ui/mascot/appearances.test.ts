// The dragon appearances setting (#363): Full shows every placement, Minimal keeps
// the reactive state poses only, and Off shows no dragon anywhere.
import { placementVisible, setMascotStorage, useDragonAppearance } from './appearances';

function memoryStore() {
  const map = new Map<string, string>();
  return {
    getString: (key: string) => map.get(key),
    set: (key: string, value: string) => void map.set(key, value),
  };
}

describe('dragon appearance setting', () => {
  beforeEach(() => {
    setMascotStorage(memoryStore());
    useDragonAppearance.setState({ appearance: 'full' });
  });

  it('is full until the person picks', () => {
    expect(useDragonAppearance.getState().appearance).toBe('full');
  });

  it('keeps the choice across reloads', () => {
    useDragonAppearance.getState().set('minimal');
    useDragonAppearance.setState({ appearance: 'full' });
    useDragonAppearance.getState().reload();
    expect(useDragonAppearance.getState().appearance).toBe('minimal');
  });
});

describe('placementVisible', () => {
  it.each([
    ['full', 'decorative', true],
    ['full', 'state', true],
    ['minimal', 'decorative', false],
    ['minimal', 'state', true],
    ['off', 'decorative', false],
    ['off', 'state', false],
  ] as const)('%s shows %s placements: %s', (appearance, placement, visible) => {
    expect(placementVisible(appearance, placement)).toBe(visible);
  });
});
