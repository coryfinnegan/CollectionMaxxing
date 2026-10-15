import { onlineManager } from '@tanstack/react-query';
import { screen } from '@testing-library/react-native';
import { MemoryOwnedStorage, setOwnedStorage } from '@/features/offline/ownedCache';
import { syncLibrary } from '@/features/offline/sync';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { resetOwnershipMock } from '@/mocks/ownership';
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
    expect(await screen.findByText('You own 1: Living room › Shelf B')).toBeTruthy();
    expect(screen.getByText(/^Based on your last sync, /)).toBeTruthy();
  });

  it('says "Not in your library" for a code the copy does not hold', async () => {
    await renderWithProviders(<ResultCards cards={[failed('4006381333931')]} actions={actions} />);
    expect(await screen.findByText('Not in your library')).toBeTruthy();
    expect(screen.getByText(/^Based on your last sync, /)).toBeTruthy();
  });

  it('leaves a card the server resolved exactly as it was', async () => {
    await renderWithProviders(<ResultCards cards={[dune]} actions={actions} />);
    expect(await screen.findByText('You own 1')).toBeTruthy();
    expect(screen.queryByText(/Based on your last sync/)).toBeNull();
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
    expect(await screen.findByLabelText('Open Dune, Paperback · 1990, You own 1')).toBeTruthy();
  });
});
