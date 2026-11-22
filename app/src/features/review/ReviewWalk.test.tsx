// Review one at a time (#833) on a phone-width screen, end to end against the mock API: the cards that need you, lit on your own
// shelf photo with a large cover; Add this, Not this one, Fix it and Undo; the move to the next card; the end with its one Add.
import { act, fireEvent, screen, waitFor } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import { AccessibilityInfo, StyleSheet, useWindowDimensions } from 'react-native';
import type { CandidateDto } from '@/api/generated/models';
import { useCardsStore } from '@/features/scan/cards/cardsStore';
import { createPipelineStore, getPipelineStore, setPipelineStore } from '@/features/scan/pipeline/pipelineStore';
import { MemorySnapFiles, SnapStore, setSnapStore, type StoredSnap } from '@/features/scan/pipeline/snapStore';
import { createDiscardOutbox, setDiscardOutbox } from '@/features/scan/session/discards';
import { createSessionCountersStore, setSessionCountersStore } from '@/features/scan/session/sessionCounters';
import { setSessionContextStorage } from '@/features/scan/session/sessionContext';
import { setTrayStore } from '@/features/scan/tray/storage';
import { createTrayStore, MemoryKeyValueStore } from '@/features/scan/tray/trayStore';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { resetItems } from '@/mocks/items';
import { scanMock } from '@/mocks/scan';
import { reviewMock } from '@/mocks/scan-review';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { createProvisionalOutbox, setProvisionalOutbox } from './provisionals';
import { ReviewScreen } from './ReviewScreen';
import { candidate, release } from './testData';
import { createWalkStore, setWalkStore } from './walkStore';

jest.mock('react-native/Libraries/Utilities/useWindowDimensions', () => ({
  __esModule: true,
  default: jest.fn(() => ({ width: 390, height: 844, scale: 3, fontScale: 1 })),
}));
const windowSize = useWindowDimensions as unknown as jest.Mock;

const SESSION = 'a0000000-0000-4000-8000-000000000001';
const STILL = { width: 2576, height: 1932 };
const unconfirmed = { unconfirmed: true } as never;

const seed = (list: CandidateDto[]) => {
  scanMock.sessions.set(SESSION, { id: SESSION, libraryId: MOCK_LIBRARY_ID, seq: 100, candidates: list, answers: new Map(), status: 'open', provisionals: [] });
};
const onServer = (id: string) => scanMock.sessions.get(SESSION)!.candidates.find((c) => c.id === id)!;

const confident = (title = 'Dune', n = 1) => candidate({ release: release(n, title, { format: 'paperback' }) });
const serverRead = (title: string, n: number, extra: Partial<CandidateDto> = {}) =>
  candidate({ source: 'picture_text', release: release(n, title, { format: 'paperback', creators: ['A. Writer'] as never }), inferred: unconfirmed, ...extra });
const alien = [release(11, 'Alien', { year: 1979, coverUrl: 'https://art.test/alien.jpg' }), release(12, 'Aliens', { year: 1986, coverUrl: 'https://art.test/aliens.jpg' })];
const pick = (extra: Partial<CandidateDto> = {}) => candidate({ resolution: 'ambiguous', release: alien[0], alternatives: alien as never, ...extra });
const lost = (extra: Partial<CandidateDto> = {}) => candidate({ resolution: 'not_found', source: 'picture_text', rawCode: null, visibleText: 'Dune Mess', ...extra });
const owned = () => candidate({ release: release(9, 'Heat', { format: 'dvd' }), alreadyOwnedCount: 1, ownedAt: ['Shelf 2'] });

