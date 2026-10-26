import { onlineManager } from '@tanstack/react-query';
import { act, fireEvent, screen } from '@testing-library/react-native';
import { MemoryOwnedStorage, setOwnedStorage } from '@/features/offline/ownedCache';
import { syncLibrary } from '@/features/offline/sync';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { deleteOwned, resetOwnershipMock, upsertOwned } from '@/mocks/ownership';
import { useLibraryStore } from '@/state/library';
import { AccessibilityInfo, Animated } from 'react-native';
import type { CandidateDto } from '@/api/generated/models';
import { renderWithProviders } from '@/test/render';
import { ResultCard, ResultCards, type CardActions } from './ResultCards';

const noop = () => undefined;
const actions: CardActions = { canAdd: true, onOpen: noop, onAdd: noop, onWishlist: noop };

const dune = {
  id: 'c1',
  seq: 1,
  rawCode: '9780441172719',
  resolution: 'matched',
  alreadyOwnedCount: 1,
  ownedAt: ['Living room › Shelf 2'],
  decision: 'undecided',
  release: { id: 'r1', title: 'Dune', format: 'paperback', year: 1990, platform: null, creators: [], coverUrl: null },
} as unknown as CandidateDto;

// The mock library owns Jaws on VHS (release 101) and on DVD (release 102); nothing on Blu-ray.
const dvdReleaseId = '00000102-0000-4000-8000-000000000000';
const blurayReleaseId = '00000109-0000-4000-8000-000000000000';
const jawsWorkId = '00000201-0000-4000-8000-000000000000';

const jawsDvd = {
  id: 'c-owned',
  seq: 1,
  rawCode: '5050582000000',
  resolution: 'matched',
  alreadyOwnedCount: 1,
  ownedAt: ['Living room › Shelf A'],
  decision: 'undecided',
  release: { id: dvdReleaseId, title: 'Jaws', format: 'dvd', year: 1975, platform: null, creators: [], coverUrl: null },
} as unknown as CandidateDto;

const triState = (): CardActions => ({
  ...actions,
  onSkip: jest.fn(),
  onMove: jest.fn(),
  moveTo: { locationId: 'a0000000-0000-4000-8000-000000000002', label: 'Game room › Shelf 1' },
});

afterEach(() => jest.restoreAllMocks());

const failed = (code: string) =>
  ({ id: `x-${code}`, seq: 1, rawCode: code, resolution: 'error', alreadyOwnedCount: 0, ownedAt: [], decision: 'undecided', release: null }) as unknown as CandidateDto;

describe('result cards when the server cannot answer', () => {
  beforeEach(async () => {
    resetOwnershipMock();
    setOwnedStorage(new MemoryOwnedStorage());
    useLibraryStore.setState({ activeLibraryId: MOCK_LIBRARY_ID });
    onlineManager.setOnline(true);
    await syncLibrary(MOCK_LIBRARY_ID, () => 0);
    onlineManager.setOnline(false);
  });
  afterEach(() => {
    onlineManager.setOnline(true);
    useLibraryStore.setState({ activeLibraryId: null });
  });
  afterAll(() => setOwnedStorage(undefined));

  it('says what you own and where, from the local copy, with how old it is', async () => {
    await renderWithProviders(<ResultCards cards={[failed('042100005264')]} actions={actions} />);
    expect(await screen.findByText('Already own — VHS, Living room › Shelf B')).toBeTruthy();
    expect(screen.getByText(/^Based on your last sync, /)).toBeTruthy();
  });

  it('says "Not in your library" for a code the copy does not hold', async () => {
    await renderWithProviders(<ResultCards cards={[failed('4006381333931')]} actions={actions} />);
    expect(await screen.findByText('Not in your library')).toBeTruthy();
    expect(screen.getByText(/^Based on your last sync, /)).toBeTruthy();
  });

  it('leaves a card the server resolved exactly as it was', async () => {
    await renderWithProviders(<ResultCards cards={[dune]} actions={actions} />);
    expect(await screen.findByText('Already own — Paperback, Living room › Shelf 2')).toBeTruthy();
    expect(screen.queryByText(/Based on your last sync/)).toBeNull();
  });

  it('answers an owned card offline from the local copy', async () => {
    await renderWithProviders(<ResultCards cards={[jawsDvd]} actions={triState()} />);
    expect(await screen.findByText('Already own — DVD, Living room › Shelf A')).toBeTruthy();
    expect(screen.getByText(/^Based on your last sync, /)).toBeTruthy();
  });
});

describe('result cards', () => {
  it('slides a new card in over a short time', async () => {
    const timing = jest.spyOn(Animated, 'timing');
    await renderWithProviders(<ResultCard candidate={dune} actions={actions} reduceMotion={false} />);
    expect(timing).toHaveBeenCalledWith(expect.anything(), expect.objectContaining({ duration: 220 }));
  });

  it('does not slide with Reduce Motion on', async () => {
    const timing = jest.spyOn(Animated, 'timing');
    await renderWithProviders(<ResultCard candidate={dune} actions={actions} reduceMotion />);
    expect(timing).toHaveBeenCalledWith(expect.anything(), expect.objectContaining({ duration: 0 }));
  });

  it('reads the Reduce Motion setting from the system', async () => {
    jest.spyOn(AccessibilityInfo, 'isReduceMotionEnabled').mockResolvedValue(true);
    const timing = jest.spyOn(Animated, 'timing');
    await renderWithProviders(<ResultCards cards={[dune]} actions={actions} />);
    await screen.findByText('Dune');
    await new Promise((r) => setTimeout(r, 20));
    expect(timing).toHaveBeenLastCalledWith(expect.anything(), expect.objectContaining({ duration: 0 }));
  });

  it('gives the card and its parts accessibility labels', async () => {
    await renderWithProviders(<ResultCards cards={[dune]} actions={actions} />);
    expect(await screen.findByLabelText('Open Dune, Paperback · 1990, Already own — Paperback, Living room › Shelf 2')).toBeTruthy();
  });
});

