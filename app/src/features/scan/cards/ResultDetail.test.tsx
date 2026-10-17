import { screen } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import type { CandidateDto } from '@/api/generated/models';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { scanMock } from '@/mocks/scan';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { DEV_CODES } from '../scanner/devCodes';
import { setTrayStore } from '../tray/storage';
import { createTrayStore, MemoryKeyValueStore } from '../tray/trayStore';
import { ResultDetail } from './ResultDetail';
import { useCardsStore } from './cardsStore';

jest.mock('expo-router', () => ({ useRouter: () => ({ push: jest.fn(), back: jest.fn() }) }));

const seedSession = async (code: string) => {
  const store = createTrayStore({ storage: new MemoryKeyValueStore() });
  setTrayStore(store);
  const res = await fetch(`http://api.test/api/v1/libraries/${MOCK_LIBRARY_ID}/scan-sessions`, { method: 'POST', body: '{}' });
  const { id } = (await res.json()) as { id: string };
  store.getState().setSessionId(id);
  const batch = await fetch(`http://api.test/api/v1/scan-sessions/${id}/barcodes`, {
    method: 'POST',
    body: JSON.stringify({ codes: [{ value: code, symbology: 'ean-13', copies: 1 }] }),
  });
  const { candidates } = (await batch.json()) as { candidates: CandidateDto[] };
  return candidates[0]!;
};

beforeEach(() => {
  scanMock.reset();
  useCardsStore.getState().reset();
});
afterEach(() => setTrayStore(undefined));

describe('result detail', () => {
  it('shows the facts and where the owned copy lives, and no add buttons', async () => {
    const candidate = await seedSession(DEV_CODES.duneOwned);
    useCardsStore.getState().merge([candidate], { announce: false });
    await renderWithProviders(<ResultDetail id={candidate.id} />);

    expect(await screen.findByText('Dune')).toBeTruthy();
    expect(screen.getByText('You own this: Living room › Shelf 2')).toBeTruthy();
    expect(screen.queryByLabelText('Add to library')).toBeNull();
  });

  it('offers Add to library and Wishlist for something new', async () => {
    const candidate = await seedSession(DEV_CODES.mario64New);
    useCardsStore.getState().merge([candidate], { announce: false });
    await renderWithProviders(<ResultDetail id={candidate.id} />);

    expect(await screen.findByText('Not in your library')).toBeTruthy();
    expect(await screen.findByLabelText('Add to library')).toBeTruthy();
    expect(screen.getByLabelText('Wishlist')).toBeTruthy();
  });

  it('hides the add buttons from a viewer but still shows ownership', async () => {
    server.use(http.get('*/api/v1/me', () => HttpResponse.json({ id: 'u', memberships: [{ libraryId: MOCK_LIBRARY_ID, role: 'viewer' }] })));
    const candidate = await seedSession(DEV_CODES.mario64New);
    useCardsStore.getState().merge([candidate], { announce: false });
    await renderWithProviders(<ResultDetail id={candidate.id} />);

    expect(await screen.findByText('Not in your library')).toBeTruthy();
    expect(screen.queryByLabelText('Add to library')).toBeNull();
  });

  it('finds the card from the session when the app restarted since the scan', async () => {
    const candidate = await seedSession(DEV_CODES.duneOwned);
    await renderWithProviders(<ResultDetail id={candidate.id} />);
    expect(await screen.findByText('You own this: Living room › Shelf 2')).toBeTruthy();
  });

  it('says so when the result is gone', async () => {
    setTrayStore(createTrayStore({ storage: new MemoryKeyValueStore() }));
    await renderWithProviders(<ResultDetail id="missing" />);
    expect(await screen.findByText("That result isn't here any more")).toBeTruthy();
  });
});
