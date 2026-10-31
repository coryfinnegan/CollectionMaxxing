// #448: adding an item refreshes the library list by itself — no pull-to-refresh.
// A Review commit, its Undo, and a manual add each show up in the grid on their own.
import { QueryClientProvider } from '@tanstack/react-query';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react-native';
import { SafeAreaProvider } from 'react-native-safe-area-context';
import type { StoreApi } from 'zustand/vanilla';
import type { CandidateDto } from '@/api/generated/models';
import { useCardsStore } from '@/features/scan/cards/cardsStore';
import { useAutoCommitUndoStore } from '@/features/scan/session/autoCommitUndo';
import { createDiscardOutbox, setDiscardOutbox } from '@/features/scan/session/discards';
import { useScanSession } from '@/features/scan/session/useScanSession';
import { createSessionCountersStore, setSessionCountersStore } from '@/features/scan/session/sessionCounters';
import { setSessionContextStorage } from '@/features/scan/session/sessionContext';
import { createPipelineStore, setPipelineStore } from '@/features/scan/pipeline/pipelineStore';
import { setTrayStore } from '@/features/scan/tray/storage';
import { createTrayStore, MemoryKeyValueStore, type TrayStore } from '@/features/scan/tray/trayStore';
import { createProvisionalOutbox, setProvisionalOutbox } from '@/features/review/provisionals';
import { ReviewScreen } from '@/features/review/ReviewScreen';
import { candidate, release } from '@/features/review/testData';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { resetItems } from '@/mocks/items';
import { scanMock } from '@/mocks/scan';
import { reviewMock } from '@/mocks/scan-review';
import { server } from '@/mocks/server';
import { makeQueryClient, renderWithProviders, testMetrics } from '@/test/render';
import { LibraryView } from './LibraryView';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));

const SESSION = 'c0000000-0000-4000-8000-000000000001';

const seed = (list: CandidateDto[]) => {
  scanMock.sessions.set(SESSION, { id: SESSION, libraryId: MOCK_LIBRARY_ID, seq: 100, candidates: list, answers: new Map(), status: 'open', provisionals: [] });
};

/** Zelda is not on the seed shelf, so its tile proves the list refetched. */
const zelda = () => candidate({ release: release(3, 'Zelda', { format: 'game_cartridge', year: 1998 }) });

/** Library and Review side by side on one cache, like the phone's tabs. */
const renderLibraryAndReview = async () =>
  render(
    <QueryClientProvider client={makeQueryClient()}>
      <SafeAreaProvider initialMetrics={testMetrics}>
        <LibraryView title="Library" />
        <ReviewScreen libraryId={MOCK_LIBRARY_ID} sessionId={SESSION} onOpenResult={jest.fn()} />
      </SafeAreaProvider>
    </QueryClientProvider>,
  );

const libraryGrid = () => within(screen.getByTestId('library-grid'));

/** The scanner's session brain with no camera, already joined to the session. */
function ScanHarness({ store }: { store: StoreApi<TrayStore> }) {
  useScanSession({ libraryId: MOCK_LIBRARY_ID, sources: [], store, tuning: { retryMs: 20, pollMs: 20, resendMs: 60_000, flashMs: 50 } });
  return null;
}

beforeEach(() => {
  scanMock.reset();
  reviewMock.reset();
  resetItems();
  useCardsStore.getState().reset();
  useAutoCommitUndoStore.setState({ last: null });
  mockRouter.push.mockClear();
  setSessionContextStorage(new MemoryKeyValueStore());
  setProvisionalOutbox(createProvisionalOutbox({ storage: new MemoryKeyValueStore() }));
  setDiscardOutbox(createDiscardOutbox({ storage: new MemoryKeyValueStore() }));
  setTrayStore(createTrayStore({ storage: new MemoryKeyValueStore() }));
  setPipelineStore(createPipelineStore({ storage: new MemoryKeyValueStore() }));
  setSessionCountersStore(createSessionCountersStore({ storage: new MemoryKeyValueStore() }));
});

afterEach(() => {
  setSessionContextStorage(undefined);
  setProvisionalOutbox(undefined);
  setDiscardOutbox(undefined);
  setTrayStore(undefined);
  setPipelineStore(undefined);
  setSessionCountersStore(undefined);
});

describe('library refresh after an add (#448)', () => {
  it('shows a committed scan in the grid with no manual refresh', async () => {
    seed([zelda()]);
    await renderLibraryAndReview();
    await libraryGrid().findByText('The Matrix');
    expect(libraryGrid().queryByText('Zelda')).toBeNull();

    fireEvent.press(await screen.findByLabelText('Add 1 item'));
    await screen.findByText('Added 1 item');

    expect(await libraryGrid().findByText('Zelda')).toBeTruthy();
  });

  it('drops the items from the grid when the commit is undone, with no manual refresh', async () => {
    seed([zelda()]);
    await renderLibraryAndReview();
    await libraryGrid().findByText('The Matrix');
    fireEvent.press(await screen.findByLabelText('Add 1 item'));
    await screen.findByText('Added 1 item');
    await libraryGrid().findByText('Zelda');

    fireEvent.press(screen.getByLabelText('Undo'));
    await screen.findByText(/Undone/);

    await waitFor(() => expect(libraryGrid().queryByText('Zelda')).toBeNull());
  });

  it('shows a manual add in the grid with no manual refresh', async () => {
    await renderWithProviders(<LibraryView title="Library" />);
    await screen.findByText('The Matrix');

    await fireEvent.press(await screen.findByLabelText('Add item'));
    await fireEvent.press(screen.getByLabelText('None of these? Add it yourself'));
    await fireEvent.changeText(screen.getByLabelText('Title'), 'A hand-typed game');
    await fireEvent.press(screen.getByLabelText('Add to library'));

    expect(await libraryGrid().findByText('A hand-typed game')).toBeTruthy();
  });

  it('refetches the grid when a T0 auto-add lands mid-scan', async () => {
    seed([]);
    const store = createTrayStore({ storage: new MemoryKeyValueStore() });
    store.getState().setSessionId(SESSION);
    let listFetches = 0;
    server.events.on('request:start', ({ request }) => {
      if (new URL(request.url).pathname.endsWith('/items')) listFetches += 1;
    });
    try {
      await render(
        <QueryClientProvider client={makeQueryClient()}>
          <SafeAreaProvider initialMetrics={testMetrics}>
            <LibraryView title="Library" />
            <ScanHarness store={store} />
          </SafeAreaProvider>
        </QueryClientProvider>,
      );
      await libraryGrid().findByText('The Matrix');
      expect(listFetches).toBe(1);

      await act(async () => {
        useAutoCommitUndoStore.getState().setLast({ sessionId: SESSION, candidateId: 'c-auto' });
      });

      await waitFor(() => expect(listFetches).toBe(2));
    } finally {
      server.events.removeAllListeners();
    }
  });
});
