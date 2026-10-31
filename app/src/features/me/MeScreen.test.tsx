import { fireEvent, screen } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import { mockMe } from '@/mocks/data';
import { resetItems } from '@/mocks/items';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { MeScreen } from './MeScreen';

const mockPush = jest.fn();
jest.mock('expo-router', () => ({ useRouter: () => ({ push: mockPush }) }));

beforeEach(() => {
  mockPush.mockClear();
  resetItems();
});

const asRole = (role: 'owner' | 'editor' | 'viewer') =>
  server.use(
    http.get('*/api/v1/me', () =>
      HttpResponse.json({ ...mockMe(), memberships: [{ libraryId: mockMe().memberships[0]!.libraryId, role }] }),
    ),
  );

describe('MeScreen Sold row', () => {
  it('keeps what you sold out of the way: a Sold row with its count, beside Recently deleted', async () => {
    await renderWithProviders(<MeScreen />);

    expect(await screen.findByText('Sold')).toBeTruthy();
    expect(await screen.findByLabelText('Sold, 1 item')).toBeTruthy();
    expect(screen.getByText('Recently deleted')).toBeTruthy();
  });

  it('does not count what you sold as part of the library on the Me card', async () => {
    await renderWithProviders(<MeScreen />);

    // The library has 34 items (owned, lent and missing). The sold one is counted on its own row.
    expect(await screen.findByText(/34 items/)).toBeTruthy();
  });

  it('opens the Sold page from its row', async () => {
    await renderWithProviders(<MeScreen />);

    await fireEvent.press(await screen.findByLabelText('Sold, 1 item'));

    expect(mockPush).toHaveBeenCalledWith('/sold');
  });

  it('shows the Sold row to a viewer too, who can look at what was sold but not change it', async () => {
    asRole('viewer');
    await renderWithProviders(<MeScreen />);

    expect(await screen.findByLabelText('Sold, 1 item')).toBeTruthy();
    expect(screen.queryByText('Recently deleted')).toBeNull();
  });
});

describe('MeScreen Members row', () => {
  it('tells an owner they can invite people and manage roles', async () => {
    await renderWithProviders(<MeScreen />);

    expect(await screen.findByText('Invite people and manage roles')).toBeTruthy();
  });

  it.each(['editor', 'viewer'] as const)('tells %s members they can see who shares the library', async (role) => {
    asRole(role);
    await renderWithProviders(<MeScreen />);

    expect(await screen.findByText('See who shares this library')).toBeTruthy();
    expect(screen.queryByText('Invite people and manage roles')).toBeNull();
  });

  it('no longer claims members are managed on the web, which is wrong in the web app itself', async () => {
    await renderWithProviders(<MeScreen />);
    await screen.findByText('Invite people and manage roles');

    expect(screen.queryByText('Managed on the web')).toBeNull();
  });
});
