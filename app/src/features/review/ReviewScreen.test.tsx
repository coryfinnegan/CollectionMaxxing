// Review end to end against the mock API: grouping on screen, one commit, Undo, pick-one, and read-only for a viewer.
import { act, fireEvent, screen, waitFor } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import type { CandidateDto } from '@/api/generated/models';
import { useCardsStore } from '@/features/scan/cards/cardsStore';
import { rememberSessionContext, setSessionContextStorage } from '@/features/scan/session/sessionContext';
import { MemoryKeyValueStore } from '@/features/scan/tray/trayStore';
import { setHapticDriver, type HapticDriver } from '@/lib/haptics';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { QUOTA_COPY } from '@/features/scan/cards/copy';
import { itemsState, resetItems } from '@/mocks/items';
import { scanMock } from '@/mocks/scan';
import { reviewMock } from '@/mocks/scan-review';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { createProvisionalOutbox, setProvisionalOutbox } from './provisionals';
import { ReviewScreen } from './ReviewScreen';
import { candidate, release } from './testData';

const SESSION = 'a0000000-0000-4000-8000-000000000001';
const SHELF_1 = 'a0000000-0000-4000-8000-000000000002';
const SHELF_2 = 'a0000000-0000-4000-8000-000000000003';
const alien = [release(11, 'Alien', { year: 1979 }), release(12, 'Aliens', { year: 1986 }), release(13, 'Alien 3', { year: 1992 })];

const seed = (list: CandidateDto[]) => {
  scanMock.sessions.set(SESSION, { id: SESSION, libraryId: MOCK_LIBRARY_ID, seq: 100, candidates: list, answers: new Map(), status: 'open', provisionals: [] });
};

const mario = () => candidate({ release: release(2, 'Super Mario 64', { format: 'game_cartridge' }) });
const dune = () => candidate({ release: release(1, 'Dune', { format: 'paperback' }), alreadyOwnedCount: 1, ownedAt: ['Living room › Shelf 2'] });
const pick = () => candidate({ resolution: 'ambiguous', release: alien[0], alternatives: alien as never });
const lost = () => candidate({ resolution: 'not_found', release: null, rawCode: '000111' });

const asViewer = () => {
  scanMock.role = 'viewer';
  server.use(http.get('*/api/v1/me', () => HttpResponse.json({ id: 'u1', memberships: [{ libraryId: MOCK_LIBRARY_ID, role: 'viewer' }] })));
};

const open = async (undoMs?: number) => {
  await renderWithProviders(<ReviewScreen libraryId={MOCK_LIBRARY_ID} sessionId={SESSION} undoMs={undoMs} />);
};

/** One tap, flushed before the next (state updates inside a sheet need their own act). */
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
  useCardsStore.getState().reset();
  setSessionContextStorage(new MemoryKeyValueStore());
  setProvisionalOutbox(createProvisionalOutbox({ storage: new MemoryKeyValueStore() }));
});

afterEach(() => {
  setSessionContextStorage(undefined);
  setProvisionalOutbox(undefined);
});

