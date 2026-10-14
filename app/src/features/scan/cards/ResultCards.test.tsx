import { screen } from '@testing-library/react-native';
import { AccessibilityInfo, Animated } from 'react-native';
import type { CandidateDto } from '@/api/generated/models';
import { renderWithProviders } from '@/test/render';
import { ResultCard, ResultCards, type CardActions } from './ResultCards';

const noop = () => undefined;
const actions: CardActions = { canAdd: true, onOpen: noop, onAdd: noop, onWishlist: noop };

const dune = {
  id: 'c1',
  seq: 1,
  rawCode: '9780441172719',
  resolution: 'matched',
  alreadyOwnedCount: 1,
  ownedAt: ['Living room › Shelf 2'],
  decision: 'undecided',
  release: { id: 'r1', title: 'Dune', format: 'paperback', year: 1990, platform: null, creators: [], coverUrl: null },
} as unknown as CandidateDto;

afterEach(() => jest.restoreAllMocks());

describe('result cards', () => {
  it('slides a new card in over a short time', async () => {
    const timing = jest.spyOn(Animated, 'timing');
    await renderWithProviders(<ResultCard candidate={dune} actions={actions} reduceMotion={false} />);
    expect(timing).toHaveBeenCalledWith(expect.anything(), expect.objectContaining({ duration: 220 }));
  });

  it('does not slide with Reduce Motion on', async () => {
    const timing = jest.spyOn(Animated, 'timing');
    await renderWithProviders(<ResultCard candidate={dune} actions={actions} reduceMotion />);
    expect(timing).toHaveBeenCalledWith(expect.anything(), expect.objectContaining({ duration: 0 }));
  });

  it('reads the Reduce Motion setting from the system', async () => {
    jest.spyOn(AccessibilityInfo, 'isReduceMotionEnabled').mockResolvedValue(true);
    const timing = jest.spyOn(Animated, 'timing');
    await renderWithProviders(<ResultCards cards={[dune]} actions={actions} />);
    await screen.findByText('Dune');
    await new Promise((r) => setTimeout(r, 20));
    expect(timing).toHaveBeenLastCalledWith(expect.anything(), expect.objectContaining({ duration: 0 }));
  });

  it('gives the card and its parts accessibility labels', async () => {
    await renderWithProviders(<ResultCards cards={[dune]} actions={actions} />);
    expect(await screen.findByLabelText('Open Dune, Paperback · 1990, You own 1')).toBeTruthy();
  });
});
