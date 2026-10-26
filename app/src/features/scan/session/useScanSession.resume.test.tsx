// Resuming the scanner after the app was killed never brings a discarded scan back: a server-rejected
// candidate stays out of the cards, and an offline queued discard stays queued and hidden until it lands.
import { waitFor } from '@testing-library/react-native';
import { http, HttpResponse } from 'msw';
import type { StoreApi } from 'zustand/vanilla';
import type { CandidateDto } from '@/api/generated/models';
import { candidate } from '@/features/review/testData';
import { useCardsStore } from '../cards/cardsStore';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { scanMock } from '@/mocks/scan';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { createTrayStore, MemoryKeyValueStore, type TrayStore } from '../tray/trayStore';
import { createDiscardOutbox, getDiscardOutbox, isDiscarded, setDiscardOutbox } from './discards';
import { useScanSession } from './useScanSession';

const SESSION = 'b2000000-0000-4000-8000-000000000001';
const tuning = { retryMs: 20, pollMs: 20, resendMs: 40, flashMs: 50 };

const seed = (list: CandidateDto[]) => {
  scanMock.sessions.set(SESSION, { id: SESSION, libraryId: MOCK_LIBRARY_ID, seq: 100, candidates: list, answers: new Map(), status: 'open', provisionals: [] });
};

/** A resumed scanner: the tray already knows its session id, as after the app was killed. */
const resumedTray = (): StoreApi<TrayStore> => {
  const store = createTrayStore({ storage: new MemoryKeyValueStore() });
  store.getState().setSessionId(SESSION);
  return store;
};

function Harness({ store }: { store: StoreApi<TrayStore> }) {
  useScanSession({ libraryId: MOCK_LIBRARY_ID, sources: [], store, tuning });
  return null;
}

beforeEach(() => {
  scanMock.reset();
  useCardsStore.getState().reset();
  setDiscardOutbox(createDiscardOutbox({ storage: new MemoryKeyValueStore() }));
});

afterEach(() => {
  setDiscardOutbox(undefined);
});

describe('scanner resume after a discard', () => {
  it('does not bring back a server-rejected candidate', async () => {
    const rejected = candidate({ decision: 'rejected' });
    const kept = candidate();
    seed([rejected, kept]);

    await renderWithProviders(<Harness store={resumedTray()} />);

    await waitFor(() => expect(useCardsStore.getState().byId[kept.id]).toBeDefined());
    expect(useCardsStore.getState().byId[rejected.id]).toBeUndefined();
  });

  it('flushes a queued discard on start, so Review is not needed to land it', async () => {
    const tossed = candidate();
    const kept = candidate();
    seed([tossed, kept]);
    getDiscardOutbox().enqueue({ libraryId: MOCK_LIBRARY_ID, sessionId: SESSION, candidateId: tossed.id });

    await renderWithProviders(<Harness store={resumedTray()} />);

    await waitFor(() => expect(scanMock.sessions.get(SESSION)!.candidates.find((c) => c.id === tossed.id)!.decision).toBe('rejected'));
    expect(useCardsStore.getState().byId[tossed.id]).toBeUndefined();
    expect(useCardsStore.getState().byId[kept.id]).toBeDefined();
    expect(isDiscarded(tossed.id)).toBe(false);
  });

  it('keeps an offline queued discard hidden and queued until it can land', async () => {
    const tossed = candidate();
    const kept = candidate();
    seed([tossed, kept]);
    getDiscardOutbox().enqueue({ libraryId: MOCK_LIBRARY_ID, sessionId: SESSION, candidateId: tossed.id });
    server.use(http.patch('*/api/v1/scan-sessions/:id/candidates/:cid', () => HttpResponse.error()));

    await renderWithProviders(<Harness store={resumedTray()} />);

    await waitFor(() => expect(useCardsStore.getState().byId[kept.id]).toBeDefined());
    expect(useCardsStore.getState().byId[tossed.id]).toBeUndefined();
    expect(scanMock.sessions.get(SESSION)!.candidates.find((c) => c.id === tossed.id)!.decision).toBe('undecided');
    expect(isDiscarded(tossed.id)).toBe(true);
  });
});
