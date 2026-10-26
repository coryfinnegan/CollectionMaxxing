// The scanner route before any library is ready: a loading state while it loads, an empty state when none is found.
import { screen } from '@testing-library/react-native';
import { useActiveLibraryId } from '@/features/locations/useLocations';
import { renderWithProviders } from '@/test/render';
import { ScannerRoute } from './ScannerRoute';

jest.mock('@/features/locations/useLocations', () => ({ useActiveLibraryId: jest.fn() }));
// These branches return before the scanner is built; stub the heavy siblings they never render.
jest.mock('./ScannerScreen', () => ({ ScannerScreen: () => null }));
jest.mock('../pipeline/device', () => ({ createDevicePipelineDeps: jest.fn() }));
jest.mock('../pipeline/useAutoSnapConfig', () => ({ useAutoSnapConfig: jest.fn() }));
const library = useActiveLibraryId as jest.Mock;

describe('scanner route without a library', () => {
  it('shows a loading state while the library loads', async () => {
    library.mockReturnValue({ libraryId: undefined, isLoading: true });
    await renderWithProviders(<ScannerRoute />);
    expect(screen.getByTestId('loading-state')).toBeTruthy();
    expect(screen.getByText('Getting your library ready…')).toBeTruthy();
  });

  it('shows an empty state when no library is found', async () => {
    library.mockReturnValue({ libraryId: undefined, isLoading: false });
    await renderWithProviders(<ScannerRoute />);
    expect(screen.getByText('No library found.')).toBeTruthy();
    expect(screen.getByText('Pair this iPhone again.')).toBeTruthy();
  });

  it('hides Close when embedded in the Scan tab', async () => {
    library.mockReturnValue({ libraryId: undefined, isLoading: false });
    await renderWithProviders(<ScannerRoute showClose={false} />);
    expect(screen.getByText('No library found.')).toBeTruthy();
    expect(screen.queryByLabelText('Close')).toBeNull();
  });
});
