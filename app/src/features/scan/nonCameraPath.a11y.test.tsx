// The camera is never the only way in: Import from Photos sits on the scanner (which works with no camera picture),
// and the Scan tab's session bar offers adding by hand, which lands on the library's Add an item panel.
import { fireEvent, screen } from '@testing-library/react-native';
import { scanMock } from '@/mocks/scan';
import { renderWithProviders } from '@/test/render';
import { useCardsStore } from './cards/cardsStore';
import { makeKit, resetKit } from './pipeline/testKit';
import { importKit } from './import/importKit';
import { ScanTab } from './ScanTab';
import { dismissScanCoach, setScanEntryStorage } from './scanner/scanEntry';
import { setSessionContextStorage } from './session/sessionContext';
import { setTrayStore } from './tray/storage';
import { createTrayStore, MemoryKeyValueStore } from './tray/trayStore';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));
// Entry routing, not the camera: the preview's own fallback is covered by the lazy boundary.
jest.mock('./scanner/LazyCameraPreview', () => ({ LazyCameraPreview: () => null }));

beforeEach(() => {
  scanMock.reset();
  useCardsStore.getState().reset();
  setTrayStore(createTrayStore({ storage: new MemoryKeyValueStore() }));
  setSessionContextStorage(new MemoryKeyValueStore());
  setScanEntryStorage(new MemoryKeyValueStore());
});

afterEach(() => {
  resetKit();
  mockRouter.push.mockClear();
  setTrayStore(undefined);
  setSessionContextStorage(undefined);
  setScanEntryStorage(undefined);
});

describe('using the app without the camera', () => {
  it('keeps Import from Photos on the scanner even though no camera picture is shown', async () => {
    // importKit opens the scanner with no `preview` element: nothing from the camera is on screen.
    await importKit(makeKit()).open();
    const photos = screen.getByRole('button', { name: 'Photos' });
    expect(photos.props.accessibilityHint).toBe('Choose pictures from your library to read, with no camera needed');
  });

  it('offers adding an item by hand from the Scan tab', async () => {
    dismissScanCoach();
    await renderWithProviders(<ScanTab />);
    fireEvent.press(await screen.findByRole('button', { name: 'Add it by hand' }));
    expect(mockRouter.push).toHaveBeenCalledWith({ pathname: '/library', params: { add: '1' } });
  });
});
