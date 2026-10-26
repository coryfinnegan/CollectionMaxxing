// Layout quality (#269): the scan screens respect the safe area, and the scanner's
// waiting room (no library yet) offers a way back out of the modal. The Scan tab
// itself is the scanner: the camera fills the tab and the chrome pads from the insets.
import { fireEvent, screen } from '@testing-library/react-native';
import { mockAccount } from '@/mocks/data';
import { scanMock } from '@/mocks/scan';
import { renderWithProviders } from '@/test/render';
import { ResultDetail } from './cards/ResultDetail';
import { useCardsStore } from './cards/cardsStore';
import { ScanTab } from './ScanTab';
import { dismissScanCoach, setScanEntryStorage } from './scanner/scanEntry';
import { ScannerRoute } from './scanner/ScannerRoute';
import { setSessionContextStorage } from './session/sessionContext';
import { setTrayStore } from './tray/storage';
import { createTrayStore, MemoryKeyValueStore } from './tray/trayStore';

jest.mock('expo-router', () => ({ useRouter: () => ({ push: jest.fn(), back: jest.fn(), replace: jest.fn() }) }));
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
  setTrayStore(undefined);
  setSessionContextStorage(undefined);
  setScanEntryStorage(undefined);
});

describe('scan layout', () => {
  it('opens the Scan tab on the scanner, with no landing in between', async () => {
    dismissScanCoach();
    await renderWithProviders(<ScanTab />);
    expect(await screen.findByLabelText('Session: Not placed, Owned, Mixed, Home. Change settings')).toBeTruthy();
    expect(await screen.findByLabelText('Take picture')).toBeTruthy();
    expect(screen.queryByText('Open scanner')).toBeNull();
  });

  it('keeps the result detail inside the safe area', async () => {
    await renderWithProviders(<ResultDetail id="missing" />);
    expect(await screen.findByText("That result isn't here any more")).toBeTruthy();
    expect(screen.getByTestId('result-safe-area')).toBeTruthy();
  });

  it('offers a way out when the scanner opens with no library', async () => {
    mockAccount.libraries = [];
    const onClose = jest.fn();
    await renderWithProviders(<ScannerRoute onClose={onClose} />);
    expect(await screen.findByText('No library found.')).toBeTruthy();
    expect(screen.getByText('Pair this iPhone again.')).toBeTruthy();
    expect(screen.getByTestId('scanner-fallback-safe-area')).toBeTruthy();
    fireEvent.press(screen.getByLabelText('Close'));
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});
