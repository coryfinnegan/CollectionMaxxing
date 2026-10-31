import { screen } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import { mockMe } from '@/mocks/data';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { MeScreen } from './MeScreen';

jest.mock('expo-router', () => ({ useRouter: () => ({ push: jest.fn() }) }));

const asRole = (role: 'owner' | 'editor' | 'viewer') =>
  server.use(
    http.get('*/api/v1/me', () =>
      HttpResponse.json({ ...mockMe(), memberships: [{ libraryId: mockMe().memberships[0]!.libraryId, role }] }),
    ),
  );

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
