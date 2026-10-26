// The themed flows stay completable with a screen reader and at 200% text (#363):
// browse (open an item), find (search and filter), and edit (the item's Edit
// button) all expose their names and stay pressable, and the dragons stay
// decorative so they never talk over the words.
import { fireEvent, screen } from '@testing-library/react-native';
import { useWindowDimensions } from 'react-native';
import DetailRoute from '@/app/item/[id]/index';
import { ItemDetailScreen } from '@/features/item/ItemDetailScreen';
import { CollectionStats } from '@/features/me/CollectionStats';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { resetItems } from '@/mocks/items';
import { renderWithProviders } from '@/test/render';
import { LibraryView } from './LibraryView';

jest.mock('react-native/Libraries/Utilities/useWindowDimensions', () => ({
  __esModule: true,
  default: jest.fn(),
}));
const dimensions = useWindowDimensions as jest.Mock;

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({
  useRouter: () => mockRouter,
  useLocalSearchParams: () => ({ id: 'c0000000-0000-4000-8000-000000000001' }),
}));

beforeEach(() => {
  resetItems();
  mockRouter.push.mockClear();
  dimensions.mockReturnValue({ width: 390, height: 844, scale: 3, fontScale: 2 });
});

describe('themed flows at 200% text', () => {
  it('browses: the framed header names the library and a cover opens its item', async () => {
    await renderWithProviders(<LibraryView title="Library" />);
    await screen.findByText('The Matrix');

    expect(screen.getByRole('header', { name: 'Library' })).toBeTruthy();
    await fireEvent.press(screen.getByLabelText('The Matrix, DVD'));
    expect(mockRouter.push).toHaveBeenCalledWith('/item/c0000000-0000-4000-8000-000000000001');
  });

  it('finds: search and the media chips narrow the grid', async () => {
    await renderWithProviders(<LibraryView title="Library" />);
    await screen.findByText('The Matrix');

    await fireEvent.changeText(screen.getByLabelText('Search'), 'mario');
    expect(await screen.findByText('Super Mario 64')).toBeTruthy();

    await fireEvent.changeText(screen.getByLabelText('Search'), '');
    await fireEvent.press(screen.getByLabelText('Books'));
    expect(await screen.findByText('Dune')).toBeTruthy();
    expect(screen.queryByText('The Matrix')).toBeNull();
  });

  it('edits: the lightly themed detail screen still opens the editor', async () => {
    await renderWithProviders(<ItemDetailScreen id="c0000000-0000-4000-8000-000000000001" />);
    await screen.findByText('The Matrix');

    await fireEvent.press(screen.getByLabelText('Edit'));
    expect(mockRouter.push).toHaveBeenCalledWith('/item/c0000000-0000-4000-8000-000000000001/edit');
  });

  it('keeps the pushed detail route working', async () => {
    await renderWithProviders(<DetailRoute />);
    expect(await screen.findByText('The Matrix')).toBeTruthy();
  });
});

describe('themed dragons and screen readers', () => {
  it('keeps the perched dragon decorative so the header speaks alone', async () => {
    await renderWithProviders(<LibraryView title="Library" />);
    await screen.findByText('The Matrix');

    expect(screen.getByTestId('castle-perch').props.accessible).toBe(false);
  });

  it('walks the Great Hall by headings and buttons', async () => {
    await renderWithProviders(<CollectionStats libraryId={MOCK_LIBRARY_ID} />);
    await screen.findByText('Great hall');

    expect(screen.getByRole('header', { name: 'Great hall' })).toBeTruthy();
    for (const button of screen.getAllByRole('button')) {
      expect(button.props.accessibilityLabel).toBeTruthy();
    }
  });
});
