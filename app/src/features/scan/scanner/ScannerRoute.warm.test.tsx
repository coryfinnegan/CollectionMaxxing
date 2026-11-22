// The scanner route wakes the server's shelf reader (#832): once while a scanner shows, never otherwise.
import { warmShelfReader } from '@/api/generated/recognition/recognition';
import { useActiveLibraryId } from '@/features/locations/useLocations';
import { renderWithProviders } from '@/test/render';
import { resetAppReaderWarmer } from '../pipeline/warmReader';
import { getRecognition } from '../recognition/device';
import { ScannerRoute } from './ScannerRoute';
import { useScreenActive } from './useScreenActive';

jest.mock('@/features/locations/useLocations', () => ({ useActiveLibraryId: jest.fn() }));
jest.mock('./ScannerScreen', () => ({ ScannerScreen: jest.fn(() => null) }));
jest.mock('./useScreenActive', () => ({ useScreenActive: jest.fn() }));
jest.mock('../pipeline/device', () => ({ createDevicePipelineDeps: jest.fn() }));
jest.mock('../pipeline/useAutoSnapConfig', () => ({ useAutoSnapConfig: jest.fn() }));
jest.mock('@/api/generated/recognition/recognition', () => ({ warmShelfReader: jest.fn(() => Promise.resolve({ status: 202 })) }));
jest.mock('../recognition/device', () => ({ getRecognition: jest.fn() }));

const library = useActiveLibraryId as jest.Mock;
const screenActive = useScreenActive as jest.Mock;
const warm = warmShelfReader as jest.Mock;
const recognition = getRecognition as jest.Mock;

beforeEach(() => {
  resetAppReaderWarmer();
  warm.mockClear();
  library.mockReturnValue({ libraryId: 'lib-1', isLoading: false });
  screenActive.mockReturnValue(true);
  recognition.mockReturnValue({ store: { shelfReader: () => true } });
});

describe('scanner route waking the shelf reader', () => {
  it('warms once when the scanner is showing', async () => {
    await renderWithProviders(<ScannerRoute />);
    expect(warm).toHaveBeenCalledTimes(1);
  });

  it('does not warm when the scanner is not showing', async () => {
    screenActive.mockReturnValue(false);
    await renderWithProviders(<ScannerRoute />);
    expect(warm).not.toHaveBeenCalled();
  });

  it('does not warm when the server does not read shelves', async () => {
    recognition.mockReturnValue({ store: { shelfReader: () => false } });
    await renderWithProviders(<ScannerRoute />);
    expect(warm).not.toHaveBeenCalled();
  });

  it('makes no warm-up without a library', async () => {
    library.mockReturnValue({ libraryId: undefined, isLoading: true });
    await renderWithProviders(<ScannerRoute />);
    expect(warm).not.toHaveBeenCalled();
  });

  it('shares the five-minute rule between two scanner screens', async () => {
    await renderWithProviders(<ScannerRoute />);
    await renderWithProviders(<ScannerRoute showClose={false} />);
    expect(warm).toHaveBeenCalledTimes(1);
  });
});
