// "Needs attention" against the mock API: kept rows list with their crops, and the three ways out.
import { fireEvent, screen, waitFor } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import type { ProvisionalDto } from '@/api/generated/models';
import { boundScanStore } from '@/features/review/boundScan';
import { itemsState, resetItems } from '@/mocks/items';
import { MOCK_LIBRARY_ID } from '@/mocks/data';
import { scanMock } from '@/mocks/scan';
import { reviewMock } from '@/mocks/scan-review';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { NeedsAttention } from './NeedsAttention';

const mockRouter = { push: jest.fn(), back: jest.fn(), replace: jest.fn() };
jest.mock('expo-router', () => ({ useRouter: () => mockRouter }));

const SESSION = 'b0000000-0000-4000-8000-000000000001';

const kept = (n: number, extra: Partial<ProvisionalDto> = {}): ProvisionalDto => ({
  id: `b1000000-0000-4000-8000-${String(n).padStart(12, '0')}`,
  libraryId: MOCK_LIBRARY_ID,
  sessionId: SESSION,
  snapId: null,
  titleGuess: `Unreadable spine ${n}`,
  isGuess: true,
  mediaHint: 'books',
  locationId: null,
  status: 'unverified',
  candidateId: `b2000000-0000-4000-8000-${String(n).padStart(12, '0')}`,
  cropUrl: null,
  createdAt: `2026-10-2${n}T12:00:00Z`,
  resolvedAt: null,
  resolveAction: null,
  createdItemId: null,
  ...extra,
});

const seed = (list: ProvisionalDto[]) => {
  scanMock.sessions.set(SESSION, { id: SESSION, libraryId: MOCK_LIBRARY_ID, seq: 1, candidates: [], answers: new Map(), status: 'open', provisionals: list });
};

beforeEach(() => {
  scanMock.reset();
  reviewMock.reset();
  resetItems();
  boundScanStore.getState().clear();
  mockRouter.push.mockClear();
});

describe('Needs attention', () => {
  it('lists kept rows with their guessed titles and crops', async () => {
    seed([kept(1, { cropUrl: '/media/covers/crop-1' }), kept(2)]);
    await renderWithProviders(<NeedsAttention libraryId={MOCK_LIBRARY_ID} />);

    expect(await screen.findByText('"Unreadable spine 1"')).toBeTruthy();
    expect(screen.getByText('"Unreadable spine 2"')).toBeTruthy();
    expect(screen.getByLabelText('Cover of Unreadable spine 1')).toBeTruthy();
  });

  it('says plainly when nothing needs attention', async () => {
    seed([]);
    await renderWithProviders(<NeedsAttention libraryId={MOCK_LIBRARY_ID} />);

    expect(await screen.findByText('Nothing needs attention.')).toBeTruthy();
  });

  it('adds the guess to the library on promote', async () => {
    seed([kept(1)]);
    await renderWithProviders(<NeedsAttention libraryId={MOCK_LIBRARY_ID} />);
    const before = itemsState.entries.length;

    await fireEvent.press(await screen.findByLabelText('Add "Unreadable spine 1" to your library'));

    await waitFor(() => expect(itemsState.entries.length).toBe(before + 1));
    expect(itemsState.entries[itemsState.entries.length - 1]!.release.title).toBe('Unreadable spine 1');
    await screen.findByText('Nothing needs attention.');
  });

  it('links the guess to a picked item', async () => {
    seed([kept(1)]);
    await renderWithProviders(<NeedsAttention libraryId={MOCK_LIBRARY_ID} />);

    await fireEvent.press(await screen.findByLabelText('Link "Unreadable spine 1" to an item'));
    await fireEvent.press(await screen.findByText('The Matrix'));

    await screen.findByText('Nothing needs attention.');
  });

  it('deletes only on the second tap', async () => {
    seed([kept(1)]);
    await renderWithProviders(<NeedsAttention libraryId={MOCK_LIBRARY_ID} />);

    await fireEvent.press(await screen.findByLabelText('Delete "Unreadable spine 1"'));
    expect(screen.getByLabelText('Confirm deleting "Unreadable spine 1"')).toBeTruthy();
    expect(screen.queryByText('Nothing needs attention.')).toBeNull();
    await fireEvent.press(screen.getByLabelText('Confirm deleting "Unreadable spine 1"'));

    await screen.findByText('Nothing needs attention.');
  });

  it('lets a viewer look but not act', async () => {
    server.use(http.get('*/api/v1/me', () => HttpResponse.json({ id: 'u1', memberships: [{ libraryId: MOCK_LIBRARY_ID, role: 'viewer' }] })));
    seed([kept(1)]);
    await renderWithProviders(<NeedsAttention libraryId={MOCK_LIBRARY_ID} />);

    expect(await screen.findByText('"Unreadable spine 1"')).toBeTruthy();
    expect(screen.queryByLabelText(/to your library/)).toBeNull();
    expect(screen.queryByLabelText(/Link "/)).toBeNull();
    expect(screen.queryByLabelText(/Delete "/)).toBeNull();
    expect(screen.queryByLabelText(/Scan barcode/)).toBeNull();
  });

  it('scans a barcode for the row it was kept from', async () => {
    seed([kept(1)]);
    await renderWithProviders(<NeedsAttention libraryId={MOCK_LIBRARY_ID} />);

    await fireEvent.press(await screen.findByLabelText('Scan barcode for "Unreadable spine 1"'));

    expect(boundScanStore.getState().binding).toEqual({
      sessionId: SESSION,
      candidateId: 'b2000000-0000-4000-8000-000000000001',
      title: 'Unreadable spine 1',
      kind: 'barcode',
    });
    expect(mockRouter.push).toHaveBeenCalledWith('/scanner');
  });

  it('offers no barcode scan once the session is gone', async () => {
    seed([kept(1, { sessionId: null, candidateId: null })]);
    await renderWithProviders(<NeedsAttention libraryId={MOCK_LIBRARY_ID} />);

    expect(await screen.findByText('"Unreadable spine 1"')).toBeTruthy();
    expect(screen.queryByLabelText(/Scan barcode/)).toBeNull();
  });
});
