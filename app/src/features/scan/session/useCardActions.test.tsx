// The owned card's three actions end to end against the mock API: Add another copy accepts through the commit
// path, Move it here patches the location with one-tap undo, Skip rejects and the card goes away. Nothing adds silently.
import { onlineManager } from '@tanstack/react-query';
import { act, fireEvent, screen, waitFor } from '@testing-library/react-native';
import type { CandidateDto } from '@/api/generated/models';
import { MemoryOwnedStorage, setOwnedStorage } from '@/features/offline/ownedCache';
import { useCardsStore } from '@/features/scan/cards/cardsStore';
import { ResultCards } from '@/features/scan/cards/ResultCards';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { itemsState, resetItems } from '@/mocks/items';
import { deleteOwned, resetOwnershipMock, upsertOwned } from '@/mocks/ownership';
import { scanMock } from '@/mocks/scan';
import { useLibraryStore } from '@/state/library';
import { Snackbar } from '@/ui';
import { renderWithProviders } from '@/test/render';
import { useCardActions } from './useCardActions';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));

const SESSION = 'f0000000-0000-4000-8000-000000000001';
const SHELF_1 = 'a0000000-0000-4000-8000-000000000002';
const SHELF_2 = 'a0000000-0000-4000-8000-000000000003';
// The items mock's Dune paperback, on Shelf 2.
const DUNE_ITEM_ID = 'c0000000-0000-4000-8000-000000000003';
const RELEASE_ID = 'd0000000-0000-4000-8000-000000000001';
const WORK_ID = 'd0000000-0000-4000-8000-000000000002';

const dune = {
  id: 'c-owned',
  seq: 1,
  source: 'barcode',
  rawCode: '9780441172719',
  resolution: 'matched',
  alreadyOwnedCount: 1,
  ownedAt: ['Game room › Shelf 2'],
  decision: 'undecided',
  release: { id: RELEASE_ID, title: 'Dune', format: 'paperback', year: 1990, platform: null, creators: [], coverUrl: null },
} as unknown as CandidateDto;

function Harness({ moveToLocationId = SHELF_1 }: { moveToLocationId?: string | null }) {
  const actions = useCardActions({ libraryId: MOCK_LIBRARY_ID, sessionId: SESSION, canAdd: true, moveToLocationId });
  const byId = useCardsStore((s) => s.byId);
  return (
    <>
      {actions.moveToast ? (
        <Snackbar
          message={actions.moveToast.message}
          actionLabel={actions.moveToast.canUndo ? 'Undo' : undefined}
          onAction={actions.moveToast.canUndo ? actions.onUndoMove : undefined}
        />
      ) : null}
      <ResultCards cards={Object.values(byId)} actions={actions} />
    </>
  );
}

const press = async (label: string) => {
  const target = await screen.findByLabelText(label);
  await act(async () => {
    fireEvent.press(target);
  });
};

beforeEach(() => {
  scanMock.reset();
  resetItems();
  resetOwnershipMock();
  setOwnedStorage(new MemoryOwnedStorage());
  useCardsStore.getState().reset();
  useLibraryStore.setState({ activeLibraryId: MOCK_LIBRARY_ID });
  onlineManager.setOnline(true);
  upsertOwned({
    itemId: DUNE_ITEM_ID,
    deleted: false,
    releaseId: RELEASE_ID,
    workId: WORK_ID,
    title: 'Dune',
    format: 'paperback',
    platform: null,
    identifiers: ['9780441172719'],
    locationPath: 'Game room › Shelf 2',
  });
  const fresh = (): CandidateDto => JSON.parse(JSON.stringify(dune)) as CandidateDto;
  scanMock.sessions.set(SESSION, { id: SESSION, libraryId: MOCK_LIBRARY_ID, seq: 100, candidates: [fresh()], answers: new Map(), status: 'open' });
  useCardsStore.getState().merge([fresh()], { announce: false });
});

afterEach(() => {
  useLibraryStore.setState({ activeLibraryId: null });
});

afterAll(() => setOwnedStorage(undefined));

describe('the owned card actions', () => {
  it('adds another copy through the commit path, creating nothing yet', async () => {
    await renderWithProviders(<Harness />);
    await screen.findByText('Already own — Paperback, Game room › Shelf 2');

    await press('Add another copy of Dune');

    expect(await screen.findByText('Added to this scan')).toBeTruthy();
    expect(scanMock.sessions.get(SESSION)!.candidates[0]!.decision).toBe('accepted');
    // Accepting alone adds nothing: the copy is created when the scan is committed at Review.
    expect(itemsState.entries).toHaveLength(35);
  });

  it('moves the copy to the session spot, undone with one tap', async () => {
    await renderWithProviders(<Harness />);
    await screen.findByText('Already own — Paperback, Game room › Shelf 2');

    await press('Move Dune to Game room › Shelf 1');

    expect(await screen.findByText('Moved to Game room › Shelf 1.')).toBeTruthy();
    expect(itemsState.entries.find((e) => e.item.id === DUNE_ITEM_ID)!.item.locationId).toBe(SHELF_1);
    expect(itemsState.entries).toHaveLength(35);

    await press('Undo');

    expect(await screen.findByText('Move undone.')).toBeTruthy();
    expect(itemsState.entries.find((e) => e.item.id === DUNE_ITEM_ID)!.item.locationId).toBe(SHELF_2);
  });

  it('says when the copy is already at the session spot', async () => {
    await renderWithProviders(<Harness moveToLocationId={SHELF_2} />);
    await screen.findByText('Already own — Paperback, Game room › Shelf 2');

    await press('Move Dune to Game room › Shelf 2');

    expect(await screen.findByText('Already at Game room › Shelf 2.')).toBeTruthy();
    expect(screen.queryByLabelText('Undo')).toBeNull();
  });

  it('skips the owned default: the card goes away and nothing is added', async () => {
    await renderWithProviders(<Harness />);
    await screen.findByText('Already own — Paperback, Game room › Shelf 2');

    await press('Skip Dune');

    await waitFor(() => expect(screen.queryByText('Dune')).toBeNull());
    expect(scanMock.sessions.get(SESSION)!.candidates[0]!.decision).toBe('rejected');
    expect(itemsState.entries).toHaveLength(35);
  });

  it('says when the copy is gone instead of moving nothing', async () => {
    deleteOwned(DUNE_ITEM_ID);
    await renderWithProviders(<Harness />);
    await screen.findByText('Already own — Paperback, Game room › Shelf 2');

    await press('Move Dune to Game room › Shelf 1');

    expect(await screen.findByText("That's no longer here. Refresh and try again.")).toBeTruthy();
    expect(itemsState.entries.find((e) => e.item.id === DUNE_ITEM_ID)!.item.locationId).toBe(SHELF_2);
  });

  it('refuses undo when the copy moved again since', async () => {
    await renderWithProviders(<Harness />);
    await screen.findByText('Already own — Paperback, Game room › Shelf 2');
    await press('Move Dune to Game room › Shelf 1');
    await screen.findByText('Moved to Game room › Shelf 1.');

    // Someone put it somewhere else after the move.
    itemsState.entries.find((e) => e.item.id === DUNE_ITEM_ID)!.item.locationId = MOCK_LIBRARY_ID;
    await press('Undo');

    expect(await screen.findByText("Couldn't undo. It changed since, so it stays where it is.")).toBeTruthy();
    expect(itemsState.entries.find((e) => e.item.id === DUNE_ITEM_ID)!.item.locationId).toBe(MOCK_LIBRARY_ID);
  });
});
