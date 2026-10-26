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
import { estimateStripHeight } from './stripHeight';

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

  it('offers Verify edition on a T1 auto-added card, and nothing on a T0', async () => {
    const added = { ...dune, decision: 'accepted', createdItemId: 'item-1', tier: 'T1' } as unknown as CandidateDto;
    const onVerifyEdition = jest.fn();
    const { unmount } = await renderWithProviders(<ResultCards cards={[added]} actions={{ ...actions, onVerifyEdition }} />);

    await fireEvent.press(await screen.findByLabelText('Verify edition for Dune'));
    expect(onVerifyEdition).toHaveBeenCalledWith(added);
    await unmount();

    const barcodeProof = { ...added, tier: 'T0' } as unknown as CandidateDto;
    await renderWithProviders(<ResultCards cards={[barcodeProof]} actions={{ ...actions, onVerifyEdition }} />);
    expect(await screen.findByText('Dune')).toBeTruthy();
    expect(screen.queryByLabelText('Verify edition for Dune')).toBeNull();
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

const closeCall = {
  id: 'c-pick',
  seq: 1,
  source: 'picture_apple_model',
  rawCode: null,
  visibleText: 'Pokemon Yellow',
  resolution: 'ambiguous',
  alreadyOwnedCount: 0,
  ownedAt: [],
  decision: 'undecided',
  release: null,
  alternatives: [
    { id: 'r1', title: 'Pokemon Yellow', format: 'game_cartridge', year: 1999, platform: null, creators: [] },
    { id: 'r2', title: "Pokemon Yellow (Player's Choice)", format: 'game_cartridge', year: 2000, platform: null, creators: [] },
  ],
} as unknown as CandidateDto;

describe('close calls', () => {

  it('lists the alternatives and binds the tapped one', async () => {
    const onPick = jest.fn();
    await renderWithProviders(<ResultCards cards={[closeCall]} actions={{ ...actions, onPick }} />);
    expect(await screen.findByText('Pick one')).toBeTruthy();
    const target = screen.getByLabelText("Pick Pokemon Yellow (Player's Choice)");
    await act(async () => {
      fireEvent.press(target);
    });
    expect(onPick).toHaveBeenCalledWith(closeCall, (closeCall.alternatives as unknown[])[1]);
  });

  it('offers no pick without alternatives', async () => {
    await renderWithProviders(<ResultCards cards={[{ ...closeCall, alternatives: null }]} actions={{ ...actions, onPick: jest.fn() }} />);
    expect(await screen.findByText('Pick one')).toBeTruthy();
    expect(screen.queryByLabelText(/Pick Pokemon/)).toBeNull();
  });

  it('offers no pick without the action', async () => {
    await renderWithProviders(<ResultCards cards={[closeCall]} actions={actions} />);
    expect(await screen.findByText('Pick one')).toBeTruthy();
    expect(screen.queryByLabelText(/Pick Pokemon/)).toBeNull();
  });

  it('offers no pick read-only', async () => {
    await renderWithProviders(<ResultCards cards={[closeCall]} actions={{ ...actions, canAdd: false, onPick: jest.fn() }} />);
    expect(await screen.findByText('Pick one')).toBeTruthy();
    expect(screen.queryByLabelText(/Pick Pokemon/)).toBeNull();
  });
});

describe('strip height', () => {
  // A picture still identifying: header, one status line, nothing else.
  const identifying = {
    id: 'c-identifying',
    seq: 1,
    source: 'picture_text',
    rawCode: null,
    visibleText: 'HYRULE',
    resolution: 'pending',
    alreadyOwnedCount: 0,
    ownedAt: [],
    decision: 'undecided',
    release: null,
  } as unknown as CandidateDto;

  it('compacts the strip for a short card instead of a fixed height', () => {
    // 8 pad + 66 header + 8 + 18 status + 8 + 2 border. The old fixed 300 left
    // most of the strip empty over the camera (#373).
    expect(estimateStripHeight([identifying], actions, 1)).toBe(110);
  });

  it('fits an owned card with its answer and three actions', () => {
    // 8 pad + 66 header + 8 + 88 answer ceilings (3 label lines, 2 caption)
    // + 8 + 140 buttons + 8 + 2 border. The old fixed 300 cut the buttons off (#373).
    expect(estimateStripHeight([jawsDvd], triState(), 1)).toBe(328);
  });

  it('fits a close call with two picks, the longer button wrapping', () => {
    // 8 pad + 66 header + 8 + 18 status + 8 + 106 picks (44 + 54 + gap) + 8 + 2 border.
    expect(estimateStripHeight([closeCall], { ...actions, onPick: jest.fn() }, 1)).toBe(224);
  });

  it("counts a failing card's error lines", () => {
    const errored = failed('042100005264');
    const without = estimateStripHeight([errored], actions, 1);
    const message = 'No connection. Try again.';
    const withError = estimateStripHeight([errored], { ...actions, errors: { [errored.id]: message } }, 1);

    // Two caption lines for the message plus its gap.
    expect(withError - without).toBe(2 * 17 + 8);
  });

  it('grows with Dynamic Type and clamps past triple size', () => {
    const single = estimateStripHeight([jawsDvd], triState(), 1);
    const double = estimateStripHeight([jawsDvd], triState(), 2);

    expect(double).toBeGreaterThan(single);
    expect(double).toBe(656);
    expect(estimateStripHeight([jawsDvd], triState(), 9)).toBe(estimateStripHeight([jawsDvd], triState(), 3));
    expect(estimateStripHeight([jawsDvd], triState(), 0.5)).toBe(single);
  });

  it('renders no strip without cards', async () => {
    await renderWithProviders(<ResultCards cards={[]} actions={actions} />);

    expect(screen.queryByTestId('result-cards')).toBeNull();
  });

  it('sizes the rendered strip to its tallest card', async () => {
    await renderWithProviders(<ResultCards cards={[identifying, jawsDvd]} actions={triState()} />);

    // Jest's ambient fontScale is 2, so the strip renders the doubled estimate (656).
    const strip = await screen.findByTestId('result-cards');
    expect(strip.props.style.height).toBe(estimateStripHeight([identifying, jawsDvd], triState(), 2));
  });

  it('renders a compact strip for a short card', async () => {
    await renderWithProviders(<ResultCards cards={[identifying]} actions={actions} />);

    // 164 at the ambient doubled scale, against the old fixed 600.
    const strip = await screen.findByTestId('result-cards');
    expect(strip.props.style.height).toBe(estimateStripHeight([identifying], actions, 2));
  });
});