/** The photo the rows came from: the pipeline's receipt names each row; the snap store has the still and the boxes. */
function seedPhoto(items: { row: CandidateDto; box?: { x: number; y: number; width: number; height: number } }[], { still = true, snapId = 'snap-1', createdAt = 1 } = {}) {
  const pipeline = getPipelineStore();
  pipeline.getState().addSnap({
    snapId,
    libraryId: MOCK_LIBRARY_ID,
    trigger: 'shutter',
    createdAt,
    thumbUri: 'file:///thumb.jpg',
    phase: 'read',
    sent: true,
    sessionId: SESSION,
    items: items.map(({ row }, i) => ({ localId: `${snapId}-${i}`, text: 't', title: 't', payload: { localId: `${snapId}-${i}` } as never, candidateId: row.id })),
    unreadableCount: 0,
  });
  const storage = new MemoryKeyValueStore();
  const snap: StoredSnap = {
    snapId,
    createdAt,
    stillUri: `file:///${snapId}-still.jpg`,
    ...STILL,
    items: items.map(({ box }, i) => ({ localId: `${snapId}-${i}`, text: 't', ...(box ? { box } : {}) })),
  };
  storage.set('snap-store-v1', JSON.stringify(still ? [snap] : []));
  setSnapStore(new SnapStore({ files: new MemorySnapFiles(), storage }));
}
const spine = (x: number) => ({ x, y: 0.1, width: 0.05, height: 0.8 });

const open = async () => {
  const onOpenResult = jest.fn();
  await renderWithProviders(<ReviewScreen libraryId={MOCK_LIBRARY_ID} sessionId={SESSION} onOpenResult={onOpenResult} />);
  return onOpenResult;
};
const press = async (label: string | RegExp) => {
  const target = await screen.findByLabelText(label);
  await act(async () => {
    fireEvent.press(target);
  });
};
/** The match on screen, as VoiceOver reads it. */
const match = () => screen.findByTestId('match-reel');
/** Waits for the card to finish coming in: the old one slides away first. */
const onCard = (title: RegExp) => waitFor(() => expect(screen.getByTestId('match-reel').props.accessibilityLabel).toMatch(title));

beforeEach(() => {
  scanMock.reset();
  reviewMock.reset();
  resetItems();
  useCardsStore.getState().reset();
  windowSize.mockReturnValue({ width: 390, height: 844, scale: 3, fontScale: 1 });
  setSessionContextStorage(new MemoryKeyValueStore());
  setProvisionalOutbox(createProvisionalOutbox({ storage: new MemoryKeyValueStore() }));
  setDiscardOutbox(createDiscardOutbox({ storage: new MemoryKeyValueStore() }));
  setTrayStore(createTrayStore({ storage: new MemoryKeyValueStore() }));
  setPipelineStore(createPipelineStore({ storage: new MemoryKeyValueStore() }));
  setSessionCountersStore(createSessionCountersStore({ storage: new MemoryKeyValueStore() }));
  setSnapStore(new SnapStore({ files: new MemorySnapFiles(), storage: new MemoryKeyValueStore() }));
  setWalkStore(createWalkStore({ storage: new MemoryKeyValueStore() }));
});

afterEach(() => {
  jest.restoreAllMocks();
  setSessionContextStorage(undefined);
  setProvisionalOutbox(undefined);
  setDiscardOutbox(undefined);
  setTrayStore(undefined);
  setPipelineStore(undefined);
  setSessionCountersStore(undefined);
  setSnapStore(undefined);
  setWalkStore(undefined);
});

