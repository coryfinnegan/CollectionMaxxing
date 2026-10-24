// Layout quality (#269): the scan screens respect the safe area, and the scanner's
// waiting room (no library yet) offers a way back out of the modal.
import { fireEvent, screen } from '@testing-library/react-native';
import { mockAccount } from '@/mocks/data';
import { scanMock } from '@/mocks/scan';
import { renderWithProviders } from '@/test/render';
import { ResultDetail } from './cards/ResultDetail';
import { useCardsStore } from './cards/cardsStore';
import { ScanTab } from './ScanTab';
import { ScannerRoute } from './scanner/ScannerRoute';
import { setTrayStore } from './tray/storage';
import { createTrayStore, MemoryKeyValueStore } from './tray/trayStore';

jest.mock('expo-router', () => ({ useRouter: () => ({ push: jest.fn(), back: jest.fn(), replace: jest.fn() }) }));

beforeEach(() => {
  scanMock.reset();
  useCardsStore.getState().reset();
  setTrayStore(createTrayStore({ storage: new MemoryKeyValueStore() }));
});
afterEach(() => setTrayStore(undefined));

describe('scan layout', () => {
  it('keeps the Scan tab inside the safe area', async () => {
    await renderWithProviders(<ScanTab />);
    expect(await screen.findByText('Scan')).toBeTruthy();
    expect(screen.getByTestId('scan-tab-safe-area')).toBeTruthy();
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
    expect(await screen.findByText('No library found. Pair this iPhone again.')).toBeTruthy();
    expect(screen.getByTestId('scanner-fallback-safe-area')).toBeTruthy();
    fireEvent.press(screen.getByLabelText('Close'));
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});
