// The auto-add snackbar: "Added Dune" with Undo for 10 s, then it hides (#451: the only Undo on
// the scanner). A failed undo says so without poisoning the next add.
import { act, fireEvent, screen, waitFor } from '@testing-library/react-native';
import type { CandidateDto } from '@/api/generated/models';
import { scanMock } from '@/mocks/scan';
import { reviewMock } from '@/mocks/scan-review';
import { useCardsStore } from '@/features/scan/cards/cardsStore';
import { renderWithProviders } from '@/test/render';
import { useAutoCommitUndoStore } from './autoCommitUndo';
import { AutoCommitSnackbar } from './AutoCommitSnackbar';

const SESSION = 'f0000000-0000-4000-8000-000000000001';

const dune = {
  id: 'c-added',
  seq: 1,
  changeSeq: 1,
  source: 'barcode',
  rawCode: '9780441172719',
  resolution: 'matched',
  alreadyOwnedCount: 0,
  ownedAt: [],
  decision: 'undecided',
  createdItemId: 'item-auto-1',
  release: { id: 'r1', title: 'Dune', format: 'paperback', year: 1990, platform: null, creators: [], coverUrl: null },
} as unknown as CandidateDto;

const seed = (candidate: CandidateDto = dune) => {
  scanMock.sessions.set(SESSION, {
    id: SESSION,
    libraryId: 'library-1',
    seq: 100,
    candidates: [JSON.parse(JSON.stringify(candidate)) as CandidateDto],
    answers: new Map(),
    status: 'open',
  });
  useCardsStore.getState().merge([candidate], { announce: false });
  useAutoCommitUndoStore.getState().setLast({ sessionId: SESSION, candidateId: candidate.id });
};

beforeEach(() => {
  scanMock.reset();
  reviewMock.reset();
  useCardsStore.getState().reset();
  useAutoCommitUndoStore.setState({ last: null });
});

describe('AutoCommitSnackbar', () => {
  it('takes the add back on Undo and goes away', async () => {
    seed();
    await renderWithProviders(<AutoCommitSnackbar libraryId="library-1" />);
    expect(await screen.findByText('Added Dune')).toBeTruthy();

    await fireEvent.press(screen.getByLabelText('Undo'));

    await waitFor(() => expect(screen.queryByText('Added Dune')).toBeNull());
    expect(useAutoCommitUndoStore.getState().last).toBeNull();
    expect(useCardsStore.getState().byId['c-added']!.createdItemId).toBeNull();
    expect(reviewMock.autoUndos).toEqual([{ sessionId: SESSION, key: expect.any(String), candidateId: 'c-added' }]);
  });

  it('says when the undo failed and offers nothing stale after that', async () => {
    seed();
    reviewMock.failNextAutoUndo = 409;
    await renderWithProviders(<AutoCommitSnackbar libraryId="library-1" />);
    expect(await screen.findByText('Added Dune')).toBeTruthy();

    await fireEvent.press(screen.getByLabelText('Undo'));

    expect(await screen.findByText('It changed since, so it stays in your library.')).toBeTruthy();
    expect(screen.queryByLabelText('Undo')).toBeNull();
    expect(useAutoCommitUndoStore.getState().last).not.toBeNull();
  });

  it('hides after 10 s but keeps the session’s last add undoable', async () => {
    jest.useFakeTimers();
    try {
      seed();
      await renderWithProviders(<AutoCommitSnackbar libraryId="library-1" />);
      expect(screen.getByText('Added Dune')).toBeTruthy();

      await act(async () => {
        jest.advanceTimersByTime(10_000);
      });

      expect(screen.queryByText('Added Dune')).toBeNull();
      expect(useAutoCommitUndoStore.getState().last).toEqual({ sessionId: SESSION, candidateId: 'c-added' });
    } finally {
      jest.useRealTimers();
    }
  });

  it('shows a newer add when one lands while an older snackbar is up', async () => {
    seed();
    await renderWithProviders(<AutoCommitSnackbar libraryId="library-1" />);
    expect(await screen.findByText('Added Dune')).toBeTruthy();

    const jaws = { ...dune, id: 'c-added-2', release: { ...dune.release!, title: 'Jaws' } } as CandidateDto;
    await act(async () => {
      scanMock.sessions.get(SESSION)!.candidates.push(JSON.parse(JSON.stringify(jaws)) as CandidateDto);
      useCardsStore.getState().merge([jaws], { announce: false });
      useAutoCommitUndoStore.getState().setLast({ sessionId: SESSION, candidateId: jaws.id });
    });

    expect(await screen.findByText('Added Jaws')).toBeTruthy();
    expect(screen.queryByText('Added Dune')).toBeNull();
  });
});