describe('Review one at a time', () => {
  it('walks only the rows that need you, then the end', async () => {
    const a = serverRead('Emma', 2);
    const b = pick();
    seed([confident('Dune', 1), confident('Heat', 3), a, b]);
    await open();
    expect((await match()).props.accessibilityLabel).toBe('Emma, A. Writer, 1990, Paperback');
    expect(screen.getByText('1 of 2')).toBeTruthy();
    await press('Add this: Emma');
    expect(await screen.findByText('2 of 2')).toBeTruthy();
    await press('Add this: Alien');
    expect(await screen.findByText('2 matched by themselves.')).toBeTruthy();
    expect(screen.getByText('2 you picked.')).toBeTruthy();
    expect(screen.getByLabelText('Add 4 items')).toBeTruthy();
  });

  it('opens on the end screen when everything matched', async () => {
    seed([confident('Dune', 1), confident('Heat', 3)]);
    await open();
    expect(await screen.findByText('Nothing needed you this time.')).toBeTruthy();
    expect(screen.getByText('2 matched by themselves.')).toBeTruthy();
    expect(screen.queryByTestId('match-reel')).toBeNull();
    expect(screen.getByLabelText('Add 2 items')).toBeTruthy();
    expect(screen.getByLabelText('See all 2')).toBeTruthy();
  });

  it('walks the photo left to right: a row further left comes first', async () => {
    const right = serverRead('Right', 2);
    const left = serverRead('Left', 3);
    seed([right, left]);
    seedPhoto([{ row: right, box: spine(0.7) }, { row: left, box: spine(0.2) }]);
    await open();
    expect((await match()).props.accessibilityLabel).toMatch(/^Left/);
    expect(screen.getByLabelText('Your photo, item highlighted, 1st from left')).toBeTruthy();
  });
});

describe('the photo', () => {
  it("lights the row's box and dims the rest", async () => {
    const row = serverRead('Emma', 2);
    seed([row]);
    seedPhoto([{ row, box: spine(0.4) }]);
    await open();
    expect(await screen.findByTestId('walk-band')).toBeTruthy();
    expect(screen.getByTestId('walk-box')).toBeTruthy();
    expect(screen.queryByText("Found in this photo. The server didn't mark where.")).toBeNull();
  });

  it('shows the whole photo with nothing lit when the place is not known, and says so', async () => {
    const row = serverRead('Emma', 2);
    seed([row]);
    seedPhoto([{ row }]);
    await open();
    expect(await screen.findByTestId('walk-band')).toBeTruthy();
    expect(screen.queryByTestId('walk-box')).toBeNull();
    expect(screen.getByText("Found in this photo. The server didn't mark where.")).toBeTruthy();
  });

  it('shows no band when the still is gone, or when no photo is known', async () => {
    const row = serverRead('Emma', 2);
    seed([row]);
    seedPhoto([{ row, box: spine(0.4) }], { still: false });
    await open();
    await match();
    expect(screen.queryByTestId('walk-band')).toBeNull();
    expect(screen.getByText("The photo isn't on this phone any more. The details below are still right.")).toBeTruthy();
  });

  it('draws a tall spine upright and does not turn a flat stack', async () => {
    const tall = serverRead('Tall', 2);
    const flat = serverRead('Flat', 3);
    seed([tall, flat]);
    seedPhoto([{ row: tall, box: spine(0.1) }, { row: flat, box: { x: 0.5, y: 0.6, width: 0.4, height: 0.08 } }]);
    await open();
    expect(await screen.findByTestId('spine-strip-turned')).toBeTruthy();
    expect(screen.getByLabelText('Your photo of this spine')).toBeTruthy();
    await press('Add this: Tall');
    expect(await screen.findByTestId('spine-strip-flat')).toBeTruthy();
  });

  it('opens the whole photo from the band', async () => {
    const row = serverRead('Emma', 2);
    seed([row]);
    seedPhoto([{ row, box: spine(0.4) }]);
    const onOpenResult = await open();
    await press(/^Your photo, item highlighted/);
    expect(onOpenResult).toHaveBeenCalledWith(expect.objectContaining({ id: row.id }));
  });
});

