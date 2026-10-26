import { act, fireEvent, screen, waitFor } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import type { DeletedItemDto } from '@/api/generated/models';
import { useUndoStore } from '@/features/item/useItem';
import { LibraryView } from '@/features/library/LibraryView';
import { mockMe } from '@/mocks/data';
import { itemsState, resetItems } from '@/mocks/items';
import { mockAccount, resetMockAccount } from '@/mocks/me';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { RecentlyDeletedScreen, daysLeft } from './RecentlyDeletedScreen';

const MATRIX = 'c0000000-0000-4000-8000-000000000001';
const HOUR = 3_600_000;

const matrixBinRow = (): DeletedItemDto => ({
  id: MATRIX,
  releaseId: 'e0000000-0000-4000-8000-000000000001',
  workId: 'd0000000-0000-4000-8000-000000000001',
  title: 'The Matrix',
  kind: 'film',
  format: 'dvd',
  platformName: null,
  year: 1999,
  coverUrl: null,
  deletedAt: new Date().toISOString(),
  purgesAt: new Date(Date.now() + 28 * 86_400_000).toISOString(),
});

// The real API puts the restored item back in the library; the bin and items mocks
// don't share state, so this override links them for the restore round trip.
const linkBinToLibrary = () =>
  server.use(
    http.post(/\/api\/v1\/libraries\/[^/]+\/items\/([^/]+):restore$/, ({ request }) => {
      const id = new URL(request.url).pathname.split('/').pop()?.replace(':restore', '');
      mockAccount.bin = mockAccount.bin.filter((d) => d.id !== id);
      const entry = itemsState.entries.find((e) => e.item.id === id);
      if (entry) {
        entry.item.deletedAt = null;
        entry.version += 1;
        return HttpResponse.json(entry.item);
      }
      return HttpResponse.json({ id, deletedAt: null });
    }),
  );

beforeEach(() => {
  resetMockAccount();
  resetItems();
  useUndoStore.setState({ deleted: null });
});

const asRole = (role: 'owner' | 'editor' | 'viewer') =>
  server.use(http.get('*/api/v1/me', () => HttpResponse.json({ ...mockMe(), memberships: [{ ...mockMe().memberships[0], role }] })));

describe('RecentlyDeletedScreen', () => {
  it('lists deleted items with the days left', async () => {
    await renderWithProviders(<RecentlyDeletedScreen />);

    expect(await screen.findByText('Aliens')).toBeTruthy();
    expect(screen.getByText('28 days left')).toBeTruthy();
    expect(screen.getByText('Star Fox 64')).toBeTruthy();
    expect(screen.getByText('1 day left')).toBeTruthy();
  });

  it('restores an item and removes its row', async () => {
    await renderWithProviders(<RecentlyDeletedScreen />);
    await screen.findByText('Aliens');

    await fireEvent.press(screen.getByLabelText('Restore Aliens'));

    await waitFor(() => expect(screen.queryByText('Aliens')).toBeNull());
    expect(screen.getByText('Aliens is back in your library.')).toBeTruthy();
    expect(mockAccount.bin).toHaveLength(1);
  });

  it('shows the restored item in the library without a refresh', async () => {
    itemsState.entries.find((e) => e.item.id === MATRIX)!.item.deletedAt = new Date().toISOString();
    mockAccount.bin = [matrixBinRow()];
    linkBinToLibrary();

    await renderWithProviders(
      <>
        <LibraryView title="Library" />
        <RecentlyDeletedScreen />
      </>,
    );

    // Both views have loaded: the bin holds The Matrix, the library does not.
    await screen.findByLabelText('Restore The Matrix');
    await screen.findByLabelText('Super Mario 64, Cartridge, Nintendo 64');
    expect(screen.queryByLabelText('The Matrix, DVD')).toBeNull();

    await fireEvent.press(screen.getByLabelText('Restore The Matrix'));

    expect(await screen.findByLabelText('The Matrix, DVD')).toBeTruthy();
    expect(screen.getByText('The Matrix is back in your library.')).toBeTruthy();
  });

  it('shows the empty state when nothing is deleted', async () => {
    mockAccount.bin = [];
    await renderWithProviders(<RecentlyDeletedScreen />);

    expect(await screen.findByText('Nothing deleted')).toBeTruthy();
    expect(screen.getByText('Items you delete wait here for 30 days, so you can bring them back.')).toBeTruthy();
  });

  it('does not show the bin to a viewer', async () => {
    asRole('viewer');
    await renderWithProviders(<RecentlyDeletedScreen />);

    expect(await screen.findByText(/Only editors and the owner/)).toBeTruthy();
    expect(screen.queryByText('Aliens')).toBeNull();
  });

  it('refreshes the countdown while the screen stays open', async () => {
    jest.useFakeTimers();
    try {
      mockAccount.bin = [{ ...mockAccount.bin[0]!, purgesAt: new Date(Date.now() + 26 * HOUR).toISOString() }];
      await renderWithProviders(<RecentlyDeletedScreen />);
      expect(await screen.findByText('2 days left')).toBeTruthy();

      await act(async () => {
        jest.advanceTimersByTime(3 * HOUR);
      });

      expect(await screen.findByText('1 day left')).toBeTruthy();
    } finally {
      jest.useRealTimers();
    }
  });

  it('counts whole days, rounding up', () => {
    const now = Date.parse('2026-10-15T12:00:00Z');
    expect(daysLeft('2026-10-16T00:00:00Z', now)).toBe('1 day left');
    expect(daysLeft('2026-10-15T11:00:00Z', now)).toBe('Removed for good today');
  });
});
