// Review end to end against the mock API: grouping on screen, one commit, Undo, pick-one, and read-only for a viewer.
import { act, fireEvent, screen, waitFor } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import type { CandidateDto } from '@/api/generated/models';
import { useCardsStore } from '@/features/scan/cards/cardsStore';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { itemsState, resetItems } from '@/mocks/items';
import { scanMock } from '@/mocks/scan';
import { reviewMock } from '@/mocks/scan-review';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { ReviewScreen } from './ReviewScreen';
import { candidate, release } from './testData';

const SESSION = 'a0000000-0000-4000-8000-000000000001';
const alien = [release(11, 'Alien', { year: 1979 }), release(12, 'Aliens', { year: 1986 }), release(13, 'Alien 3', { year: 1992 })];

const seed = (list: CandidateDto[]) => {
  scanMock.sessions.set(SESSION, { id: SESSION, libraryId: MOCK_LIBRARY_ID, seq: 100, candidates: list, answers: new Map(), status: 'open' });
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

beforeEach(() => {
  scanMock.reset();
  reviewMock.reset();
  resetItems();
  useCardsStore.getState().reset();
});

describe('Review screen', () => {
  it('shows the groups in order of attention, with counts', async () => {
    seed([mario(), dune(), pick(), lost()]);
    await open();
    await screen.findByText('Pick one · 1');
    const headers = screen.getAllByRole('header').map((h) => String(h.props.children));
    expect(headers.filter((h) => h.includes(' · ') && h !== 'Review')).toEqual(['Pick one · 1', 'Not found · 1', 'You own these · 1', 'Ready · 1']);
  });

  it('counts only checked rows and adds them with one commit, then offers Undo', async () => {
    seed([mario(), dune(), pick(), lost()]);
    await open();
    const button = await screen.findByLabelText('Add 1 item');
    expect(screen.getByLabelText('Snap cover, coming soon')).toBeDisabled();
    await act(async () => {
      fireEvent.press(button);
      fireEvent.press(button);
    });
    await screen.findByText('Added 1 item');
    expect(reviewMock.commits).toHaveLength(1);
    const added = scanMock.sessions.get(SESSION)!.candidates.filter((c) => c.createdItemId);
    expect(added).toHaveLength(1);

    fireEvent.press(screen.getByLabelText('Undo'));
    await screen.findByText(/Undone/);
    expect(itemsState.entries.find((e) => e.item.id === added[0]!.createdItemId)?.item.deletedAt).toBeTruthy();
  });

  it('takes the Undo away once its time is up', async () => {
    seed([mario()]);
    await open(150);
    fireEvent.press(await screen.findByLabelText('Add 1 item'));
    await screen.findByText('Added 1 item');
    await waitFor(() => expect(screen.queryByText('Added 1 item')).toBeNull(), { timeout: 2000 });
    expect(screen.queryByLabelText('Undo')).toBeNull();
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

  it('collapses a long Ready group to one line', async () => {
    seed(Array.from({ length: 21 }, (_, i) => candidate({ release: release(200 + i, `Game ${i}`) })));
    await open();
    fireEvent.press(await screen.findByLabelText('21 items ready. Show them'));
    await screen.findByLabelText('Add Game 0');
    expect(screen.getByLabelText('Add 21 items')).toBeTruthy();
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
});