describe('the match', () => {
  it('draws the cover large', async () => {
    seed([pick()]);
    await open();
    const cover = await screen.findByLabelText('Cover of Alien');
    expect(StyleSheet.flatten(cover.props.style)).toEqual(expect.objectContaining({ width: 160, height: 240 }));
  });

  it('puts the spine strip in place of a missing cover, never an empty frame', async () => {
    const row = serverRead('Emma', 2);
    seed([row]);
    seedPhoto([{ row, box: spine(0.4) }]);
    await open();
    await match();
    expect(screen.queryByTestId('match-cover')).toBeNull();
    expect(screen.queryByTestId('match-own-picture')).toBeNull();
    expect(screen.getByTestId('spine-strip')).toBeTruthy();
  });

  it("fills a cover shot's missing art with your own photo, captioned", async () => {
    const row = serverRead('Emma', 2);
    seed([row]);
    seedPhoto([{ row, box: { x: 0.3, y: 0.2, width: 0.3, height: 0.5 } }]);
    await open();
    expect(await screen.findByTestId('match-own-picture')).toBeTruthy();
    expect(screen.getByText('Your photo')).toBeTruthy();
  });

  it('flips between matches with a swipe and stages the pick; it never adds or skips', async () => {
    const row = pick();
    seed([row, confident()]);
    await open();
    const reel = await match();
    expect(reel.props.accessibilityLabel).toMatch(/^Match 1 of 2: Alien/);
    await act(async () => {
      fireEvent(reel, 'layout', { nativeEvent: { layout: { width: 300, height: 260, x: 0, y: 0 } } });
    });
    await act(async () => {
      fireEvent.scroll(screen.getByTestId('match-pages'), { nativeEvent: { contentOffset: { x: 300, y: 0 }, layoutMeasurement: { width: 300, height: 260 }, contentSize: { width: 600, height: 260 } } });
    });
    expect((await match()).props.accessibilityLabel).toMatch(/^Match 2 of 2: Aliens/);
    expect(onServer(row.id).decision).toBe('undecided');
    expect(onServer(row.id).resolution).toBe('ambiguous');
    // Staged, not added: the same card is still on screen and Add this names the swiped-to match.
    expect(screen.getByLabelText('Add this: Aliens')).toBeTruthy();
    await press('Add this: Aliens');
    await waitFor(() => expect(onServer(row.id).release?.title).toBe('Aliens'));
  });

  it('turns the match for VoiceOver: increment shows the next one', async () => {
    seed([pick()]);
    await open();
    const reel = await match();
    expect(reel.props.accessibilityRole).toBe('adjustable');
    await act(async () => {
      fireEvent(reel, 'accessibilityAction', { nativeEvent: { actionName: 'increment' } });
    });
    expect((await match()).props.accessibilityLabel).toMatch(/^Match 2 of 2: Aliens/);
  });
});

