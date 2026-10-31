import { screen } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import type { CandidateDto } from '@/api/generated/models';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { resetOwnershipMock, upsertOwned } from '@/mocks/ownership';
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

  it('says a new match is already in this scan, as the server accepts it by itself', async () => {
    const candidate = await seedSession(DEV_CODES.mario64New);
    expect(candidate.decision).toBe('accepted');
    useCardsStore.getState().merge([candidate], { announce: false });
    await renderWithProviders(<ResultDetail id={candidate.id} />);

    expect(await screen.findByText('Not in your library')).toBeTruthy();
    expect(await screen.findByText('Added to this scan')).toBeTruthy();
    expect(screen.queryByLabelText('Add to library')).toBeNull();
  });

  it('offers Add to library and Wishlist for something new that is not in the scan', async () => {
    const candidate = await seedSession(DEV_CODES.mario64New);
    useCardsStore.getState().merge([{ ...candidate, decision: 'undecided' }], { announce: false }); // taken out of the scan again
    await renderWithProviders(<ResultDetail id={candidate.id} />);

    expect(await screen.findByText('Not in your library')).toBeTruthy();
    expect(await screen.findByLabelText('Add to library')).toBeTruthy();
    expect(screen.getByLabelText('Wishlist')).toBeTruthy();
  });

  it('hides the add buttons from a viewer but still shows ownership', async () => {
    server.use(http.get('*/api/v1/me', () => HttpResponse.json({ id: 'u', memberships: [{ libraryId: MOCK_LIBRARY_ID, role: 'viewer' }] })));
    const candidate = await seedSession(DEV_CODES.mario64New);
    useCardsStore.getState().merge([{ ...candidate, decision: 'undecided' }], { announce: false });
    await renderWithProviders(<ResultDetail id={candidate.id} />);

    expect(await screen.findByText('Not in your library')).toBeTruthy();
    expect(screen.queryByLabelText('Add to library')).toBeNull();
  });

  it('says a release on the wishlist is on the wishlist, offers Got it, and does not offer to add it', async () => {
    const candidate = await seedSession(DEV_CODES.wishlisted);
    expect(candidate.wishlistItemId).toBeTruthy();
    useCardsStore.getState().merge([candidate], { announce: false });
    await renderWithProviders(<ResultDetail id={candidate.id} />);

    expect(await screen.findByText('On your wishlist')).toBeTruthy();
    expect(screen.getByLabelText('Got it: Metroid Prime')).toBeTruthy();
    expect(screen.queryByText(/You own this/)).toBeNull();
    expect(screen.queryByLabelText('Add to library')).toBeNull();
    expect(screen.queryByLabelText('Wishlist')).toBeNull();
  });

  it('adds a muted "You sold this" to a release you sold, and keeps the add buttons', async () => {
    const candidate = await seedSession(DEV_CODES.sold);
    expect(candidate.soldItemId).toBeTruthy();
    useCardsStore.getState().merge([{ ...candidate, decision: 'undecided' }], { announce: false });
    await renderWithProviders(<ResultDetail id={candidate.id} />);

    expect(await screen.findByText('Not in your library')).toBeTruthy();
    expect(screen.getByText('You sold this')).toBeTruthy();
    expect(screen.getByLabelText('Add to library')).toBeTruthy();
  });

  it('says a missing copy is marked missing, and offers Found it', async () => {
    resetOwnershipMock();
    upsertOwned({
      itemId: 'd0000000-0000-4000-8000-000000000008',
      deleted: false,
      releaseId: 'c1000000-0000-4000-8000-000000000008',
      workId: 'c2000000-0000-4000-8000-000000000008',
      title: 'Pong',
      format: 'game_cartridge',
      platform: 'Atari 2600',
      identifiers: [],
      locationPath: 'Game room › Shelf 1',
      status: 'missing',
    });
    const candidate = await seedSession(DEV_CODES.missing);
    useCardsStore.getState().merge([candidate], { announce: false });
    await renderWithProviders(<ResultDetail id={candidate.id} />);

    expect(await screen.findByText('You own this: Game room › Shelf 1 · marked missing')).toBeTruthy();
    expect(screen.getByLabelText('Found it: Pong')).toBeTruthy();
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

  it('opens a barcode result as a hero that fills the screen', async () => {
    const candidate = await seedSession(DEV_CODES.duneOwned);
    useCardsStore.getState().merge([candidate], { announce: false });
    await renderWithProviders(<ResultDetail id={candidate.id} />);

    // No photo to show, so the facts card composes like the item detail hero instead of a small row over dead space (#373).
    expect(await screen.findByTestId('result-hero')).toBeTruthy();
    expect(screen.getByText('Dune')).toBeTruthy();
  });
});
