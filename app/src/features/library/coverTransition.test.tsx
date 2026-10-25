// The library end of the cover-to-detail flight (#304): tiles settle in with a
// stagger, and each cover carries the tag its detail screen matches.
import { screen } from '@testing-library/react-native';
import { useReduceMotion } from '@/lib/a11y/useReduceMotion';
import { resetItems } from '@/mocks/items';
import { renderWithProviders } from '@/test/render';
import { LibraryView } from './LibraryView';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));
jest.mock('@/lib/a11y/useReduceMotion', () => ({ useReduceMotion: jest.fn() }));

const MATRIX = 'c0000000-0000-4000-8000-000000000001';

const reduceMotion = (on: boolean) => jest.mocked(useReduceMotion).mockReturnValue(on);

beforeEach(() => {
  resetItems();
  mockRouter.push.mockClear();
});

describe('library tiles in motion', () => {
  it('tags each cover with its detail tag and settles tiles in', async () => {
    reduceMotion(false);
    const { root } = await renderWithProviders(<LibraryView title="Library" />);
    const tile = await screen.findByTestId(`item-${MATRIX}`);

    expect(tile.queryAll((n) => n.props.sharedTransitionTag === `cover-${MATRIX}`)).toHaveLength(1);
    expect(tile.parent!.props.entering).toBeDefined();
    expect(tile.parent!.props.layout).toBeDefined();
    expect(root).toBeTruthy();
  });

  it('leaves covers untagged and tiles still when Reduce Motion is on', async () => {
    reduceMotion(true);
    await renderWithProviders(<LibraryView title="Library" />);
    const tile = await screen.findByTestId(`item-${MATRIX}`);

    expect(tile.queryAll((n) => n.props.sharedTransitionTag !== undefined)).toHaveLength(0);
    expect(tile.parent!.props.entering).toBeUndefined();
    expect(tile.parent!.props.layout).toBeUndefined();
  });
});