describe('Review screen', () => {
  it('shows the groups in order of attention, with counts', async () => {
    seed([mario(), dune(), pick(), lost()]);
    await open();
    await screen.findByText('Pick one · 1');
    const headers = screen.getAllByRole('header').map((h) => String(h.props.children));
    expect(headers.filter((h) => h.includes(' · ') && h !== 'Review')).toEqual(['Pick one · 1', 'Not found · 1', 'You own these · 1', 'Ready · 1']);
  });

  it('shows an empty state when there is nothing to review', async () => {
    seed([]);
    await open();
    expect(await screen.findByText('Nothing to review.')).toBeTruthy();
    expect(screen.getByText('Scan something and it shows up here.')).toBeTruthy();
  });

  it('keeps a T2 row as unverified: the row leaves Review and the keep is sent', async () => {
    const row = lost();
    seed([row]);
    await open();
    await press(`Keep ${`Code ${row.rawCode}`} as unverified`);
    await waitFor(() => expect(reviewMock.keeps).toHaveLength(1));
    expect(reviewMock.keeps[0]).toEqual({ sessionId: SESSION, candidateId: row.id, cropAssetId: null });
    await screen.findByText('Nothing to review.');
  });

  it('offers no keep to a viewer', async () => {
    asViewer();
    seed([lost()]);
    await open();
    await screen.findByText('Not found · 1');
    expect(screen.queryByLabelText(/Keep .* as unverified/)).toBeNull();
  });

  it('shows the kept crop beside a T2 row', async () => {
    const row = lost();
    row.cropUrl = '/media/covers/crop-1';
    seed([row]);
    await open();
    expect(await screen.findByLabelText(`Cover of Code ${row.rawCode}`)).toBeTruthy();
  });

  it('counts only checked rows and adds them with one commit, then offers Undo', async () => {
    seed([mario(), dune(), pick(), lost()]);
    await open();
    const button = await screen.findByLabelText('Add 1 item');
    expect(screen.queryByLabelText(/Snap cover/)).toBeNull(); // no cover handler, no cover button
    await act(async () => {
      fireEvent.press(button);
      fireEvent.press(button);
    });
    await screen.findByText('Added 1 item');
    expect(reviewMock.commits).toHaveLength(1);
    const added = scanMock.sessions.get(SESSION)!.candidates.filter((c) => c.createdItemId);
    expect(added).toHaveLength(1);

    const addedId = added[0]!.createdItemId;
    fireEvent.press(screen.getByLabelText('Undo'));
    await screen.findByText(/Undone/);
    expect(itemsState.entries.find((e) => e.item.id === addedId)).toBeUndefined(); // gone for good, not in Recently deleted
    await screen.findByLabelText('Add 1 item'); // the results are back in Review, ready to fix and add again
  });

  it('buzzes with weight on the commit and plays success when the items land', async () => {
    const calls: string[] = [];
    const driver: HapticDriver = {
      tap: () => void calls.push('tap'),
      mediumTap: () => void calls.push('medium'),
      longTap: () => void calls.push('long'),
      tick: () => void calls.push('tick'),
      success: () => void calls.push('success'),
      wait: async () => undefined,
    };
    setHapticDriver(driver);
    try {
      seed([mario()]);
      await open();
      await act(async () => {
        fireEvent.press(await screen.findByLabelText('Add 1 item'));
      });
      await screen.findByText('Added 1 item');
      expect(calls).toEqual(['medium', 'success']);
    } finally {
      setHapticDriver(null);
    }
  });

  it('takes the Undo away once its time is up', async () => {
    seed([mario()]);
    await open(150);
    fireEvent.press(await screen.findByLabelText('Add 1 item'));
    await screen.findByText('Added 1 item');
    await waitFor(() => expect(screen.queryByText('Added 1 item')).toBeNull(), { timeout: 2000 });
    expect(screen.queryByLabelText('Undo')).toBeNull();
  });

  it('celebrates a commit with the success moment, dismissed on a tap, Undo intact', async () => {
    seed([mario()]);
    await open();
    fireEvent.press(await screen.findByLabelText('Add 1 item'));

    const party = await screen.findByTestId('commit-celebration');
    expect(screen.getByTestId('celebration-spot', { includeHiddenElements: true })).toBeTruthy();
    await act(async () => {
      fireEvent.press(party);
    });
    await waitFor(() => expect(screen.queryByTestId('commit-celebration')).toBeNull());
    expect(screen.getByText('Added 1 item')).toBeTruthy();
    expect(screen.getByLabelText('Undo')).toBeTruthy();
  });

  it('takes the celebration away with the Undo', async () => {
    seed([mario()]);
    await open();
    fireEvent.press(await screen.findByLabelText('Add 1 item'));
    await screen.findByTestId('commit-celebration');

    fireEvent.press(screen.getByLabelText('Undo'));
    await screen.findByText(/Undone/);
    expect(screen.queryByTestId('commit-celebration')).toBeNull();
  });

  it('shows no celebration when the commit fails', async () => {
    seed([mario()]);
    reviewMock.failNextCommit = true;
    await open();
    fireEvent.press(await screen.findByLabelText('Add 1 item'));

    await screen.findByText(/Nothing was added/);
    expect(screen.queryByTestId('commit-celebration')).toBeNull();
  });

  it('keeps the Undo for ten seconds by default', async () => {
    const { UNDO_MS } = jest.requireActual('./useReview');
    expect(UNDO_MS).toBe(10_000);
  });

  it('keeps everything checked and explains when the commit fails', async () => {
    seed([mario()]);
    reviewMock.failNextCommit = true;
    await open();
    fireEvent.press(await screen.findByLabelText('Add 1 item'));
    await screen.findByText(/Nothing was added/);
    expect(screen.getByLabelText('Add 1 item')).toBeTruthy();
    await press('1 item ready. Show it');
    expect(screen.getByLabelText('Add Super Mario 64').props.accessibilityState.checked).toBe(true);
  });

  it('choosing an alternative sends that release', async () => {
    seed([pick()]);
    await open();
    fireEvent.press(await screen.findByLabelText('Aliens (1986) DVD'));
    await waitFor(() => expect(scanMock.sessions.get(SESSION)!.candidates[0]!.release?.id).toBe(alien[1]!.id));
    await screen.findByText('Ready · 1');
  });

  it('skipping a pick-one row removes it from the count', async () => {
    seed([pick(), mario()]);
    await open();
    fireEvent.press(await screen.findByLabelText('Skip Alien'));
    await waitFor(() => expect(screen.queryByText('Pick one · 1')).toBeNull());
    expect(screen.getByLabelText('Add 1 item')).toBeTruthy();
  });

  it('aggregates the successes to one line each, still counted while collapsed', async () => {
    seed([...Array.from({ length: 21 }, (_, i) => candidate({ release: release(200 + i, `Game ${i}`) })), dune()]);
    await open();
    await screen.findByText('Ready · 21');
    expect(screen.getByLabelText('21 items ready. Show them')).toBeTruthy();
    expect(screen.getByLabelText('1 item you own. Show it')).toBeTruthy();
    expect(screen.queryByLabelText('Add Game 0')).toBeNull();
    expect(screen.getByLabelText('Add 21 items')).toBeTruthy(); // the collapsed rows still count
  });

  it('opens and closes an aggregate without losing what each row had', async () => {
    seed([mario()]);
    await open();
    await press('1 item ready. Show it');
    expect(screen.getByLabelText('Add Super Mario 64').props.accessibilityState.checked).toBe(true);
    await press('Add Super Mario 64'); // unchecked while open
    await press('Hide these items');
    expect(screen.queryByLabelText('Add Super Mario 64')).toBeNull();
    expect(screen.getByLabelText('Add 0 items')).toBeTruthy();
    await press('1 item ready. Show it');
    expect(screen.getByLabelText('Add Super Mario 64').props.accessibilityState.checked).toBe(false);
  });

  it('lets a viewer look but not add', async () => {
    asViewer();
    seed([mario()]);
    await open();
    await screen.findByText('Ready · 1');
    await waitFor(() => expect(screen.getByText(/You can look but not add/)).toBeTruthy());
    expect(screen.queryByLabelText(/^Add \d+ items?$/)).toBeNull();
    expect(screen.queryByLabelText('Add Super Mario 64')).toBeNull();
  });

  describe('Undo', () => {
    it('removes the items for good in one call, never restores, and puts the results back in Review', async () => {
      seed([mario(), candidate({ release: release(3, 'Zelda', { format: 'game_cartridge', year: 1998 }) })]);
      await open();
      fireEvent.press(await screen.findByLabelText('Add 2 items'));
      await screen.findByText('Added 2 items');
      const ids = itemsState.entries.map((e) => e.item.id).slice(-2);
      expect(screen.queryByLabelText('Add Super Mario 64')).toBeNull(); // added, so no longer waiting in Review
      fireEvent.press(screen.getByLabelText('Undo'));
      await screen.findByText(/Undone/);
      expect(reviewMock.undos).toHaveLength(1);
      expect(reviewMock.undos[0]!.key).toBeTruthy();
      expect(reviewMock.restores).toHaveLength(0);
      expect(itemsState.entries.filter((e) => ids.includes(e.item.id))).toHaveLength(0);
      expect(scanMock.sessions.get(SESSION)!.status).toBe('open');
      await press('2 items ready. Show them');
      await screen.findByLabelText('Add Super Mario 64');
      expect(screen.getByLabelText('Add Zelda').props.accessibilityState.checked).toBe(true);
      expect(screen.getByLabelText('Add 2 items')).toBeTruthy();
    });

    it('can add again after an Undo, with a new commit', async () => {
      seed([mario()]);
      await open();
      fireEvent.press(await screen.findByLabelText('Add 1 item'));
      await screen.findByText('Added 1 item');
      fireEvent.press(screen.getByLabelText('Undo'));
      fireEvent.press(await screen.findByLabelText('Add 1 item'));
      await screen.findByText('Added 1 item');
      expect(reviewMock.commits).toHaveLength(2);
      expect(reviewMock.commits[1]!.key).not.toBe(reviewMock.commits[0]!.key);
    });

    it('shows friendly copy and keeps the Undo when it fails for a reason that may pass', async () => {
      seed([mario()]);
      await open();
      fireEvent.press(await screen.findByLabelText('Add 1 item'));
      await screen.findByText('Added 1 item');
      reviewMock.failNextUndo = 500;
      fireEvent.press(screen.getByLabelText('Undo'));
      await screen.findByText(/still in your library/);
      expect(screen.getByLabelText('Undo')).toBeTruthy();
      expect(screen.queryByText(/Undone/)).toBeNull();
      expect(itemsState.entries.some((e) => e.item.id === scanMock.sessions.get(SESSION)!.candidates[0]!.createdItemId)).toBe(true);
    });

    it('says why and leaves the items alone when the server refuses (too late, or an item changed)', async () => {
      seed([mario()]);
      await open();
      fireEvent.press(await screen.findByLabelText('Add 1 item'));
      await screen.findByText('Added 1 item');
      reviewMock.failNextUndo = 409;
      fireEvent.press(screen.getByLabelText('Undo'));
      await screen.findByText(/Couldn't undo/);
      expect(screen.queryByLabelText('Undo')).toBeNull(); // it will not work later either
      expect(scanMock.sessions.get(SESSION)!.status).toBe('committed');
      expect(itemsState.entries.some((e) => e.item.id === scanMock.sessions.get(SESSION)!.candidates[0]!.createdItemId)).toBe(true);
    });
  });

  it('sends Add to, status, condition and per-line overrides in the one commit', async () => {
    seed([mario(), candidate({ release: release(3, 'Zelda', { format: 'game_cartridge' }) })]);
    await open();
    await press('Status Wishlist');
    await press('2 items ready. Show them');
    await press('Details for Zelda');
    await press('Condition Good');
    await press('Status for Zelda: Owned');
    const button = screen.getByLabelText('Add 2 items');
    await act(async () => {
      fireEvent.press(button);
      fireEvent.press(button);
    });
    await screen.findByText('Added 2 items');
    expect(reviewMock.commits).toHaveLength(1);
    const body = reviewMock.commits[0]!.body!;
    expect(body.status).toBe('wishlist');
    expect(body.overrides).toEqual([{ candidateId: scanMock.sessions.get(SESSION)!.candidates[1]!.id, status: 'owned', condition: 'good' }]);
    const items = itemsState.entries.slice(-2).map((e) => [e.item.status, e.item.condition]);
    expect(items).toEqual([['wishlist', null], ['owned', 'good']]);
  });

  describe('candidate sheet', () => {
    it('sends the edited platform and edition, and the row shows them', async () => {
      seed([candidate({ release: release(4, 'Super Mario 64', { format: 'game_cartridge' }) })]);
      await open();
      await press('1 item ready. Show it');
      await press('Details for Super Mario 64');
      await press('Platform for Super Mario 64: Nintendo 64');
      await waitFor(() => expect(scanMock.sessions.get(SESSION)!.candidates[0]!.edits?.platform).toBe('Nintendo 64'));
      await act(async () => {
        fireEvent.changeText(screen.getByLabelText('Edition'), 'Player’s Choice');
      });
      await press('Save edition for Super Mario 64');
      await waitFor(() => expect(scanMock.sessions.get(SESSION)!.candidates[0]!.edits?.edition).toBe('Player’s Choice'));
      expect(scanMock.sessions.get(SESSION)!.candidates[0]!.userEdited).toBe(true);
      expect((await screen.findAllByText(/Nintendo 64 · 1990/)).length).toBeGreaterThan(0);
    });

    it('sends the edited format', async () => {
      seed([mario()]);
      await open();
      await press('1 item ready. Show it');
      await press('Details for Super Mario 64');
      await press('Format for Super Mario 64: Game disc');
      await waitFor(() => expect(scanMock.sessions.get(SESSION)!.candidates[0]!.edits?.format).toBe('game_disc'));
    });
  });

  it('says a quota wait will be looked up tomorrow, and a plain wait does not', async () => {
    seed([
      candidate({ resolution: 'pending', release: null, rawCode: '111', pendingReason: 'quota', retryAfter: '2026-10-17T00:00:00Z' }),
      candidate({ resolution: 'pending', release: null, rawCode: '222', pendingReason: 'lookup' }),
    ]);
    await open();
    await screen.findByText('Still looking up · 2');
    expect(screen.getAllByText(QUOTA_COPY)).toHaveLength(1);
  });

  it('counts the quota wait in one line', async () => {
    seed([
      candidate({ resolution: 'pending', release: null, rawCode: '111', pendingReason: 'quota' }),
      candidate({ resolution: 'pending', release: null, rawCode: '222', pendingReason: 'quota' }),
      candidate({ resolution: 'pending', release: null, rawCode: '333', pendingReason: 'lookup' }),
    ]);
    await open();
    await screen.findByText('quota paused — 2 pending');
  });

  it('shows no quota line when nothing waits for the allowance', async () => {
    seed([mario()]);
    await open();
    await screen.findByText('Ready · 1');
    expect(screen.queryByText(/quota paused/)).toBeNull();
  });

  it('offers Scan barcode on every T2 row, and tapping opens the camera bound to that row', async () => {
    const onScanBarcode = jest.fn();
    seed([
      pick(),
      lost(),
      candidate({ resolution: 'pending', release: null, rawCode: '111', pendingReason: 'quota' }),
      mario(),
      dune(),
    ]);
    await renderWithProviders(<ReviewScreen libraryId={MOCK_LIBRARY_ID} sessionId={SESSION} onScanBarcode={onScanBarcode} />);
    await screen.findByText('Pick one · 1');
    expect(screen.getAllByText('Scan barcode')).toHaveLength(3);

    await press('Scan barcode for Alien');
    expect(onScanBarcode).toHaveBeenCalledTimes(1);
    expect(onScanBarcode.mock.calls[0]?.[0].release.title).toBe('Alien');
  });

  it('offers no Scan barcode on rows that already name a release', async () => {
    seed([mario(), dune()]);
    await renderWithProviders(<ReviewScreen libraryId={MOCK_LIBRARY_ID} sessionId={SESSION} onScanBarcode={jest.fn()} />);
    await screen.findByText('Ready · 1');
    await press('1 item ready. Show it');
    await press('1 item you own. Show it');
    expect(screen.queryByText('Scan barcode')).toBeNull();
  });

  it('offers no Scan barcode to a viewer', async () => {
    asViewer();
    seed([pick()]);
    await renderWithProviders(<ReviewScreen libraryId={MOCK_LIBRARY_ID} sessionId={SESSION} onScanBarcode={jest.fn()} />);
    await screen.findByText('Pick one · 1');
    await waitFor(() => expect(screen.getByText(/You can look but not add/)).toBeTruthy());
    expect(screen.queryByText('Scan barcode')).toBeNull();
  });

  it('starts Add to and Status from the session context, and the commit carries them', async () => {
    seed([mario()]);
    scanMock.sessions.get(SESSION)!.context = { defaultLocationId: SHELF_1, defaultStatus: 'wishlist', mediaHint: 'games', channel: 'home' };
    await open();

    expect(await screen.findByLabelText('Add to: Game room › Shelf 1. Change')).toBeTruthy();
    expect(screen.getByLabelText('Status Wishlist').props.accessibilityState.selected).toBe(true);
    fireEvent.press(await screen.findByLabelText('Add 1 item'));

    await screen.findByText('Added 1 item');
    expect(reviewMock.commits).toHaveLength(1);
    expect(reviewMock.commits[0]!.body).toEqual({ addTo: SHELF_1, status: 'wishlist' });
  });

  it('an in-store session says the locations are decided later and starts at the last home spot', async () => {
    rememberSessionContext(MOCK_LIBRARY_ID, { locationId: SHELF_2, status: 'owned', mediaHint: 'mixed', channel: 'home' });
    seed([mario()]);
    scanMock.sessions.get(SESSION)!.context = { defaultLocationId: null, defaultStatus: null, mediaHint: 'mixed', channel: 'in_store' };
    await open();

    expect(await screen.findByText('Decide each location later. New items start at Game room › Shelf 2.')).toBeTruthy();
    expect(screen.getByLabelText('Add to: Game room › Shelf 2. Change')).toBeTruthy();
  });

  it('keeps a choice made before the session answers', async () => {
    seed([mario()]);
    scanMock.sessions.get(SESSION)!.context = { defaultLocationId: SHELF_1, defaultStatus: 'wishlist', mediaHint: 'mixed', channel: 'home' };
    await open();

    await press('Status Owned'); // tapped before the session context lands: the tap wins
    await screen.findByLabelText('Add to: Game room › Shelf 1. Change');

    expect(screen.getByLabelText('Status Owned').props.accessibilityState.selected).toBe(true);
  });

  it('names the specific next step on every T2 row', async () => {
    seed([
      candidate({ resolution: 'ambiguous', release: alien[0], alternatives: alien as never, guidanceCode: 'pick_match' }),
      candidate({ resolution: 'not_found', release: null, visibleText: 'Dune Mess', guidanceCode: 'move_closer' }),
    ]);
    await open();
    await screen.findByText('Pick one · 1');
    expect(screen.getByText('Pick the right one')).toBeTruthy();
    expect(screen.getByText('Move closer — spines too small')).toBeTruthy();
    expect(screen.queryByText('Not in the catalog. Try the front cover.')).toBeNull();
  });

  it('starts the search from what was read and carries the segment neighbors', async () => {
    const neighbor = release(5, 'Dune');
    seed([
      candidate({ id: 'row', resolution: 'not_found', release: null, visibleText: 'Dune Mess', segmentId: 'seg-1' }),
      candidate({ id: 'near', resolution: 'matched', release: neighbor, segmentId: 'seg-1' }),
    ]);
    await open();
    await screen.findByText('Not found · 1');
    expect(screen.getByLabelText('Title').props.value).toBe('Dune Mess');
    await press('Search');
    await waitFor(() => expect(reviewMock.searches).toHaveLength(1));
    expect(reviewMock.searches[0]).toMatchObject({ candidateId: 'row', text: 'dune mess', contextWorkIds: [neighbor.workId] });
  });

  it('offers Snap cover on every T2 row, and tapping opens the camera bound to that row', async () => {
    const onSnapCover = jest.fn();
    seed([
      pick(),
      lost(),
      candidate({ resolution: 'pending', release: null, rawCode: '111', pendingReason: 'quota' }),
      mario(),
      dune(),
    ]);
    await renderWithProviders(<ReviewScreen libraryId={MOCK_LIBRARY_ID} sessionId={SESSION} onSnapCover={onSnapCover} />);
    await screen.findByText('Pick one · 1');
    expect(screen.getAllByText('Snap cover')).toHaveLength(3);

    await press('Snap cover for Alien');
    expect(onSnapCover).toHaveBeenCalledTimes(1);
    expect(onSnapCover.mock.calls[0]?.[0].release.title).toBe('Alien');
  });

  it('offers no Snap cover on rows that already name a release', async () => {
    seed([mario(), dune()]);
    await renderWithProviders(<ReviewScreen libraryId={MOCK_LIBRARY_ID} sessionId={SESSION} onSnapCover={jest.fn()} />);
    await screen.findByText('Ready · 1');
    await press('1 item ready. Show it');
    await press('1 item you own. Show it');
    expect(screen.queryByText('Snap cover')).toBeNull();
  });
});
