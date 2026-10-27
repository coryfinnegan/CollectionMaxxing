// The scanner count bar (#306): a frosted dock over the camera on iOS 26+, the surface bar elsewhere.
import { act, fireEvent, screen, waitFor } from '@testing-library/react-native';
import type { CandidateDto } from '@/api/generated/models';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { scanMock } from '@/mocks/scan';
import { reviewMock } from '@/mocks/scan-review';
import { renderWithProviders } from '@/test/render';
import { useCardsStore } from '../cards/cardsStore';
import { useAutoCommitUndoStore } from '../session/autoCommitUndo';
import type { SessionCounters } from '../session/sessionCounters';
import { ReceiptBar } from './ReceiptBar';
import type { TrayLine } from './tray';

const mockGlassState = { available: false };

jest.mock('expo-glass-effect', () => {
  const React = jest.requireActual<typeof import('react')>('react');
  const { View } = jest.requireActual<typeof import('react-native')>('react-native');
  return {
    GlassView: (props: object) => React.createElement(View, { ...props, accessibilityLabel: 'liquid glass' }),
    isLiquidGlassAvailable: () => mockGlassState.available,
  };
});

const noop = () => undefined;

const bar = (available: boolean) => {
  mockGlassState.available = available;
  return renderWithProviders(
    <ReceiptBar libraryId={MOCK_LIBRARY_ID} lines={[]} cards={[]} online items={3} processing={1} problem={null} notice={null} onPlusOne={noop} onRemove={noop} onReview={noop} />,
  );
};

describe('ReceiptBar glass', () => {
  it('keeps the count bar a plain surface bar without liquid glass', async () => {
    await bar(false);

    expect(screen.getByRole('button', { name: /Show the receipt/ })).toBeTruthy();
    expect(screen.queryByLabelText('liquid glass')).toBeNull();
  });

  it('floats the count bar in liquid glass on iOS 26+', async () => {
    await bar(true);

    expect(screen.getByLabelText('liquid glass')).toBeTruthy();
    expect(screen.getByRole('button', { name: /Show the receipt/ })).toBeTruthy();
  });
});

describe('ReceiptBar auto-add', () => {
  const line: TrayLine = { code: '9780441172719', symbology: 'ean-13', copies: 1, sentCopies: 1, addedAt: 1, lastSeenAt: 1 };
  const card = (createdItemId: string | null) =>
    ({ id: 'c1', rawCode: '9780441172719', resolution: 'matched', createdItemId, release: { title: 'Dune' } }) as unknown as CandidateDto;

  const openReceipt = async (cards: CandidateDto[]) => {
    mockGlassState.available = false;
    await renderWithProviders(
      <ReceiptBar libraryId={MOCK_LIBRARY_ID} lines={[line]} cards={cards} online items={1} processing={0} problem={null} notice={null} onPlusOne={noop} onRemove={noop} onReview={noop} />,
    );
    fireEvent.press(screen.getByLabelText('1 item. Show the receipt'));
  };

  it('marks an auto-added line ADDED', async () => {
    await openReceipt([card('item-1')]);

    expect(await screen.findByText('ADDED')).toBeTruthy();
    expect(screen.getByLabelText('Dune, 1 copy, added')).toBeTruthy();
  });

  it('leaves a resolved-but-not-added line unmarked', async () => {
    await openReceipt([card(null)]);

    expect(await screen.findByText('Dune')).toBeTruthy();
    expect(screen.queryByText('ADDED')).toBeNull();
    expect(screen.getByLabelText('Dune, 1 copy')).toBeTruthy();
  });
});