describe('the answers', () => {
  it('accepts on the server at once with Add this, and counts it in Add N', async () => {
    const row = serverRead('Emma', 2);
    seed([row, confident()]);
    await open();
    await press('Add this: Emma');
    await waitFor(() => expect(onServer(row.id).decision).toBe('accepted'));
    expect(await screen.findByLabelText('Add 2 items')).toBeTruthy();
  });

  it('keeps an offline Add this in the draft, and Add still counts it', async () => {
    const row = serverRead('Emma', 2);
    seed([row, confident()]);
    await open();
    await match();
    server.use(http.patch('*/api/v1/scan-sessions/:id/candidates/:cid', () => HttpResponse.error()));
    await press('Add this: Emma');
    expect(await screen.findByLabelText('Add 2 items')).toBeTruthy();
    expect(onServer(row.id).decision).toBe('undecided');
  });

  it('skips with Not this one, and the row can be put back', async () => {
    const row = serverRead('Emma', 2);
    seed([row, confident()]);
    await open();
    await press('Not this one: Emma');
    await waitFor(() => expect(onServer(row.id).decision).toBe('rejected'));
    expect(await screen.findByText('Skipped · 1')).toBeTruthy();
    await press('1 item skipped. Show it');
    expect(screen.getByText('You skipped it')).toBeTruthy();
    expect(screen.getByLabelText('Put back Emma')).toBeTruthy();
  });

  it('opens the search and the extras with Fix it', async () => {
    const row = serverRead('Emma', 2, { visibleText: 'Emma' });
    seed([row]);
    await open();
    await press('Fix Emma');
    expect(await screen.findByRole('header', { name: 'Fix it' })).toBeTruthy();
    expect(screen.getByDisplayValue('Emma')).toBeTruthy();
    expect(screen.getByLabelText('Discard Emma')).toBeTruthy();
    expect(screen.getByText('Copies')).toBeTruthy();
  });

  it('makes Search the first answer of a Not found card', async () => {
    seed([lost()]);
    await open();
    expect(await screen.findByText('Nothing in the catalog matched')).toBeTruthy();
    expect(screen.getByText('Read as “Dune Mess”')).toBeTruthy();
    await press('Search for Unknown item');
    expect(await screen.findByDisplayValue('Dune Mess')).toBeTruthy();
  });

  it('brings back the last card and its answer with Undo', async () => {
    const a = serverRead('Emma', 2);
    const b = serverRead('Persuasion', 3);
    seed([a, b]);
    await open();
    await press('Add this: Emma');
    expect(await screen.findByText('2 of 2')).toBeTruthy();
    await waitFor(() => expect(onServer(a.id).decision).toBe('accepted'));
    await waitFor(() => expect(screen.getByTestId('match-reel').props.accessibilityLabel).toMatch(/^Persuasion/));
    await press('Undo the last answer');
    expect(await screen.findByText('1 of 2')).toBeTruthy();
    await waitFor(() => expect(screen.getByTestId('match-reel').props.accessibilityLabel).toMatch(/^Emma/));
    await waitFor(() => expect(onServer(a.id).decision).toBe('undecided'));
  });

  it('survives a restart at the same card, with its offline answer still counted', async () => {
    const a = serverRead('Emma', 2);
    const b = serverRead('Persuasion', 3);
    seed([a, b]);
    // What a killed app left on the phone: Emma answered offline, Persuasion on screen.
    const storage = new MemoryKeyValueStore();
    createWalkStore({ storage }).getState().update(SESSION, () => ({ order: [a.id, b.id], stack: [{ id: a.id, kind: 'accepted', prevChecked: false }], place: b.id, ended: false }));
    setWalkStore(createWalkStore({ storage }));
    await open();
    expect(await screen.findByText('2 of 2')).toBeTruthy();
    expect((await match()).props.accessibilityLabel).toMatch(/^Persuasion/);
    await press('Not this one: Persuasion');
    expect(await screen.findByLabelText('Add 1 item')).toBeTruthy();
  });
});

describe('late rows and server skips', () => {
  it('adds a late row to the end of the walk, never before the card on screen', async () => {
    const a = serverRead('Emma', 2);
    seed([a, serverRead('Persuasion', 3)]);
    await open();
    expect(await screen.findByText('1 of 2')).toBeTruthy();
    await act(async () => {
      useCardsStore.getState().merge([serverRead('Aaa first by seq', 4, { seq: 0 })], { announce: false });
    });
    expect(await screen.findByText('1 of 3')).toBeTruthy();
    expect((await match()).props.accessibilityLabel).toMatch(/^Emma/);
  });

  it('drops an unseen row the server skips, and keeps the card on screen with a line', async () => {
    const a = serverRead('Emma', 2);
    const b = serverRead('Persuasion', 3);
    const c = serverRead('Sanditon', 4);
    seed([a, b, c]);
    await open();
    expect(await screen.findByText('1 of 3')).toBeTruthy();
    await act(async () => {
      useCardsStore.getState().skip([{ ...b, decision: 'rejected', skipReason: 'server_did_not_see', changeSeq: 500 }]);
    });
    expect(await screen.findByText('1 of 2')).toBeTruthy();
    await act(async () => {
      useCardsStore.getState().skip([{ ...a, decision: 'rejected', skipReason: 'server_did_not_see', changeSeq: 501 }]);
    });
    expect(await screen.findByText('The server read this shelf differently')).toBeTruthy();
    expect((await match()).props.accessibilityLabel).toMatch(/^Emma/);
    // Add this overrides the skip.
    await press('Add this: Emma');
    await waitFor(() => expect(onServer(a.id).decision).toBe('accepted'));
    await onCard(/^Sanditon/);
  });
});

