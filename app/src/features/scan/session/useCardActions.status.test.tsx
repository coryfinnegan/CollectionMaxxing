// The scan card for a release on the wishlist, one you sold, and one you own but cannot find: what each says (online from the
// candidate, offline from the phone's own copy) and what Got it and Found it do against the mock API, with Undo.
import { onlineManager } from '@tanstack/react-query';
import { act, fireEvent, screen, waitFor } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import type { CandidateDto } from '@/api/generated/models';
import { localDateString } from '@/lib/dates';
import { MemoryOwnedStorage, setOwnedStorage } from '@/features/offline/ownedCache';
import { syncLibrary } from '@/features/offline/sync';
import { useCardsStore } from '@/features/scan/cards/cardsStore';
import { ResultCards } from '@/features/scan/cards/ResultCards';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { itemsState, resetItems } from '@/mocks/items';
import { resetOwnershipMock, upsertOwned } from '@/mocks/ownership';
import { scanMock } from '@/mocks/scan';
import { reviewMock } from '@/mocks/scan-review';
import { server } from '@/mocks/server';
import { useLibraryStore } from '@/state/library';
import { renderWithProviders } from '@/test/render';
import { useCardActions } from './useCardActions';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));

const SESSION = 'f0000000-0000-4000-8000-000000000001';
const entryOf = (title: string) => itemsState.entries.find((e) => e.work.title === title)!;

const card = (over: Partial<CandidateDto> & { title: string; releaseId: string }): CandidateDto =>
  ({
    id: `c-${over.releaseId}`,
    seq: 1,
    source: 'barcode',
    rawCode: '9786000000028',
    resolution: 'matched',
    alreadyOwnedCount: 0,
    ownedAt: [],
    decision: 'undecided',
    release: { id: over.releaseId, workId: `w-${over.releaseId}`, title: over.title, format: 'game_disc', year: 2002, platform: null, creators: [], coverUrl: null },
    ...over,
  }) as unknown as CandidateDto;

function Harness({ cards, canAdd = true }: { cards: CandidateDto[]; canAdd?: boolean }) {
  const actions = useCardActions({ libraryId: MOCK_LIBRARY_ID, sessionId: SESSION, canAdd });
  return <ResultCards cards={cards} actions={actions} />;
}

const press = async (label: string) => {
  const target = await screen.findByLabelText(label);
  await act(async () => {
    fireEvent.press(target);
  });
};

beforeEach(() => {
  scanMock.reset();
  reviewMock.reset();
  resetItems();
  mockRouter.push.mockClear();
  resetOwnershipMock();
  setOwnedStorage(new MemoryOwnedStorage());
  useCardsStore.getState().reset();
  useLibraryStore.setState({ activeLibraryId: MOCK_LIBRARY_ID });
  onlineManager.setOnline(true);
  scanMock.sessions.set(SESSION, { id: SESSION, libraryId: MOCK_LIBRARY_ID, seq: 100, candidates: [], answers: new Map(), status: 'open' });
});
afterEach(() => {
  onlineManager.setOnline(true);
  useLibraryStore.setState({ activeLibraryId: null });
});
afterAll(() => setOwnedStorage(undefined));