describe('ReceiptBar resolved labels (#450)', () => {
  const line: TrayLine = { code: '9781974723386', symbology: 'ean-13', copies: 1, sentCopies: 1, addedAt: 1, lastSeenAt: 1 };

  beforeEach(() => useCardsStore.getState().reset());

  const openReceipt = async (cards: CandidateDto[]) => {
    mockGlassState.available = false;
    await renderWithProviders(
      <ReceiptBar libraryId={MOCK_LIBRARY_ID} lines={[line]} cards={cards} online items={1} processing={0} problem={null} notice={null} onPlusOne={noop} onRemove={noop} onReview={noop} />,
    );
    fireEvent.press(screen.getByLabelText('1 item. Show the receipt'));
  };

  it('shows the book for a resolved-but-rejected line instead of the raw code', async () => {
    useCardsStore.getState().rememberTitles([{ rawCode: line.code, release: { title: 'The Legend of Zelda: Twilight Princess 9' } }]);
    await openReceipt([]);

    expect(await screen.findByText('The Legend of Zelda: Twilight Princess 9')).toBeTruthy();
    expect(screen.queryByText(line.code)).toBeNull();
  });

  it('shows an honest not-found state for an unanswered line', async () => {
    const notFound = { id: 'c1', rawCode: line.code, resolution: 'not_found', release: null } as unknown as CandidateDto;
    await openReceipt([notFound]);

    expect(await screen.findByText('Not in the catalog')).toBeTruthy();
    expect(screen.queryByText(line.code)).toBeNull();
  });

  it('keeps the raw code while the lookup is still running', async () => {
    const pending = { id: 'c1', rawCode: line.code, resolution: 'pending', release: null } as unknown as CandidateDto;
    await openReceipt([pending]);

    expect(await screen.findByText(line.code)).toBeTruthy();
    expect(await screen.findByText('LOOKING UP')).toBeTruthy();
  });
});

describe('ReceiptBar remove', () => {
  const line: TrayLine = { code: '9780441172719', symbology: 'ean-13', copies: 1, sentCopies: 1, addedAt: 1, lastSeenAt: 1 };

  it('removes a barcode line with its Remove button', async () => {
    const onRemove = jest.fn();
    mockGlassState.available = false;
    await renderWithProviders(
      <ReceiptBar libraryId={MOCK_LIBRARY_ID} lines={[line]} cards={[]} online items={1} processing={0} problem={null} notice={null} onPlusOne={noop} onRemove={onRemove} onReview={noop} />,
    );
    fireEvent.press(screen.getByLabelText('1 item. Show the receipt'));

    fireEvent.press(await screen.findByLabelText('Remove 9780441172719 from this scan'));

    expect(onRemove).toHaveBeenCalledWith('9780441172719');
  });

  it('removes a picture line with its Remove button', async () => {
    const onRemovePicture = jest.fn();
    mockGlassState.available = false;
    await renderWithProviders(
      <ReceiptBar
        libraryId={MOCK_LIBRARY_ID}
        lines={[]}
        pictureLines={[{ key: 'snap-1:item-1', snapId: 'snap-1', localId: 'item-1', thumbUri: undefined, title: 'Dune', status: 'identifying', createdAt: 1 }]}
        onRemovePicture={onRemovePicture}
        cards={[]}
        online
        items={1}
        processing={1}
        problem={null}
        notice={null}
        onPlusOne={noop}
        onRemove={noop}
        onReview={noop}
      />,
    );
    fireEvent.press(screen.getByLabelText('1 item · 1 processing. Show the receipt'));

    fireEvent.press(await screen.findByLabelText('Remove Dune from this scan'));

    expect(onRemovePicture).toHaveBeenCalledWith('snap-1', 'item-1');
  });
});

const SESSION = 'session-1';
const ZERO: SessionCounters = { captured: 0, added: 0, toReview: 0, owned: 0, skipped: 0 };

describe('ReceiptBar status line', () => {
  const statusBar = (
    props: {
      online?: boolean;
      items?: number;
      processing?: number;
      counters?: SessionCounters;
      sessionId?: string | null;
      canAdd?: boolean;
    } = {},
  ) => {
    mockGlassState.available = false;
    return renderWithProviders(
      <ReceiptBar
        libraryId={MOCK_LIBRARY_ID}
        lines={[]}
        cards={[]}
        online={props.online ?? true}
        items={props.items ?? 0}
        processing={props.processing ?? 0}
        problem={null}
        notice={null}
        counters={props.counters ?? ZERO}
        sessionId={props.sessionId === undefined ? SESSION : props.sessionId}
        canAdd={props.canAdd ?? true}
        onPlusOne={noop}
        onRemove={noop}
        onReview={noop}
      />,
    );
  };

  it('merges the tray and the session counters into one status line', async () => {
    await statusBar({ items: 5, processing: 1, counters: { ...ZERO, added: 2, toReview: 4, owned: 1 } });

    expect(await screen.findByLabelText('5 items · 1 processing · 4 to review · 1 owned · 2 added. Show the receipt')).toBeTruthy();
  });

  it('badges offline and keeps counting', async () => {
    await statusBar({ online: false, items: 1, processing: 1, counters: { ...ZERO, toReview: 1 } });

    expect(
      await screen.findByLabelText('1 item · 1 processing · 1 to review. Offline: counts include scans still on this phone. Show the receipt'),
    ).toBeTruthy();
    expect(await screen.findByLabelText('Offline')).toBeTruthy();
  });
});

