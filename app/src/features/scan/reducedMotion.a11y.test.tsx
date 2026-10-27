// Reduce Motion: the shutter flash and the card slide-in are skipped when the setting is on.
import { QueryClientProvider } from '@tanstack/react-query';
import { render } from '@testing-library/react-native';
import { Animated } from 'react-native';
import type { ReactNode } from 'react';
import type { CandidateDto } from '@/api/generated/models';
import { makeQueryClient } from '@/test/render';
import { NiceScanFlash } from './autosnap/NiceScanFlash';
import { ResultCard } from './cards/ResultCards';

jest.mock('@/lib/a11y/announce', () => ({ announceForScreenReader: jest.fn() }));

afterEach(() => jest.restoreAllMocks());

const card = {
  id: 'c1',
  seq: 1,
  rawCode: '9780441172719',
  resolution: 'matched',
  alreadyOwnedCount: 0,
  ownedAt: [],
  decision: 'undecided',
  release: { id: 'r1', title: 'Dune', format: 'paperback', year: 1990, platform: null, creators: [], coverUrl: null },
} as unknown as CandidateDto;
const actions = { canAdd: false, onOpen: () => undefined, onAdd: () => undefined, onWishlist: () => undefined };

describe('with Reduce Motion on', () => {
  it('the "Nice scan!" flash appears without animating', async () => {
    const timing = jest.spyOn(Animated, 'timing');
    await render(<NiceScanFlash visible reduceMotion />);
    expect(timing).not.toHaveBeenCalled();
  });

  it('a result card appears with a zero-length slide', async () => {
    const timing = jest.spyOn(Animated, 'timing');
    const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={makeQueryClient()}>{children}</QueryClientProvider>;
    await render(<ResultCard candidate={card} actions={actions} reduceMotion />, { wrapper });
    expect(timing).toHaveBeenCalled();
    expect(timing.mock.calls.every(([, config]) => config.duration === 0)).toBe(true);
  });
});

describe('with Reduce Motion off', () => {
  it('the flash pops in', async () => {
    const timing = jest.spyOn(Animated, 'timing');
    await render(<NiceScanFlash visible reduceMotion={false} />);
    expect(timing).toHaveBeenCalled();
  });
});