describe('the wishlist card', () => {
  // Blade Runner is on the mock library's wishlist.
  const bladeRunner = () =>
    card({ title: 'Blade Runner', releaseId: entryOf('Blade Runner').release.id, wishlistItemId: entryOf('Blade Runner').item.id });

  it('says "On your wishlist", never "Already own", and offers Got it instead of Add', async () => {
    await renderWithProviders(<Harness cards={[bladeRunner()]} />);

    expect(await screen.findByText('On your wishlist')).toBeTruthy();
    expect(screen.getByLabelText('Got it: Blade Runner')).toBeTruthy();
    expect(screen.queryByText(/Already own/)).toBeNull();
    expect(screen.queryByLabelText('Add Blade Runner')).toBeNull();
    expect(screen.queryByLabelText('Add Blade Runner to wishlist')).toBeNull();
  });

  it('makes the wishlist item owned, dated today on the device, and the card says it is in the library', async () => {
    await renderWithProviders(<Harness cards={[bladeRunner()]} />);

    await press('Got it: Blade Runner');

    expect(await screen.findByText('In your library')).toBeTruthy();
    const item = entryOf('Blade Runner').item;
    expect(item.status).toBe('owned');
    expect(item.acquiredOn).toBe(localDateString());
    expect(screen.queryByLabelText('Got it: Blade Runner')).toBeNull();
    expect(itemsState.entries.filter((e) => e.work.title === 'Blade Runner')).toHaveLength(1); // no second item was added
  });

  it('puts it back on the wishlist when Undo is pressed', async () => {
    await renderWithProviders(<Harness cards={[bladeRunner()]} />);
    await press('Got it: Blade Runner');
    await screen.findByText('In your library');

    await press('Undo: Blade Runner');

    await waitFor(() => expect(entryOf('Blade Runner').item.status).toBe('wishlist'));
    expect(entryOf('Blade Runner').item.acquiredOn).toBeNull();
    expect(await screen.findByLabelText('Got it: Blade Runner')).toBeTruthy();
  });

  it('refuses an Undo after someone changed the item, and leaves it as it is', async () => {
    await renderWithProviders(<Harness cards={[bladeRunner()]} />);
    await press('Got it: Blade Runner');
    await screen.findByText('In your library');
    entryOf('Blade Runner').item.status = 'sold'; // changed by someone else since

    await press('Undo: Blade Runner');

    expect(await screen.findByText('Someone changed this. Reload to see the latest.')).toBeTruthy();
    expect(entryOf('Blade Runner').item.status).toBe('sold');
  });

  it('shows a viewer the line but no Got it: they could not save it', async () => {
    await renderWithProviders(<Harness cards={[bladeRunner()]} canAdd={false} />);

    expect(await screen.findByText('On your wishlist')).toBeTruthy();
    expect(screen.queryByLabelText('Got it: Blade Runner')).toBeNull();
  });
});

describe('the sold card', () => {
  const tetris = () => card({ title: 'Tetris', releaseId: entryOf('Tetris').release.id, soldItemId: entryOf('Tetris').item.id });

  it('says "You sold this" in a muted line and keeps the normal Add and Wishlist actions', async () => {
    await renderWithProviders(<Harness cards={[tetris()]} />);

    expect(await screen.findByText('You sold this')).toBeTruthy();
    expect(screen.getByLabelText('Add Tetris')).toBeTruthy();
    expect(screen.getByLabelText('Add Tetris to wishlist')).toBeTruthy();
    expect(screen.queryByText(/Already own/)).toBeNull();
    expect(screen.queryByLabelText('Got it: Tetris')).toBeNull();
  });
});