describe('ReceiptBar undo last add', () => {
  beforeEach(() => {
    scanMock.reset();
    reviewMock.reset();
    useCardsStore.getState().reset();
    useAutoCommitUndoStore.setState({ last: null });
  });

  const added = (id: string, seq: number) =>
    ({
      id,
      seq,
      changeSeq: seq,
      source: 'barcode',
      rawCode: `code-${id}`,
      resolution: 'matched',
      alreadyOwnedCount: 0,
      ownedAt: [],
      decision: 'undecided',
      createdItemId: `item-${id}`,
      release: { id: `r-${id}`, title: `Title ${id}` },
    }) as unknown as CandidateDto;

  const seedStanding = (...candidates: CandidateDto[]) => {
    scanMock.sessions.set(SESSION, {
      id: SESSION,
      libraryId: MOCK_LIBRARY_ID,
      seq: 100,
      candidates: JSON.parse(JSON.stringify(candidates)) as CandidateDto[],
      answers: new Map(),
      status: 'open',
    });
    useCardsStore.getState().merge(candidates, { announce: false });
  };

  const barWith = (props: { sessionId?: string | null; canAdd?: boolean } = {}) => {
    mockGlassState.available = false;
    return renderWithProviders(
      <ReceiptBar
        libraryId={MOCK_LIBRARY_ID}
        lines={[]}
        cards={[]}
        online
        items={0}
        processing={0}
        problem={null}
        notice={null}
        counters={ZERO}
        sessionId={props.sessionId === undefined ? SESSION : props.sessionId}
        canAdd={props.canAdd ?? true}
        onPlusOne={noop}
        onRemove={noop}
        onReview={noop}
      />,
    );
  };

  const pressUndoLast = async () => {
    const target = await screen.findByLabelText('Undo last add');
    await act(async () => {
      fireEvent.press(target);
    });
  };

  it('undoes the newest standing add first, then hides when none stand', async () => {
    seedStanding(added('c1', 1), added('c2', 2));
    await barWith();

    await pressUndoLast();

    await waitFor(() => expect(useCardsStore.getState().byId['c2']!.createdItemId).toBeNull());
    expect(useCardsStore.getState().byId['c1']!.createdItemId).toBe('item-c1');
    expect(reviewMock.autoUndos).toEqual([{ sessionId: SESSION, key: expect.any(String), candidateId: null }]);
    // c1 still stands: the button stays.
    expect(screen.getByLabelText('Undo last add')).toBeTruthy();

    await pressUndoLast();

    await waitFor(() => expect(useCardsStore.getState().byId['c1']!.createdItemId).toBeNull());
    expect(screen.queryByLabelText('Undo last add')).toBeNull();
  });

  it('dismisses the snackbar when it undoes the add the snackbar shows', async () => {
    seedStanding(added('c1', 1));
    useAutoCommitUndoStore.getState().setLast({ sessionId: SESSION, candidateId: 'c1' });
    await barWith();

    await pressUndoLast();

    await waitFor(() => expect(useAutoCommitUndoStore.getState().last).toBeNull());
  });

  it('hides when nothing stands', async () => {
    await barWith();
    expect(screen.queryByLabelText('Undo last add')).toBeNull();
  });

  it('hides for a viewer', async () => {
    seedStanding(added('c1', 1));
    await barWith({ canAdd: false });

    expect(await screen.findByLabelText('0 items. Show the receipt')).toBeTruthy();
    expect(screen.queryByLabelText('Undo last add')).toBeNull();
  });

  it('hides without a session', async () => {
    seedStanding(added('c1', 1));
    await barWith({ sessionId: null });

    expect(await screen.findByLabelText('0 items. Show the receipt')).toBeTruthy();
    expect(screen.queryByLabelText('Undo last add')).toBeNull();
  });

  it('says when the undo failed and keeps the button', async () => {
    seedStanding(added('c1', 1));
    reviewMock.failNextAutoUndo = 409;
    await barWith();

    await pressUndoLast();

    expect(await screen.findByText('It changed since, so it stays in your library.')).toBeTruthy();
    expect(useCardsStore.getState().byId['c1']!.createdItemId).toBe('item-c1');
    expect(screen.getByLabelText('Undo last add')).toBeTruthy();
  });
});