describe('the end', () => {
  it('walks the confident rows with Check them', async () => {
    const a = confident('Dune', 1);
    const b = confident('Heat', 3);
    seed([a, b]);
    await open();
    await press('Check the 2 matched by themselves');
    expect(await screen.findByText('1 of 2')).toBeTruthy();
    await press('Not this one: Dune');
    expect(await screen.findByText('2 of 2')).toBeTruthy();
    await onCard(/^Heat/);
    await press('Add this: Heat');
    expect(await screen.findByText('1 matched by itself.')).toBeTruthy();
    expect(screen.getByLabelText('Add 1 item')).toBeTruthy();
  });

  it('never adds the rows you own unless asked', async () => {
    seed([owned(), confident()]);
    await open();
    expect(await screen.findByText('1 you own. Not added.')).toBeTruthy();
    expect(screen.getByLabelText('Add 1 item')).toBeTruthy();
    await press('Check the 1 you own');
    await press('Add another copy of Heat');
    expect(await screen.findByLabelText('Add 2 items')).toBeTruthy();
  });

  it('lets you stop checking at any time', async () => {
    seed([confident('Dune', 1), confident('Heat', 3)]);
    await open();
    await press('Check the 2 matched by themselves');
    await press('Stop checking');
    expect(await screen.findByText('2 matched by themselves.')).toBeTruthy();
  });

  it('opens the whole list with See all', async () => {
    seed([confident('Dune', 1), serverRead('Emma', 2)]);
    await open();
    await match();
    await press('Not this one: Emma');
    await press('See all 1');
    expect(await screen.findByText('Ready · 1')).toBeTruthy();
    await press('Back to one at a time');
    expect(await screen.findByText('1 matched by itself.')).toBeTruthy();
  });
});

describe('motion and access', () => {
  it('swaps cards without sliding under Reduce Motion', async () => {
    jest.spyOn(AccessibilityInfo, 'isReduceMotionEnabled').mockResolvedValue(true);
    seed([serverRead('Emma', 2), serverRead('Persuasion', 3)]);
    await open();
    await match();
    await act(async () => {
      fireEvent.press(screen.getByLabelText('Add this: Emma'));
    });
    // No slide: the next card is there in the same frame.
    expect(screen.getByTestId('match-reel').props.accessibilityLabel).toMatch(/^Persuasion/);
  });

  it('slides the old card away before the next one comes in', async () => {
    jest.spyOn(AccessibilityInfo, 'isReduceMotionEnabled').mockResolvedValue(false);
    seed([serverRead('Emma', 2), serverRead('Persuasion', 3)]);
    await open();
    await match();
    await act(async () => {
      fireEvent.press(screen.getByLabelText('Add this: Emma'));
    });
    expect(screen.getByTestId('match-reel').props.accessibilityLabel).toMatch(/^Emma/);
    await waitFor(() => expect(screen.getByTestId('match-reel').props.accessibilityLabel).toMatch(/^Persuasion/));
  });

  it('lets VoiceOver hear the progress and the match, and moves focus to the match on each card', async () => {
    const focus = jest.spyOn(AccessibilityInfo, 'sendAccessibilityEvent');
    seed([serverRead('Emma', 2), serverRead('Persuasion', 3)]);
    await open();
    await match();
    expect(screen.getByText('1 of 2').props.accessibilityLiveRegion).toBe('polite');
    const before = focus.mock.calls.length;
    expect(before).toBeGreaterThan(0);
    await press('Add this: Emma');
    await waitFor(() => expect(screen.getByTestId('match-reel').props.accessibilityLabel).toMatch(/^Persuasion/));
    await waitFor(() => expect(focus.mock.calls.length).toBeGreaterThan(before));
    expect(focus.mock.calls.at(-1)?.[1]).toBe('focus');
  });

  it('keeps the list on a wide screen', async () => {
    windowSize.mockReturnValue({ width: 1280, height: 800, scale: 1, fontScale: 1 });
    seed([serverRead('Emma', 2), confident()]);
    await open();
    expect(await screen.findByText('Ready · 2')).toBeTruthy();
    expect(screen.queryByTestId('match-reel')).toBeNull();
  });
});
