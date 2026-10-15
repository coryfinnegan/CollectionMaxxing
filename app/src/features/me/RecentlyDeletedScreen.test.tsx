import { fireEvent, screen, waitFor } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import { mockMe } from '@/mocks/data';
import { mockAccount, resetMockAccount } from '@/mocks/me';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { RecentlyDeletedScreen, daysLeft } from './RecentlyDeletedScreen';

beforeEach(() => resetMockAccount());

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

  it('counts whole days, rounding up', () => {
    const now = Date.parse('2026-10-15T12:00:00Z');
    expect(daysLeft('2026-10-16T00:00:00Z', now)).toBe('1 day left');
    expect(daysLeft('2026-10-15T11:00:00Z', now)).toBe('Removed for good today');
  });
});
