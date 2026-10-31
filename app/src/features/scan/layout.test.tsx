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

jest.mock('expo-router', () => ({ useRouter: () => ({ push: jest.fn(), back: jest.fn(), replace: jest.fn() }), useIsFocused: () => true }));
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

  it('reserves no fixed block below the scanner chrome: the status bar is the bottom row', async () => {
    dismissScanCoach();
    await renderWithProviders(<ScanTab />);

    expect(await screen.findByLabelText('Take picture')).toBeTruthy();
    // An empty tray leaves no spacer behind: no tab-bottom-pad, no result strip,
    // and the status line + Review stay the single bottom row of the chrome (#432, #451).
    expect(screen.queryByTestId('tab-bottom-pad')).toBeNull();
    expect(screen.queryByTestId('result-cards')).toBeNull();
    expect(await screen.findByText('0 items')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Review' })).toBeTruthy();
  });

  it('shows no cards or strip over the camera even when the session has answers', async () => {
    dismissScanCoach();
    // Answers waiting do not bring the strip back: the Scan view stays camera, shutter and status bar (#432).
    useCardsStore.getState().merge(
      [
        { id: 'c1', seq: 1, changeSeq: 1, resolution: 'matched', rawCode: '111' },
        { id: 'c2', seq: 2, changeSeq: 2, resolution: 'pending', rawCode: '222' },
      ],
      { announce: false },
    );
    await renderWithProviders(<ScanTab />);

    expect(await screen.findByLabelText('Take picture')).toBeTruthy();
    expect(screen.queryByTestId('result-cards')).toBeNull();
    expect(screen.queryAllByTestId(/^card-/)).toHaveLength(0);
    expect(screen.queryByLabelText('Snaps in this scan')).toBeNull();
    expect(await screen.findByText('0 items')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Review' })).toBeTruthy();
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