describe('owned cards', () => {
  beforeEach(async () => {
    resetOwnershipMock();
    setOwnedStorage(new MemoryOwnedStorage());
    useLibraryStore.setState({ activeLibraryId: MOCK_LIBRARY_ID });
    onlineManager.setOnline(true);
    await syncLibrary(MOCK_LIBRARY_ID, () => 0);
  });
  afterEach(() => {
    useLibraryStore.setState({ activeLibraryId: null });
  });
  afterAll(() => setOwnedStorage(undefined));

  it('offers Add another copy, Move it here and Skip on an exact-owned hit', async () => {
    await renderWithProviders(<ResultCards cards={[jawsDvd]} actions={triState()} />);
    expect(await screen.findByText('Already own — DVD, Living room › Shelf A')).toBeTruthy();
    expect(screen.getByLabelText('Add another copy of Jaws')).toBeTruthy();
    expect(screen.getByLabelText('Move Jaws to Game room › Shelf 1')).toBeTruthy();
    expect(screen.getByLabelText('Skip Jaws')).toBeTruthy();
  });

  it('hides Move without a target, as in-store has none', async () => {
    const inStore: CardActions = { ...triState(), moveTo: null };
    await renderWithProviders(<ResultCards cards={[jawsDvd]} actions={inStore} />);
    expect(await screen.findByLabelText('Add another copy of Jaws')).toBeTruthy();
    expect(screen.getByLabelText('Skip Jaws')).toBeTruthy();
    expect(screen.queryByLabelText(/Move Jaws/)).toBeNull();
  });

  it('calls through to each action', async () => {
    const stubbed = triState();
    const onAdd = jest.fn();
    await renderWithProviders(<ResultCards cards={[jawsDvd]} actions={{ ...stubbed, onAdd }} />);
    await screen.findByText('Already own — DVD, Living room › Shelf A');
    // One tap, flushed before the next: bare presses overlap the ownership answer landing and corrupt later tests.
    for (const label of ['Add another copy of Jaws', 'Move Jaws to Game room › Shelf 1', 'Skip Jaws']) {
      const target = screen.getByLabelText(label);
      await act(async () => {
        fireEvent.press(target);
      });
    }
    expect(onAdd).toHaveBeenCalledWith(jawsDvd);
    expect(stubbed.onMove).toHaveBeenCalledWith(jawsDvd);
    expect(stubbed.onSkip).toHaveBeenCalledWith(jawsDvd);
  });

  it('says the copy was added instead of offering actions twice', async () => {
    const accepted = { ...jawsDvd, decision: 'accepted' } as CandidateDto;
    await renderWithProviders(<ResultCards cards={[accepted]} actions={triState()} />);
    expect(await screen.findByText('Added to this scan')).toBeTruthy();
    expect(screen.queryByLabelText('Add another copy of Jaws')).toBeNull();
  });
});

describe('soft matches', () => {
  beforeEach(async () => {
    resetOwnershipMock();
    setOwnedStorage(new MemoryOwnedStorage());
    useLibraryStore.setState({ activeLibraryId: MOCK_LIBRARY_ID });
    onlineManager.setOnline(true);
    await syncLibrary(MOCK_LIBRARY_ID, () => 0);
    // The catalog names a Jaws Blu-ray the library does not own: the VHS and DVD copies are soft matches for it.
    const ghostId = '00000009-0000-4000-8000-000000000000';
    upsertOwned({
      itemId: ghostId,
      deleted: false,
      releaseId: blurayReleaseId,
      workId: jawsWorkId,
      title: 'Jaws',
      format: 'bluray',
      platform: null,
      identifiers: [],
      locationPath: null,
    });
    deleteOwned(ghostId);
  });
  afterEach(() => {
    useLibraryStore.setState({ activeLibraryId: null });
  });
  afterAll(() => setOwnedStorage(undefined));

  it('renders a soft match informationally, with Add still the default', async () => {
    const bluray = {
      id: 'c-new',
      seq: 1,
      rawCode: '5050582099999',
      resolution: 'matched',
      alreadyOwnedCount: 0,
      ownedAt: [],
      decision: 'undecided',
      release: { id: blurayReleaseId, title: 'Jaws', format: 'bluray', year: 1975, platform: null, creators: [], coverUrl: null },
    } as unknown as CandidateDto;
    await renderWithProviders(<ResultCards cards={[bluray]} actions={actions} />);
    expect(await screen.findByText('Also own — VHS, Living room › Shelf B')).toBeTruthy();
    expect(screen.getByLabelText('Add Jaws')).toBeTruthy();
  });

  it('answers a pending card from the server while the catalog lookup runs', async () => {
    // The DVD copy is gone, but its barcode still names the Jaws work: the VHS copy is a soft match for it.
    deleteOwned('00000002-0000-4000-8000-000000000000');
    await renderWithProviders(<ResultCards cards={[failed('5050582000000')]} actions={actions} />);
    expect(await screen.findByText('Also own — VHS, Living room › Shelf B')).toBeTruthy();
    expect(screen.queryByText(/Based on your last sync/)).toBeNull();
  });
});
