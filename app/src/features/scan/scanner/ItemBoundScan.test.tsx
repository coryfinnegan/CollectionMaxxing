// Item detail's "Scan barcode to verify edition" without a camera: the bound read verifies the item in
// place, bypasses the tray and the scan session, and leaves the scanner. A cross-work barcode names its
// owner and the camera waits for another read.
import { act, screen, waitFor } from '@testing-library/react-native';
import type { StoreApi } from 'zustand/vanilla';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { CROSS_WORK_BARCODE, itemsState, resetItems } from '@/mocks/items';
import { scanMock } from '@/mocks/scan';
import { renderWithProviders } from '@/test/render';
import { useCardsStore } from '../cards/cardsStore';
import { setTrayStore } from '../tray/storage';
import { createTrayStore, MemoryKeyValueStore, type TrayStore } from '../tray/trayStore';
import { ScannerScreen } from './ScannerScreen';
import { FakeScannerSource } from './ScannerSource';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));

const tuning = { retryMs: 20, pollMs: 20, resendMs: 40, flashMs: 50 };
const MATRIX = 'c0000000-0000-4000-8000-000000000001';
const entry = () => itemsState.entries.find((e) => e.item.id === MATRIX)!;
const code = (value: string) => ({ value, symbology: 'ean-13' });

const bound = (onDone: jest.Mock, onCancel: jest.Mock = jest.fn()) => ({
  libraryId: MOCK_LIBRARY_ID,
  itemId: MATRIX,
  title: 'The Matrix',
  kind: 'itemBarcode' as const,
  onDone,
  onCancel,
});

let tray: StoreApi<TrayStore>;

beforeEach(() => {
  scanMock.reset();
  resetItems();
  useCardsStore.getState().reset();
  tray = createTrayStore({ storage: new MemoryKeyValueStore(), now: () => 1_000 });
  setTrayStore(tray);
});

afterEach(() => {
  scanMock.reset();
  setTrayStore(undefined);
});

describe('item-bound scan', () => {
  it('verifies the item in place, bypasses the tray, and leaves', async () => {
    entry().item.editionVerified = false;
    const onDone = jest.fn();
    const source = new FakeScannerSource();
    await renderWithProviders(
      <ScannerScreen libraryId={MOCK_LIBRARY_ID} source={source} tuning={tuning} boundScan={bound(onDone)} />,
    );
    expect(await screen.findByText('Scan the barcode for The Matrix')).toBeTruthy();

    await act(async () => source.inject([code('9780441172719')]));

    await waitFor(() => expect(onDone).toHaveBeenCalledTimes(1));
    expect(entry().item.editionVerified).toBe(true);
    expect(tray.getState().lines).toHaveLength(0);
    // An item binding never touches the scan session.
    expect(scanMock.barcodeRequests).toHaveLength(0);
  });

  it('a cross-work barcode names its owner and the camera waits for another read', async () => {
    entry().item.editionVerified = false;
    const onDone = jest.fn();
    const source = new FakeScannerSource();
    await renderWithProviders(
      <ScannerScreen libraryId={MOCK_LIBRARY_ID} source={source} tuning={tuning} boundScan={bound(onDone)} />,
    );
    await screen.findByText('Scan the barcode for The Matrix');

    await act(async () => source.inject([code(CROSS_WORK_BARCODE)]));

    expect(await screen.findByText('That barcode belongs to Dune Messiah.')).toBeTruthy();
    expect(onDone).not.toHaveBeenCalled();
    expect(entry().item.editionVerified).toBe(false);

    // And the next read still sends: the camera waits, it doesn't wedge.
    await act(async () => source.inject([code('9780441172719')]));

    await waitFor(() => expect(onDone).toHaveBeenCalledTimes(1));
    expect(entry().item.editionVerified).toBe(true);
  });
});
