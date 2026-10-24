// The review route before the library is ready: a loading state, not a bare line.
import { screen } from '@testing-library/react-native';
import { useActiveLibraryId } from '@/features/locations/useLocations';
import { renderWithProviders } from '@/test/render';
import { ReviewRoute } from './ReviewRoute';

jest.mock('expo-router', () => ({ useRouter: () => ({ push: jest.fn(), back: jest.fn() }) }));
jest.mock('@/features/locations/useLocations', () => ({ useActiveLibraryId: jest.fn() }));
// This branch returns before the screen is built; stub the screen it never renders.
jest.mock('./ReviewScreen', () => ({ ReviewScreen: () => null }));
const library = useActiveLibraryId as jest.Mock;

describe('review route without a library', () => {
  it('shows a loading state while the library loads', async () => {
    library.mockReturnValue({ libraryId: undefined, isLoading: true });
    await renderWithProviders(<ReviewRoute />);
    expect(screen.getByTestId('loading-state')).toBeTruthy();
    expect(screen.getByText('Loading…')).toBeTruthy();
  });
});
