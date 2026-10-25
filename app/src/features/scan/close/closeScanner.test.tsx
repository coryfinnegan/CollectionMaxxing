// The close-scanner behavior wired to the real tray and the mock API.
import { act, fireEvent, screen, waitFor } from '@testing-library/react-native';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { scanMock } from '@/mocks/scan';
import { Button } from '@/ui';
import { renderWithProviders } from '@/test/render';
import { useCardsStore } from '../cards/cardsStore';
import { setTrayStore } from '../tray/storage';
import { createTrayStore, MemoryKeyValueStore } from '../tray/trayStore';
import { CloseScannerSheet } from './CloseScannerSheet';
import { useCloseScanner } from './useCloseScanner';

const SESSION = 'a0000000-0000-4000-8000-0000000000aa';
let leave: jest.Mock;
let review: jest.Mock;

function Harness({ store }: { store: ReturnType<typeof createTrayStore> }) {
  const close = useCloseScanner({ store, onLeave: leave, onReview: review });
  return (
    <>
      <CloseScannerSheet state={close.state} unreviewed={close.unreviewed} onChoose={close.choose} onDismiss={close.dismiss} />
      {close.state.phase === 'idle' ? <Button label="Close scanner" onPress={close.request} /> : null}
    </>
  );
}

const trayWithItems = (n: number) => {
  const store = createTrayStore({ storage: new MemoryKeyValueStore(), newKey: () => 'k', now: () => 1 });
  store.getState().setSessionId(SESSION);
  for (let i = 0; i < n; i += 1) store.getState().scan({ value: `code-${i}`, symbology: 'ean-13' });
  return store;
};

beforeEach(() => {
  scanMock.reset();
  useCardsStore.getState().reset();
  leave = jest.fn();
  review = jest.fn();
  scanMock.sessions.set(SESSION, { id: SESSION, libraryId: MOCK_LIBRARY_ID, seq: 1, candidates: [], answers: new Map(), status: 'open' });
});
afterEach(() => setTrayStore(undefined));

describe('closing the scanner', () => {
  it('closes straight away with nothing scanned', async () => {
    await renderWithProviders(<Harness store={trayWithItems(0)} />);
    fireEvent.press(screen.getByLabelText('Close scanner'));
    expect(leave).toHaveBeenCalled();
    expect(screen.queryByText('Keep for later')).toBeNull();
  });

  it('Review goes to Review and keeps the tray', async () => {
    const store = trayWithItems(2);
    await renderWithProviders(<Harness store={store} />);
    fireEvent.press(screen.getByLabelText('Close scanner'));
    fireEvent.press(await screen.findByLabelText('Review 2 items'));
    expect(review).toHaveBeenCalled();
    expect(store.getState().lines).toHaveLength(2);
  });

  it('Keep for later leaves and keeps the tray and the session', async () => {
    const store = trayWithItems(2);
    await renderWithProviders(<Harness store={store} />);
    fireEvent.press(screen.getByLabelText('Close scanner'));
    fireEvent.press(await screen.findByLabelText('Keep for later'));
    expect(leave).toHaveBeenCalled();
    expect(store.getState().lines).toHaveLength(2);
    expect(store.getState().sessionId).toBe(SESSION);
    expect(scanMock.sessions.get(SESSION)!.status).toBe('open');
  });

  it('Discard abandons the session and clears the tray', async () => {
    const store = trayWithItems(2);
    await renderWithProviders(<Harness store={store} />);
    fireEvent.press(screen.getByLabelText('Close scanner'));
    await act(async () => {
      fireEvent.press(await screen.findByLabelText('Discard'));
    });
    await waitFor(() => expect(leave).toHaveBeenCalled());
    expect(scanMock.sessions.get(SESSION)!.status).toBe('abandoned');
    expect(store.getState().lines).toHaveLength(0);
  });
});