describe('the missing card', () => {
  const pong = () => card({ title: 'Pong', releaseId: entryOf('Pong').release.id, alreadyOwnedCount: 1, ownedAt: ['Game room › Shelf 1'] });
  // The server's answer: the copy is still yours, marked missing.
  const answerMissing = () =>
    server.use(
      http.get('*/api/v1/libraries/:libraryId/ownership', () =>
        HttpResponse.json({
          owned: true,
          count: 1,
          sameWorkOtherFormat: [],
          copies: [
            {
              itemId: entryOf('Pong').item.id,
              releaseId: entryOf('Pong').release.id,
              workId: entryOf('Pong').work.id,
              title: 'Pong',
              format: 'game_cartridge',
              platform: 'Atari 2600',
              status: 'missing',
              locationId: null,
              locationPath: 'Game room › Shelf 1',
            },
          ],
        }),
      ),
    );

  it('says you own it, marked missing, and offers Found it', async () => {
    answerMissing();
    await renderWithProviders(<Harness cards={[pong()]} />);

    expect(await screen.findByText('Already own — Atari 2600, Game room › Shelf 1 · marked missing')).toBeTruthy();
    expect(screen.getByLabelText('Found it: Pong')).toBeTruthy();
    expect(screen.queryByLabelText('Add another copy of Pong')).toBeNull();
  });

  it('marks it owned again with Found it, and Undo marks it missing again', async () => {
    answerMissing();
    await renderWithProviders(<Harness cards={[pong()]} />);
    await screen.findByLabelText('Found it: Pong');

    await press('Found it: Pong');

    expect(await screen.findByText('Back in your library')).toBeTruthy();
    expect(entryOf('Pong').item.status).toBe('owned');

    await press('Undo: Pong');
    await waitFor(() => expect(entryOf('Pong').item.status).toBe('missing'));
  });

  it('keeps the ordinary owned card when a copy is on the shelf', async () => {
    server.use(
      http.get('*/api/v1/libraries/:libraryId/ownership', () =>
        HttpResponse.json({
          owned: true,
          count: 1,
          sameWorkOtherFormat: [],
          copies: [
            { itemId: 'i', releaseId: 'r', workId: 'w', title: 'Pong', format: 'game_cartridge', platform: 'Atari 2600', status: 'owned', locationId: null, locationPath: 'Game room › Shelf 1' },
          ],
        }),
      ),
    );
    await renderWithProviders(<Harness cards={[pong()]} />);

    expect(await screen.findByText('Already own — Atari 2600, Game room › Shelf 1')).toBeTruthy();
    expect(screen.getByLabelText('Add another copy of Pong')).toBeTruthy();
    expect(screen.queryByLabelText('Found it: Pong')).toBeNull();
  });
});

describe('the cards offline, from the phone\'s own copy', () => {
  const FAILED = (code: string) =>
    ({ id: `x-${code}`, seq: 1, rawCode: code, resolution: 'error', alreadyOwnedCount: 0, ownedAt: [], decision: 'undecided', release: null }) as unknown as CandidateDto;
  const snapshotRow = (n: number, status: 'wishlist' | 'sold' | 'missing', code: string) => ({
    itemId: `0000008${n}-0000-4000-8000-000000000000`,
    deleted: false,
    releaseId: `0000018${n}-0000-4000-8000-000000000000`,
    workId: `0000028${n}-0000-4000-8000-000000000000`,
    title: `Offline ${status}`,
    format: 'dvd' as const,
    platform: null,
    identifiers: [code],
    locationPath: status === 'missing' ? 'Garage' : null,
    status,
  });

  beforeEach(async () => {
    upsertOwned(snapshotRow(1, 'wishlist', '5051889000006'));
    upsertOwned(snapshotRow(2, 'sold', '5051889000013'));
    upsertOwned(snapshotRow(3, 'missing', '5051889000020'));
    await syncLibrary(MOCK_LIBRARY_ID, () => 0);
    onlineManager.setOnline(false);
  });

  it('says "On your wishlist" for a code on the wishlist', async () => {
    await renderWithProviders(<Harness cards={[FAILED('5051889000006')]} />);

    expect(await screen.findByText('On your wishlist')).toBeTruthy();
    expect(screen.queryByText(/Already own/)).toBeNull();
    expect(screen.queryByText('Not in your library')).toBeNull();
  });

  it('says "You sold this" for a code you sold', async () => {
    await renderWithProviders(<Harness cards={[FAILED('5051889000013')]} />);

    expect(await screen.findByText('You sold this')).toBeTruthy();
    expect(screen.queryByText(/Already own/)).toBeNull();
  });

  it('says you own a missing one, marked missing', async () => {
    await renderWithProviders(<Harness cards={[FAILED('5051889000020')]} />);

    expect(await screen.findByText('Already own — DVD, Garage · marked missing')).toBeTruthy();
  });
});
