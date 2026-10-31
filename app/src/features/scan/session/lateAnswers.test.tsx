// What the session brings back belongs to the scan it was asked for. After a library switch or leaving the scanner, a
// barcode answer, a session snapshot or an event that lands late is not drawn into the cards the next library shows.
import { act, waitFor } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import type { StoreApi } from 'zustand/vanilla';
import { candidate } from '@/features/review/testData';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { scanMock } from '@/mocks/scan';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { useCardsStore } from '../cards/cardsStore';
import { DEV_CODES } from '../scanner/devCodes';
import { FakeScannerSource } from '../scanner/ScannerSource';
import { createTrayStore, MemoryKeyValueStore, type TrayStore } from '../tray/trayStore';
import { createDiscardOutbox, setDiscardOutbox } from './discards';
import { flushDeps, routeSessionMessage } from './sessionApi';
import { useScanSession } from './useScanSession';

const SESSION = 'b3000000-0000-4000-8000-000000000001';
const tuning = { retryMs: 20, pollMs: 20, resendMs: 40, flashMs: 50 };
const cards = () => Object.values(useCardsStore.getState().byId);

const gate = () => {
  let release: () => void = () => undefined;
  const hold = new Promise<void>((resolve) => (release = resolve));
  return { hold, release };
};

beforeEach(() => {
  scanMock.reset();
  useCardsStore.getState().reset();
  setDiscardOutbox(createDiscardOutbox({ storage: new MemoryKeyValueStore() }));
});
afterEach(() => {
  setDiscardOutbox(undefined);
});

const openSession = (candidates = [candidate()]) => {
  scanMock.sessions.set(SESSION, { id: SESSION, libraryId: MOCK_LIBRARY_ID, seq: 100, candidates, answers: new Map(), status: 'open', provisionals: [] });
};

describe('a barcode answer that lands late', () => {
  const batch = { key: 'k1', entries: [{ value: DEV_CODES.duneOwned, symbology: 'ean-13', copies: 1 }] };

  it('is drawn while its scan is still the one on screen', async () => {
    openSession([]);
    await flushDeps(MOCK_LIBRARY_ID, () => ({}), () => true).sendBatch(SESSION, batch);
    expect(cards()).toHaveLength(1);
  });

  it('is not drawn once its scan is not the one on screen', async () => {
    openSession([]);
    let current = true;
    const answer = gate();
    server.use(
      http.post('*/api/v1/scan-sessions/:id/barcodes', async () => {
        await answer.hold;
        return undefined; // the default handler answers
      }),
    );

    const sending = flushDeps(MOCK_LIBRARY_ID, () => ({}), () => current).sendBatch(SESSION, batch);
    current = false; // the library was switched
    answer.release();
    await sending;

    expect(cards()).toEqual([]);
  });
});

describe('an event that lands late', () => {
  it('is not drawn when its scan is not the one on screen', () => {
    routeSessionMessage(SESSION, { event: 'candidate.updated', id: '1', data: JSON.stringify(candidate()) }, () => false);
    expect(cards()).toEqual([]);
  });

  it('is drawn otherwise', () => {
    routeSessionMessage(SESSION, { event: 'candidate.updated', id: '1', data: JSON.stringify(candidate()) });
    expect(cards()).toHaveLength(1);
  });
});

describe('the scanner that was left while its session loaded', () => {
  const resumedTray = (): StoreApi<TrayStore> => {
    const store = createTrayStore({ storage: new MemoryKeyValueStore() });
    store.getState().setSessionId(SESSION);
    return store;
  };
  function Harness({ store, source }: { store: StoreApi<TrayStore>; source?: FakeScannerSource }) {
    useScanSession({ libraryId: MOCK_LIBRARY_ID, sources: source ? [source] : [], store, tuning });
    return null;
  }

  it('does not draw the loaded session into the cards', async () => {
    openSession();
    const loading = gate();
    server.use(
      http.get('*/api/v1/scan-sessions/:id', async () => {
        await loading.hold;
        return undefined; // the default handler answers with the session
      }),
    );
    const view = await renderWithProviders(<Harness store={resumedTray()} />);

    await view.unmount(); // the library was switched
    loading.release();
    await new Promise((resolve) => setTimeout(resolve, 100));

    expect(cards()).toEqual([]);
  });

  it('does not draw a barcode answer that was on its way', async () => {
    openSession([]);
    const source = new FakeScannerSource();
    const answer = gate();
    let asked = false;
    server.use(
      http.post('*/api/v1/scan-sessions/:id/barcodes', async () => {
        asked = true;
        await answer.hold;
        return undefined;
      }),
    );
    const view = await renderWithProviders(<Harness store={resumedTray()} source={source} />);
    await act(async () => source.inject([{ value: DEV_CODES.duneOwned, symbology: 'ean-13' }]));
    await waitFor(() => expect(asked).toBe(true));

    await view.unmount();
    answer.release();
    await new Promise((resolve) => setTimeout(resolve, 100));

    expect(cards()).toEqual([]);
  });

  it('still loads the session while the scanner stays on screen', async () => {
    openSession();
    server.use(http.get('*/api/v1/scan-sessions/:id/events', () => new HttpResponse(null, { status: 503 })));
    await renderWithProviders(<Harness store={resumedTray()} />);
    await waitFor(() => expect(cards()).toHaveLength(1));
  });
});
