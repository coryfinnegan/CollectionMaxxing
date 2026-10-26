// The Scan tab opens the camera directly: a returning run shows the scanner with no intermediate taps, the first run
// shows a one-time coach overlay over it, and no-library and web keep their fallbacks.
import { act, fireEvent, screen } from '@testing-library/react-native';
import { Platform } from 'react-native';
import { mockAccount } from '@/mocks/data';
import { scanMock } from '@/mocks/scan';
import { renderWithProviders } from '@/test/render';
import { useCardsStore } from './cards/cardsStore';
import { ScanTab } from './ScanTab';
import { dismissScanCoach, hasSeenScanCoach, setScanEntryStorage } from './scanner/scanEntry';
import { setSessionContextStorage } from './session/sessionContext';
import { setTrayStore } from './tray/storage';
import { createTrayStore, MemoryKeyValueStore } from './tray/trayStore';

jest.mock('expo-router', () => ({ useRouter: () => ({ push: jest.fn(), back: jest.fn(), replace: jest.fn() }) }));
// Entry routing, not the camera: the preview's own fallback is covered by the lazy boundary.
jest.mock('./scanner/LazyCameraPreview', () => ({ LazyCameraPreview: () => null }));

const originalOs = Platform.OS;

beforeEach(() => {
  scanMock.reset();
  useCardsStore.getState().reset();
  setTrayStore(createTrayStore({ storage: new MemoryKeyValueStore(), now: () => 1_000 }));
  setSessionContextStorage(new MemoryKeyValueStore());
  setScanEntryStorage(new MemoryKeyValueStore());
});

afterEach(() => {
  Platform.OS = originalOs;
  scanMock.reset();
  setTrayStore(undefined);
  setSessionContextStorage(undefined);
  setScanEntryStorage(undefined);
});

describe('scan tab entry', () => {
  it('shows the scanner with no intermediate taps on a returning run', async () => {
    dismissScanCoach();
    await renderWithProviders(<ScanTab />);

    expect(await screen.findByLabelText('Session: Not placed, Owned, Mixed, Home. Change settings')).toBeTruthy();
    expect(await screen.findByLabelText('Take picture')).toBeTruthy();
    expect(screen.queryByText('Open scanner')).toBeNull();
    expect(screen.queryByTestId('scan-coach')).toBeNull();
  });

  it('coaches over the scanner on the first run, then never again', async () => {
    await renderWithProviders(<ScanTab />);

    expect(await screen.findByTestId('scan-coach')).toBeTruthy();
    // The coach presents both capture paths as equals: barcodes read straight away, shelves wait for review (#371).
    expect(
      await screen.findByText(
        'Point the camera at a barcode and it reads straight away. Point it at a shelf of spines and hold steady — check the matches before anything is added. Add anything else by hand from the bar at the top.',
      ),
    ).toBeTruthy();
    expect(await screen.findByLabelText('Take picture')).toBeTruthy();
    // The dismiss press needs its own act: the update lands after the sync flush.
    await act(async () => {
      fireEvent.press(screen.getByLabelText('Start scanning'));
    });
    expect(screen.queryByTestId('scan-coach')).toBeNull();
    // Dismissed is permanent: the flag is in storage, so a fresh mount shows no coach (proven above).
    expect(hasSeenScanCoach()).toBe(true);
  });

  it('shows the no-library fallback with no coach and no way back needed', async () => {
    mockAccount.libraries = [];
    await renderWithProviders(<ScanTab />);

    expect(await screen.findByText('No library found.')).toBeTruthy();
    expect(screen.getByText('Pair this iPhone again.')).toBeTruthy();
    expect(screen.queryByTestId('scan-coach')).toBeNull();
    expect(screen.queryByLabelText('Close')).toBeNull();
  });

  it('keeps the iPhone-only empty state on the web', async () => {
    Platform.OS = 'web';
    await renderWithProviders(<ScanTab />);

    expect(await screen.findByText('Scanning is on the iPhone app')).toBeTruthy();
    expect(screen.getByText('Open CollectionMaxxing on your iPhone to scan barcodes and shelves.')).toBeTruthy();
    expect(screen.getByTestId('scan-tab-safe-area')).toBeTruthy();
  });
});
