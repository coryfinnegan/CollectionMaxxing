// The detail end of the cover-to-detail flight (#304): the big cover carries the
// same tag the grid tile set, so the cover flies across the push.
import { screen } from '@testing-library/react-native';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { resetItems } from '@/mocks/items';
import { renderWithProviders } from '@/test/render';
import { ItemDetailScreen } from './ItemDetailScreen';
import { useUndoStore } from './useItem';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({
  useRouter: () => mockRouter,
  useLocalSearchParams: () => ({ id: 'c0000000-0000-4000-8000-000000000001' }),
}));
jest.mock('@/lib/a11y/useReduceMotion', () => ({ useReduceMotion: jest.fn() }));

const MATRIX = 'c0000000-0000-4000-8000-000000000001';

const reduceMotion = (on: boolean) => jest.mocked(useReduceMotion).mockReturnValue(on);

beforeEach(() => {
  resetItems();
  useUndoStore.setState({ deleted: null });
});

describe('detailScreen cover', () => {
  it('carries the same shared tag the grid tile set', async () => {
    reduceMotion(false);
    const { root } = await renderWithProviders(<ItemDetailScreen id={MATRIX} />);
    await screen.findByText('The Matrix');

    expect(root!.queryAll((n) => n.props.sharedTransitionTag === `cover-${MATRIX}`)).toHaveLength(1);
  });

  it('leaves the cover untagged when Reduce Motion is on', async () => {
    reduceMotion(true);
    const { root } = await renderWithProviders(<ItemDetailScreen id={MATRIX} />);
    await screen.findByText('The Matrix');

    expect(root!.queryAll((n) => n.props.sharedTransitionTag !== undefined)).toHaveLength(0);
  });
});
