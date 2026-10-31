// The wide web's sidebar: the places you go, with the Me pages listed directly (the wide layout has no Me tab).
import { screen, waitFor, within } from '@testing-library/react-native';
import { HttpResponse, http } from 'msw';
import { mockMe } from '@/mocks/data';
import { server } from '@/mocks/server';
import { renderWithProviders } from '@/test/render';
import { Sidebar } from './Sidebar';

jest.mock('expo-router', () => {
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const React = require('react') as typeof import('react');
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const { View } = require('react-native') as typeof import('react-native');
  return {
    usePathname: () => '/library',
    // Each link renders as a marker that names where it goes, around the real pressable.
    Link: ({ children, href }: { children: React.ReactNode; href: string }) => React.createElement(View, { testID: `link:${href}` }, children),
  };
});
jest.mock('@/features/onboarding/LibrarySwitcher', () => ({ LibrarySwitcher: () => null }));

const asRole = (role: 'owner' | 'editor' | 'viewer') =>
  server.use(
    http.get('*/api/v1/me', () => HttpResponse.json({ ...mockMe(), memberships: [{ ...mockMe().memberships[0]!, role }] })),
  );

/** Where each link goes, top to bottom. */
const destinations = () => screen.getAllByTestId(/^link:/).map((link) => String(link.props.testID).slice('link:'.length));

describe('the sidebar', () => {
  it('lists Sold after Import and export, and Recently deleted beside it, for an owner or an editor', async () => {
    for (const role of ['owner', 'editor'] as const) {
      asRole(role);
      const view = await renderWithProviders(<Sidebar />);
      await screen.findByText('Recently deleted');

      expect(destinations()).toEqual([
        '/library',
        '/wishlist',
        '/lent-out',
        '/me/locations',
        '/members',
        '/me/export',
        '/sold',
        '/me/deleted',
        '/me/devices',
        '/settings',
      ]);
      await view.unmount();
    }
  });

  it('names each link in plain words, Wishlist and Lent out where they were', async () => {
    asRole('owner');
    await renderWithProviders(<Sidebar />);
    await screen.findByText('Recently deleted');

    const label = (href: string) => within(screen.getByTestId(`link:${href}`)).getByText(/./).props.children;
    expect(label('/sold')).toBe('Sold');
    expect(label('/me/deleted')).toBe('Recently deleted');
    expect(label('/wishlist')).toBe('Wishlist');
    expect(label('/lent-out')).toBe('Lent out');
    expect(label('/me/export')).toBe('Import and export');
  });

  it('shows a viewer Sold but not Recently deleted, the same rule Me uses', async () => {
    asRole('viewer');
    await renderWithProviders(<Sidebar />);

    expect(await screen.findByText('Sold')).toBeTruthy();
    await waitFor(() => expect(screen.queryByText('Recently deleted')).toBeNull());
    expect(destinations()).toEqual(['/library', '/wishlist', '/lent-out', '/me/locations', '/members', '/me/export', '/sold', '/me/devices', '/settings']);
  });

  it('shows no Recently deleted until the role is known, and Sold at once', async () => {
    server.use(http.get('*/api/v1/me', () => new Promise(() => undefined)));
    await renderWithProviders(<Sidebar />);

    expect(await screen.findByText('Sold')).toBeTruthy();
    expect(screen.queryByText('Recently deleted')).toBeNull();
  });
});
